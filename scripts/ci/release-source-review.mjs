import { repository, releaseReviewStatus, requireString, requireThat } from './release-policy.mjs';
import { parseGithubTimestamp } from './release-github.mjs';
import { bindStatusToHead, verifySquadVerdict, verdictWorkflowPath } from './verify-squad-verdict.mjs';

const shaPattern = /^[a-f0-9]{40}$/;
const loginPattern = /^[a-zA-Z0-9][a-zA-Z0-9-]{0,38}$/;

async function nativeReview(api, pull, transaction, now, maximumAgeMs) {
  const owners = await api(`contents/.github/CODEOWNERS?ref=${transaction.sourceCommit}`);
  requireThat(owners.encoding === 'base64', 'Missing source CODEOWNERS');
  const lines = Buffer.from(owners.content, 'base64').toString('utf8')
    .split(/\r?\n/).map(line => line.trim()).filter(line => line && !line.startsWith('#'));
  const catchAll = /^\*\s+(@[a-zA-Z0-9][a-zA-Z0-9-]{0,38}(?:\s+@[a-zA-Z0-9][a-zA-Z0-9-]{0,38})*)$/.exec(lines.at(-1) ?? '');
  requireThat(catchAll, 'Native review requires supported catch-all source CODEOWNERS');
  const ownersSet = new Set(catchAll[1].toLowerCase().split(/\s+/));
  const run = await api(`actions/runs/${transaction.runId}`);
  const identities = [pull.user?.login, run.actor?.login, run.triggering_actor?.login];
  identities.forEach(login => requireString(login, loginPattern, 'native review initiator'));
  const excluded = new Set(identities.map(login => login.toLowerCase()));
  const reviews = await api(`pulls/${pull.number}/reviews?per_page=100`);
  requireThat(Array.isArray(reviews), 'Missing native review evidence');
  const latest = new Map();
  for (const review of reviews.sort((a, b) => a.id - b.id)) {
    requireThat(Number.isSafeInteger(review.id) && review.id > 0, 'Invalid native review ID');
    requireString(review.user?.login, loginPattern, 'native reviewer');
    if (['APPROVED', 'CHANGES_REQUESTED', 'DISMISSED'].includes(review.state)) {
      latest.set(review.user.login.toLowerCase(), review);
    }
  }
  requireThat(![...latest.values()].some(review => review.state === 'CHANGES_REQUESTED'),
    'Outstanding native change request');
  for (const [login, review] of latest) {
    if (review.state !== 'APPROVED' || review.commit_id !== pull.head.sha ||
        excluded.has(login) || !ownersSet.has(`@${login}`)) continue;
    const submitted = parseGithubTimestamp(review.submitted_at, 'native review time');
    requireThat(submitted >= now - maximumAgeMs && submitted <= now,
      'Native review is stale or future-dated');
    const permission = await api(`collaborators/${review.user.login}/permission`);
    requireThat(permission.user?.login?.toLowerCase() === login &&
      ['admin', 'maintain', 'write'].includes(permission.permission),
    'Native reviewer lacks live write permission');
    return;
  }
  throw new Error('Missing exact-tree non-self code-owner native review');
}

export async function verifyReleaseSourceReview(api, transaction, now, maximumAgeMs) {
  const source = transaction.sourceCommit;
  const associated = await api(`commits/${source}/pulls?per_page=100`);
  requireThat(Array.isArray(associated), 'Missing source pull request evidence');
  const candidates = associated.filter(pull => pull.merge_commit_sha === source &&
    pull.base?.ref === transaction.sourceBranch && pull.base?.repo?.full_name === repository);
  requireThat(candidates.length === 1, 'Source must identify one merged reviewed pull request');
  requireThat(Number.isSafeInteger(candidates[0].number) && candidates[0].number > 0,
    'Invalid source pull request number');
  const pull = await api(`pulls/${candidates[0].number}`);
  requireThat(pull.number === candidates[0].number && pull.merged === true &&
    pull.state === 'closed' && pull.draft === false && pull.merge_commit_sha === source &&
    pull.base?.ref === transaction.sourceBranch && pull.base?.repo?.full_name === repository &&
    pull.base.repo.default_branch === 'development' && pull.head?.repo?.full_name === repository,
  'Source is not the merged result of a trusted canonical pull request');
  requireString(pull.head.sha, shaPattern, 'reviewed source head');
  const trees = await Promise.all([source, pull.head.sha].map(async sha => {
    const commit = await api(`git/commits/${sha}`);
    requireThat(commit.sha === sha, 'Review tree commit binding mismatch');
    requireString(commit.tree?.sha, shaPattern, 'reviewed source tree');
    return commit.tree.sha;
  }));
  requireThat(trees[0] === trees[1], 'Canonical source tree differs from the reviewed PR head');
  const response = await api(`commits/${pull.head.sha}/status?per_page=100`);
  requireThat(response.sha === pull.head.sha && Number.isSafeInteger(response.total_count) &&
    Array.isArray(response.statuses) && response.total_count === response.statuses.length,
  'Missing or truncated exact-head review status evidence');
  const statuses = response.statuses.filter(status => status.context === releaseReviewStatus);
  requireThat(statuses.length > 0 &&
    statuses.every(status => Number.isSafeInteger(status.id) && status.id > 0),
  'Missing genuine source review status');
  const status = statuses.sort((a, b) => b.id - a.id)[0];
  const created = parseGithubTimestamp(status.created_at, 'source review status creation');
  const updated = parseGithubTimestamp(status.updated_at, 'source review status update');
  requireThat(created >= now - maximumAgeMs && created <= updated && updated <= now,
    'Source review status is stale, future-dated, or reversed');
  const target = new RegExp(`^https://github.com/${repository}/actions/runs/([1-9][0-9]*)$`)
    .exec(status.target_url ?? '');
  requireThat(target, 'Review status has no trusted workflow target');
  const run = await api(`actions/runs/${target[1]}`);
  if (['issue_comment', 'workflow_dispatch'].includes(run.event)) {
    requireString(run.head_sha, shaPattern, 'review workflow commit');
    const branch = await api('git/ref/heads/development');
    requireString(branch.object?.sha, shaPattern, 'review workflow branch head');
    const comparison = await api(`compare/${run.head_sha}...${branch.object.sha}`);
    run.default_branch_contains_run = ['ahead', 'identical'].includes(comparison.status) &&
      comparison.merge_base_commit?.sha === run.head_sha;
  } else if (run.event === 'pull_request_review') {
    requireString(run.head_sha, shaPattern, 'review workflow commit');
    const branch = await api('git/ref/heads/development');
    requireString(branch.object?.sha, shaPattern, 'review workflow branch head');
    const blobs = await Promise.all([run.head_sha, branch.object.sha].map(async sha => {
      const file = await api(`contents/${verdictWorkflowPath}?ref=${sha}`);
      requireString(file.sha, shaPattern, 'review workflow blob');
      return file.sha;
    }));
    run.workflow_definition_matches_default_branch = blobs[0] === blobs[1];
  }
  const verdict = verifySquadVerdict({ pull, status: bindStatusToHead(status, pull.head.sha), run });
  requireThat(['REVIEWED', 'APPROVED'].includes(verdict.classification),
    `Untrusted source review evidence: ${verdict.reason}`);
  if (transaction.approvalMode === 'separation-of-duties') {
    await nativeReview(api, pull, transaction, now, maximumAgeMs);
  }
  return {
    sourceCommit: source, reviewedHead: pull.head.sha, tree: trees[0], pullRequest: pull.number,
    classification: verdict.classification, reviewUrl: status.target_url,
    ...(verdict.carriedAcrossSync === true ? { carriedAcrossSync: true } : {}),
    statusId: status.id, createdAt: status.created_at, updatedAt: status.updated_at,
  };
}

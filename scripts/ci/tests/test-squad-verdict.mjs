import assert from 'node:assert/strict';
import test from 'node:test';
import {
  bindStatusToHead,
  selectSquadVerdict,
  verdictContext,
  verdictWorkflowPath,
  verifySquadVerdict,
} from '../verify-squad-verdict.mjs';

const reviewedHeadSha = 'a'.repeat(40);
const movedHeadSha = 'b'.repeat(40);
const shortSha = reviewedHeadSha.slice(0, 12);

function fixture(verdict = 'APPROVE') {
  const actor = 'jpapiez';
  const state = verdict === 'APPROVE' ? 'success' : 'failure';
  const description = verdict === 'APPROVE'
    ? `APPROVE @ ${shortSha} by bishop+hicks+vasquez`
    : `REQUEST_CHANGES @ ${shortSha} by vasquez`;
  const pull = {
    number: 1116,
    user: { login: 'pr-author' },
    head: { sha: reviewedHeadSha },
    base: {
      ref: 'development',
      repo: {
        full_name: 'OlyForge3D/PrintFarmer',
        default_branch: 'development',
      },
    },
  };
  const status = {
    id: 42,
    context: verdictContext,
    state,
    sha: reviewedHeadSha,
    description,
    target_url:
      'https://github.com/OlyForge3D/PrintFarmer/actions/runs/123456',
    creator: { login: 'github-actions[bot]' },
    created_at: '2026-08-07T03:00:10Z',
  };
  const run = {
    id: 123456,
    html_url: status.target_url,
    path: verdictWorkflowPath,
    event: 'issue_comment',
    run_attempt: 1,
    head_branch: 'development',
    head_sha: 'c'.repeat(40),
    default_branch_contains_run: true,
    repository: { full_name: 'OlyForge3D/PrintFarmer' },
    actor: { login: actor },
    triggering_actor: { login: actor },
    display_title: 'Squad verdict gate for PR #1116',
    status: 'completed',
    conclusion: 'success',
    run_started_at: '2026-08-07T03:00:00Z',
    updated_at: '2026-08-07T03:00:20Z',
  };
  return { pull, status, run };
}

test('accepts a trusted approval for the exact current head', () => {
  const evidence = fixture();
  const verdict = verifySquadVerdict(evidence);
  assert.equal(verdict.classification, 'APPROVED');
  assert.equal(verdict.reviewedHeadSha, reviewedHeadSha);
  assert.equal(verdict.actor, 'bishop+hicks+vasquez');
});

test('accepts the agent-verdict events the gate actually runs on', () => {
  for (const event of [
    'pull_request_target', 'issue_comment', 'pull_request_review', 'workflow_dispatch',
  ]) {
    const evidence = fixture();
    evidence.run.event = event;
    assert.equal(verifySquadVerdict(evidence).classification, 'APPROVED', event);
  }
});

test('rejects a run triggered from the pull request head ref', () => {
  const evidence = fixture();
  evidence.run.event = 'pull_request';
  assert.equal(verifySquadVerdict(evidence).classification, 'INVALID');
});

test('accepts a verdict recorded by the PR author account', () => {
  // Every squad agent acts through the owner token, so GitHub-account-level
  // author checking is exactly what made the old gate unsatisfiable. Reviewer
  // separation is enforced at squad-identity level inside the gate itself.
  const evidence = fixture();
  evidence.pull.user.login = 'jpapiez';
  assert.equal(verifySquadVerdict(evidence).classification, 'APPROVED');
});

test('binds the list-statuses API shape to the requested head', () => {
  const evidence = fixture();
  const apiStatus = { ...evidence.status };
  delete apiStatus.sha;
  const status = bindStatusToHead(apiStatus, reviewedHeadSha);
  const verdict = verifySquadVerdict({ ...evidence, status });
  assert.equal(verdict.classification, 'APPROVED');
});

test('rejects a status whose explicit SHA disagrees with the requested head', () => {
  const evidence = fixture();
  assert.throws(
    () => bindStatusToHead(evidence.status, movedHeadSha),
    /does not match the requested head/,
  );
});

test('blocks a trusted changes-requested verdict for the exact current head', () => {
  const evidence = fixture('REQUEST_CHANGES');
  const verdict = verifySquadVerdict(evidence);
  assert.equal(verdict.classification, 'CHANGES_REQUESTED');
  assert.equal(verdict.verdict, 'REQUEST_CHANGES');
});

test('supersedes an approval when rebase or force-push moves the head', () => {
  const evidence = fixture();
  evidence.pull.head.sha = movedHeadSha;
  const verdict = verifySquadVerdict(evidence);
  assert.equal(verdict.classification, 'SUPERSEDED');
  assert.equal(verdict.verdict, 'APPROVE');
});

test('supersedes a rejection when rebase or force-push moves the head', () => {
  const evidence = fixture('REQUEST_CHANGES');
  evidence.pull.head.sha = movedHeadSha;
  const verdict = verifySquadVerdict(evidence);
  assert.equal(verdict.classification, 'SUPERSEDED');
  assert.equal(verdict.verdict, 'REQUEST_CHANGES');
});

for (const verdictName of ['APPROVE', 'REQUEST_CHANGES']) {
  test(`selector supersedes stale ${verdictName} evidence from an expected head`, () => {
    const evidence = fixture(verdictName);
    evidence.pull.head.sha = movedHeadSha;
    const verdict = selectSquadVerdict({
      pull: evidence.pull,
      statuses: [evidence.status],
      statusHeadSha: reviewedHeadSha,
      loadRun: () => evidence.run,
    });
    assert.equal(verdict.classification, 'SUPERSEDED');
    assert.equal(verdict.verdict, verdictName);
  });
}

test('rejects a status not created by GitHub Actions', () => {
  const evidence = fixture();
  evidence.status.creator.login = 'pr-author';
  assert.equal(verifySquadVerdict(evidence).classification, 'INVALID');
});

test('rejects a lookalike status from an untrusted workflow', () => {
  const evidence = fixture();
  evidence.run.path = '.github/workflows/lookalike.yml';
  assert.equal(verifySquadVerdict(evidence).classification, 'INVALID');
});

test('rejects a run whose workflow definition came off the default branch', () => {
  const evidence = fixture();
  evidence.run.head_branch = 'dev/jpapiez/rewrite-the-gate';
  assert.equal(verifySquadVerdict(evidence).classification, 'INVALID');
});

test('accepts a pull_request_target run sitting on a non-default base ref', () => {
  const evidence = fixture();
  evidence.pull.base.ref = 'main';
  evidence.run.event = 'pull_request_target';
  evidence.run.head_branch = 'main';
  assert.equal(verifySquadVerdict(evidence).classification, 'APPROVED');
});

test('rejects a success status whose description is not a gate approval', () => {
  const evidence = fixture();
  evidence.status.description = 'looks fine to me';
  assert.equal(verifySquadVerdict(evidence).classification, 'INVALID');
});

test('rejects an approval description pinned to a different short SHA', () => {
  const evidence = fixture();
  evidence.status.description = `APPROVE @ ${movedHeadSha.slice(0, 12)} by bishop`;
  assert.equal(verifySquadVerdict(evidence).classification, 'INVALID');
});

test('a gate block is missing evidence, not a reviewer rejection', () => {
  // Ralph routes CHANGES_REQUESTED back to the author but treats MISSING as
  // "no squad evidence", which permits the administrator fallback. Conflating
  // the two would suppress that fallback for PRs nobody has reviewed yet.
  for (const detail of [
    `no verdict found for ${shortSha}`,
    'have 1/3, missing hicks+vasquez',
    'reviewer parker is the PR author',
    `have 0/3 (stale at bishop@${movedHeadSha.slice(0, 12)})`,
    'cross-repository PR needs human review',
  ]) {
    const evidence = fixture('REQUEST_CHANGES');
    evidence.status.description = `BLOCKED @ ${shortSha}: ${detail}`;
    const verdict = verifySquadVerdict(evidence);
    assert.equal(verdict.classification, 'MISSING', detail);
    assert.match(verdict.reason, /without a reviewer verdict/);
  }
});

test('author-authored lookalike comments cannot satisfy the verifier', () => {
  const evidence = fixture();
  const verdict = verifySquadVerdict({
    pull: evidence.pull,
    comments: [{
      user: { login: 'pr-author' },
      body: `APPROVE @ ${shortSha} by bishop+hicks+vasquez`,
    }],
  });
  assert.equal(verdict.classification, 'MISSING');
});

test('newest trusted-run evidence fails closed instead of reviving an older approval', () => {
  const older = fixture();
  older.status.id = 41;
  older.status.created_at = '2026-08-07T02:59:10Z';

  const newer = fixture('REQUEST_CHANGES');
  newer.status.id = 43;
  newer.status.target_url =
    'https://github.com/OlyForge3D/PrintFarmer/actions/runs/123457';
  newer.status.description = 'BLOCKED: something else entirely';
  newer.run.id = 123457;
  newer.run.html_url = newer.status.target_url;

  const verdict = selectSquadVerdict({
    pull: newer.pull,
    statuses: [older.status, newer.status],
    loadRun: (runId) => runId === newer.run.id ? newer.run : older.run,
  });
  assert.equal(verdict.classification, 'INVALID');
});

test('rerunning an older approval cannot supersede a newer rejection', () => {
  const rejection = fixture('REQUEST_CHANGES');
  rejection.status.id = 44;
  rejection.status.created_at = '2026-08-07T03:01:10Z';
  rejection.status.target_url =
    'https://github.com/OlyForge3D/PrintFarmer/actions/runs/123458';
  rejection.run.id = 123458;
  rejection.run.html_url = rejection.status.target_url;

  const replayedApproval = fixture();
  replayedApproval.status.id = 45;
  replayedApproval.status.created_at = '2026-08-07T03:02:10Z';
  replayedApproval.run.run_attempt = 2;

  const verdict = selectSquadVerdict({
    pull: replayedApproval.pull,
    statuses: [rejection.status, replayedApproval.status],
    loadRun: (runId) =>
      runId === replayedApproval.run.id ? replayedApproval.run : rejection.run,
  });
  assert.equal(verdict.classification, 'INVALID');
});

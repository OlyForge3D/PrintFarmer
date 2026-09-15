import { repository, releaseReviewStatus } from '../../release-policy.mjs';

export function sourceReviewFixture(source, branch, now = Date.now(), reviewedHead = source) {
  const at = minutes => new Date(now - minutes * 60_000).toISOString();
  const pull = {
    id: 60, number: 60, merged: true, state: 'closed', draft: false,
    merge_commit_sha: source, user: { login: 'author' },
    head: { sha: reviewedHead, repo: { full_name: repository } },
    base: { ref: branch, repo: { full_name: repository, default_branch: 'development' } },
  };
  const run = {
    id: 31, path: '.github/workflows/squad-review-verdict.yml',
    repository: { full_name: repository }, head_repository: { full_name: repository },
    head_sha: reviewedHead, head_branch: 'reviewed-feature', run_attempt: 1,
    event: 'pull_request_target', status: 'completed', conclusion: 'success',
    actor: { login: 'author' }, triggering_actor: { login: 'author' },
    display_title: 'Squad review record for PR #60',
    html_url: `https://github.com/${repository}/actions/runs/31`,
    run_started_at: at(30), updated_at: at(28),
  };
  const status = {
    id: 1, context: releaseReviewStatus, state: 'success',
    description: `REVIEWED (self-attested) @ ${reviewedHead.slice(0, 12)} by bishop+hicks+vasquez`,
    target_url: run.html_url, creator: { login: 'github-actions[bot]' },
    created_at: at(29), updated_at: at(29),
  };
  const values = new Map([
    [`commits/${source}/pulls?per_page=100`, [pull]],
    ['pulls/60', pull],
    [`git/commits/${source}`, { sha: source, tree: { sha: 'd'.repeat(40) } }],
    [`git/commits/${reviewedHead}`, { sha: reviewedHead, tree: { sha: 'd'.repeat(40) } }],
    [`commits/${reviewedHead}/status?per_page=100`,
      { sha: reviewedHead, total_count: 1, statuses: [status] }],
    ['actions/runs/31', run],
    [`contents/.github/CODEOWNERS?ref=${source}`, {
      encoding: 'base64', content: Buffer.from('* @native-reviewer').toString('base64'),
    }],
    ['pulls/60/reviews?per_page=100', [{
      id: 70, commit_id: reviewedHead, user: { login: 'native-reviewer' },
      state: 'APPROVED', submitted_at: at(31),
    }]],
    ['collaborators/native-reviewer/permission',
      { user: { login: 'native-reviewer' }, permission: 'write' }],
  ]);
  return { values, status, pull, run };
}

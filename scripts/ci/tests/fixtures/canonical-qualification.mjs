import { repository, releaseBuildChecks, releaseReviewStatus } from '../../release-policy.mjs';
import { confirmationBody, qualificationTitle, qualificationDescription,
  qualificationWorkflow, evidenceWorkflow } from '../../canonical-qualification.mjs';

export function canonicalAuthorizationFixture(sha, channel, mode) {
  const at = minutes => new Date(Date.now() - minutes * 60_000).toISOString();
  const branch = channel === 'stable' ? 'main' : 'development';
  const definitions = [
    { id: 1, path: '.github/workflows/ci.yml', state: 'active' },
    { id: 2, path: qualificationWorkflow, state: 'active' },
    { id: 3, path: evidenceWorkflow, state: 'active' },
  ];
  const runs = definitions.map((definition, index) => ({
    id: (index + 1) * 10, workflow_id: definition.id, path: definition.path,
    repository: { full_name: repository }, head_repository: { full_name: repository },
    head_branch: index === 0 ? branch : 'development', head_sha: sha, run_attempt: 1,
    event: index === 2 ? 'workflow_run' : 'workflow_dispatch',
    html_url: `https://github.com/${repository}/actions/runs/${(index + 1) * 10}`,
    status: 'completed', conclusion: 'success', actor: { login: 'jpapiez' }, triggering_actor: { login: 'jpapiez' },
    created_at: at(30 - index * 10), run_started_at: at(30 - index * 10), updated_at: at(25 - index * 10),
  }));
  runs[1].display_title = qualificationTitle(channel, '10', '50', mode === 'single-maintainer' ? '0' : '60', mode);
  runs[0].check_suite_id = 100;
  runs[2].display_title = 'Canonical evidence for 20';
  const names = [
    [...releaseBuildChecks, 'Select affected tests', 'CI summary',
      'Dependency license & provenance validation', '.NET provider tests (DbHeavy)'],
    ['Verify canonical qualification'], ['Record canonical evidence'],
  ];
  const reviewer = mode === 'single-maintainer' ? 'jpapiez' : 'native-reviewer';
  const stamp = at(22);
  const status = { id: 1, context: releaseReviewStatus, state: 'success',
    creator: { login: 'github-actions[bot]' }, target_url: runs[2].html_url, created_at: at(8),
    description: qualificationDescription({ approvalMode: mode, sourceCommit: sha }) };
  const values = new Map([
    ['', { full_name: repository, default_branch: 'development' }],
    [`commits/${sha}/statuses?per_page=100`, [status]],
    [`commits/${sha}/comments?per_page=100`, [{ id: 50, commit_id: sha, user: { login: reviewer, type: 'User' },
      body: confirmationBody(sha, '10', mode), created_at: stamp, updated_at: stamp }]],
    ['actions/workflows/ci.yml/runs?head_sha=' + sha + '&per_page=100',
      { total_count: 1, workflow_runs: [runs[0]] }],
    [`actions/workflows/qualify-canonical-release.yml/runs?created=${encodeURIComponent(`>=${runs[0].created_at}`)}&per_page=100`,
      { total_count: 1, workflow_runs: [runs[1]] }],
    ['collaborators/jpapiez/permission', { user: { login: 'jpapiez' }, permission: 'admin' }],
    ['collaborators/native-reviewer/permission', { user: { login: 'native-reviewer' }, permission: 'write' }],
    [`contents/.github/CODEOWNERS?ref=${sha}`, { encoding: 'base64',
      content: Buffer.from('* @native-reviewer').toString('base64') }],
    ['pulls/60', { number: 60, head: { sha, repo: { full_name: repository } },
      base: { ref: branch, repo: { full_name: repository } }, user: { login: 'jpapiez' }, draft: false }],
    ['pulls/60/reviews?per_page=100', [{ id: 70, commit_id: sha, user: { login: 'native-reviewer' },
      state: 'APPROVED', submitted_at: at(23) }]],
  ]);
  for (const [index, run] of runs.entries()) {
    values.set(`actions/runs/${run.id}`, run);
    values.set(`actions/workflows/${run.path.split('/').at(-1)}`, definitions[index]);
    values.set(`actions/runs/${run.id}/attempts/1/jobs?per_page=100`, {
      total_count: names[index].length, jobs: names[index].map((name, i) => ({
        id: run.id * 100 + i, run_id: run.id, run_attempt: 1, head_sha: sha,
        check_run_url: `https://api.github.com/repos/${repository}/check-runs/${i + 1}`,
        status: 'completed', conclusion: 'success', name,
      })),
    });
  }
  return { values, status };
}

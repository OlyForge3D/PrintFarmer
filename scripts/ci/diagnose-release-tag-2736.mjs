import { isDeepStrictEqual } from 'node:util';
import { githubClient, GithubReleaseRequestError, readReleaseProtection, verifyCanonicalSource } from './release-github.mjs';
import { readDispatchEvent, verifyOwnerRun } from './release-dispatch.mjs';
import { ReleasePolicyError, repository, requireKeys, requireOwnerReleaseMode, requireThat,
  verifyEnvironmentRestrictions, verifyReleaseProtectionRules } from './release-policy.mjs';

export const diagnostic = Object.freeze({
  workflow: '.github/workflows/diagnose-release-tag-2736.yml',
  ref: 'refs/tags/v0.2.3-insider.1',
  tagObject: '68b1513644bf62265b8797240f56520c1e6d5600',
  source: 'a9253ae4d8578934ee27903d39ae69e2dc1cf769',
  failedRun: '35046281532',
  appId: '4927270',
  installationId: '161288519',
  workflowId: 359999367,
  priorRun: 35159038922,
  priorSource: 'c517cea2a78e176b2cec436986290c8a767fba04',
});
const historyEndpoint = 'actions/workflows/diagnose-release-tag-2736.yml/runs?per_page=100';
let requestSpent = false;

function sameRun(entry, run) {
  return ['id', 'run_number', 'run_attempt', 'workflow_id', 'path', 'head_sha', 'head_branch',
    'event', 'status', 'conclusion'].every(key => entry?.[key] === run[key]) &&
    ['actor', 'triggering_actor'].every(key =>
      ['id', 'login', 'type'].every(field => entry?.[key]?.[field] === run[key][field])) &&
    ['repository', 'head_repository'].every(key =>
      ['id', 'full_name'].every(field => entry?.[key]?.[field] === run[key][field]));
}

async function verifyPriorAdmission(api, run) {
  const prior = await api(`actions/runs/${diagnostic.priorRun}`);
  requireThat(prior?.id === diagnostic.priorRun && prior.run_number === 1 && prior.run_attempt === 1 &&
    prior.workflow_id === diagnostic.workflowId && prior.path === diagnostic.workflow &&
    prior.head_sha === diagnostic.priorSource && prior.head_branch === 'development' &&
    prior.event === 'workflow_dispatch' && prior.status === 'completed' && prior.conclusion === 'failure' &&
    ['repository', 'head_repository'].every(key =>
      prior[key]?.full_name === repository && prior[key]?.id === run.repository.id) &&
    ['actor', 'triggering_actor'].every(key =>
      ['id', 'login', 'type'].every(field => prior[key]?.[field] === run.actor[field])),
  'Exact prior diagnostic run must remain an initial terminal admission failure');
  const evidence = await api(`actions/runs/${diagnostic.priorRun}/attempts/1/jobs?per_page=100`);
  const boundary = evidence?.jobs?.find(job => job?.id === 105005241763);
  const request = evidence?.jobs?.find(job => job?.id === 105005305468);
  requireThat(evidence?.total_count === 2 && Array.isArray(evidence.jobs) && evidence.jobs.length === 2 &&
    boundary?.name === 'Verify owner and first-run boundary without publisher credentials' &&
    boundary.conclusion === 'failure' &&
    request?.name === 'Attempt the single fixed tag request and stop' && request.conclusion === 'skipped' &&
    [boundary, request].every(job => job.run_id === diagnostic.priorRun && job.run_attempt === 1 &&
      job.head_sha === diagnostic.priorSource && job.status === 'completed') &&
    Array.isArray(request.steps) && request.steps.length === 0 &&
    Array.isArray(boundary.steps) && isDeepStrictEqual(boundary.steps.map(step =>
      [step?.number, step?.name, step?.status, step?.conclusion]), [
      [1, 'Set up job', 'completed', 'success'],
      [2, 'Run actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1', 'completed', 'success'],
      [3, 'Run actions/setup-node@820762786026740c76f36085b0efc47a31fe5020', 'completed', 'success'],
      [4, 'Read-only admission', 'completed', 'failure'],
      [7, 'Post Run actions/setup-node@820762786026740c76f36085b0efc47a31fe5020', 'completed', 'skipped'],
      [8, 'Post Run actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1', 'completed', 'success'],
      [9, 'Complete job', 'completed', 'success'],
    ]),
  'Exact prior jobs must prove read-only admission failed and the request was skipped');
  return prior;
}

async function verifyOneShot(env, api, event) {
  requireOwnerReleaseMode(env.RELEASE_APPROVAL_MODE);
  requireKeys(event.inputs ?? {}, [], [], 'diagnostic inputs');
  const run = await verifyOwnerRun(env, api, event, diagnostic.workflow);
  requireThat(run.workflow_id === diagnostic.workflowId && run.id !== diagnostic.priorRun &&
    run.run_number === 2 && env.GITHUB_RUN_NUMBER === '2',
  'Diagnostic permits only the authorized successor on the original workflow, including failure or cancellation');
  const prior = await verifyPriorAdmission(api, run);
  const history = await api(historyEndpoint);
  requireThat(history?.total_count === 2 && Array.isArray(history.workflow_runs) &&
    history.workflow_runs.length === 2 && [run, prior].every(expected =>
      history.workflow_runs.filter(entry => sameRun(entry, expected)).length === 1),
  'Diagnostic history must contain exactly the authorized successor and preserved prior run');
  return run;
}

async function verifyNoPublishers(api) {
  for (const status of ['queued', 'in_progress', 'waiting', 'pending', 'requested']) {
    const runs = await api(`actions/workflows/consolidated-release.yml/runs?status=${status}&per_page=100`);
    requireThat(runs?.total_count === 0 && Array.isArray(runs.workflow_runs) && runs.workflow_runs.length === 0,
      'An active canonical release or incomplete run listing blocks the diagnostic');
  }
}

async function verifyBinding(api) {
  const original = await api(`actions/runs/${diagnostic.failedRun}`);
  requireThat(String(original?.id) === diagnostic.failedRun && original.run_attempt === 1 &&
    original.path === '.github/workflows/consolidated-release.yml' &&
    original.head_sha === diagnostic.source && original.head_branch === 'development' &&
    original.repository?.full_name === repository && original.head_repository?.full_name === repository &&
    original.event === 'workflow_dispatch' && original.status === 'completed' && original.conclusion === 'failure',
  'Original failed run binding changed or is unavailable');
  await verifyCanonicalSource(api, 'development', diagnostic.source);
  const object = await api(`git/tags/${diagnostic.tagObject}`);
  requireThat(object?.sha === diagnostic.tagObject && object.tag === diagnostic.ref.slice('refs/tags/'.length) &&
    object.object?.type === 'commit' && object.object.sha === diagnostic.source,
  'Fixed annotated tag object, type, tag or source binding differs');
  let absent = false;
  try { await api(`git/ref/${diagnostic.ref.slice('refs/'.length)}`); }
  catch (error) {
    if (error instanceof GithubReleaseRequestError && error.status === 404) absent = true;
    else throw error;
  }
  requireThat(absent, 'Target tag already exists; diagnostic must stop without mutation');
}

export async function preflightDiagnostic(env, api, event) {
  await verifyOneShot(env, api, event);
  await verifyCanonicalSource(api, 'development', env.GITHUB_WORKFLOW_SHA);
  verifyEnvironmentRestrictions(await api('environments/release-insider'),
    await api('environments/release-insider/deployment-branch-policies'), 'insider');
  await verifyBinding(api);
  await verifyNoPublishers(api);
}

export async function preflightProtectedDiagnostic(env, api, event) {
  requireThat(env.RELEASE_PUBLISHER_APP_ID === diagnostic.appId,
    'Diagnostic publisher App must remain the existing approved App');
  await preflightDiagnostic(env, api, event);
}

async function protectionSnapshot(api, env) {
  const evidence = await readReleaseProtection(api, 'insider', diagnostic.appId);
  verifyReleaseProtectionRules(evidence, 'insider', diagnostic.appId, env.RELEASE_APPROVAL_MODE);
  const effective = await api('rulesets?per_page=100&includes_parents=true');
  requireThat(Array.isArray(effective) && effective.length < 100 &&
    effective.every(rule => Number.isSafeInteger(rule?.id) && rule.id > 0 &&
      ['active', 'evaluate', 'disabled'].includes(rule.enforcement) &&
      ['branch', 'tag', 'push'].includes(rule.target)),
  'Effective protection list is incomplete or malformed');
  // Extra active tag/push rules are not assumed irrelevant or silently bypassed.
  const tagRules = evidence.rulesets.filter(rule => rule.target === 'tag');
  requireThat(effective.filter(rule => rule.enforcement === 'active' && rule.target !== 'branch')
    .every(rule => rule.target === 'tag' && tagRules.some(tag => tag.id === rule.id && tag.name === rule.name)),
  'Additional active tag or push protection requires owner review');
  requireThat(tagRules.every(tag => effective.some(rule =>
    rule.id === tag.id && rule.name === tag.name && rule.enforcement === 'active' && rule.target === 'tag')),
  'Effective canonical tag protection changed');
  const { verifiedAt, ...snapshot } = evidence;
  return { ...snapshot, effective };
}

export async function executeDiagnostic(env, readApi, publisherApi, event) {
  requireThat(!requestSpent, 'The diagnostic request is already spent in this process');
  requireThat(env.RELEASE_PUBLICATION_ENVIRONMENT === 'release-insider' &&
    env.RELEASE_PUBLISHER_INSTALLATION_ID === diagnostic.installationId,
  'Diagnostic requires the existing insider environment and publisher installation');
  await preflightProtectedDiagnostic(env, readApi, event);
  const protection = await protectionSnapshot(publisherApi, env);
  await verifyBinding(publisherApi);
  requireThat(isDeepStrictEqual(await protectionSnapshot(publisherApi, env), protection),
    'Live protection changed during diagnostic preparation');
  await verifyOneShot(env, readApi, event);
  await verifyNoPublishers(readApi);
  requestSpent = true;
  await publisherApi('git/refs', 'POST', { ref: diagnostic.ref, sha: diagnostic.tagObject });
  const ref = await publisherApi(`git/ref/${diagnostic.ref.slice('refs/'.length)}`);
  requireThat(ref?.ref === diagnostic.ref && ref.object?.type === 'tag' && ref.object.sha === diagnostic.tagObject,
    'POST completed but exact ref verification failed; stop, do not retry, reservation remains incomplete');
  return 'Tag created only: refs/tags/v0.2.3-insider.1 -> 68b1513644bf62265b8797240f56520c1e6d5600. Reservation remains incomplete; no release recovery or publication.';
}

export function diagnosticFailure(error) {
  const detail = error instanceof GithubReleaseRequestError || error instanceof ReleasePolicyError
    ? error.message : 'Untrusted transport, parsing or runtime failure (details omitted)';
  return `${detail}\nStop; no retry. Tag creation may be uncertain after a request failure. Reservation remains incomplete.`;
}

async function main(env) {
  const operation = process.argv[2];
  requireThat(['preflight', 'protected-preflight', 'request'].includes(operation) && process.argv.length === 3,
    'Unknown diagnostic operation');
  const event = readDispatchEvent(env);
  const readApi = githubClient(env.GH_TOKEN);
  if (operation !== 'request') {
    await (operation === 'preflight' ? preflightDiagnostic : preflightProtectedDiagnostic)(env, readApi, event);
    return 'Read-only diagnostic boundary passed; no tag request made.';
  }
  return executeDiagnostic(env, readApi, githubClient(env.RELEASE_PUBLISHER_TOKEN), event);
}

if (process.argv[1]?.replaceAll('\\', '/').endsWith('/scripts/ci/diagnose-release-tag-2736.mjs')) {
  main(process.env).then(result => console.log(result))
    .catch(error => { console.error(diagnosticFailure(error)); process.exitCode = 1; });
}

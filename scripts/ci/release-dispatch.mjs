import { closeSync, constants, fstatSync, openSync, readFileSync, realpathSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { githubClient, verifyCanonicalSource } from './release-github.mjs';
import { hash, repository, requireKeys, requireString, requireThat } from './release-policy.mjs';
import { transactionFromEnvironment } from './release-transaction.mjs';

const workflowPath = '.github/workflows/consolidated-release.yml';
const ownerLogin = 'jpapiez';
const positivePattern = /^[1-9][0-9]*$/;

function identity(value) {
  requireThat(Number.isSafeInteger(value?.id) && value.id > 0 &&
    ['User', 'Bot'].includes(value.type) &&
    typeof value.login === 'string' && /^[a-zA-Z0-9][a-zA-Z0-9[\]-]{0,99}$/.test(value.login),
  'Dispatch actor identity is missing or malformed');
  return { id: value.id, login: value.login, type: value.type };
}

function sameIdentity(left, right) {
  return left.id === right.id && left.login === right.login && left.type === right.type;
}

export function readDispatchEvent(env = process.env) {
  requireThat(env.RUNNER_TEMP && env.GITHUB_EVENT_PATH,
    'Trusted runner event path is unavailable');
  const expected = join(realpathSync(env.RUNNER_TEMP), '_github_workflow', 'event.json');
  requireThat(resolve(env.GITHUB_EVENT_PATH) === expected &&
    realpathSync(env.GITHUB_EVENT_PATH) === expected,
  'Dispatch event must be the trusted runner event file');
  const descriptor = openSync(expected, constants.O_RDONLY | constants.O_NOFOLLOW);
  try {
    const file = fstatSync(descriptor);
    requireThat(file.isFile() && file.nlink === 1 && file.size > 0 && file.size <= 1024 * 1024,
      'Dispatch event is linked, empty, or oversized');
    try { return JSON.parse(readFileSync(descriptor, 'utf8')); }
    catch { throw new Error('Dispatch event is malformed'); }
  } finally {
    closeSync(descriptor);
  }
}

// This read-only assessment does not replace an environment approval or grant credentials.
export async function assessOwnerDispatch(env = process.env, api = githubClient(env.GH_TOKEN),
  event = readDispatchEvent(env)) {
  const transaction = transactionFromEnvironment(env);
  requireThat(transaction.approvalMode === env.RELEASE_APPROVAL_MODE,
    'Dispatch approval policy changed after transaction selection');
  const run = await api(`actions/runs/${transaction.runId}`);
  const definition = await api('actions/workflows/consolidated-release.yml');
  requireThat(Number.isSafeInteger(definition?.id) && definition.id > 0 &&
    definition.path === workflowPath && definition.state === 'active' &&
    String(run?.id) === transaction.runId && String(run.run_attempt) === env.GITHUB_RUN_ATTEMPT &&
    run.workflow_id === definition.id && run.path === workflowPath &&
    run.repository?.full_name === repository && run.head_repository?.full_name === repository &&
    Number.isSafeInteger(run.repository.id) && run.repository.id > 0 &&
    run.head_repository.id === run.repository.id &&
    run.head_branch === 'development' && run.head_sha === transaction.workflowCommit &&
    run.event === env.GITHUB_EVENT_NAME && ['workflow_dispatch', 'schedule'].includes(run.event) &&
    run.status === 'in_progress',
  'Dispatch run, attempt, repository, or workflow evidence does not match');
  const actor = identity(run.actor);
  const triggeringActor = identity(run.triggering_actor);
  requireThat(env.GITHUB_ACTOR === actor.login && env.GITHUB_ACTOR_ID === String(actor.id) &&
    env.GITHUB_TRIGGERING_ACTOR === triggeringActor.login,
  'Runner actor claims differ from GitHub run evidence');
  const scheduled = run.event === 'schedule';
  let operation = 'publish';
  let reservationTarget = '';
  if (!scheduled) {
    requireThat(event?.repository?.full_name === repository &&
      event.repository.id === run.repository.id &&
      ['development', 'refs/heads/development'].includes(event.ref) &&
      sameIdentity(identity(event.sender), actor),
    'Dispatch event repository, ref, or sender does not match GitHub run evidence');
    requireKeys(event.inputs, ['channel', 'operation'], ['source_sha', 'reservation_target'],
      'dispatch inputs');
    operation = event.inputs.operation;
    reservationTarget = event.inputs.reservation_target ?? '';
    requireThat(['publish', 'abandon'].includes(operation) &&
      ['stable', 'insider'].includes(event.inputs.channel) &&
      transaction.channel === (operation === 'abandon' ? 'insider' : event.inputs.channel) &&
      typeof reservationTarget === 'string' &&
      (operation === 'publish' ? reservationTarget === '' : /^[a-f0-9]{64}$/.test(reservationTarget)),
    'Dispatch channel, operation, or reservation target differs from the selected transaction');
    requireThat(event.inputs.source_sha === undefined || typeof event.inputs.source_sha === 'string',
      'Dispatch source input is malformed');
    const selectedSource = event.inputs.source_sha?.trim();
    requireThat(selectedSource ? selectedSource === transaction.sourceCommit :
      transaction.sourceCommit === transaction.observedBranchHead,
    'Dispatch source differs from the immutable source selection');
  } else {
    requireThat(transaction.channel === 'insider' &&
      transaction.sourceCommit === transaction.observedBranchHead &&
      (event?.inputs === undefined || Object.keys(event.inputs).length === 0),
    'Scheduled release cannot inherit manual dispatch inputs');
  }
  requireThat(env.RELEASE_OPERATION === operation &&
    (env.RELEASE_ABANDONMENT_TARGET ?? '') === reservationTarget,
  'Executing operation differs from the original dispatch');
  await verifyCanonicalSource(api, 'development', transaction.workflowCommit);
  await verifyCanonicalSource(api, transaction.sourceBranch, transaction.sourceCommit);
  let ownerDispatchEligible = false;
  if (!scheduled && operation === 'publish' && env.GITHUB_RUN_ATTEMPT === '1' &&
    transaction.approvalMode === 'single-maintainer' &&
    actor.login === ownerLogin && actor.type === 'User' && sameIdentity(actor, triggeringActor)) {
    const permission = await api(`collaborators/${ownerLogin}/permission`);
    requireThat(permission && typeof permission.permission === 'string' &&
      sameIdentity(identity(permission.user), actor),
    'Owner live repository permission evidence is missing or mismatched');
    ownerDispatchEligible = permission.permission === 'admin' && permission.role_name === 'admin';
  }
  const assessment = {
    kind: 'release-dispatch-assessment', schema: 1,
    repository, transactionSha256: hash(transaction),
    workflowCommit: transaction.workflowCommit, sourceCommit: transaction.sourceCommit,
    channel: transaction.channel, operation, reservationTarget,
    runId: transaction.runId, executionAttempt: env.GITHUB_RUN_ATTEMPT,
    event: run.event, approvalMode: transaction.approvalMode,
    ownerDispatchEligible,
  };
  validateDispatchAssessment(assessment);
  return assessment;
}

export function validateDispatchAssessment(value) {
  requireKeys(value, ['kind', 'schema', 'repository', 'transactionSha256', 'workflowCommit',
    'sourceCommit', 'channel', 'operation', 'reservationTarget', 'runId', 'executionAttempt',
    'event', 'approvalMode', 'ownerDispatchEligible'], [], 'dispatch assessment');
  requireThat(value.kind === 'release-dispatch-assessment' && value.schema === 1 &&
    value.repository === repository && ['stable', 'insider'].includes(value.channel) &&
    ['publish', 'abandon'].includes(value.operation) &&
    ['workflow_dispatch', 'schedule'].includes(value.event) &&
    ['single-maintainer', 'separation-of-duties'].includes(value.approvalMode) &&
    typeof value.ownerDispatchEligible === 'boolean',
  'Invalid dispatch assessment');
  requireString(value.transactionSha256, /^[a-f0-9]{64}$/, 'dispatch transaction hash');
  for (const field of ['workflowCommit', 'sourceCommit']) {
    requireString(value[field], /^[a-f0-9]{40}$/, `dispatch ${field}`);
  }
  for (const field of ['runId', 'executionAttempt']) {
    requireString(value[field], positivePattern, `dispatch ${field}`);
  }
  requireThat(value.operation === 'publish' ? value.reservationTarget === '' :
    value.channel === 'insider' && /^[a-f0-9]{64}$/.test(value.reservationTarget),
  'Invalid dispatch operation binding');
  requireThat(!value.ownerDispatchEligible || (value.executionAttempt === '1' &&
    value.event === 'workflow_dispatch' && value.operation === 'publish' &&
    value.approvalMode === 'single-maintainer'),
  'Dispatch authority cannot be inherited by another execution path');
  return value;
}

if (process.argv[1]?.replaceAll('\\', '/').endsWith('/scripts/ci/release-dispatch.mjs')) {
  assessOwnerDispatch().then(value => process.stdout.write(`${JSON.stringify(value)}\n`))
    .catch(error => { console.error(error.message); process.exitCode = 1; });
}

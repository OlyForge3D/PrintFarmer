import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { canonicalValidationChecks } from './canonical-qualification.mjs';
import { githubClient, verifyReleaseChecks } from './release-github.mjs';
import {
  releaseRequiredChecks, repository, requireKeys, requireString, requireThat, validateApprovalMode,
} from './release-policy.mjs';

export const qualificationPath = '.artifacts/release-transaction/qualification.json';
export const rehearsalReceiptPath = '.artifacts/release-transaction/rehearsal-receipt.json';
const shaPattern = /^[a-f0-9]{40}$/;
const positivePattern = /^[1-9][0-9]*$/;

function channelBranch(channel) {
  requireThat(['stable', 'insider'].includes(channel), 'Invalid release channel');
  return channel === 'stable' ? 'main' : 'development';
}

export function selectTransaction(env = process.env) {
  requireThat(env.GITHUB_REPOSITORY === repository &&
    env.GITHUB_EVENT_NAME === 'workflow_dispatch' &&
    env.GITHUB_RUN_ATTEMPT === '1', 'Untrusted release dispatch or rerun');
  const channel = env.RELEASE_CHANNEL;
  const branch = channelBranch(channel);
  const mode = env.RELEASE_MODE;
  requireThat(['release', 'rehearsal'].includes(mode), 'Invalid release mode');
  requireString(env.GITHUB_SHA, shaPattern, 'workflow commit');
  requireString(env.GITHUB_WORKFLOW_SHA, shaPattern, 'workflow definition commit');
  requireThat(env.GITHUB_REF === `refs/heads/${branch}` &&
    env.GITHUB_WORKFLOW_REF ===
      `${repository}/.github/workflows/consolidated-release.yml@refs/heads/${branch}` &&
    env.GITHUB_WORKFLOW_SHA === env.GITHUB_SHA,
  'Dispatch ref must be the selected canonical branch');
  const requested = env.RELEASE_SOURCE_SHA?.trim();
  if (requested) {
    requireString(requested, shaPattern, 'requested source SHA');
    requireThat(requested === env.GITHUB_SHA,
      'Explicit source SHA must be the selected canonical branch HEAD observed by this dispatch');
  }
  validateApprovalMode(env.RELEASE_APPROVAL_MODE);
  return {
    kind: 'release-transaction',
    schema: 1,
    repository,
    mode,
    channel,
    sourceBranch: branch,
    sourceCommit: requested || env.GITHUB_SHA,
    observedBranchHead: env.GITHUB_SHA,
    workflowIdentity: env.GITHUB_WORKFLOW_REF,
    workflowCommit: env.GITHUB_WORKFLOW_SHA,
    runId: env.GITHUB_RUN_ID,
    runAttempt: env.GITHUB_RUN_ATTEMPT,
    approvalMode: env.RELEASE_APPROVAL_MODE,
  };
}

export function validateTransaction(value) {
  requireKeys(value, [
    'kind', 'schema', 'repository', 'mode', 'channel', 'sourceBranch', 'sourceCommit',
    'observedBranchHead', 'workflowIdentity', 'workflowCommit', 'runId', 'runAttempt',
    'approvalMode',
  ], [], 'release transaction');
  requireThat(value.kind === 'release-transaction' && value.schema === 1 &&
    value.repository === repository && ['release', 'rehearsal'].includes(value.mode),
  'Invalid release transaction identity');
  const branch = channelBranch(value.channel);
  requireThat(value.sourceBranch === branch &&
    value.workflowIdentity ===
      `${repository}/.github/workflows/consolidated-release.yml@refs/heads/${branch}`,
  'Invalid release transaction branch binding');
  for (const field of ['sourceCommit', 'observedBranchHead', 'workflowCommit']) {
    requireString(value[field], shaPattern, `transaction ${field}`);
  }
  requireThat(value.sourceCommit === value.observedBranchHead &&
    value.workflowCommit === value.observedBranchHead,
  'Release transaction source was not pinned exactly once at dispatch');
  for (const field of ['runId', 'runAttempt']) {
    requireString(value[field], positivePattern, `transaction ${field}`);
  }
  validateApprovalMode(value.approvalMode);
  return value;
}

function qualificationReceipt(transaction, checkedAt) {
  validateTransaction(transaction);
  requireThat(typeof checkedAt === 'string' && new Date(checkedAt).toISOString() === checkedAt,
    'Invalid qualification timestamp');
  return {
    kind: 'release-qualification',
    schema: 1,
    transaction,
    checkedAt,
    checks: [...releaseRequiredChecks, ...canonicalValidationChecks],
  };
}

export function validateQualificationReceipt(value, transaction, requiredMode) {
  requireKeys(value, ['kind', 'schema', 'transaction', 'checkedAt', 'checks'], [],
    'release qualification receipt');
  requireThat(value.kind === 'release-qualification' && value.schema === 1,
    'Rehearsal evidence cannot authorize release');
  validateTransaction(value.transaction);
  validateTransaction(transaction);
  requireThat(JSON.stringify(value.transaction) === JSON.stringify(transaction),
    'Qualification receipt belongs to another release transaction');
  requireThat(value.transaction.mode === requiredMode, 'Qualification mode mismatch');
  requireThat(new Date(value.checkedAt).toISOString() === value.checkedAt,
    'Invalid qualification timestamp');
  requireThat(JSON.stringify(value.checks) ===
    JSON.stringify([...releaseRequiredChecks, ...canonicalValidationChecks]),
  'Incomplete qualification evidence');
  return value;
}

export async function qualifyTransaction(transaction, api = githubClient(process.env.GH_TOKEN),
  now = new Date().toISOString()) {
  validateTransaction(transaction);
  await verifyReleaseChecks(api, transaction.sourceCommit,
    [...releaseRequiredChecks, ...canonicalValidationChecks].map(context => ({ context })));
  const receipt = qualificationReceipt(transaction, now);
  mkdirSync('.artifacts/release-transaction', { recursive: true });
  writeFileSync(qualificationPath, `${JSON.stringify(receipt, undefined, 2)}\n`, { mode: 0o600 });
  return receipt;
}

export function readQualificationReceipt(transaction, requiredMode) {
  let value;
  try {
    value = JSON.parse(readFileSync(qualificationPath, 'utf8'));
  } catch {
    throw new Error('Qualification receipt unavailable or malformed');
  }
  return validateQualificationReceipt(value, transaction, requiredMode);
}

export function writeRehearsalReceipt(transaction, qualification) {
  validateQualificationReceipt(qualification, transaction, 'rehearsal');
  const receipt = {
    kind: 'release-rehearsal-only',
    schema: 2,
    passed: true,
    transaction,
    qualificationCheckedAt: qualification.checkedAt,
    publicationAuthorized: false,
  };
  mkdirSync('.artifacts/release-transaction', { recursive: true });
  writeFileSync(rehearsalReceiptPath, `${JSON.stringify(receipt, undefined, 2)}\n`, { mode: 0o600 });
  return receipt;
}

export function transactionFromEnvironment(env = process.env) {
  return validateTransaction(JSON.parse(env.RELEASE_TRANSACTION || '{}'));
}

async function main() {
  const operation = process.argv[2];
  requireThat(['select', 'qualify', 'rehearse'].includes(operation), 'Unknown release transaction operation');
  if (operation === 'select') {
    const transaction = selectTransaction();
    process.stdout.write(`${JSON.stringify(transaction)}\n`);
    return;
  }
  const transaction = transactionFromEnvironment();
  if (operation === 'qualify') {
    await qualifyTransaction(transaction);
    return;
  }
  writeRehearsalReceipt(transaction, readQualificationReceipt(transaction, 'rehearsal'));
}

if (process.argv[1]?.replaceAll('\\', '/').endsWith('/scripts/ci/release-transaction.mjs')) {
  main().catch(error => {
    console.error(error.message);
    process.exitCode = 1;
  });
}

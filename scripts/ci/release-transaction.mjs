import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { canonicalValidationChecks } from './canonical-qualification.mjs';
import {
  branchHead, githubClient, verifyCanonicalSource, verifyReleaseChecks,
} from './release-github.mjs';
import {
  releaseRequiredChecks, repository, requireKeys, requireString, requireThat, validateApprovalMode,
} from './release-policy.mjs';

export const qualificationPath = '.artifacts/release-transaction/qualification.json';
export const rehearsalReceiptPath = '.artifacts/release-transaction/rehearsal-receipt.json';
export const qualificationLifetimeMs = 30 * 60 * 1000;
export const qualificationJobNamespace = 'Automatic exact-source qualification';
const controlWorkflow = '.github/workflows/consolidated-release.yml';
const shaPattern = /^[a-f0-9]{40}$/;
const positivePattern = /^[1-9][0-9]*$/;
const requiredQualificationJobs = [
  ...releaseRequiredChecks.filter(name => name !== 'squad/pre-pr-verdict'),
  ...canonicalValidationChecks,
];

function channelBranch(channel) {
  requireThat(['stable', 'insider'].includes(channel), 'Invalid release channel');
  return channel === 'stable' ? 'main' : 'development';
}

function timestamp(value, description) {
  requireThat(typeof value === 'string' && new Date(value).toISOString() === value,
    `Invalid ${description}`);
  return Date.parse(value);
}

function completeList(value, field, description) {
  requireThat(Number.isSafeInteger(value?.total_count) && value.total_count >= 0 &&
    Array.isArray(value[field]) && value[field].length === value.total_count,
  `${description} malformed or truncated`);
  return value[field];
}

export async function selectTransaction(env = process.env, api = githubClient(env.GH_TOKEN)) {
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
    env.GITHUB_WORKFLOW_REF === `${repository}/${controlWorkflow}@refs/heads/${branch}` &&
    env.GITHUB_WORKFLOW_SHA === env.GITHUB_SHA,
  'Dispatch must use the trusted release-control workflow on development');
  const observedBranchHead = await branchHead(api, branch);
  const requested = env.RELEASE_SOURCE_SHA?.trim();
  let sourceCommit = observedBranchHead;
  if (requested) {
    requireString(requested, shaPattern, 'requested source SHA');
    if (requested !== observedBranchHead) {
      const comparison = await api(`compare/${requested}...${observedBranchHead}`);
      requireThat(comparison?.status === 'ahead' &&
        comparison.merge_base_commit?.sha === requested,
      'Pinned source is no longer trusted canonical branch history');
    }
    sourceCommit = requested;
  }
  validateApprovalMode(env.RELEASE_APPROVAL_MODE);
  return {
    kind: 'release-transaction',
    schema: 2,
    repository,
    mode,
    channel,
    sourceBranch: branch,
    sourceCommit,
    observedBranchHead,
    workflowIdentity: `${repository}/${controlWorkflow}@refs/heads/${branch}`,
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
  requireThat(value.kind === 'release-transaction' && value.schema === 2 &&
    value.repository === repository && ['release', 'rehearsal'].includes(value.mode),
  'Invalid release transaction identity');
  const branch = channelBranch(value.channel);
  requireThat(value.sourceBranch === branch &&
    value.workflowIdentity === `${repository}/${controlWorkflow}@refs/heads/${branch}`,
  'Invalid release transaction branch binding');
  for (const field of ['sourceCommit', 'observedBranchHead', 'workflowCommit']) {
    requireString(value[field], shaPattern, `transaction ${field}`);
  }
  for (const field of ['runId', 'runAttempt']) {
    requireString(value[field], positivePattern, `transaction ${field}`);
  }
  validateApprovalMode(value.approvalMode);
  return value;
}

async function requiredCheckPolicy(api, transaction) {
  const rules = await api(`rules/branches/${transaction.sourceBranch}?per_page=100`);
  requireThat(Array.isArray(rules) && rules.length > 0 && rules.length < 100,
    'Owner blocker: missing or truncated live branch policy');
  const policies = rules.filter(rule => rule?.type === 'required_status_checks');
  requireThat(policies.length > 0 && policies.every(rule =>
    rule.parameters?.strict_required_status_checks_policy === true &&
    Array.isArray(rule.parameters.required_status_checks)),
  'Owner blocker: canonical branch required checks are not strict');
  const required = policies.flatMap(rule => rule.parameters.required_status_checks);
  requireThat([...releaseRequiredChecks, ...canonicalValidationChecks].every(name =>
    required.some(rule => rule?.context === name)),
  'Owner blocker: live canonical branch policy is missing release qualification checks');
  return required;
}

function matchingJob(jobs, name, transaction) {
  const matches = jobs.filter(job =>
    (job.name === name || job.name === `${qualificationJobNamespace} / ${name}`) &&
    String(job.run_id) === transaction.runId &&
    String(job.run_attempt) === transaction.runAttempt &&
    job.status === 'completed' && job.conclusion === 'success');
  requireThat(matches.length === 1, `Required qualification job missing or ambiguous: ${name}`);
  return matches[0];
}

export async function verifyTransactionQualification(
  transaction,
  api = githubClient(process.env.GH_TOKEN),
  now = Date.now(),
) {
  validateTransaction(transaction);
  const currentHead = await verifyCanonicalSource(api, transaction.sourceBranch, transaction.sourceCommit);
  const run = await api(`actions/runs/${transaction.runId}`);
  const definition = await api('actions/workflows/consolidated-release.yml');
  requireThat(String(run.id) === transaction.runId &&
    String(run.run_attempt) === transaction.runAttempt &&
    run.path === controlWorkflow && run.workflow_id === definition.id &&
    definition.path === controlWorkflow && definition.state === 'active' &&
    run.repository?.full_name === repository && run.head_repository?.full_name === repository &&
    run.head_branch === transaction.sourceBranch && run.head_sha === transaction.workflowCommit &&
    run.event === 'workflow_dispatch' && run.html_url ===
      `https://github.com/${repository}/actions/runs/${transaction.runId}` &&
    ['in_progress', 'completed'].includes(run.status) &&
    (run.status !== 'completed' || run.conclusion === 'success'),
  'Untrusted, moved, failed, or rerun release transaction');
  const jobs = completeList(
    await api(`actions/runs/${transaction.runId}/attempts/${transaction.runAttempt}/jobs?per_page=100`),
    'jobs',
    'Release transaction jobs',
  );
  const selectedJobs = requiredQualificationJobs.map(name => matchingJob(jobs, name, transaction));
  const checks = completeList(
    await api(`commits/${transaction.workflowCommit}/check-runs?per_page=100`),
    'check_runs',
    'Release transaction checks',
  );
  requireThat(Number.isSafeInteger(run.check_suite_id) && run.check_suite_id > 0,
    'Release transaction lacks a check suite');
  const jobLinks = selectedJobs.map(job => {
    const id = Number(new URL(job.check_run_url).pathname.split('/').at(-1));
    const matching = checks.filter(check => check.id === id &&
      check.name === job.name && check.head_sha === transaction.workflowCommit &&
      check.check_suite?.id === run.check_suite_id && check.app?.slug === 'github-actions' &&
      check.status === 'completed' && check.conclusion === 'success' &&
      check.url === job.check_run_url);
    requireThat(matching.length === 1, `Qualification check-suite binding failed: ${job.name}`);
    return { name: job.name, url: job.html_url ?? run.html_url };
  });
  const required = await requiredCheckPolicy(api, transaction);
  const sourceEvidence = await verifyReleaseChecks(api, transaction.sourceCommit, required);
  const checkedAt = new Date(now).toISOString();
  return {
    kind: 'release-qualification',
    schema: 2,
    transaction,
    checkedAt,
    expiresAt: new Date(now + qualificationLifetimeMs).toISOString(),
    qualifiedBranchHead: currentHead,
    run: {
      id: transaction.runId,
      attempt: transaction.runAttempt,
      checkSuiteId: String(run.check_suite_id),
      url: run.html_url,
    },
    jobs: jobLinks,
    sourceEvidence,
  };
}

export function validateQualificationReceipt(value, transaction, requiredMode, now = Date.now()) {
  requireKeys(value, [
    'kind', 'schema', 'transaction', 'checkedAt', 'expiresAt', 'qualifiedBranchHead',
    'run', 'jobs', 'sourceEvidence',
  ], [], 'release qualification receipt');
  requireThat(value.kind === 'release-qualification' && value.schema === 2,
    'Rehearsal evidence cannot authorize release');
  validateTransaction(value.transaction);
  validateTransaction(transaction);
  requireThat(JSON.stringify(value.transaction) === JSON.stringify(transaction),
    'Qualification receipt belongs to another release transaction');
  requireThat(value.transaction.mode === requiredMode, 'Qualification mode mismatch');
  const checkedAt = timestamp(value.checkedAt, 'qualification timestamp');
  const expiresAt = timestamp(value.expiresAt, 'qualification expiry');
  requireThat(expiresAt - checkedAt === qualificationLifetimeMs &&
    checkedAt <= now && now <= expiresAt,
  'Qualification receipt is expired, future-dated, or has an invalid lifetime');
  requireString(value.qualifiedBranchHead, shaPattern, 'qualified branch HEAD');
  requireThat(value.run?.id === transaction.runId &&
    value.run.attempt === transaction.runAttempt &&
    value.run.checkSuiteId && value.run.url ===
      `https://github.com/${repository}/actions/runs/${transaction.runId}`,
  'Qualification run binding mismatch');
  requireThat(Array.isArray(value.jobs) && value.jobs.length === requiredQualificationJobs.length &&
    value.jobs.every(job => typeof job?.name === 'string' && typeof job?.url === 'string'),
  'Incomplete qualification job evidence');
  requireThat(value.sourceEvidence?.sourceCommit === transaction.sourceCommit &&
    Array.isArray(value.sourceEvidence.checks),
  'Qualification source evidence mismatch');
  return value;
}

export async function qualifyTransaction(transaction, api = githubClient(process.env.GH_TOKEN),
  now = Date.now()) {
  const receipt = await verifyTransactionQualification(transaction, api, now);
  mkdirSync('.artifacts/release-transaction', { recursive: true });
  writeFileSync(qualificationPath, `${JSON.stringify(receipt, undefined, 2)}\n`, { mode: 0o600 });
  return receipt;
}

export function readQualificationReceipt(transaction, requiredMode, now = Date.now()) {
  let value;
  try {
    value = JSON.parse(readFileSync(qualificationPath, 'utf8'));
  } catch {
    throw new Error('Qualification receipt unavailable or malformed');
  }
  return validateQualificationReceipt(value, transaction, requiredMode, now);
}

export function writeRehearsalReceipt(transaction, qualification, now = Date.now()) {
  validateQualificationReceipt(qualification, transaction, 'rehearsal', now);
  const receipt = {
    kind: 'release-rehearsal-only',
    schema: 3,
    passed: true,
    transaction,
    qualificationCheckedAt: qualification.checkedAt,
    qualificationExpiresAt: qualification.expiresAt,
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
  requireThat(['select', 'validate', 'qualify', 'rehearse'].includes(operation), 'Unknown release transaction operation');
  if (operation === 'select') {
    const transaction = await selectTransaction();
    process.stdout.write(`${JSON.stringify(transaction)}\n`);
    return;
  }
  const transaction = transactionFromEnvironment();
  if (operation === 'validate') {
    process.stdout.write(`${JSON.stringify(transaction)}\n`);
    return;
  }
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

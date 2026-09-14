import { lstatSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, relative, resolve, sep } from 'node:path';
import { canonicalValidationChecks } from './canonical-qualification.mjs';
import {
  branchHead, githubClient, parseGithubTimestamp, verifyCanonicalSource, verifyReleaseChecks,
} from './release-github.mjs';
import {
  releaseRequiredChecks, repository, requireKeys, requireString, requireThat, validateApprovalMode,
} from './release-policy.mjs';

export const qualificationPath = '.artifacts/release-transaction/qualification.json';
export const rehearsalReceiptPath = '.artifacts/release-transaction/rehearsal-receipt.json';
export const transactionPath = '.artifacts/release-transaction/transaction.json';
export const qualificationLifetimeMs = 30 * 60 * 1000;
export const evidenceLifetimeMs = 24 * 60 * 60 * 1000;
export const qualificationJobNamespace = 'Automatic exact-source qualification';
const controlWorkflow = '.github/workflows/consolidated-release.yml';
const shaPattern = /^[a-f0-9]{40}$/;
const positivePattern = /^[1-9][0-9]*$/;
const maximumReceiptBytes = 1024 * 1024;
const requiredQualificationJobs = [
  ...releaseRequiredChecks.filter(name => name !== 'squad/pre-pr-verdict'),
  ...canonicalValidationChecks,
];

function channelBranch(channel) {
  requireThat(['stable', 'insider'].includes(channel), 'Invalid release channel');
  return channel === 'stable' ? 'main' : 'development';
}

function timestamp(value, description) {
  return parseGithubTimestamp(value, description);
}

function freshEvidenceTimestamp(value, description, now, notBefore = now - evidenceLifetimeMs) {
  const parsed = timestamp(value, description);
  requireThat(parsed >= notBefore && parsed <= now, `${description} is stale, future-dated, or post-collection`);
  return parsed;
}

function completeList(value, field, description) {
  requireThat(Number.isSafeInteger(value?.total_count) && value.total_count >= 0 &&
    Array.isArray(value[field]) && value[field].length === value.total_count,
  `${description} malformed or truncated`);
  return value[field];
}

function artifactFile(path) {
  const root = resolve('.artifacts/release-transaction');
  const target = resolve(path);
  requireThat(target.startsWith(`${root}${sep}`) &&
    !relative(root, target).split(/[\\/]/).includes('..'),
  'Release artifact path escaped its trusted directory');
  return target;
}

function writeValidatedJson(path, value, validate) {
  validate(value);
  const content = `${JSON.stringify(value, undefined, 2)}\n`;
  requireThat(Buffer.byteLength(content, 'utf8') <= maximumReceiptBytes,
    'Release artifact exceeds the maximum receipt size');
  const target = artifactFile(path);
  mkdirSync(dirname(target), { recursive: true });
  // codeql[js/http-to-file-access]: The network-derived receipt is schema-validated,
  // size-bounded, and written only beneath the constant release artifact directory.
  writeFileSync(target, content, { mode: 0o600 });
}

function readBoundedJson(path, description) {
  const target = artifactFile(path);
  const metadata = lstatSync(target);
  requireThat(metadata.isFile() && !metadata.isSymbolicLink() &&
    metadata.size > 0 && metadata.size <= maximumReceiptBytes,
  `${description} is missing, linked, empty, or oversized`);
  return JSON.parse(readFileSync(target, 'utf8'));
}

export async function selectTransaction(env = process.env, api = githubClient(env.GH_TOKEN)) {
  requireThat(env.GITHUB_REPOSITORY === repository &&
    env.GITHUB_EVENT_NAME === 'workflow_dispatch', 'Untrusted release dispatch');
  requireString(env.GITHUB_RUN_ATTEMPT, positivePattern, 'run attempt');
  const channel = env.RELEASE_CHANNEL;
  const branch = channelBranch(channel);
  const mode = env.RELEASE_MODE;
  requireThat(['release', 'rehearsal'].includes(mode), 'Invalid release mode');
  requireString(env.GITHUB_SHA, shaPattern, 'workflow commit');
  requireString(env.GITHUB_WORKFLOW_SHA, shaPattern, 'workflow definition commit');
  requireThat(env.GITHUB_REF === 'refs/heads/development' &&
    env.GITHUB_WORKFLOW_REF === `${repository}/${controlWorkflow}@refs/heads/development` &&
    env.GITHUB_WORKFLOW_SHA === env.GITHUB_SHA,
  'Dispatch must use the immutable release-control workflow on development');
  if (env.GITHUB_RUN_ATTEMPT !== '1') {
    let recovered;
    try {
      recovered = validateTransaction(readBoundedJson(transactionPath, 'Recovery transaction'));
    } catch {
      throw new Error('Rerun recovery transaction is missing or malformed');
    }
    requireThat(recovered.runId === env.GITHUB_RUN_ID && recovered.runAttempt === '1' &&
      recovered.channel === channel && recovered.mode === mode &&
      recovered.workflowCommit === env.GITHUB_WORKFLOW_SHA &&
      recovered.approvalMode === env.RELEASE_APPROVAL_MODE,
    'Rerun recovery transaction does not match the immutable original dispatch');
    await verifyCanonicalSource(api, recovered.sourceBranch, recovered.sourceCommit);
    return recovered;
  }
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
  const transaction = {
    kind: 'release-transaction',
    schema: 2,
    repository,
    mode,
    channel,
    sourceBranch: branch,
    sourceCommit,
    observedBranchHead,
    workflowIdentity: `${repository}/${controlWorkflow}@refs/heads/development`,
    workflowCommit: env.GITHUB_WORKFLOW_SHA,
    runId: env.GITHUB_RUN_ID,
    runAttempt: env.GITHUB_RUN_ATTEMPT,
    approvalMode: env.RELEASE_APPROVAL_MODE,
  };
  return transaction;
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
    value.workflowIdentity === `${repository}/${controlWorkflow}@refs/heads/development` &&
    value.runAttempt === '1',
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
  requireThat(releaseRequiredChecks.every(name =>
    required.some(rule => rule?.context === name)),
  'Owner blocker: live canonical branch policy is missing genuine source-commit checks');
  return required.filter(rule => releaseRequiredChecks.includes(rule.context));
}

function matchingJob(jobs, name, transaction, executionAttempt) {
  const matches = jobs.filter(job =>
    (job.name === name || job.name === `${qualificationJobNamespace} / ${name}`) &&
    String(job.run_id) === transaction.runId &&
    String(job.run_attempt) === executionAttempt &&
    job.status === 'completed' && job.conclusion === 'success');
  requireThat(matches.length === 1, `Required qualification job missing or ambiguous: ${name}`);
  return matches[0];
}

export async function verifyTransactionQualification(
  transaction,
  api = githubClient(process.env.GH_TOKEN),
  now = Date.now(),
  executionAttempt = process.env.GITHUB_RUN_ATTEMPT || transaction.runAttempt,
) {
  validateTransaction(transaction);
  requireString(executionAttempt, positivePattern, 'qualification execution attempt');
  const currentHead = await verifyCanonicalSource(api, transaction.sourceBranch, transaction.sourceCommit);
  const run = await api(`actions/runs/${transaction.runId}`);
  const definition = await api('actions/workflows/consolidated-release.yml');
  requireThat(String(run.id) === transaction.runId &&
    String(run.run_attempt) === executionAttempt &&
    run.path === controlWorkflow && run.workflow_id === definition.id &&
    definition.path === controlWorkflow && definition.state === 'active' &&
    run.repository?.full_name === repository && run.head_repository?.full_name === repository &&
    run.head_branch === 'development' && run.head_sha === transaction.workflowCommit &&
    run.event === 'workflow_dispatch' && run.html_url ===
      `https://github.com/${repository}/actions/runs/${transaction.runId}` &&
    ['in_progress', 'completed'].includes(run.status) &&
    (run.status !== 'completed' || run.conclusion === 'success'),
  'Untrusted, moved, failed, or rerun release transaction');
  const runStartedAt = freshEvidenceTimestamp(run.run_started_at, 'qualification run start', now);
  const runCompletedAt = timestamp(run.updated_at, 'qualification run update');
  requireThat(runCompletedAt >= runStartedAt && runCompletedAt <= now,
    'Qualification run timestamps are reversed or post-collection');
  const jobs = completeList(
    await api(`actions/runs/${transaction.runId}/attempts/${executionAttempt}/jobs?per_page=100`),
    'jobs',
    'Release transaction jobs',
  );
  const selectedJobs = requiredQualificationJobs.map(name => matchingJob(jobs, name, transaction, executionAttempt));
  for (const job of selectedJobs) {
    const started = freshEvidenceTimestamp(job.started_at, `${job.name} start`, now, runStartedAt);
    const completed = timestamp(job.completed_at, `${job.name} completion`);
    requireThat(completed >= started && completed <= now,
      `Qualification job timestamps are reversed or post-collection: ${job.name}`);
  }
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
      check.url === job.check_run_url &&
      freshEvidenceTimestamp(check.completed_at, `${job.name} check completion`, now, runStartedAt) <= now);
    requireThat(matching.length === 1, `Qualification check-suite binding failed: ${job.name}`);
    return {
      name: job.name,
      url: job.html_url ?? run.html_url,
      startedAt: job.started_at,
      completedAt: job.completed_at,
      checkCompletedAt: matching[0].completed_at,
    };
  });
  const required = await requiredCheckPolicy(api, transaction);
  const sourceEvidence = await verifyReleaseChecks(api, transaction.sourceCommit, required, now, evidenceLifetimeMs);
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
      attempt: executionAttempt,
      checkSuiteId: String(run.check_suite_id),
      url: run.html_url,
      startedAt: run.run_started_at,
      completedAt: run.updated_at,
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
    typeof value.run.attempt === 'string' && positivePattern.test(value.run.attempt) &&
    value.run.checkSuiteId && value.run.url ===
      `https://github.com/${repository}/actions/runs/${transaction.runId}`,
  'Qualification run binding mismatch');
  requireThat(timestamp(value.run.startedAt, 'qualification receipt run start') <=
    timestamp(value.run.completedAt, 'qualification receipt run completion') &&
    timestamp(value.run.completedAt, 'qualification receipt run completion') <= checkedAt,
  'Qualification receipt run timestamps are reversed or post-collection');
  requireThat(Array.isArray(value.jobs) && value.jobs.length === requiredQualificationJobs.length &&
    value.jobs.every(job => typeof job?.name === 'string' && typeof job?.url === 'string' &&
      timestamp(job.startedAt, `${job.name} receipt start`) <=
        timestamp(job.completedAt, `${job.name} receipt completion`) &&
      timestamp(job.checkCompletedAt, `${job.name} receipt check completion`) <= checkedAt),
  'Incomplete qualification job evidence');
  requireThat(value.sourceEvidence?.sourceCommit === transaction.sourceCommit &&
    timestamp(value.sourceEvidence.collectedAt, 'source evidence collection') === checkedAt &&
    Array.isArray(value.sourceEvidence.checks) &&
    value.sourceEvidence.checks.length === releaseRequiredChecks.length &&
    value.sourceEvidence.checks.every(item =>
      releaseRequiredChecks.includes(item?.context) &&
      (!item.completedAt || timestamp(item.completedAt, `${item.context} completion`) <= checkedAt) &&
      (!item.createdAt || timestamp(item.createdAt, `${item.context} creation`) <= checkedAt) &&
      (!item.updatedAt || timestamp(item.updatedAt, `${item.context} update`) <= checkedAt)),
  'Qualification source evidence mismatch');
  return value;
}

export async function qualifyTransaction(transaction, api = githubClient(process.env.GH_TOKEN),
  now = Date.now()) {
  const receipt = await verifyTransactionQualification(transaction, api, now);
  writeValidatedJson(qualificationPath, receipt,
    value => validateQualificationReceipt(value, transaction, transaction.mode, now));
  return receipt;
}

export function readQualificationReceipt(transaction, requiredMode, now = Date.now()) {
  let value;
  try {
    value = readBoundedJson(qualificationPath, 'Qualification receipt');
  } catch {
    throw new Error('Qualification receipt unavailable or malformed');
  }
  return validateQualificationReceipt(value, transaction, requiredMode, now);
}

export function writeDiagnosticReceipt(transaction, qualification, now = Date.now()) {
  validateQualificationReceipt(qualification, transaction, transaction.mode, now);
  const receipt = {
    kind: 'release-rehearsal-only',
    schema: 3,
    passed: true,
    transaction,
    qualificationCheckedAt: qualification.checkedAt,
    qualificationExpiresAt: qualification.expiresAt,
    publicationAuthorized: false,
  };
  writeValidatedJson(rehearsalReceiptPath, receipt, value => {
    requireKeys(value, [
      'kind', 'schema', 'passed', 'transaction', 'qualificationCheckedAt',
      'qualificationExpiresAt', 'publicationAuthorized',
    ], [], 'diagnostic receipt');
    requireThat(value.kind === 'release-rehearsal-only' && value.schema === 3 &&
      value.passed === true && value.publicationAuthorized === false,
    'Invalid non-authorizing diagnostic receipt');
    validateTransaction(value.transaction);
    requireThat(JSON.stringify(value.transaction) === JSON.stringify(transaction) &&
      value.qualificationCheckedAt === qualification.checkedAt &&
      value.qualificationExpiresAt === qualification.expiresAt,
    'Diagnostic receipt evidence mismatch');
  });
  return receipt;
}

export const writeRehearsalReceipt = writeDiagnosticReceipt;

export function transactionFromEnvironment(env = process.env) {
  return validateTransaction(JSON.parse(env.RELEASE_TRANSACTION || '{}'));
}

async function main() {
  const operation = process.argv[2];
  requireThat(['select', 'validate', 'qualify', 'diagnose', 'rehearse'].includes(operation),
    'Unknown release transaction operation');
  if (operation === 'select') {
    const transaction = await selectTransaction();
    writeValidatedJson(transactionPath, transaction, validateTransaction);
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
  const requiredMode = operation === 'rehearse' ? 'rehearsal' : transaction.mode;
  writeDiagnosticReceipt(transaction, readQualificationReceipt(transaction, requiredMode));
}

if (process.argv[1]?.replaceAll('\\', '/').endsWith('/scripts/ci/release-transaction.mjs')) {
  main().catch(error => {
    console.error(error.message);
    process.exitCode = 1;
  });
}

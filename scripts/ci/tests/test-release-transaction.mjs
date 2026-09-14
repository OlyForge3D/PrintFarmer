import assert from 'node:assert/strict';
import { mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import test from 'node:test';
import { load } from 'js-yaml';
import { randomUUID } from 'node:crypto';
import { resolve } from 'node:path';
import {
  qualificationJobNamespace, qualificationLifetimeMs, selectTransaction,
  transactionPath, validateQualificationReceipt, validateTransaction, verifyTransactionQualification,
  writeRehearsalReceipt,
} from '../release-transaction.mjs';
import { admit } from '../release-policy.mjs';
import { validateTransactionOperation } from '../release-control.mjs';

const workflowSha = 'a'.repeat(40);
const sourceSha = 'b'.repeat(40);
const movedHead = 'c'.repeat(40);
const now = Date.parse('2026-09-13T20:00:00.000Z');
const required = [
  'CI tooling tests', '.NET build', 'Frontend build & tests', 'squad/pre-pr-verdict',
  'path-casing', 'Build (iOS)', 'Contract drift gate',
];
const jobNames = required.filter(name => name !== 'squad/pre-pr-verdict');
const base = {
  GITHUB_REPOSITORY: 'OlyForge3D/PrintFarmer',
  GITHUB_EVENT_NAME: 'workflow_dispatch',
  GITHUB_REF: 'refs/heads/development',
  GITHUB_SHA: workflowSha,
  GITHUB_WORKFLOW_SHA: workflowSha,
  GITHUB_WORKFLOW_REF:
    'OlyForge3D/PrintFarmer/.github/workflows/consolidated-release.yml@refs/heads/development',
  GITHUB_RUN_ID: '42',
  GITHUB_RUN_ATTEMPT: '1',
  RELEASE_CHANNEL: 'insider',
  RELEASE_MODE: 'release',
  RELEASE_APPROVAL_MODE: 'single-maintainer',
  GH_TOKEN: 'test-only',
};

function apiFixture(channel = 'insider', head = sourceSha) {
  const branch = channel === 'stable' ? 'main' : 'development';
  const runUrl = 'https://github.com/OlyForge3D/PrintFarmer/actions/runs/42';
  const jobs = jobNames.map((name, index) => ({
    id: 100 + index,
    name: `${qualificationJobNamespace} / ${name}`,
    run_id: 42,
    run_attempt: 1,
    head_sha: workflowSha,
    status: 'completed',
    conclusion: 'success',
    started_at: '2026-09-13T19:40:00.000Z',
    completed_at: '2026-09-13T19:50:00.000Z',
    check_run_url: `https://api.github.com/repos/OlyForge3D/PrintFarmer/check-runs/${100 + index}`,
    html_url: `${runUrl}/job/${100 + index}`,
  }));
  const transactionChecks = jobs.map(job => ({
    id: job.id,
    name: job.name,
    head_sha: workflowSha,
    status: 'completed',
    conclusion: 'success',
    completed_at: '2026-09-13T19:50:00.000Z',
    app: { id: 15368, slug: 'github-actions' },
    check_suite: { id: 77 },
    url: job.check_run_url,
  }));
  const sourceChecks = required.filter(name => name !== 'squad/pre-pr-verdict').map((name, index) => ({
    id: 200 + index,
    name,
    head_sha: sourceSha,
    status: 'completed',
    conclusion: 'success',
    completed_at: '2026-09-13T19:30:00.000Z',
    app: { id: 15368, slug: 'github-actions' },
    check_suite: { id: 66 },
  }));
  const values = new Map([
    [`git/ref/heads/${branch}`, { object: { sha: head } }],
    [`compare/${sourceSha}...${head}`, { status: 'ahead', merge_base_commit: { sha: sourceSha } }],
    ['actions/workflows/consolidated-release.yml',
      { id: 9, path: '.github/workflows/consolidated-release.yml', state: 'active' }],
    ['actions/runs/42', {
      id: 42,
      run_attempt: 1,
      path: '.github/workflows/consolidated-release.yml',
      workflow_id: 9,
      repository: { full_name: 'OlyForge3D/PrintFarmer' },
      head_repository: { full_name: 'OlyForge3D/PrintFarmer' },
      head_branch: 'development',
      head_sha: workflowSha,
      event: 'workflow_dispatch',
      html_url: runUrl,
      status: 'in_progress',
      conclusion: null,
      check_suite_id: 77,
      run_started_at: '2026-09-13T19:35:00.000Z',
      updated_at: '2026-09-13T19:55:00.000Z',
    }],
    ['actions/runs/42/attempts/1/jobs?per_page=100',
      { total_count: jobs.length, jobs }],
    [`commits/${workflowSha}/check-runs?per_page=100`,
      { total_count: transactionChecks.length, check_runs: transactionChecks }],
    [`rules/branches/${branch}?per_page=100`, [{
      type: 'required_status_checks',
      parameters: {
        strict_required_status_checks_policy: true,
        required_status_checks: required.map(context => ({ context })),
      },
    }]],
    [`commits/${sourceSha}/check-runs?per_page=100`,
      { total_count: sourceChecks.length, check_runs: sourceChecks }],
    [`commits/${sourceSha}/status?per_page=100`, {
      sha: sourceSha,
      total_count: 1,
      statuses: [{
        id: 300,
        context: 'squad/pre-pr-verdict',
        state: 'success',
        description: `REVIEWED (self-attested) @ ${sourceSha.slice(0, 12)} by bishop`,
        target_url: 'https://github.com/OlyForge3D/PrintFarmer/actions/runs/31',
        created_at: '2026-09-13T19:31:00.000Z',
        updated_at: '2026-09-13T19:31:00.000Z',
      }],
    }],
  ]);
  const api = async endpoint => {
    if (!values.has(endpoint)) throw new Error(`Unexpected endpoint: ${endpoint}`);
    return structuredClone(values.get(endpoint));
  };
  return { api, values, jobs, transactionChecks, sourceChecks, branch };
}

async function transaction(channel = 'insider', requested = '') {
  const f = apiFixture(channel);
  const branch = channel === 'stable' ? 'main' : 'development';
  return {
    value: await selectTransaction({
      ...base,
      RELEASE_CHANNEL: channel,
      RELEASE_SOURCE_SHA: requested,
      GITHUB_REF: 'refs/heads/development',
      GITHUB_WORKFLOW_REF:
        'OlyForge3D/PrintFarmer/.github/workflows/consolidated-release.yml@refs/heads/development',
    }, f.api),
    fixture: f,
  };
}

test('stable and insider pin branch HEAD once while workflow and source commits remain distinct', async () => {
  for (const channel of ['stable', 'insider']) {
    for (const requested of ['', sourceSha]) {
      const { value } = await transaction(channel, requested);
      assert.equal(value.sourceCommit, sourceSha);
      assert.equal(value.observedBranchHead, sourceSha);
      assert.equal(value.workflowCommit, workflowSha);
      assert.equal(value.sourceBranch, channel === 'stable' ? 'main' : 'development');
    }
  }
});

test('explicit source may be a trusted ancestor but not an unrelated commit', async () => {
  const accepted = apiFixture('insider', movedHead);
  const value = await selectTransaction({ ...base, RELEASE_SOURCE_SHA: sourceSha }, accepted.api);
  assert.equal(value.sourceCommit, sourceSha);
  assert.equal(value.observedBranchHead, movedHead);
  accepted.values.set(`compare/${sourceSha}...${movedHead}`, { status: 'diverged', merge_base_commit: { sha: workflowSha } });
  await assert.rejects(selectTransaction({ ...base, RELEASE_SOURCE_SHA: sourceSha }, accepted.api),
    /trusted canonical branch history/);
});

test('selection rejects foreign refs, malformed sources, and workflow substitution', async () => {
  for (const overrides of [
    { GITHUB_REF: 'refs/heads/main' },
    { RELEASE_SOURCE_SHA: 'bad' },
    { RELEASE_MODE: 'dry-run' },
    { GITHUB_WORKFLOW_SHA: sourceSha },
  ]) {
    await assert.rejects(selectTransaction({ ...base, ...overrides }, apiFixture().api));
  }
});

test('same-run rerun recovers immutable attempt-one transaction and revalidates ancestry', async t => {
  const first = await transaction('stable', sourceSha);
  const cwd = process.cwd();
  const scratch = resolve('.artifacts', `transaction-recovery-${randomUUID()}`);
  mkdirSync(resolve(scratch, '.artifacts/release-transaction'), { recursive: true });
  process.chdir(scratch);
  t.after(() => {
    process.chdir(cwd);
    rmSync(scratch, { recursive: true, force: true });
  });
  writeFileSync(transactionPath, `${JSON.stringify(first.value)}\n`);
  const recovered = await selectTransaction({
    ...base,
    GITHUB_RUN_ATTEMPT: '2',
    RELEASE_CHANNEL: 'stable',
    RELEASE_SOURCE_SHA: sourceSha,
  }, first.fixture.api);
  assert.deepEqual(recovered, first.value);
  first.fixture.values.set(`compare/${sourceSha}...${sourceSha}`,
    { status: 'diverged', merge_base_commit: { sha: workflowSha } });
  first.fixture.values.set('git/ref/heads/main', { object: { sha: movedHead } });
  first.fixture.values.set(`compare/${sourceSha}...${movedHead}`,
    { status: 'diverged', merge_base_commit: { sha: workflowSha } });
  await assert.rejects(selectTransaction({
    ...base,
    GITHUB_RUN_ATTEMPT: '2',
    RELEASE_CHANNEL: 'stable',
  }, first.fixture.api), /trusted canonical branch history/);
});

test('rehearsal transaction passes real admission before authorization is considered', async () => {
  const { value } = await transaction('insider');
  value.mode = 'rehearsal';
  const admission = admit({
    repository: value.repository,
    event: 'workflow_dispatch',
    ref: 'refs/heads/development',
    eventSha: value.sourceCommit,
    workflowIdentity: value.workflowIdentity,
    workflowSha: value.workflowCommit,
    workflowBranch: 'development',
    observedBranchHead: value.observedBranchHead,
    buildId: value.runId,
    buildAttempt: value.runAttempt,
    channel: value.channel,
  }, value.sourceCommit, 'v1.2.3\n');
  assert.equal(admission.sourceCommit, sourceSha);
  assert.equal(admission.workflowCommit, workflowSha);
  assert.equal(validateTransactionOperation('admit', value), value);
  assert.throws(() => validateTransactionOperation('authorize', value),
    /cannot enter release authorization/);
});

test('qualification binds namespaced jobs to this run, attempt, app, suite and source checks', async () => {
  const { value, fixture } = await transaction();
  const receipt = await verifyTransactionQualification(value, fixture.api, now);
  assert.equal(receipt.run.checkSuiteId, '77');
  assert.equal(receipt.jobs.length, jobNames.length);
  assert.ok(receipt.jobs.every(job => job.name.startsWith(`${qualificationJobNamespace} / `)));
  assert.equal(receipt.sourceEvidence.sourceCommit, sourceSha);
  for (const mutate of [
    f => { f.jobs[0].run_attempt = 2; },
    f => { f.jobs[0].name = 'CI tooling tests'; f.jobs.push({ ...f.jobs[0] }); },
    f => { f.transactionChecks[0].check_suite.id = 99; },
    f => { f.transactionChecks[0].app.slug = 'foreign'; },
    f => { f.sourceChecks[0].conclusion = 'failure'; },
  ]) {
    const isolated = await transaction();
    mutate(isolated.fixture);
    await assert.rejects(verifyTransactionQualification(isolated.value, isolated.fixture.api, now));
  }
});

test('qualification receipt freshness rejects expiry, future, malformed lifetime and delayed approval', async () => {
  const { value, fixture } = await transaction();
  const receipt = await verifyTransactionQualification(value, fixture.api, now);
  assert.equal(validateQualificationReceipt(receipt, value, 'release', now), receipt);
  assert.equal(validateQualificationReceipt(receipt, value, 'release', now + qualificationLifetimeMs), receipt);
  assert.throws(() => validateQualificationReceipt(receipt, value, 'release', now + qualificationLifetimeMs + 1),
    /expired/);
  assert.throws(() => validateQualificationReceipt({ ...receipt, checkedAt: new Date(now + 1).toISOString() },
    value, 'release', now), /future-dated|invalid lifetime/);
  assert.throws(() => validateQualificationReceipt({ ...receipt,
    expiresAt: new Date(now + qualificationLifetimeMs + 1).toISOString() }, value, 'release', now),
  /invalid lifetime/);
});

test('underlying qualification evidence rejects missing, stale, future and post-collection timestamps', async () => {
  for (const mutate of [
    f => { delete f.sourceChecks[0].completed_at; },
    f => { f.sourceChecks[0].completed_at = '2026-09-12T19:59:59.999Z'; },
    f => { f.sourceChecks[0].completed_at = '2026-09-13T20:00:00.001Z'; },
    f => {
      const statuses = f.values.get(`commits/${sourceSha}/status?per_page=100`);
      statuses.statuses[0].created_at = '2026-09-13T20:00:00.001Z';
      statuses.statuses[0].updated_at = '2026-09-13T20:00:00.001Z';
    },
    f => { f.jobs[0].completed_at = '2026-09-13T20:00:00.001Z'; },
    f => { f.sourceChecks[0].completed_at = '2026-09-13T19:30:00+00:00'; },
    f => { f.sourceChecks[0].completed_at = '2026-02-30T19:30:00Z'; },
    f => { f.sourceChecks[0].completed_at = '2026-09-13T19:30:00.1234Z'; },
    f => { f.sourceChecks[0].completed_at = '2026-09-13T19:30:00'; },
    f => {
      const statuses = f.values.get(`commits/${sourceSha}/status?per_page=100`);
      statuses.statuses[0].created_at = '2026-09-13T19:32:00Z';
      statuses.statuses[0].updated_at = '2026-09-13T19:31:00Z';
    },
    f => { f.jobs[0].completed_at = '2026-09-13T19:39:59Z'; },
  ]) {
    const isolated = await transaction();
    mutate(isolated.fixture);
    await assert.rejects(
      verifyTransactionQualification(isolated.value, isolated.fixture.api, now),
      /Invalid|missing|stale|future|post-collection|timestamps/,
    );
  }
});

test('GitHub REST second-precision timestamps remain valid throughout qualification and receipt validation', async () => {
  const { value, fixture } = await transaction();
  const run = fixture.values.get('actions/runs/42');
  run.run_started_at = '2026-09-13T19:35:00Z';
  run.updated_at = '2026-09-13T19:55:00Z';
  for (const job of fixture.jobs) {
    job.started_at = '2026-09-13T19:40:00Z';
    job.completed_at = '2026-09-13T19:50:00Z';
  }
  for (const check of [...fixture.transactionChecks, ...fixture.sourceChecks]) {
    check.completed_at = check.head_sha === workflowSha ?
      '2026-09-13T19:50:00Z' : '2026-09-13T19:30:00Z';
  }
  const statuses = fixture.values.get(`commits/${sourceSha}/status?per_page=100`);
  statuses.statuses[0].created_at = '2026-09-13T19:31:00Z';
  statuses.statuses[0].updated_at = '2026-09-13T19:31:00Z';
  const receipt = await verifyTransactionQualification(value, fixture.api, now);
  receipt.checkedAt = '2026-09-13T20:00:00Z';
  receipt.expiresAt = '2026-09-13T20:30:00Z';
  receipt.sourceEvidence.collectedAt = receipt.checkedAt;
  assert.equal(validateQualificationReceipt(receipt, value, 'release', now), receipt);
});

test('qualification and rehearsal receipts are closed transaction-bound variants', async t => {
  const { value, fixture } = await transaction();
  value.mode = 'rehearsal';
  const qualification = await verifyTransactionQualification(value, fixture.api, now);
  assert.equal(validateQualificationReceipt(qualification, value, 'rehearsal', now), qualification);
  const cwd = process.cwd();
  const scratch = resolve('.artifacts', `release-transaction-${randomUUID()}`);
  mkdirSync(scratch, { recursive: true });
  process.chdir(scratch);
  t.after(() => {
    process.chdir(cwd);
    rmSync(scratch, { recursive: true, force: true });
  });
  const receipt = writeRehearsalReceipt(value, qualification, now);
  assert.equal(receipt.kind, 'release-rehearsal-only');
  assert.equal(receipt.publicationAuthorized, false);
  assert.throws(() => validateQualificationReceipt(receipt, value, 'release', now),
    /cannot authorize release|qualification receipt/);
  assert.throws(() => validateTransaction({ ...value, future: true }));
});

test('single authority has direct dependencies, one approval, isolated rehearsal, and diagnostics', () => {
  const workflow = load(readFileSync('.github/workflows/consolidated-release.yml', 'utf8'));
  assert.deepEqual(Object.keys(workflow.on.workflow_dispatch.inputs), ['channel', 'source_sha']);
  assert.deepEqual(workflow.jobs.publish.needs, ['admit', 'qualification', 'collect-qualification']);
  assert.equal(workflow.jobs.authorize, undefined);
  assert.equal(workflow.jobs.rehearsal, undefined);
  assert.equal(workflow.jobs.publish.with.transaction, '${{ needs.admit.outputs.transaction }}');
  assert.equal(workflow.jobs.publish.with.source_sha, '${{ needs.admit.outputs.source_sha }}');
  assert.equal(workflow.jobs.publish.with.channel, '${{ needs.admit.outputs.channel }}');
  assert.equal(workflow.jobs.publish.with.approval_mode, '${{ needs.admit.outputs.approval_mode }}');
  assert.equal(workflow.jobs.publish.with.verified_branch_head, undefined);
  assert.match(JSON.stringify(workflow.jobs['schedule-insider']), /--ref development/);
  assert.doesNotMatch(JSON.stringify(workflow.jobs['schedule-insider']), /mode=/);
  const diagnostics = JSON.stringify(workflow.jobs.diagnostics);
  assert.match(diagnostics, /public_identity|qualification|evidence|publication|source/i);
});

test('legacy qualifier and recorder remain reachable while diagnostics are hidden', () => {
  const qualify = load(readFileSync('.github/workflows/qualify-canonical-release.yml', 'utf8'));
  const record = load(readFileSync('.github/workflows/record-canonical-qualification.yml', 'utf8'));
  const rehearsal = load(readFileSync('.github/workflows/release-protection-rehearsal.yml', 'utf8'));
  assert.ok(qualify.on.workflow_dispatch);
  assert.ok(record.on.workflow_run);
  assert.notEqual(record.jobs.record.if, false);
  assert.ok(rehearsal.on.workflow_call);
  assert.equal(rehearsal.on.workflow_dispatch, undefined);
});

test('publisher has exactly one protected deployment containing every credential and mutation', () => {
  const publisher = load(readFileSync('.github/workflows/docker-publish.yml', 'utf8'));
  assert.deepEqual(Object.keys(publisher.jobs), ['publish']);
  const environments = Object.values(publisher.jobs).filter(job => job.environment).map(job => job.environment);
  assert.deepEqual(environments, ['release-${{ inputs.channel }}']);
  const job = JSON.stringify(publisher.jobs.publish);
  assert.match(job, /RELEASE_PUBLISHER_PRIVATE_KEY/);
  assert.match(job, /RELEASE_REGISTRY_TOKEN/);
  assert.match(job, /release-control\.mjs authorize/);
  assert.match(job, /release-set\.mjs tag/);
  assert.match(job, /release-control\.mjs advance/);
  assert.doesNotMatch(JSON.stringify(publisher), /release-(?:publisher|rehearsal)-/);
});

test('every docker publisher release-control consumer follows one immutable workflow checkout in its job', () => {
  const publisher = load(readFileSync('.github/workflows/docker-publish.yml', 'utf8'));
  const steps = publisher.jobs.publish.steps;
  const checkout = steps.findIndex(step =>
    step.uses?.startsWith('actions/checkout@') &&
    step.with?.ref === '${{ fromJSON(inputs.transaction).workflowCommit }}' &&
    step.with?.['persist-credentials'] === false);
  assert.equal(checkout, 0);
  const consumers = steps.flatMap((step, index) =>
    typeof step.run === 'string' && /scripts\/ci\/release-(?:control|set)\.mjs/.test(step.run) ? [index] : []);
  assert.ok(consumers.length >= 5);
  assert.ok(consumers.every(index => checkout < index));
});

test('privileged workflow actions are pinned to full commit SHAs with version comments', () => {
  const pending = [
    '.github/workflows/consolidated-release.yml',
    '.github/workflows/docker-publish.yml',
    '.github/actions/release-authorization/action.yml',
  ];
  const visited = new Set();
  while (pending.length > 0) {
    const file = pending.pop();
    if (visited.has(file)) continue;
    visited.add(file);
    const text = readFileSync(file, 'utf8');
    for (const line of text.split(/\r?\n/).filter(line =>
      /^\s*(?:-\s+)?uses:\s+(?!\.\/)[^@]+@/.test(line))) {
      assert.match(line, /@[0-9a-f]{40}\s+#\s+v[0-9]/, `${file}: ${line.trim()}`);
    }
    for (const match of text.matchAll(/uses:\s+\.\/(\.github\/actions\/[^\s]+)/g)) {
      pending.push(`${match[1]}/action.yml`);
    }
  }
});

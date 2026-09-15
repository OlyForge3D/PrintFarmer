import assert from 'node:assert/strict';
import { existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import test from 'node:test';
import { load } from 'js-yaml';
import { randomUUID } from 'node:crypto';
import { resolve } from 'node:path';
import {
  qualificationJobNamespace, qualificationLifetimeMs, qualificationPath,
  qualifyTransaction, selectTransaction, transactionPath,
  validateQualificationReceipt, validateTransaction, verifyTransactionQualification,
} from '../release-transaction.mjs';

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
    { GITHUB_WORKFLOW_SHA: sourceSha },
  ]) {
    await assert.rejects(selectTransaction({ ...base, ...overrides }, apiFixture().api));
  }
});

test('trusted scheduled insider publication selects a transaction while untrusted schedule contexts fail closed', async () => {
  const trustedSchedule = {
    ...base,
    GITHUB_EVENT_NAME: 'schedule',
    RELEASE_CHANNEL: 'insider',
    RELEASE_SOURCE_SHA: '',
  };
  const selected = await selectTransaction(trustedSchedule, apiFixture().api);
  assert.equal(selected.channel, 'insider');
  assert.equal(selected.sourceBranch, 'development');
  for (const overrides of [
    { GITHUB_REPOSITORY: 'attacker/PrintFarmer' },
    { GITHUB_REF: 'refs/heads/main' },
    { GITHUB_WORKFLOW_REF: 'OlyForge3D/PrintFarmer/.github/workflows/consolidated-release.yml@refs/heads/main' },
    { RELEASE_CHANNEL: 'stable' },
    { RELEASE_SOURCE_SHA: sourceSha },
  ]) {
    await assert.rejects(selectTransaction({ ...trustedSchedule, ...overrides }, apiFixture().api),
      /Untrusted release dispatch|immutable release-control workflow/);
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
  assert.equal(validateQualificationReceipt(receipt, value, now), receipt);
  assert.equal(validateQualificationReceipt(receipt, value, now + qualificationLifetimeMs), receipt);
  assert.throws(() => validateQualificationReceipt(receipt, value, now + qualificationLifetimeMs + 1),
    /expired/);
  assert.throws(() => validateQualificationReceipt({ ...receipt, checkedAt: new Date(now + 1).toISOString() },
    value, now), /future-dated|invalid lifetime/);
  assert.throws(() => validateQualificationReceipt({ ...receipt,
    expiresAt: new Date(now + qualificationLifetimeMs + 1).toISOString() }, value, now),
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
  assert.equal(validateQualificationReceipt(receipt, value, now), receipt);
  assert.throws(() => validateTransaction({ ...value, unsupported: true }),
    /release transaction fields/);
});

test('qualification writer rejects oversized network evidence before writing', async t => {
  const { value, fixture } = await transaction();
  fixture.jobs[0].html_url = `https://github.com/${'x'.repeat(1024 * 1024)}`;
  const cwd = process.cwd();
  const scratch = resolve('.artifacts', `release-qualification-size-${randomUUID()}`);
  mkdirSync(scratch, { recursive: true });
  process.chdir(scratch);
  t.after(() => {
    process.chdir(cwd);
    rmSync(scratch, { recursive: true, force: true });
  });
  await assert.rejects(qualifyTransaction(value, fixture.api, now),
    /maximum receipt size/);
  assert.equal(existsSync(qualificationPath), false);
});

test('single authority has direct dependencies, one approval, and no alternate ceremony', () => {
  const workflow = load(readFileSync('.github/workflows/consolidated-release.yml', 'utf8'));
  assert.deepEqual(Object.keys(workflow.on.workflow_dispatch.inputs), ['channel', 'source_sha', 'operation', 'reservation_target']);
  assert.deepEqual(workflow.jobs.publish.needs,
    ['admit', 'qualification', 'collect-qualification']);
  assert.equal(workflow.jobs.authorize, undefined);
  assert.deepEqual(Object.keys(workflow.jobs),
    ['admit', 'qualification', 'collect-qualification', 'publish', 'summary']);
  assert.equal(workflow.jobs.publish.with.transaction, '${{ needs.admit.outputs.transaction }}');
  assert.deepEqual(Object.keys(workflow.jobs.publish.with), ['transaction', 'operation', 'reservation_target']);
  assert.equal(workflow.jobs.publish.with.verified_branch_head, undefined);
  assert.equal(workflow.jobs.admit.if, undefined);
  assert.match(workflow.concurrency.group, /inputs\.operation \|\| 'publish'/);
  assert.match(workflow.jobs.admit.steps.find(step => step.id === 'select').env.RELEASE_CHANNEL,
    /github\.event_name == 'schedule' && 'insider'/);
  assert.equal(workflow.jobs.admit.steps.find(step => step.id === 'admit').if,
    "(inputs.operation || 'publish') == 'publish'");
  assert.equal(workflow.jobs.qualification.if, "(inputs.operation || 'publish') == 'publish'");
  assert.equal(workflow.jobs['collect-qualification'].if, "(inputs.operation || 'publish') == 'publish'");
  assert.match(workflow.jobs.publish.if, /\(inputs\.operation \|\| 'publish'\) == 'abandon'/);
  assert.equal(workflow.jobs.publish.with.operation, "${{ inputs.operation || 'publish' }}");
  assert.equal(workflow.jobs.summary.if, 'always()');
  const summary = JSON.stringify(workflow.jobs.summary);
  assert.match(summary, /public_identity|qualification|evidence|publication|source/i);
  assert.doesNotMatch(JSON.stringify(workflow), /RELEASE_MODE/);
});

test('legacy qualifier and recorder remain reachable without an alternate release workflow', () => {
  const qualify = load(readFileSync('.github/workflows/qualify-canonical-release.yml', 'utf8'));
  const record = load(readFileSync('.github/workflows/record-canonical-qualification.yml', 'utf8'));
  assert.ok(qualify.on.workflow_dispatch);
  assert.ok(record.on.workflow_run);
  assert.notEqual(record.jobs.record.if, false);
});

test('publisher has exactly one protected deployment containing every credential and mutation', () => {
  const publisher = load(readFileSync('.github/workflows/docker-publish.yml', 'utf8'));
  assert.deepEqual(Object.keys(publisher.on.workflow_call.inputs), ['transaction', 'operation', 'reservation_target']);
  assert.deepEqual(Object.keys(publisher.jobs), ['publish']);
  const environments = Object.values(publisher.jobs).filter(job => job.environment).map(job => job.environment);
  assert.deepEqual(environments, [
    "${{ inputs.operation == 'abandon' && 'release-insider' || (fromJSON(inputs.transaction).channel == 'stable' && 'release-stable' || 'release-insider') }}",
  ]);
  assert.equal(publisher.concurrency.group,
    "release-publication-${{ inputs.operation == 'abandon' && 'insider' || (fromJSON(inputs.transaction).channel == 'stable' && 'stable' || 'insider') }}");
  assert.equal(publisher.jobs.publish.steps[2].with.ref,
    '${{ fromJSON(inputs.transaction).sourceCommit }}');
  const job = JSON.stringify(publisher.jobs.publish);
  assert.doesNotMatch(job, /inputs\.(?:channel|source_sha|approval_mode)/);
  assert.match(job, /inputs\.operation/);
  assert.match(job, /inputs\.reservation_target/);
  for (const value of job.matchAll(/"RELEASE_SOURCE_COMMIT":"([^"]+)"/g)) {
    assert.ok(['${{ fromJSON(inputs.transaction).sourceCommit }}', '${{ steps.recover_abandonment.outputs.source_sha }}'].includes(value[1]));
  }
  assert.match(job, /RELEASE_PUBLISHER_PRIVATE_KEY/);
  assert.match(job, /RELEASE_REGISTRY_TOKEN/);
  assert.match(job, /release-control\.mjs authorize/);
  assert.match(job, /release-set\.mjs tag/);
  assert.match(job, /release-set\.mjs alias/);
  assert.match(job, /release-control\.mjs advance/);
  assert.ok(job.indexOf('release-transaction.mjs validate') <
    job.indexOf('actions/create-github-app-token@'));
});

test('abandonment has a minimal protected path with no source checkout or publication step', () => {
  const publisher = load(readFileSync('.github/workflows/docker-publish.yml', 'utf8'));
  const steps = publisher.jobs.publish.steps;
  assert.ok(steps.some(step => step.name === 'Record approved immutable reservation abandonment' && step.if === "inputs.operation == 'abandon'"));
  for (const step of steps.filter(step => step['working-directory'] === 'source' || step.with?.path === 'source')) {
    assert.match(step.if ?? '', /inputs\.operation == 'publish'/);
  }
});

test('publisher completes revocable preflight before registry login and image publication', () => {
  const publisher = load(readFileSync('.github/workflows/docker-publish.yml', 'utf8'));
  const steps = publisher.jobs.publish.steps;
  const named = name => steps.findIndex(step => step.name === name);
  const login = steps.findIndex(step => step.uses?.startsWith('docker/login-action@'));
  assert.ok(named('Validate all revocable controls before publication') >
    named('Consume the signed authorization in this protected job'));
  assert.ok(named('Validate all revocable controls before publication') < login);
  assert.ok(login < named('Build, attest and sign the complete immutable image set'));
  assert.ok(named('Build, attest and sign the complete immutable image set') <
    named('Verify every pushed digest signature and SPDX attestation'));
  assert.ok(named('Verify every pushed digest signature and SPDX attestation') <
    named('Validate complete immutable set and stage verifier evidence'));
  assert.ok(named('Validate complete immutable set and stage verifier evidence') <
    named('Publish and verify public corresponding-source assets'));
  assert.ok(named('Publish and verify signed manifest before mutable aliases') <
    named('Reverify the activated public release before aliases'));
  assert.ok(named('Reverify the activated public release before aliases') <
    named('Publish channel-isolated verified image aliases'));
  assert.ok(named('Reverify the activated public release before aliases') <
    named('Advance the complete channel pointer last'));
  assert.ok(named('Validate complete immutable set and stage verifier evidence') <
    named('Publish and verify public corresponding-source assets'));
});

test('publisher uses one reusable-workflow signer identity for every verification path', () => {
  const publisher = load(readFileSync('.github/workflows/docker-publish.yml', 'utf8'));
  const expected =
    'https://github.com/OlyForge3D/PrintFarmer/.github/workflows/docker-publish.yml@refs/heads/development';
  assert.equal(publisher.env.RELEASE_SIGNER_IDENTITY, expected);
  const job = JSON.stringify(publisher.jobs.publish);
  const verificationCommands = job.split(/cosign verify(?:-attestation|-blob)?/).slice(1);
  assert.ok(verificationCommands.length >= 5);
  for (const command of verificationCommands) {
    const invocation = command.split('cosign ')[0];
    assert.match(invocation, /--certificate-identity \\"?\$RELEASE_SIGNER_IDENTITY/);
    assert.match(invocation, /--certificate-oidc-issuer https:\/\/token\.actions\.githubusercontent\.com/);
  }
  assert.match(job, /cosign verify --output json \\"\$reference\\"/);
  assert.match(job, /cosign verify-attestation --output json \\"\$reference\\"/);
  assert.match(job, /--type spdxjson/);
  assert.match(job, /registry_digest/);
  assert.ok(job.indexOf('cosign verify --output json \\"$reference\\"') <
    job.indexOf('release-set.mjs tag'));
  assert.ok(job.indexOf('cosign verify-attestation --output json \\"$reference\\"') <
    job.indexOf('release-control.mjs advance'));
  assert.ok(job.indexOf('release-set.mjs tag') < job.indexOf('release-set.mjs alias'));
  assert.ok(job.indexOf('release-set.mjs alias') < job.indexOf('release-control.mjs advance'));
  assert.match(job, /env -u GH_TOKEN -u GITHUB_TOKEN curl --fail/);
  assert.match(job, /cosign download signature \\"\$reference\\" \| jq -sc/);
  assert.match(job, /cosign download attestation \\"\$reference\\" \| jq -sc/);
});

test('every docker publisher release-control consumer follows one immutable workflow checkout in its job', () => {
  const publisher = load(readFileSync('.github/workflows/docker-publish.yml', 'utf8'));
  const steps = publisher.jobs.publish.steps;
  const checkout = steps.findIndex(step =>
    step.uses?.startsWith('actions/checkout@') &&
    step.with?.ref === '${{ fromJSON(inputs.transaction).workflowCommit }}' &&
    step.with?.['persist-credentials'] === false);
  assert.equal(checkout, 1);
  assert.match(steps[0].name, /Verify invoked immutable control before checkout/);
  assert.match(steps[0].run, /GITHUB_WORKFLOW_SHA/);
  assert.match(steps[0].run, /repos\/\$GITHUB_REPOSITORY\/git\/commits\/\$workflow_commit/);
  const consumers = steps.flatMap((step, index) =>
    typeof step.run === 'string' && /scripts\/ci\/release-(?:control|set)\.mjs/.test(step.run) ? [index] : []);
  assert.ok(consumers.length >= 5);
  assert.ok(consumers.every(index => checkout < index));
});

test('privileged workflow actions are pinned to full commit SHAs with version comments', () => {
  const pending = [
    '.github/workflows/consolidated-release.yml',
    '.github/workflows/docker-publish.yml',
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

import assert from 'node:assert/strict';
import { existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import test from 'node:test';
import { load } from 'js-yaml';
import { randomUUID } from 'node:crypto';
import { resolve } from 'node:path';
import { sourceReviewFixture } from './fixtures/release-source-review.mjs';
import {
  qualificationJobNamespace, qualificationLifetimeMs, qualificationPath,
  qualifyTransaction, selectTransaction, transactionPath,
  transactionFromEnvironment, validateQualificationReceipt, validateTransaction, verifyTransactionQualification,
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

function apiFixture(channel = 'insider', head = sourceSha, reviewedHead = sourceSha) {
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
  const review = sourceReviewFixture(sourceSha, branch, now, reviewedHead);
  for (const [key, value] of review.values) values.set(key, value);
  if (reviewedHead !== sourceSha) {
    values.set(`commits/${sourceSha}/status?per_page=100`, { sha: sourceSha, total_count: 0, statuses: [] });
  }
  const transactionRun = values.get('actions/runs/42');
  transactionRun.actor = { login: 'author' };
  transactionRun.triggering_actor = { login: 'author' };
  const api = async endpoint => {
    if (!values.has(endpoint)) throw new Error(`Unexpected endpoint: ${endpoint}`);
    return structuredClone(values.get(endpoint));
  };
  return { api, values, jobs, transactionChecks, sourceChecks, branch, review };
}

async function transaction(channel = 'insider', requested = '', reviewedHead = sourceSha) {
  const f = apiFixture(channel, sourceSha, reviewedHead);
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

test('transaction consumers reject cross-run and caller substitutions before trusting stored JSON', async () => {
  const { value } = await transaction();
  const env = { ...base, RELEASE_TRANSACTION: JSON.stringify(value) };
  assert.deepEqual(transactionFromEnvironment(env), value);
  for (const override of [
    { GITHUB_RUN_ID: '43' },
    { GITHUB_WORKFLOW_SHA: movedHead },
    { GITHUB_SHA: movedHead },
    { GITHUB_WORKFLOW_REF: base.GITHUB_WORKFLOW_REF.replace('consolidated-release', 'untrusted') },
    { GITHUB_REPOSITORY: 'attacker/repo' },
    { GITHUB_REF: 'refs/heads/feature' },
    { GITHUB_EVENT_NAME: 'pull_request' },
  ]) assert.throws(() => transactionFromEnvironment({ ...env, ...override }), /executing trusted workflow/);
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
    f => { f.review.status.state = 'failure'; },
  ]) {
    const isolated = await transaction();
    mutate(isolated.fixture);
    await assert.rejects(verifyTransactionQualification(isolated.value, isolated.fixture.api, now));
  }
});

test('automatic qualification accepts a reviewed squash tree without any prior canonical status or build checks', async () => {
  const reviewed = 'e'.repeat(40);
  for (const channel of ['stable', 'insider']) {
    for (const mode of ['single-maintainer', 'separation-of-duties']) {
      const { value, fixture } = await transaction(channel, '', reviewed);
      value.approvalMode = mode;
      fixture.values.delete(`commits/${sourceSha}/status?per_page=100`);
      fixture.values.delete(`commits/${sourceSha}/check-runs?per_page=100`);
      const receipt = await qualifyTransaction(value, fixture.api, now);
      assert.equal(receipt.sourceEvidence.review.sourceCommit, sourceSha);
      assert.equal(receipt.sourceEvidence.review.reviewedHead, reviewed);
      assert.equal(receipt.sourceEvidence.review.tree, 'd'.repeat(40));
      assert.equal(receipt.sourceEvidence.review.classification, 'REVIEWED');
      assert.equal(receipt.jobs.length, jobNames.length);
    }
  }
});

test('squash qualification fails closed for source, review, tree and transaction substitutions', async () => {
  const mutations = [
    ['missing associated PR', f => f.values.set(`commits/${sourceSha}/pulls?per_page=100`, [])],
    ['ambiguous PRs', f => f.values.get(`commits/${sourceSha}/pulls?per_page=100`).push({ ...f.review.pull, number: 61 })],
    ['merge SHA', f => { f.review.pull.merge_commit_sha = movedHead; }],
    ['base branch', f => { f.review.pull.base.ref = 'untrusted'; }],
    ['base repository', f => { f.review.pull.base.repo.full_name = 'attacker/repo'; }],
    ['head repository', f => { f.review.pull.head.repo.full_name = 'attacker/repo'; }],
    ['not merged', f => { f.review.pull.merged = false; }],
    ['changed tree', f => { f.values.get(`git/commits/${sourceSha}`).tree.sha = movedHead; }],
    ['commit response substitution', f => { f.values.get(`git/commits/${sourceSha}`).sha = movedHead; }],
    ['status text', f => { f.review.status.description = `REVIEWED (self-attested) @ ${sourceSha.slice(0, 12)} by bishop`; }],
    ['status creator', f => { f.review.status.creator.login = 'attacker'; }],
    ['status target', f => { f.review.status.target_url = 'https://example.com/actions/runs/31'; }],
    ['missing review', f => f.values.set(`commits/${f.review.pull.head.sha}/status?per_page=100`,
      { sha: f.review.pull.head.sha, total_count: 0, statuses: [] })],
    ['failed review', f => { f.review.status.state = 'failure'; }],
    ['stale review', f => { f.review.status.created_at = '2026-09-12T19:00:00Z'; }],
    ['foreign review workflow', f => { f.review.run.repository.full_name = 'attacker/repo'; }],
    ['untrusted review event', f => { f.review.run.event = 'pull_request'; }],
    ['wrong review PR', f => { f.review.run.display_title = 'Squad review record for PR #61'; }],
    ['replayed review run', f => { f.review.run.run_attempt = 2; }],
    ['failed review run', f => { f.review.run.conclusion = 'failure'; }],
    ['missing qualification', f => { f.jobs[0].conclusion = 'skipped'; }],
    ['wrong qualification attempt', f => { f.jobs[0].run_attempt = 2; }],
    ['wrong qualification source workflow', f => { f.values.get('actions/runs/42').head_sha = movedHead; }],
    ['wrong qualification run', f => { f.jobs[0].run_id = 43; }],
    ['newer negative review', f => {
      const status = f.values.get(`commits/${f.review.pull.head.sha}/status?per_page=100`);
      status.statuses.push({ ...f.review.status, id: 999, state: 'failure' });
      status.total_count++;
    }],
    ['truncated reviews', f => { f.values.get(`commits/${f.review.pull.head.sha}/status?per_page=100`).total_count++; }],
    ['wrong build integration', f => {
      f.values.get(`rules/branches/${f.branch}?per_page=100`)[0].parameters.required_status_checks[0].integration_id = 999;
    }],
  ];
  for (const [name, mutate] of mutations) {
    const { value, fixture } = await transaction('insider', '', 'e'.repeat(40));
    mutate(fixture);
    await assert.rejects(qualifyTransaction(value, fixture.api, now), undefined, name);
  }
});

test('single-maintainer requires genuine review but only separation-of-duties requires non-self native code-owner review', async () => {
  const { value, fixture } = await transaction('insider', '', 'e'.repeat(40));
  fixture.values.set('pulls/60/reviews?per_page=100', []);
  await qualifyTransaction(value, fixture.api, now);
  value.approvalMode = 'separation-of-duties';
  await assert.rejects(qualifyTransaction(value, fixture.api, now), /non-self code-owner/);
  const review = { id: 70, commit_id: fixture.review.pull.head.sha,
    user: { login: 'native-reviewer' }, state: 'APPROVED', submitted_at: '2026-09-13T19:00:00Z' };
  fixture.values.set('pulls/60/reviews?per_page=100', [review]);
  await qualifyTransaction(value, fixture.api, now);
  for (const login of ['author', 'native-reviewer']) {
    fixture.values.get('actions/runs/42').actor.login = login;
    if (login === 'native-reviewer') {
      await assert.rejects(qualifyTransaction(value, fixture.api, now), /non-self code-owner/);
    }
  }
  fixture.values.get('actions/runs/42').actor.login = 'author';
  fixture.values.get('collaborators/native-reviewer/permission').permission = 'read';
  await assert.rejects(qualifyTransaction(value, fixture.api, now), /live write permission/);
});

test('same-run reattempt requires fresh qualification jobs from that attempt and preserves original source', async () => {
  const { value, fixture } = await transaction('insider', '', 'e'.repeat(40));
  fixture.values.get('actions/runs/42').run_attempt = 2;
  fixture.values.set('actions/runs/42/attempts/2/jobs?per_page=100',
    { total_count: fixture.jobs.length, jobs: fixture.jobs });
  await assert.rejects(verifyTransactionQualification(value, fixture.api, now, '2'), /qualification job/);
  fixture.jobs.forEach(job => { job.run_attempt = 2; });
  const receipt = await verifyTransactionQualification(value, fixture.api, now, '2');
  assert.equal(receipt.run.attempt, '2');
  assert.equal(receipt.transaction.runAttempt, '1');
  assert.equal(receipt.transaction.sourceCommit, sourceSha);
});

test('scheduled insider qualification collects the current full-safe job set and exact-tree review', async () => {
  const { value, fixture } = await transaction('insider', '', 'e'.repeat(40));
  fixture.values.get('actions/runs/42').event = 'schedule';
  const receipt = await qualifyTransaction(value, fixture.api, now);
  assert.equal(receipt.jobs.length, jobNames.length);
  assert.ok(receipt.sourceEvidence.checks.filter(check => check.checkId)
    .every(check => check.satisfiedBy === 'transaction-job' && check.runId === value.runId &&
      check.runAttempt === '1' && check.workflowCommit === workflowSha));
});

test('unmapped required checks still need latest exact-source evidence without repeating collection', async () => {
  const { value, fixture } = await transaction('insider', '', 'e'.repeat(40));
  const policy = fixture.values.get('rules/branches/development?per_page=100')[0].parameters;
  policy.required_status_checks.push({ context: 'extra-a', integration_id: 15368 }, { context: 'extra-b' });
  const sourceChecks = fixture.values.get(`commits/${sourceSha}/check-runs?per_page=100`);
  const extra = ['extra-a', 'extra-b'].map((name, index) => ({
    ...fixture.sourceChecks[0], name, id: 800 + index,
  }));
  sourceChecks.check_runs.push(...extra);
  sourceChecks.total_count = sourceChecks.check_runs.length;
  let reads = 0;
  const api = async endpoint => {
    if (endpoint === `commits/${sourceSha}/check-runs?per_page=100`) reads++;
    return fixture.api(endpoint);
  };
  await qualifyTransaction(value, api, now);
  assert.equal(reads, 1);
  extra[0].conclusion = 'failure';
  await assert.rejects(qualifyTransaction(value, fixture.api, now), /required qualification/);
  extra[0].conclusion = 'success';
  extra[0].app = { id: 999, slug: 'github-actions' };
  await assert.rejects(qualifyTransaction(value, fixture.api, now), /required qualification/);
});

test('a stale native owner does not mask another fresh eligible owner approval', async () => {
  const { value, fixture } = await transaction('insider', '', 'e'.repeat(40));
  value.approvalMode = 'separation-of-duties';
  fixture.values.get(`contents/.github/CODEOWNERS?ref=${sourceSha}`).content =
    Buffer.from('* @stale-owner @native-reviewer').toString('base64');
  const reviews = fixture.values.get('pulls/60/reviews?per_page=100');
  reviews.unshift({ ...reviews[0], id: 69, user: { login: 'stale-owner' },
    submitted_at: '2026-09-12T18:00:00Z' });
  await qualifyTransaction(value, fixture.api, now);
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
    f => { delete f.transactionChecks[0].completed_at; },
    f => { f.transactionChecks[0].completed_at = '2026-09-12T19:59:59.999Z'; },
    f => { f.transactionChecks[0].completed_at = '2026-09-13T20:00:00.001Z'; },
    f => {
      const statuses = f.values.get(`commits/${sourceSha}/status?per_page=100`);
      statuses.statuses[0].created_at = '2026-09-13T20:00:00.001Z';
      statuses.statuses[0].updated_at = '2026-09-13T20:00:00.001Z';
    },
    f => { f.jobs[0].completed_at = '2026-09-13T20:00:00.001Z'; },
    f => { f.transactionChecks[0].completed_at = '2026-09-13T19:30:00+00:00'; },
    f => { f.transactionChecks[0].completed_at = '2026-02-30T19:30:00Z'; },
    f => { f.transactionChecks[0].completed_at = '2026-09-13T19:30:00.1234Z'; },
    f => { f.transactionChecks[0].completed_at = '2026-09-13T19:30:00'; },
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

function assertPermissionCeiling(requested, allowed, context) {
  const levels = { none: 0, read: 1, write: 2 };
  for (const permissions of [requested, allowed]) {
    assert.ok(permissions && typeof permissions === 'object' && !Array.isArray(permissions),
      `${context}: release permissions must be explicit maps, not blanket grants`);
  }
  for (const [scope, level] of Object.entries(requested)) {
    const ceiling = allowed[scope] ?? 'none';
    assert.ok(Object.hasOwn(levels, level) && Object.hasOwn(levels, ceiling),
      `${context}: invalid permission level for ${scope}`);
    assert.ok(levels[level] <= levels[ceiling],
      `${context}: ${scope}: ${level} exceeds caller ${ceiling}`);
  }
}

test('release reusable workflows stay within caller permission ceilings', () => {
  const pending = ['.github/workflows/consolidated-release.yml'];
  const visited = new Set();
  while (pending.length > 0) {
    const file = pending.pop();
    if (visited.has(file)) continue;
    visited.add(file);
    const workflow = load(readFileSync(file, 'utf8'));
    for (const [callerId, caller] of Object.entries(workflow.jobs)) {
      if (!caller.uses) continue;
      assert.ok(caller.uses.startsWith('./.github/workflows/'),
        `${file} / ${callerId}: reusable release workflows must be locally inspectable`);
      const callee = load(readFileSync(caller.uses, 'utf8'));
      const allowed = caller.permissions ?? workflow.permissions;
      if (callee.permissions !== undefined) {
        assertPermissionCeiling(callee.permissions, allowed, `${file} / ${callerId} defaults`);
      }
      for (const [jobId, job] of Object.entries(callee.jobs)) {
        assertPermissionCeiling(job.permissions ?? callee.permissions ?? allowed, allowed,
          `${file} / ${callerId} -> ${caller.uses} / ${jobId}`);
      }
      pending.push(caller.uses);
    }
  }
});

for (const scope of ['checks', 'pull-requests', 'statuses']) {
  test(`publisher permission ceiling rejects removal of caller ${scope}: read`, () => {
    const workflow = load(readFileSync('.github/workflows/consolidated-release.yml', 'utf8'));
    const publisher = load(readFileSync('.github/workflows/docker-publish.yml', 'utf8'));
    const allowed = structuredClone(workflow.jobs.publish.permissions);
    const requested = publisher.jobs.publish.permissions;
    assertPermissionCeiling(requested, allowed, 'publisher');
    assert.equal(allowed[scope], 'read');
    delete allowed[scope];
    assert.throws(() => assertPermissionCeiling(requested, allowed, 'publisher'),
      { message: `publisher: ${scope}: read exceeds caller none` });
  });
}

test('publisher caller retains only the required reads and existing OIDC write', () => {
  const workflow = load(readFileSync('.github/workflows/consolidated-release.yml', 'utf8'));
  assert.deepEqual(workflow.jobs.publish.permissions, {
    actions: 'read', checks: 'read', contents: 'read', packages: 'read',
    'pull-requests': 'read', statuses: 'read', 'id-token': 'write',
  });
  assert.deepEqual(workflow.permissions, { contents: 'read', 'pull-requests': 'read' });
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
  assert.match(job, /cp \\"\$index_evidence\/signature\.ndjson\\" \\"\$index_evidence\/signature\.bundle\.json\\"/);
  assert.match(job, /cp \\"\$index_evidence\/attestation\.ndjson\\" \\"\$index_evidence\/attestation\.bundle\.json\\"/);
  assert.match(job, /cp \\"\$platform_evidence\/signature\.ndjson\\" \\"\$platform_evidence\/signature\.bundle\.json\\"/);
  assert.match(job, /cp \\"\$platform_evidence\/attestation\.ndjson\\" \\"\$platform_evidence\/attestation\.bundle\.json\\"/);
  assert.match(job, /cosign download signature \\"\$reference\\" > \\"\$signature_download\\"/);
  assert.match(job, /cosign download attestation \\"\$reference\\" > \\"\$attestation_download\\"/);
  assert.doesNotMatch(job, /cosign download (?:signature|attestation) \\"\$reference\\" \| jq -sc/);
  assert.match(job, /cmp -s \\"\$evidence\/signature\.bundle\.json\\" \\"\$signature_download\\"/);
  assert.match(job, /cmp -s \\"\$evidence\/attestation\.bundle\.json\\" \\"\$attestation_download\\"/);
  assert.match(job, /crypto-evidence\/\$image\/platforms\/\$scope/);
  assert.equal((job.match(/Cosign signature evidence must use LF line endings/g) ?? []).length, 3);
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

import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, mkdirSync, writeFileSync, rmSync, existsSync } from 'node:fs';
import { spawnSync, execFileSync } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { runInNewContext } from 'node:vm';
import { load } from 'js-yaml';
import { repository, releaseBuildChecks, releaseReviewStatus } from '../release-policy.mjs';
import { confirmationBody, qualificationTitle, parseQualificationTitle, qualificationRequestUrl,
  qualificationWorkflow, evidenceWorkflow, canonicalValidationChecks, verifyQualification, recordQualification,
  verifyCanonicalReleaseEvidence, qualificationDescription } from '../canonical-qualification.mjs';

const stableSha = 'a'.repeat(40);
const defaultSha = 'b'.repeat(40);
const now = Date.parse('2026-09-13T08:00:00Z');
const url = id => `https://github.com/${repository}/actions/runs/${id}`;
const read = path => readFileSync(fileURLToPath(new URL(`../../../${path}`, import.meta.url)), 'utf8');
const root = fileURLToPath(new URL('../../../', import.meta.url));
const shell = resolveBash();
const canonicalDispatch = "github.event_name == 'workflow_dispatch' && " +
  "(github.ref == 'refs/heads/main' || github.ref == 'refs/heads/development')";
const forbiddenJobCapabilities = /secrets|\bpackages\b|id-token|create-github-app-token|download-artifact/;

function resolveBash() {
  if (process.env.BASH_PATH) return process.env.BASH_PATH;
  if (process.platform !== 'win32') return 'bash';
  const gitPaths = execFileSync('where.exe', ['git.exe'], { encoding: 'utf8' }).trim().split(/\r?\n/);
  return gitPaths.flatMap(path => [join(dirname(path), 'bash.exe'), join(dirname(path), '..', 'bin', 'bash.exe')])
    .find(path => existsSync(path)) ?? 'bash';
}

function scratch(t) {
  const directory = join(root, '.artifacts', `canonical-check-${randomUUID()}`);
  mkdirSync(directory, { recursive: true });
  t.after(() => rmSync(directory, { recursive: true, force: true }));
  return directory;
}

function fixture(channel = 'stable', mode = 'single-maintainer') {
  const branch = channel === 'stable' ? 'main' : 'development';
  const sha = channel === 'stable' ? stableSha : defaultSha;
  const definitions = {
    'ci.yml': { id: 1, path: '.github/workflows/ci.yml', state: 'active' },
    'qualify-canonical-release.yml': { id: 2, path: qualificationWorkflow, state: 'active' },
    'record-canonical-qualification.yml': { id: 3, path: evidenceWorkflow, state: 'active' },
  };
  function run(id, file, runBranch, runSha, event = 'workflow_dispatch') {
    return { id, path: definitions[file].path, workflow_id: definitions[file].id, event,
      repository: { full_name: repository }, head_repository: { full_name: repository },
      head_branch: runBranch, head_sha: runSha, run_attempt: 1, html_url: url(id),
      status: 'completed', conclusion: 'success', actor: { login: 'jpapiez' },
      triggering_actor: { login: 'jpapiez' },
      created_at: '2026-09-13T05:00:00Z', run_started_at: '2026-09-13T05:00:00Z',
      updated_at: '2026-09-13T06:00:00Z' };
  }
  const ci = run(10, 'ci.yml', branch, sha);
  ci.check_suite_id = 100;
  const qualifier = run(20, 'qualify-canonical-release.yml', 'development', defaultSha);
  qualifier.created_at = qualifier.run_started_at = '2026-09-13T07:00:00Z';
  qualifier.updated_at = '2026-09-13T07:05:00Z';
  qualifier.display_title = qualificationTitle(channel, '10', '50', mode === 'single-maintainer' ? '0' : '60', mode);
  const writer = run(30, 'record-canonical-qualification.yml', 'development', defaultSha, 'workflow_run');
  writer.created_at = writer.run_started_at = '2026-09-13T07:06:00Z';
  writer.updated_at = '2026-09-13T07:10:00Z';
  writer.display_title = 'Canonical evidence for 20';
  const comment = { id: 50, commit_id: sha, user: { login: mode === 'single-maintainer' ? 'jpapiez' : 'reviewer', type: 'User' },
    body: confirmationBody(sha, '10', mode), created_at: '2026-09-13T06:30:00Z', updated_at: '2026-09-13T06:30:00Z' };
  function jobs(run, names) {
    return names.map((name, i) => ({ id: run.id * 100 + i, name, run_id: run.id, run_attempt: 1,
      head_sha: run.head_sha, status: 'completed', conclusion: 'success',
      check_run_url: `https://api.github.com/repos/${repository}/check-runs/${run.id * 100 + i}` }));
  }
  const data = {
    repo: { full_name: repository, default_branch: 'development' },
    heads: { main: stableSha, development: defaultSha }, definitions,
    runs: { 10: ci, 20: qualifier, 30: writer },
    ciRuns: [ci], qualifications: [qualifier], comments: [comment],
    jobs: {
      10: jobs(ci, [...releaseBuildChecks, ...canonicalValidationChecks, 'Select affected tests', 'CI summary',
        'Dependency license & provenance validation', '.NET provider tests (DbHeavy)', '.NET test (example)']),
      20: jobs(qualifier, ['Verify canonical qualification']),
      30: jobs(writer, ['Record canonical evidence']),
    },
    permissions: { jpapiez: 'admin', reviewer: 'write' },
    owners: '* @reviewer\n',
    pr: { number: 60, head: { sha, repo: { full_name: repository } },
      base: { ref: branch, repo: { full_name: repository } }, user: { login: 'jpapiez' }, draft: false },
    reviews: [{ id: 70, user: { login: 'reviewer' }, state: 'APPROVED', commit_id: sha,
      submitted_at: '2026-09-13T06:15:00Z' }],
    statuses: [{ id: 80, context: releaseReviewStatus, state: 'success', creator: { login: 'github-actions[bot]' },
      target_url: url(30), created_at: '2026-09-13T07:08:00Z',
      description: qualificationDescription({ approvalMode: mode, sourceCommit: sha }) }],
  };
  data.rules = [{ type: 'required_status_checks', parameters: { strict_required_status_checks_policy: true,
    required_status_checks: [...releaseBuildChecks, ...canonicalValidationChecks, releaseReviewStatus]
      .map(context => context === releaseReviewStatus ? { context } : { context, integration_id: 15368 }) } }];
  data.checks = data.jobs[10].map(job => ({ name: job.name, head_sha: sha, status: 'completed',
    conclusion: 'success', app: { id: 15368, slug: 'github-actions' }, check_suite: { id: 100 }, url: job.check_run_url }));
  const calls = [];
  const posts = [];
  const count = (items, field) => ({ total_count: items.length, [field]: structuredClone(items) });
  const api = async (endpoint, method = 'GET', body) => {
    qualificationRequestUrl(endpoint, method, true);
    calls.push(endpoint);
    if (method === 'POST') { posts.push({ endpoint, body }); return {}; }
    if (!endpoint) return structuredClone(data.repo);
    if (endpoint.startsWith('git/ref/heads/')) {
      const name = endpoint.split('/').at(-1);
      return { ref: `refs/heads/${name}`, object: { type: 'commit', sha: data.heads[name] } };
    }
    let match = /^actions\/runs\/(\d+)(\/attempts\/1\/jobs\?per_page=100)?$/.exec(endpoint);
    if (match) return match[2] ? count(data.jobs[match[1]], 'jobs') : structuredClone(data.runs[match[1]]);
    match = /^actions\/workflows\/([^/]+)$/.exec(endpoint);
    if (match) return structuredClone(data.definitions[match[1]]);
    if (endpoint.startsWith('actions/workflows/ci.yml/runs?')) return count(data.ciRuns, 'workflow_runs');
    if (endpoint.startsWith('actions/workflows/qualify-canonical-release.yml/runs?')) return count(data.qualifications, 'workflow_runs');
    if (endpoint.startsWith('rules/branches/')) return structuredClone(data.rules);
    if (endpoint.endsWith('/check-runs?per_page=100')) return count(data.checks, 'check_runs');
    if (endpoint.endsWith('/comments?per_page=100')) return structuredClone(data.comments);
    if (endpoint.endsWith('/statuses?per_page=100')) return structuredClone(data.statuses);
    match = /^collaborators\/([^/]+)\/permission$/.exec(endpoint);
    if (match) return { user: { login: match[1] }, permission: data.permissions[match[1]] };
    if (endpoint.startsWith('contents/')) return { encoding: 'base64', content: Buffer.from(data.owners).toString('base64') };
    if (endpoint === 'pulls/60') return structuredClone(data.pr);
    if (endpoint === 'pulls/60/reviews?per_page=100') return structuredClone(data.reviews);
    throw new Error(`Unexpected fixture route: ${endpoint}`);
  };
  const env = { GITHUB_REPOSITORY: repository, GITHUB_EVENT_NAME: 'workflow_run', GITHUB_RUN_ATTEMPT: '1',
    GITHUB_RUN_ID: '30', GITHUB_REF: 'refs/heads/development', GITHUB_SHA: defaultSha,
    GITHUB_WORKFLOW_SHA: defaultSha, GITHUB_WORKFLOW_REF: `${repository}/${evidenceWorkflow}@refs/heads/development`,
    RELEASE_APPROVAL_MODE: mode };
  return { data, api, calls, posts, env, sha, channel, mode };
}

for (const channel of ['stable', 'insider']) {
  for (const mode of ['single-maintainer', 'separation-of-duties']) {
    test(`${channel}/${mode}: fresh canonical review and CI qualify without publication`, async () => {
      const f = fixture(channel, mode);
      const evidence = await verifyQualification(f.api, '20', mode, now);
      assert.equal(evidence.sourceCommit, f.sha);
      assert.equal(evidence.approvalMode, mode);
      assert.equal(f.posts.length, 0);
      assert.doesNotMatch(JSON.stringify(evidence), /jpapiez|reviewer|APPROVED|permission/);
      f.data.runs[30].status = 'in_progress';
      await recordQualification(f.api, '20', f.env, now);
      assert.deepEqual(f.posts, [{ endpoint: `statuses/${f.sha}`, body: {
        context: releaseReviewStatus, state: 'success', target_url: url(30),
        description: qualificationDescription(evidence),
      } }]);
      f.data.runs[30].status = 'completed';
      assert.equal((await verifyCanonicalReleaseEvidence(f.api, f.sha, channel, mode, now)).sourceCommit, f.sha);
    });
    for (const name of canonicalValidationChecks) {
      for (const [reason, mutate] of [
        ['missing job', (f, job) => { f.data.jobs[10] = f.data.jobs[10].filter(item => item !== job); }],
        ['duplicate job', (f, job) => { f.data.jobs[10].push({ ...job }); }],
        ['failed job', (f, job) => { job.conclusion = 'failure'; }],
        ['cancelled job', (f, job) => { job.conclusion = 'cancelled'; }],
        ['selected but skipped job', (f, job) => { job.conclusion = 'skipped'; }],
        ['wrong job SHA', (f, job) => { job.head_sha = 'c'.repeat(40); }],
        ['wrong run job', (f, job) => { job.run_id = 11; }],
        ['rerun job', (f, job) => { job.run_attempt = 2; }],
        ['missing check', (f, job, check) => { f.data.checks = f.data.checks.filter(item => item !== check); }],
        ['duplicate check', (f, job, check) => { f.data.checks.push({ ...check }); }],
        ['external suite check', (f, job, check) => { check.check_suite.id = 999; }],
        ['wrong integration', (f, job, check) => { check.app.id = 999; }],
        ['foreign App', (f, job, check) => { check.app.slug = 'other'; }],
        ['wrong check SHA', (f, job, check) => { check.head_sha = 'c'.repeat(40); }],
        ['wrong check URL', (f, job, check) => { check.url += '9'; }],
        ['stale check', (f, job, check) => { check.status = 'in_progress'; }],
        ['failed check', (f, job, check) => { check.conclusion = 'failure'; }],
        ['cancelled check', (f, job, check) => { check.conclusion = 'cancelled'; }],
        ['wrong workflow', f => { f.data.runs[10].path = '.github/workflows/ios-pr-ci.yml'; }],
        ['missing run', f => { delete f.data.runs[10]; }],
        ['stale run', f => { f.data.runs[10].created_at = '2026-09-11T05:00:00Z'; }],
        ['failed run', f => { f.data.runs[10].conclusion = 'failure'; }],
        ['cancelled run', f => { f.data.runs[10].conclusion = 'cancelled'; }],
        ['replay', f => { f.data.qualifications.push({ ...f.data.runs[20], id: 19 }); }],
      ]) {
        test(`${channel}/${mode}: ${name} rejects ${reason}`, async () => {
          const f = fixture(channel, mode);
          mutate(f, f.data.jobs[10].find(job => job.name === name), f.data.checks.find(check => check.name === name));
          await assert.rejects(verifyCanonicalReleaseEvidence(f.api, f.sha, channel, mode, now));
          assert.equal(f.posts.length, 0);
        });
      }
    }
    for (const name of [...releaseBuildChecks, ...canonicalValidationChecks, releaseReviewStatus]) {
      test(`${channel}/${mode}: removing required policy ${name} blocks verification, writing and consumption`, async () => {
        const f = fixture(channel, mode);
        f.data.rules[0].parameters.required_status_checks =
          f.data.rules[0].parameters.required_status_checks.filter(rule => rule.context !== name);
        const error = /live policy must require canonical review, all release build checks and all canonical validation checks/;
        await assert.rejects(verifyQualification(f.api, '20', mode, now), error);
        await assert.rejects(verifyCanonicalReleaseEvidence(f.api, f.sha, channel, mode, now), error);
        assert.equal(f.posts.length, 0, 'verification and consumption remain read-only');
        f.data.runs[30].status = 'in_progress';
        await assert.rejects(recordQualification(f.api, '20', f.env, now), error);
        assert.deepEqual(f.posts, [{ endpoint: `statuses/${f.sha}`, body: {
          context: releaseReviewStatus, state: 'failure', target_url: url(30),
          description: `BLOCKED canonical qualification @ ${f.sha.slice(0, 12)}`,
        } }], 'green jobs/checks cannot compensate for weakened live policy');
      });
    }
    test(`${channel}/${mode}: head movement after reading canonical checks rejects`, async () => {
      const f = fixture(channel, mode);
      const api = async (...args) => {
        const response = await f.api(...args);
        if (args[0].endsWith('/check-runs?per_page=100')) {
          f.data.heads[channel === 'stable' ? 'main' : 'development'] = 'c'.repeat(40);
        }
        return response;
      };
      await assert.rejects(verifyQualification(api, '20', mode, now), /HEAD moved/);
      assert.equal(f.posts.length, 0);
    });
  }
}

const invalid = [
  ['non-canonical SHA', f => { f.data.runs[10].head_sha = 'c'.repeat(40); }],
  ['swapped branch', f => { f.data.runs[10].head_branch = 'development'; }],
  ['feature workflow', f => { f.data.runs[20].head_branch = 'feature'; }],
  ['old default workflow', f => { f.data.runs[20].head_sha = 'c'.repeat(40); }],
  ['fork CI', f => { f.data.runs[10].head_repository.full_name = 'outsider/PrintFarmer'; }],
  ['forged workflow ID', f => { f.data.runs[10].workflow_id = 7; }],
  ['disabled workflow', f => { f.data.definitions['ci.yml'].state = 'disabled_manually'; }],
  ['repository dispatch', f => { f.data.runs[20].event = 'repository_dispatch'; }],
  ['push qualification', f => { f.data.runs[20].event = 'push'; }],
  ['workflow-run qualification', f => { f.data.runs[20].event = 'workflow_run'; }],
  ['PR CI replay', f => { f.data.runs[10].event = 'pull_request'; }],
  ['push CI not fresh manual execution', f => { f.data.runs[10].event = 'push'; }],
  ['CI rerun', f => { f.data.runs[10].run_attempt = 2; }],
  ['qualifier rerun', f => { f.data.runs[20].run_attempt = 2; }],
  ['changed actor', f => { f.data.runs[20].triggering_actor.login = 'other'; }],
  ['failed CI', f => { f.data.runs[10].conclusion = 'failure'; }],
  ['cancelled qualification', f => { f.data.runs[20].conclusion = 'cancelled'; }],
  ['partial checks', f => { f.data.jobs[10].pop(); f.data.jobs[10].shift(); }],
  ['skipped required checks', f => { f.data.jobs[10][0].conclusion = 'skipped'; }],
  ['failed optional check', f => { f.data.jobs[10].at(-1).conclusion = 'failure'; }],
  ['foreign job SHA', f => { f.data.jobs[10][0].head_sha = 'c'.repeat(40); }],
  ['job attempt reuse', f => { f.data.jobs[10][0].run_attempt = 2; }],
  ['duplicate job names', f => { f.data.jobs[10].push(f.data.jobs[10][0]); }],
  ['missing required branch policy', f => { f.data.rules = []; }],
  ['non-strict checks', f => { f.data.rules[0].parameters.strict_required_status_checks_policy = false; }],
  ['missing mandatory review policy', f => { f.data.rules[0].parameters.required_status_checks.pop(); }],
  ['additional missing configured check', f => { f.data.rules[0].parameters.required_status_checks.push({ context: 'extra' }); }],
  ['insufficient check integration evidence', f => { f.data.rules[0].parameters.required_status_checks[0].integration_id = 123; }],
  ['copied checks from another run', f => { f.data.checks[0].check_suite.id = 999; }],
  ['check URL not bound to CI job', f => { f.data.checks[0].url = 'https://example.com'; }],
  ['expired CI', f => { f.data.runs[10].created_at = '2026-09-11T05:00:00Z'; }],
  ['CI newer than review run', f => { f.data.runs[10].updated_at = '2026-09-13T07:30:00Z'; }],
  ['newer failed CI', f => { f.data.ciRuns.push({ ...f.data.runs[10], id: 11, conclusion: 'failure' }); }],
  ['missing comment', f => { f.data.comments = []; }],
  ['stale comment SHA', f => { f.data.comments[0].commit_id = 'c'.repeat(40); }],
  ['stale comment run', f => { f.data.comments[0].body = confirmationBody(f.sha, '9', f.mode); }],
  ['copied PR verdict', f => { f.data.comments[0].body = `<!-- squad-verdict -->\nSquad-Head-SHA: ${f.sha}`; }],
  ['edited confirmation', f => { f.data.comments[0].updated_at = '2026-09-13T06:40:00Z'; }],
  ['predated confirmation', f => { f.data.comments[0].created_at = f.data.comments[0].updated_at = '2026-09-13T05:30:00Z'; }],
  ['quoted confirmation', f => { f.data.comments[0].body = `> ${f.data.comments[0].body}`; }],
  ['non-owner confirmation', f => { f.data.comments[0].user.login = 'reviewer'; }],
  ['bot confirmation', f => { f.data.comments[0].user.type = 'Bot'; }],
  ['lost admin permission', f => { f.data.permissions.jpapiez = 'write'; }],
  ['no permission', f => { f.data.permissions.jpapiez = 'read'; }],
  ['replayed CI', f => { f.data.qualifications.push({ ...f.data.runs[20], id: 19 }); }],
  ['superseding qualification', f => { f.data.qualifications.push({ ...f.data.runs[20], id: 21,
    display_title: qualificationTitle(f.channel, '11', '51', '0', f.mode) }); }],
];
for (const [name, mutate] of invalid) {
  test(`reject ${name}`, async () => {
    const f = fixture();
    mutate(f);
    await assert.rejects(verifyQualification(f.api, '20', f.mode, now));
    assert.equal(f.posts.length, 0);
  });
}

for (const mode of [undefined, '', 'unknown', 'SINGLE-MAINTAINER']) {
  test(`missing/unknown mode ${String(mode)} fails before API or writes`, async () => {
    const f = fixture();
    await assert.rejects(verifyQualification(f.api, '20', mode, now));
    assert.equal(f.calls.length, 0);
  });
}

for (const [name, mutate] of [
  ['self-authored native PR', f => { f.data.pr.user.login = 'reviewer'; }],
  ['native reviewer is initiator', f => { f.data.runs[10].actor.login = 'reviewer'; }],
  ['native reviewer is qualifier', f => {
    f.data.runs[20].actor.login = f.data.runs[20].triggering_actor.login = 'reviewer';
  }],
  ['case-variant PR author is reviewer', f => { f.data.pr.user.login = 'Reviewer'; }],
  ['case-variant CI actor is reviewer', f => { f.data.runs[10].actor.login = 'Reviewer'; }],
  ['case-variant qualifier is reviewer', f => {
    f.data.runs[20].actor.login = f.data.runs[20].triggering_actor.login = 'Reviewer';
    f.data.permissions.Reviewer = 'write';
  }],
  ['squash predecessor', f => { f.data.pr.head.sha = 'c'.repeat(40); }],
  ['stale native review', f => { f.data.reviews[0].commit_id = 'c'.repeat(40); }],
  ['native approval before CI', f => { f.data.reviews[0].submitted_at = '2026-09-13T05:00:00Z'; }],
  ['native approval after confirmation', f => { f.data.reviews[0].submitted_at = '2026-09-13T06:45:00Z'; }],
  ['missing native review', f => { f.data.reviews = []; }],
  ['non-code-owner', f => { f.data.owners = '* @someone-else'; }],
  ['unsupported complex code ownership', f => { f.data.owners = '/src/ @reviewer'; }],
  ['native change request', f => { f.data.reviews.push({ ...f.data.reviews[0], id: 71, state: 'CHANGES_REQUESTED' }); }],
  ['dismissed approval', f => { f.data.reviews.push({ ...f.data.reviews[0], id: 71, state: 'DISMISSED' }); }],
  ['case-variant dismissal', f => {
    f.data.reviews.push({ ...f.data.reviews[0], id: 71, user: { login: 'Reviewer' }, state: 'DISMISSED' });
  }],
  ['no native reviewer permission', f => { f.data.permissions.reviewer = 'triage'; }],
]) {
  test(`separation-of-duties rejects ${name}`, async () => {
    const f = fixture('stable', 'separation-of-duties');
    mutate(f);
    await assert.rejects(verifyQualification(f.api, '20', f.mode, now));
  });
}

for (const [role, assign] of [
  ['PR author', (f, user) => { f.data.pr.user = user; }],
  ['CI actor', (f, user) => { f.data.runs[10].actor = user; }],
  ['qualifier', (f, user) => { f.data.runs[20].actor = user; }],
  ['triggering qualifier', (f, user) => { f.data.runs[20].triggering_actor = user; }],
  ['confirmation reviewer', (f, user) => { f.data.comments[0].user = user; }],
  ['native reviewer', (f, user) => { f.data.reviews[0].user = user; }],
]) {
  for (const [name, user] of [
    ['null identity', null],
    ['missing identity', undefined],
    ['missing login', { type: 'User' }],
    ...[null, '', ' ', ' reviewer', 'reviewer ', 'reviewer\n', 123, {}, [],
      '-reviewer', 'reviewer-', 're--viewer', 'review_er', 'a'.repeat(40)]
      .map(login => [`invalid login ${JSON.stringify(login)}`, { login, type: 'User' }]),
  ]) {
    test(`separation-of-duties rejects ${role}: ${name}`, async () => {
      const f = fixture('stable', 'separation-of-duties');
      assign(f, user);
      await assert.rejects(verifyQualification(f.api, '20', f.mode, now),
        /Invalid .*login|Invalid review account|Missing, edited, stale or mismatched canonical review/);
      assert.equal(f.posts.length, 0);
    });
  }
}

test('separation-of-duties allows distinct author/initiators and case-variant native evidence', async () => {
  const f = fixture('stable', 'separation-of-duties');
  f.data.pr.user.login = 'author';
  f.data.runs[10].actor.login = 'ci-initiator';
  f.data.owners = '* @Reviewer\n';
  f.data.reviews[0].user.login = 'REVIEWER';
  f.data.runs[20].triggering_actor.login = 'JPAPIEZ';
  assert.equal((await verifyQualification(f.api, '20', f.mode, now)).sourceCommit, f.sha);
  assert.equal(f.posts.length, 0);
});

for (const [role, mutate] of [
  ['PR author', f => { f.data.pr.user = null; }],
  ['CI actor', f => { f.data.runs[10].actor = null; }],
]) {
  test(`unknown ${role} blocks release consumption and writes only bounded qualification failure`, async () => {
    const f = fixture('stable', 'separation-of-duties');
    mutate(f);
    await assert.rejects(verifyCanonicalReleaseEvidence(f.api, f.sha, f.channel, f.mode, now),
      /Invalid .*login/);
    assert.equal(f.posts.length, 0);
    f.data.runs[30].status = 'in_progress';
    await assert.rejects(recordQualification(f.api, '20', f.env, now), /Invalid .*login/);
    assert.equal(f.posts.length, 1);
    assert.equal(f.posts[0].body.state, 'failure');
    assert.equal(f.posts[0].endpoint, `statuses/${f.sha}`);
    assert.doesNotMatch(JSON.stringify(f.posts), /jpapiez|reviewer|PR author|CI actor/);
  });
}

test('HEAD movement during review fails closed', async () => {
  const f = fixture();
  const api = async (...args) => {
    const response = await f.api(...args);
    if (args[0].endsWith('/comments?per_page=100')) f.data.heads.main = 'c'.repeat(40);
    return response;
  };
  await assert.rejects(verifyQualification(api, '20', f.mode, now), /HEAD moved/);
});

test('failed/cancelled validation replaces success with bounded failure, never publication', async () => {
  const f = fixture();
  f.data.runs[30].status = 'in_progress';
  f.data.runs[20].conclusion = 'cancelled';
  await assert.rejects(recordQualification(f.api, '20', f.env, now));
  assert.equal(f.posts.length, 1);
  assert.equal(f.posts[0].body.state, 'failure');
  assert.equal(f.posts[0].endpoint, `statuses/${f.sha}`);
});

test('movement during final POST retracts the status', async () => {
  const f = fixture();
  f.data.runs[30].status = 'in_progress';
  const api = async (...args) => {
    const response = await f.api(...args);
    if (args[1] === 'POST' && args[2].state === 'success') f.data.heads.main = 'c'.repeat(40);
    return response;
  };
  await assert.rejects(recordQualification(api, '20', f.env, now), /HEAD moved/);
  assert.deepEqual(f.posts.map(post => post.body.state), ['success', 'failure']);
});

for (const [name, mutate] of [
  ['forged status creator', f => { f.data.statuses[0].creator.login = 'someone'; }],
  ['forged status URL', f => { f.data.statuses[0].target_url = 'https://example.com'; }],
  ['PR verdict replay', f => { f.data.statuses[0].description = `REVIEWED (self-attested) @ ${f.sha.slice(0, 12)} by bishop`; }],
  ['wrong writer path', f => { f.data.runs[30].path = '.github/workflows/ci.yml'; }],
  ['cancelled writer after POST', f => { f.data.runs[30].conclusion = 'cancelled'; }],
  ['writer still running', f => { f.data.runs[30].status = 'in_progress'; }],
  ['writer rerun', f => { f.data.runs[30].run_attempt = 2; }],
  ['writer from feature', f => { f.data.runs[30].head_branch = 'feature'; }],
  ['status outside writer lifetime', f => { f.data.statuses[0].created_at = '2026-09-13T05:00:00Z'; }],
  ['missing writer job', f => { f.data.jobs[30] = []; }],
]) {
  test(`release consumption rejects ${name}`, async () => {
    const f = fixture();
    mutate(f);
    await assert.rejects(verifyCanonicalReleaseEvidence(f.api, f.sha, f.channel, f.mode, now));
  });
}

test('API errors and truncated evidence do not qualify', async () => {
  const f = fixture();
  for (const api of [
    async () => { throw new Error('HTTP 403'); },
    async (...args) => args[0].includes('/jobs?') ? { total_count: 100, jobs: [] } : f.api(...args),
  ]) await assert.rejects(verifyQualification(api, '20', f.mode, now));
});

test('qualification API cannot reserve, deploy, publish, dispatch, or write arbitrary statuses', () => {
  for (const [route, method] of [
    ['git/refs', 'POST'], ['git/refs/heads/release-ledger', 'PATCH'], ['releases', 'POST'],
    ['deployments', 'POST'], ['actions/workflows/ci.yml/dispatches', 'POST'],
    ['statuses/not-a-sha', 'POST'], [`statuses/${stableSha}`, 'GET'],
    ['../actions', 'GET'], ['https://example.com', 'GET'],
  ]) assert.throws(() => qualificationRequestUrl(route, method, true));
  assert.throws(() => qualificationRequestUrl(`statuses/${stableSha}`, 'POST'));
});

test('malformed run titles and unknown/native mode combinations fail closed', () => {
  assert.throws(() => parseQualificationTitle('Squad review record for PR #1'));
  assert.throws(() => qualificationTitle('stable', '10\n', '50', '0', 'single-maintainer'));
  assert.throws(() => qualificationTitle('stable', '10', '50', '0', 'separation-of-duties'));
  assert.throws(() => qualificationTitle('insider', '10', '50', '60', 'single-maintainer'));
});

test('workflow trust/permissions and release gates remain separate from publishing', () => {
  const qualify = load(read('.github/workflows/qualify-canonical-release.yml'));
  const writer = load(read('.github/workflows/record-canonical-qualification.yml'));
  assert.deepEqual(Object.keys(qualify.on), ['workflow_dispatch']);
  assert.deepEqual(Object.keys(writer.on), ['workflow_run']);
  assert.equal(qualify.permissions.contents, 'read');
  assert.equal(qualify.jobs.verify.permissions.statuses, undefined);
  assert.deepEqual(Object.entries(writer.jobs.record.permissions).filter(([, value]) => value === 'write'),
    [['statuses', 'write']]);
  for (const [path, workflow] of [[qualificationWorkflow, qualify], [evidenceWorkflow, writer]]) {
    assert.doesNotMatch(read(path), /secrets\.|secrets:|environment:|create-github-app-token|download-artifact|npm |packages:|id-token:/);
    for (const job of Object.values(workflow.jobs)) {
      const checkout = job.steps.find(step => step.uses?.startsWith('actions/checkout@'));
      assert.equal(checkout.with.ref, '${{ github.workflow_sha }}');
      assert.equal(checkout.with['persist-credentials'], false);
    }
  }
  const control = read('scripts/ci/release-control.mjs');
  assert.equal((control.match(/await verifyCanonicalReleaseEvidence/g) ?? []).length, 2);
  assert.match(control, /qualificationClient\(env\.GH_TOKEN\)/);
  assert.match(read('.github/workflows/ci.yml'), /test-canonical-qualification\.mjs/);
});

test('manual CI executes equivalent required checks without shadowing PR check names', () => {
  const ci = load(read('.github/workflows/ci.yml'));
  const path = load(read('.github/workflows/enforce-path-casing.yml')).jobs['path-casing'];
  const drift = load(read('.github/workflows/contract-drift.yml')).jobs['contract-drift'];
  const ios = load(read('.github/workflows/ios-pr-ci.yml')).jobs.build;
  const expected = [
    ['canonical-path-casing', 'path-casing', path, 'Canonical path casing (not selected)'],
    ['canonical-contract-drift', 'Contract drift gate', drift, 'Canonical contract drift (not selected)'],
    ['canonical-ios-build', 'Build (iOS)', ios, 'Canonical iOS build (not selected)'],
  ];
  assert.deepEqual(ci.permissions, { contents: 'read' });
  for (const [id, name, original, skippedName] of expected) {
    const job = ci.jobs[id];
    assert.equal(job.if, canonicalDispatch);
    assert.equal(job.name, `\${{ ${canonicalDispatch} && '${name}' || '${skippedName}' }}`);
    assert.equal(job['runs-on'], original['runs-on']);
    assert.ok(Number.isInteger(job['timeout-minutes']) && job['timeout-minutes'] > 0 && job['timeout-minutes'] <= 45);
    assert.ok(ci.jobs.summary.needs.includes(id));
    assert.equal(job.permissions, undefined);
    assert.equal(job.environment, undefined);
    assert.equal(job['continue-on-error'], undefined);
    const checkout = job.steps.find(step => step.uses?.startsWith('actions/checkout@'));
    assert.equal(checkout.with.ref, '${{ github.sha }}');
    assert.equal(checkout.with['persist-credentials'], false);
    for (const step of job.steps.filter(step => step.uses)) {
      assert.match(step.uses, /@[a-f0-9]{40}$/);
      if (step.uses.startsWith('actions/setup-node@')) assert.equal(step.with['package-manager-cache'], false);
    }
    assert.doesNotMatch(JSON.stringify(job), forbiddenJobCapabilities);
    for (const step of original.steps.filter(step => step.run &&
      !['Report skip reason', 'Compute change set', 'Fail closed if the diff could not be computed'].includes(step.name))) {
      const equivalent = job.steps.find(candidate => candidate.name === step.name);
      const { if: selection, ...unconditional } = step;
      assert.deepEqual(equivalent, unconditional, `${name}: actual execution must match ${step.name}`);
    }
  }
  const diff = ci.jobs['canonical-contract-drift'].steps.find(step => step.id === 'diff');
  assert.equal(diff.run, 'bash scripts/ci/compute-change-set.sh');
  assert.equal(diff.env.EVENT_NAME, 'push');
  assert.equal(diff.env.BEFORE_SHA, '${{ github.sha }}^1');
  assert.equal(diff.env.AFTER_SHA, '${{ github.sha }}');
  const guard = ci.jobs['canonical-contract-drift'].steps.find(step => step.if);
  assert.equal(guard.if, "steps.diff.outputs.force_full_safe != ''");
  assert.match(guard.run, /exit 1/);
  const iosBuild = ci.jobs['canonical-ios-build'];
  assert.deepEqual(iosBuild.defaults, ios.defaults);
  assert.ok(iosBuild.steps.every(step => !step.if && !step['continue-on-error']));
  const summary = ci.jobs.summary.steps.find(step => step.name === 'Require all manual canonical checks');
  assert.equal(summary.if, canonicalDispatch);
  for (const key of ['PATH_CASING_RESULT', 'CONTRACT_DRIFT_RESULT', 'IOS_BUILD_RESULT']) {
    assert.ok(summary.run.includes(`test "$${key}" = success`));
  }
});

test('canonical job privilege matcher detects package permissions in parsed YAML', () => {
  for (const permission of ['read', 'write']) {
    const job = load(`permissions:\n  packages: ${permission}\n`);
    assert.match(JSON.stringify(job), forbiddenJobCapabilities);
  }
});

for (const ref of [
  'refs/heads/main', 'refs/heads/development', 'refs/heads/feature/pr-head',
  'refs/heads/release/v1.2.3', 'refs/heads/main-feature', 'refs/heads/development/feature',
  'refs/tags/main', 'refs/tags/development', 'refs/pull/2688/head', 'refs/pull/2688/merge',
]) {
  for (const event of ['workflow_dispatch', 'pull_request', 'push']) {
    test(`${event} on ${ref} cannot shadow required PR contexts outside canonical dispatch`, () => {
      const ci = load(read('.github/workflows/ci.yml'));
      const selected = event === 'workflow_dispatch' && ['refs/heads/main', 'refs/heads/development'].includes(ref);
      // These conditions use only JS-compatible equality/boolean operators.
      const evaluate = expression => runInNewContext(expression.replace(/^\$\{\{\s*|\s*\}\}$/g, ''),
        { github: { event_name: event, ref, sha: stableSha } });
      for (const id of ['canonical-path-casing', 'canonical-contract-drift', 'canonical-ios-build']) {
        const job = ci.jobs[id];
        assert.equal(evaluate(job.if), selected, `${id}: execution guard`);
        const name = evaluate(job.name);
        assert.equal(canonicalValidationChecks.includes(name), selected, `${id}: emitted check name`);
        if (!selected) assert.match(name, /^Canonical .+ \(not selected\)$/);
      }
      const summary = ci.jobs.summary.steps.find(step => step.name === 'Require all manual canonical checks');
      assert.equal(evaluate(summary.if), selected, 'manual summary follows the same ref boundary');
    });
  }
}

test('manual summary executes fail-closed for failed, cancelled, missing or skipped canonical checks', t => {
  const cwd = scratch(t);
  const ci = load(read('.github/workflows/ci.yml'));
  const summary = ci.jobs.summary.steps.find(step => step.name === 'Require all manual canonical checks');
  const base = { PATH_CASING_RESULT: 'success', CONTRACT_DRIFT_RESULT: 'success', IOS_BUILD_RESULT: 'success' };
  const execute = overrides => spawnSync(shell, ['-e', '-o', 'pipefail', '-s'], {
    cwd, input: summary.run, encoding: 'utf8',
    env: { ...process.env, ...base, ...overrides, GITHUB_STEP_SUMMARY: 'summary.md' },
  });
  assert.equal(execute({}).status, 0);
  for (const key of Object.keys(base)) {
    for (const value of ['failure', 'cancelled', 'skipped', '', 'unknown']) {
      assert.notEqual(execute({ [key]: value }).status, 0, `${key}=${value}`);
    }
  }
});

test('manual contract gate examines real first-parent fixture changes and rejects missing ancestry', t => {
  const cwd = scratch(t);
  const git = args => execFileSync('git', args, { cwd, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }).trim();
  git(['init']);
  const commit = message => git(['-c', 'user.name=Fixture', '-c', 'user.email=fixture@example.test',
    '-c', 'core.hooksPath=', 'commit', '-m', message]);
  writeFileSync(join(cwd, 'baseline'), 'baseline');
  git(['add', '.']);
  commit('baseline');
  const baseline = git(['rev-parse', 'HEAD']);
  const changedPath = 'fixtures/wire-contracts/api/tasks/tasks.populated.json';
  mkdirSync(join(cwd, 'fixtures', 'wire-contracts', 'api', 'tasks'), { recursive: true });
  writeFileSync(join(cwd, ...changedPath.split('/')), '{}');
  git(['add', '.']);
  commit('fixture-only squash change');
  const sha = git(['rev-parse', 'HEAD']);
  const ci = load(read('.github/workflows/ci.yml'));
  const steps = ci.jobs['canonical-contract-drift'].steps;
  const diff = steps.find(step => step.id === 'diff');
  const runDiff = head => {
    const env = Object.fromEntries(Object.entries(diff.env).map(([key, value]) =>
      [key, value.replaceAll('${{ github.sha }}', head)]));
    writeFileSync(join(cwd, 'outputs'), '');
    return spawnSync(shell, ['-s'], {
      cwd, encoding: 'utf8', input: read('scripts/ci/compute-change-set.sh'),
      env: { ...process.env, ...env, OUT_FILE: 'changed.z', GITHUB_OUTPUT: 'outputs' },
    });
  };
  assert.equal(runDiff(sha).status, 0);
  assert.equal(readFileSync(join(cwd, 'changed.z'), 'utf8'), `${changedPath}\0`);
  assert.match(readFileSync(join(cwd, 'outputs'), 'utf8'), /force_full_safe=\r?\n/);
  const gate = spawnSync(process.execPath, ['scripts/ci/check-contract-drift.mjs'], {
    cwd: root, encoding: 'utf8',
    env: { ...process.env, CONTRACT_DRIFT_CHANGED_FILE: join(cwd, 'changed.z') },
  });
  assert.equal(gate.status, 1, 'fixture-only canonical change must not be silently green');
  assert.match(gate.stdout + gate.stderr, /no accompanying producer-side change/);
  assert.equal(runDiff(baseline).status, 0);
  assert.match(readFileSync(join(cwd, 'outputs'), 'utf8'), /force_full_safe=diff-failed/);
  const guard = steps.find(step => step.if);
  const rejected = spawnSync(shell, ['-s'], {
    cwd, encoding: 'utf8', input: guard.run, env: { ...process.env, FORCE_FULL_SAFE: 'diff-failed' },
  });
  assert.equal(rejected.status, 1);
});

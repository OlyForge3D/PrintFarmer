import assert from 'node:assert/strict';
import { execFileSync, spawn } from 'node:child_process';
import { mkdtemp, mkdir, readFile, rm, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';

const worker = path.resolve('scripts/ci/ralph-macos-worker.mjs');
const suiteRoot = await mkdtemp(path.join(os.tmpdir(), 'ralph-worker-'));
const fixtureScript = path.join(suiteRoot, 'fake-copilot.mjs');
const exactCleanupPids = new Set();

await writeFile(fixtureScript, `
import { appendFileSync, writeFileSync } from 'node:fs';
import { spawnSync } from 'node:child_process';

const invocation = {
  pid: process.pid,
  args: process.argv.slice(2),
  secretPresent: process.env.GH_TOKEN !== undefined || process.env.COPILOT_GITHUB_TOKEN !== undefined,
};
appendFileSync(process.env.RALPH_MAC_WORKER_FAKE_INVOCATIONS, JSON.stringify(invocation) + '\\n');
writeFileSync(process.env.RALPH_MAC_WORKER_FAKE_PID_FILE, String(process.pid));
process.stdout.write('fake child stdout must not contaminate protocol JSON\\n');
process.stderr.write('fake child stderr is isolated\\n');
await new Promise((resolve) => setTimeout(resolve, Number(process.env.RALPH_MAC_WORKER_FAKE_DELAY_MS || 0)));
if (process.env.RALPH_MAC_WORKER_FAKE_PUSH === 'true') {
  const pushed = spawnSync('git', ['push', '-u', 'origin', 'HEAD'], { cwd: process.cwd(), encoding: 'utf8' });
  if (pushed.status !== 0) {
    process.stderr.write(pushed.stderr);
    process.exit(91);
  }
}
if (process.env.RALPH_MAC_WORKER_FAKE_SIGNAL) {
  process.kill(process.pid, process.env.RALPH_MAC_WORKER_FAKE_SIGNAL);
}
process.exit(Number(process.env.RALPH_MAC_WORKER_FAKE_EXIT_CODE || 0));
`);

const baseJob = {
  jobId: 'worker-2605',
  fence: 1,
  repository: 'OlyForge3D/PrintFarmer',
  issue: 2605,
  owner: 'hudson',
  baseSha: 'a'.repeat(40),
  model: 'gpt-5.6-terra',
  effort: 'medium',
  agent: 'squad',
  acceptanceCriteria: ['Run the targeted process-boundary validation.'],
  charter: 'mobile/AGENTS.md',
};

function git(cwd, ...args) {
  return execFileSync('git', args, { cwd, encoding: 'utf8' }).trim();
}

async function createFixture(name) {
  const root = path.join(suiteRoot, name);
  const origin = path.join(root, 'origin.git');
  const repository = path.join(root, 'repo');
  const state = path.join(root, 'state');
  const worktrees = path.join(root, 'worktrees');
  const invocations = path.join(root, 'invocations.jsonl');
  const pidFile = path.join(root, 'fake.pid');
  await mkdir(root, { recursive: true });
  execFileSync('git', ['init', '--bare', origin], { stdio: 'ignore' });
  execFileSync('git', ['init', repository], { stdio: 'ignore' });
  await writeFile(path.join(repository, 'README.md'), 'fixture\n');
  execFileSync('git', ['-C', repository, 'add', '.'], { stdio: 'ignore' });
  execFileSync('git', ['-C', repository, '-c', 'user.name=Fixture', '-c', 'user.email=fixture@example.test', 'commit', '-m', 'fixture'], { stdio: 'ignore' });
  execFileSync('git', ['-C', repository, 'branch', '-M', 'development'], { stdio: 'ignore' });
  execFileSync('git', ['-C', repository, 'remote', 'add', 'origin', origin], { stdio: 'ignore' });
  execFileSync('git', ['-C', repository, 'push', '-u', 'origin', 'development'], { stdio: 'ignore' });
  const baseSha = git(repository, 'rev-parse', 'HEAD');
  const env = {
    ...process.env,
    GH_TOKEN: 'must-not-reach-child',
    COPILOT_GITHUB_TOKEN: 'must-not-reach-child',
    RALPH_MAC_WORKER_TEST_MODE: 'true',
    RALPH_MAC_WORKER_STATE_ROOT: state,
    RALPH_MAC_WORKER_REPOSITORY_ROOT: repository,
    RALPH_MAC_WORKER_WORKTREE_ROOT: worktrees,
    RALPH_MAC_WORKER_COPILOT_PATH: process.execPath,
    RALPH_MAC_WORKER_COPILOT_PREFIX_JSON: JSON.stringify([fixtureScript]),
    RALPH_MAC_WORKER_EXPECTED_ORIGIN: origin,
    RALPH_MAC_WORKER_BASE_REF: 'origin/development',
    RALPH_MAC_WORKER_EXPECTED_HOST: 'trusted-mac.local',
    RALPH_MAC_WORKER_TEST_HOSTNAME: 'trusted-mac.local',
    RALPH_MAC_WORKER_FAKE_INVOCATIONS: invocations,
    RALPH_MAC_WORKER_FAKE_PID_FILE: pidFile,
    RALPH_MAC_WORKER_FAKE_PUSH: 'true',
  };
  return {
    root,
    origin,
    repository,
    state,
    worktrees,
    invocations,
    pidFile,
    baseSha,
    env,
    job: { ...baseJob, jobId: `${name}-2605`, baseSha },
  };
}

function invoke(request, env) {
  return new Promise((resolve, reject) => {
    const child = spawn(process.execPath, [worker], { env, stdio: ['pipe', 'pipe', 'pipe'] });
    let stdout = '';
    let stderr = '';
    child.stdout.on('data', (chunk) => { stdout += chunk; });
    child.stderr.on('data', (chunk) => { stderr += chunk; });
    child.once('error', reject);
    child.once('close', (code) => resolve({ code, stdout, stderr }));
    child.stdin.end(typeof request === 'string' ? request : JSON.stringify(request));
  });
}

async function waitForRecord(file, predicate, description) {
  for (let attempt = 0; attempt < 300; attempt += 1) {
    try {
      const record = JSON.parse(await readFile(file, 'utf8'));
      if (predicate(record)) return record;
    } catch (error) {
      if (error.code !== 'ENOENT') throw error;
    }
    await new Promise((resolve) => setTimeout(resolve, 20));
  }
  throw new Error(`Timed out waiting for ${description}.`);
}

async function waitForInvocations(file, count) {
  for (let attempt = 0; attempt < 200; attempt += 1) {
    try {
      const lines = (await readFile(file, 'utf8')).trim().split(/\r?\n/).filter(Boolean);
      if (lines.length >= count) return lines.map((line) => JSON.parse(line));
    } catch (error) {
      if (error.code !== 'ENOENT') throw error;
    }
    await new Promise((resolve) => setTimeout(resolve, 20));
  }
  throw new Error(`Timed out waiting for ${count} fake Copilot invocation(s).`);
}

async function trackFixturePid(pidFile) {
  const pid = Number(await readFile(pidFile, 'utf8'));
  exactCleanupPids.add(pid);
  return pid;
}

test.after(async () => {
  for (const pid of exactCleanupPids) {
    try {
      process.kill(pid, 'SIGTERM');
    } catch (error) {
      if (error.code !== 'ESRCH') throw error;
    }
  }
  await rm(suiteRoot, { recursive: true, force: true });
});

test('worker survives dispatch exit, isolates output, pushes its deterministic branch, and requires verified terminal evidence', async () => {
  const fixture = await createFixture('lifecycle');
  const request = { version: 1, type: 'dispatch', job: fixture.job };
  const env = { ...fixture.env, RALPH_MAC_WORKER_FAKE_DELAY_MS: '300' };
  const accepted = await invoke(request, env);
  assert.equal(accepted.code, 0, accepted.stderr);
  assert.equal(accepted.stdout.trim().split(/\r?\n/).length, 1);
  const acknowledgement = JSON.parse(accepted.stdout);

  const running = await waitForRecord(
    path.join(fixture.state, `${fixture.job.jobId}.json`),
    (record) => record.state === 'running',
    'running state',
  );
  exactCleanupPids.add(running.pid);
  process.kill(running.pid, 0);
  assert.equal(running.branch, `ralph/${fixture.job.jobId}`);
  assert.equal(running.worktree, path.join(fixture.worktrees, fixture.job.jobId));

  const replay = await invoke({ ...request, type: 'reconcile' }, env);
  assert.equal(replay.code, 0, replay.stderr);
  const replayAcknowledgement = JSON.parse(replay.stdout);
  assert.equal(replayAcknowledgement.sessionId, acknowledgement.sessionId);
  assert.equal(replayAcknowledgement.fence, acknowledgement.fence);
  assert.equal(replayAcknowledgement.jobId, acknowledgement.jobId);
  assert.equal(replayAcknowledgement.type, 'accepted');

  const record = await waitForRecord(
    path.join(fixture.state, `${fixture.job.jobId}.json`),
    (value) => value.state === 'awaiting-terminal-evidence',
    'terminal evidence hold',
  );
  exactCleanupPids.delete(running.pid);
  assert.equal(record.processResult.exitCode, 0);
  assert.match(await readFile(record.stdoutPath, 'utf8'), /must not contaminate protocol JSON/);
  assert.match(await readFile(record.stderrPath, 'utf8'), /stderr is isolated/);
  const [invocation] = await waitForInvocations(fixture.invocations, 1);
  assert.equal(invocation.secretPresent, false);
  assert.deepEqual(invocation.args.slice(0, 2), ['-C', record.worktree]);
  assert.ok(invocation.args.includes('--agent'));
  assert.ok(invocation.args.includes('squad'));
  assert.ok(invocation.args.includes('--reasoning-effort'));
  assert.ok(invocation.args.includes('medium'));
  assert.equal(git(record.worktree, 'branch', '--show-current'), `ralph/${fixture.job.jobId}`);
  assert.equal(git(fixture.repository, 'ls-remote', 'origin', `refs/heads/${record.branch}`).split(/\s+/)[0], fixture.baseSha);

  assert.equal(JSON.parse(await readFile(path.join(fixture.state, `${fixture.job.jobId}.json`), 'utf8')).state, 'awaiting-terminal-evidence');
  const terminal = await invoke({ ...request, type: 'reconcile' }, env);
  assert.equal(terminal.code, 0, terminal.stderr);
  const attestation = JSON.parse(terminal.stdout);
  assert.equal(attestation.type, 'terminal');
  assert.equal(attestation.state, 'completed');
  assert.equal(attestation.workerVerified, true);
  assert.equal(attestation.exitCode, 0);
  assert.equal(attestation.headSha, fixture.baseSha);
  assert.equal(attestation.workingTreeClean, true);
  assert.equal(attestation.allCommitsPushed, true);
  assert.equal(attestation.sessionId, acknowledgement.sessionId);
  const repeated = await invoke({ ...request, type: 'reconcile' }, env);
  assert.deepEqual(JSON.parse(repeated.stdout), attestation);
});

test('concurrent duplicate dispatches launch Copilot exactly once', async () => {
  const fixture = await createFixture('concurrent');
  const request = { version: 1, type: 'dispatch', job: fixture.job };
  const env = { ...fixture.env, RALPH_MAC_WORKER_FAKE_DELAY_MS: '250' };
  const [first, second] = await Promise.all([invoke(request, env), invoke(request, env)]);
  assert.equal(first.code, 0, first.stderr);
  assert.equal(second.code, 0, second.stderr);
  assert.equal(JSON.parse(first.stdout).sessionId, JSON.parse(second.stdout).sessionId);
  await waitForRecord(
    path.join(fixture.state, `${fixture.job.jobId}.json`),
    (record) => record.state === 'awaiting-terminal-evidence',
    'single concurrent launch completion',
  );
  assert.equal((await waitForInvocations(fixture.invocations, 1)).length, 1);
});

test('a crash after launch intent remains ambiguous and reconcile never spawns Copilot', async () => {
  const fixture = await createFixture('launch-intent-crash');
  const env = { ...fixture.env, RALPH_MAC_WORKER_TEST_CRASH_AT: 'after-launch-intent' };
  const request = { version: 1, type: 'dispatch', job: fixture.job };
  const accepted = await invoke(request, env);
  assert.equal(accepted.code, 0, accepted.stderr);
  const recordFile = path.join(fixture.state, `${fixture.job.jobId}.json`);
  await waitForRecord(recordFile, (record) => record.state === 'launching', 'ambiguous launch state');
  const reconciled = await invoke({ ...request, type: 'reconcile' }, env);
  assert.equal(reconciled.code, 0, reconciled.stderr);
  await new Promise((resolve) => setTimeout(resolve, 200));
  await assert.rejects(() => readFile(fixture.invocations, 'utf8'), (error) => error.code === 'ENOENT');
  assert.equal(JSON.parse(await readFile(recordFile, 'utf8')).state, 'launching');
});

test('a crash after child spawn retains one live child and reconcile does not launch another', async () => {
  const fixture = await createFixture('spawn-crash');
  const env = {
    ...fixture.env,
    RALPH_MAC_WORKER_TEST_CRASH_AT: 'after-child-spawn-before-pid',
    RALPH_MAC_WORKER_FAKE_DELAY_MS: '2000',
  };
  const request = { version: 1, type: 'dispatch', job: fixture.job };
  const accepted = await invoke(request, env);
  assert.equal(accepted.code, 0, accepted.stderr);
  const [invocation] = await waitForInvocations(fixture.invocations, 1);
  const pid = await trackFixturePid(fixture.pidFile);
  assert.equal(invocation.pid, pid);
  process.kill(pid, 0);
  const reconciled = await invoke({ ...request, type: 'reconcile' }, env);
  assert.equal(reconciled.code, 0, reconciled.stderr);
  await new Promise((resolve) => setTimeout(resolve, 200));
  assert.equal((await waitForInvocations(fixture.invocations, 1)).length, 1);
  const record = JSON.parse(await readFile(path.join(fixture.state, `${fixture.job.jobId}.json`), 'utf8'));
  assert.equal(record.state, 'launching');
  assert.equal(record.pid, undefined);
  process.kill(pid, 'SIGTERM');
  exactCleanupPids.delete(pid);
});

test('nonzero Copilot exit cannot be converted into success by terminal prose', async () => {
  const fixture = await createFixture('nonzero');
  const env = { ...fixture.env, RALPH_MAC_WORKER_FAKE_EXIT_CODE: '9' };
  const accepted = await invoke({ version: 1, type: 'dispatch', job: fixture.job }, env);
  assert.equal(accepted.code, 0, accepted.stderr);
  const acknowledgement = JSON.parse(accepted.stdout);
  const record = await waitForRecord(
    path.join(fixture.state, `${fixture.job.jobId}.json`),
    (value) => value.state === 'awaiting-terminal-evidence',
    'nonzero process result',
  );
  assert.equal(record.processResult.exitCode, 9);

  const failed = await invoke({ version: 1, type: 'reconcile', job: fixture.job }, env);
  assert.equal(failed.code, 0, failed.stderr);
  const attestation = JSON.parse(failed.stdout);
  assert.equal(attestation.state, 'failed');
  assert.equal(attestation.workerVerified, true);
  assert.equal(attestation.exitCode, 9);
  assert.equal(attestation.sessionId, acknowledgement.sessionId);
});

test('exit 0 without a pushed branch remains held for terminal evidence', async () => {
  const fixture = await createFixture('unpushed');
  const env = { ...fixture.env, RALPH_MAC_WORKER_FAKE_PUSH: 'false' };
  const request = { version: 1, type: 'dispatch', job: fixture.job };
  const accepted = await invoke(request, env);
  assert.equal(accepted.code, 0, accepted.stderr);
  const recordFile = path.join(fixture.state, `${fixture.job.jobId}.json`);
  await waitForRecord(recordFile, (record) => record.state === 'awaiting-terminal-evidence', 'unpushed process result');
  const status = await invoke({ ...request, type: 'reconcile' }, env);
  assert.equal(status.code, 1);
  assert.match(status.stderr, /lacks clean, pushed Git evidence/);
  assert.equal(JSON.parse(await readFile(recordFile, 'utf8')).state, 'awaiting-terminal-evidence');
});

test('signal termination produces a worker-verified failure attestation', async () => {
  const fixture = await createFixture('signal');
  const env = { ...fixture.env, RALPH_MAC_WORKER_FAKE_SIGNAL: 'SIGTERM' };
  const request = { version: 1, type: 'dispatch', job: fixture.job };
  const accepted = await invoke(request, env);
  assert.equal(accepted.code, 0, accepted.stderr);
  const record = await waitForRecord(
    path.join(fixture.state, `${fixture.job.jobId}.json`),
    (value) => value.state === 'awaiting-terminal-evidence',
    'signal process result',
  );
  if (process.platform === 'win32') assert.notEqual(record.processResult.exitCode, 0);
  else assert.equal(record.processResult.signal, 'SIGTERM');
  const status = await invoke({ ...request, type: 'reconcile' }, env);
  assert.equal(status.code, 0, status.stderr);
  const attestation = JSON.parse(status.stdout);
  assert.equal(attestation.state, 'failed');
  if (process.platform === 'win32') assert.notEqual(attestation.exitCode, 0);
  else {
    assert.equal(attestation.signal, 'SIGTERM');
    assert.equal(attestation.exitCode, undefined);
  }
});

test('pre-launch failure reconciles as failure and never as accepted work', async () => {
  const fixture = await createFixture('prelaunch-failure');
  execFileSync('git', ['-C', fixture.repository, 'branch', `ralph/${fixture.job.jobId}`, fixture.baseSha], { stdio: 'ignore' });
  const request = { version: 1, type: 'dispatch', job: fixture.job };
  const dispatched = await invoke(request, fixture.env);
  assert.equal(dispatched.code, 1);
  const reconciled = await invoke({ ...request, type: 'reconcile' }, fixture.env);
  assert.equal(reconciled.code, 0, reconciled.stderr);
  const response = JSON.parse(reconciled.stdout);
  assert.equal(response.type, 'failed');
  assert.equal(response.state, 'failed');
  assert.equal(response.workerVerified, true);
  await assert.rejects(() => readFile(fixture.invocations, 'utf8'), (error) => error.code === 'ENOENT');
});

test('reclaims an old malformed worker lock without overlapping a live generation', async () => {
  const fixture = await createFixture('malformed-lock');
  const lockDirectory = path.join(fixture.state, '.locks');
  await mkdir(lockDirectory, { recursive: true });
  await writeFile(path.join(lockDirectory, `${fixture.job.jobId}.lock`), '{}');
  await new Promise((resolve) => setTimeout(resolve, 150));
  const dispatched = await invoke({ version: 1, type: 'dispatch', job: fixture.job }, fixture.env);
  assert.equal(dispatched.code, 0, dispatched.stderr);
  await waitForRecord(
    path.join(fixture.state, `${fixture.job.jobId}.json`),
    (record) => record.state === 'awaiting-terminal-evidence',
    'post-reclaim process result',
  );
  assert.equal((await waitForInvocations(fixture.invocations, 1)).length, 1);
});

test('rejects malformed requests, untrusted hosts, repositories, models, and bases before launch', async () => {
  const fixture = await createFixture('validation');
  const malformed = await invoke('{not-json', fixture.env);
  assert.equal(malformed.code, 1);
  assert.match(malformed.stderr, /one JSON object/);

  for (const changedJob of [
    { ...fixture.job, repository: 'OlyForge3D/PrintFarmerDesktop' },
    { ...fixture.job, model: 'gpt-5.6-sol' },
    { ...fixture.job, baseSha: 'b'.repeat(40) },
  ]) {
    const result = await invoke({ version: 1, type: 'dispatch', job: changedJob }, fixture.env);
    assert.equal(result.code, 1);
  }
  const wrongHost = await invoke(
    { version: 1, type: 'dispatch', job: { ...fixture.job, jobId: 'wrong-host-2605' } },
    { ...fixture.env, RALPH_MAC_WORKER_TEST_HOSTNAME: 'impostor.local' },
  );
  assert.equal(wrongHost.code, 1);
  assert.match(wrongHost.stderr, /trusted host/);
  await assert.rejects(() => readFile(fixture.invocations, 'utf8'), (error) => error.code === 'ENOENT');
});

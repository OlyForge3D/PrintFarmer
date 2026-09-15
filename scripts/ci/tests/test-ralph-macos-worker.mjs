import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
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
import { appendFileSync, rmSync, writeFileSync } from 'node:fs';
import { spawnSync } from 'node:child_process';

const invocation = {
  pid: process.pid,
  args: process.argv.slice(2),
  developerDirectory: process.env.DEVELOPER_DIR,
  secretPresent: process.env.GH_TOKEN !== undefined || process.env.COPILOT_GITHUB_TOKEN !== undefined,
};
appendFileSync(process.env.RALPH_MAC_WORKER_FAKE_INVOCATIONS, JSON.stringify(invocation) + '\\n');
writeFileSync(process.env.RALPH_MAC_WORKER_FAKE_PID_FILE, String(process.pid));
process.stdout.write('fake child stdout must not contaminate protocol JSON\\n');
process.stderr.write('fake child stderr is isolated\\n');
await new Promise((resolve) => setTimeout(resolve, Number(process.env.RALPH_MAC_WORKER_FAKE_DELAY_MS || 0)));
if (process.env.RALPH_MAC_WORKER_FAKE_UNRELATED_HISTORY === 'true') {
  const tree = spawnSync('git', ['write-tree'], { cwd: process.cwd(), encoding: 'utf8' });
  const root = spawnSync(
    'git',
    ['-c', 'user.name=Fixture', '-c', 'user.email=fixture@example.test', 'commit-tree', tree.stdout.trim()],
    { cwd: process.cwd(), encoding: 'utf8', input: 'unrelated root\\n' },
  );
  const reset = spawnSync('git', ['reset', '--hard', root.stdout.trim()], { cwd: process.cwd(), encoding: 'utf8' });
  if (tree.status !== 0 || root.status !== 0 || reset.status !== 0) process.exit(92);
}
if (process.env.RALPH_MAC_WORKER_FAKE_PUSH === 'true') {
  const pushed = spawnSync('git', ['push', '-u', 'origin', 'HEAD'], { cwd: process.cwd(), encoding: 'utf8' });
  if (pushed.status !== 0) {
    process.stderr.write(pushed.stderr);
    process.exit(91);
  }
}
if (process.env.RALPH_MAC_WORKER_FAKE_REWRITE_ORIGIN === 'true') {
  const rewritten = spawnSync(
    'git',
    ['remote', 'set-url', 'origin', process.env.RALPH_MAC_WORKER_FAKE_ROGUE_ORIGIN],
    { cwd: process.cwd(), encoding: 'utf8' },
  );
  const roguePush = spawnSync('git', ['push', '-u', 'origin', 'HEAD'], { cwd: process.cwd(), encoding: 'utf8' });
  if (rewritten.status !== 0 || roguePush.status !== 0) process.exit(93);
}
if (process.env.RALPH_MAC_WORKER_FAKE_DELETE_GIT === 'true') {
  rmSync(process.cwd() + '/.git', { force: true });
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
  expectedHost: 'trusted-mac.local',
  model: 'gpt-5.6-terra',
  effort: 'medium',
  agent: 'squad',
  acceptanceCriteria: ['Run the targeted process-boundary validation.'],
  charter: 'mobile/AGENTS.md',
};

function ledgerRequest(job, overrides = {}) {
  const requestDigest = createHash('sha256').update(JSON.stringify({
    issue: job.issue, owner: job.owner, baseSha: job.baseSha, expectedHost: job.expectedHost, model: job.model,
    effort: job.effort, agent: job.agent, acceptanceCriteria: job.acceptanceCriteria, charter: job.charter,
  })).digest('hex');
  return {
    version: 1, type: 'reconcile-ledger', job: {
      jobId: job.jobId, fence: job.fence, repository: job.repository, issue: job.issue,
      owner: job.owner, baseSha: job.baseSha, expectedHost: job.expectedHost,
      requestDigest, allowNoRecord: true, ...overrides,
    },
  };
}

function git(cwd, ...args) {
  return execFileSync('git', args, { cwd, encoding: 'utf8' }).trim();
}

async function createFixture(name) {
  const root = path.join(suiteRoot, name);
  const origin = path.join(root, 'origin.git');
  const rogueOrigin = path.join(root, 'rogue-origin.git');
  const repository = path.join(root, 'repo');
  const state = path.join(root, 'state');
  const worktrees = path.join(root, 'worktrees');
  const invocations = path.join(root, 'invocations.jsonl');
  const pidFile = path.join(root, 'fake.pid');
  await mkdir(root, { recursive: true });
  execFileSync('git', ['init', '--bare', origin], { stdio: 'ignore' });
  execFileSync('git', ['init', '--bare', rogueOrigin], { stdio: 'ignore' });
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
    DEVELOPER_DIR: '/Applications/Xcode.app/Contents/Developer',
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
    RALPH_MAC_WORKER_FAKE_ROGUE_ORIGIN: rogueOrigin,
  };
  return {
    root,
    origin,
    rogueOrigin,
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

function invoke(request, env, args = []) {
  return new Promise((resolve, reject) => {
    const child = spawn(process.execPath, [worker, ...args], { env, stdio: ['pipe', 'pipe', 'pipe'] });
    let stdout = '';
    let stderr = '';
    child.stdout.on('data', (chunk) => { stdout += chunk; });
    child.stderr.on('data', (chunk) => { stderr += chunk; });
    child.once('error', reject);
    child.once('close', (code) => resolve({ code, stdout, stderr }));
    child.stdin.end(typeof request === 'string' ? request : JSON.stringify(request));
  });
}

function processGone(pid) {
  try {
    process.kill(pid, 0);
    return false;
  } catch (error) {
    if (error.code === 'ESRCH') return true;
    throw error;
  }
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
  assert.ok(invocation.args.includes('--allow-all-tools'));
  assert.ok(invocation.args.includes('--allow-all-paths'));
  assert.equal(invocation.developerDirectory, '/Applications/Xcode.app/Contents/Developer');
  const prompt = invocation.args[invocation.args.indexOf('--prompt') + 1];
  assert.ok(prompt.split('\n')[0].startsWith(`Ralph launch token ${running.launchToken}.`));
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
  assert.equal(attestation.repositoryIdentityVerified, true);
  assert.equal(attestation.baseAncestor, true);
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

test('a crash after launch intent becomes a worker-verified failure after its lease', async () => {
  const fixture = await createFixture('launch-intent-crash');
  const env = { ...fixture.env, RALPH_MAC_WORKER_TEST_CRASH_AT: 'after-launch-intent' };
  const request = { version: 1, type: 'dispatch', job: fixture.job };
  const accepted = await invoke(request, env);
  assert.equal(accepted.code, 0, accepted.stderr);
  const recordFile = path.join(fixture.state, `${fixture.job.jobId}.json`);
  await waitForRecord(recordFile, (record) => record.state === 'launching', 'ambiguous launch state');
  await new Promise((resolve) => setTimeout(resolve, 150));
  const reconciled = await invoke({ ...request, type: 'reconcile' }, env);
  assert.equal(reconciled.code, 0, reconciled.stderr);
  const response = JSON.parse(reconciled.stdout);
  assert.equal(response.type, 'failed');
  assert.equal(response.failureCode, 'SUPERVISOR_LOST');
  await assert.rejects(() => readFile(fixture.invocations, 'utf8'), (error) => error.code === 'ENOENT');
  assert.equal(JSON.parse(await readFile(recordFile, 'utf8')).state, 'failed');
});

test('a crash after child spawn discovers and holds the fenced child until exact-PID termination', async () => {
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
  let record = JSON.parse(await readFile(path.join(fixture.state, `${fixture.job.jobId}.json`), 'utf8'));
  assert.equal(record.state, 'orphan-running');
  assert.equal(record.pid, pid);
  process.kill(pid, 'SIGTERM');
  exactCleanupPids.delete(pid);
  await new Promise((resolve) => setTimeout(resolve, 150));
  const terminal = await invoke({ ...request, type: 'reconcile' }, env);
  assert.equal(terminal.code, 0, terminal.stderr);
  const response = JSON.parse(terminal.stdout);
  assert.equal(response.type, 'failed');
  assert.equal(response.failureCode, 'SUPERVISOR_LOST');
  record = JSON.parse(await readFile(path.join(fixture.state, `${fixture.job.jobId}.json`), 'utf8'));
  assert.equal(record.state, 'failed');
});

test('a crash after preparing expires to failure without launching Copilot', async () => {
  const fixture = await createFixture('preparing-crash');
  const env = { ...fixture.env, RALPH_MAC_WORKER_TEST_CRASH_AT: 'after-preparing' };
  const request = { version: 1, type: 'dispatch', job: fixture.job };
  const crashed = await invoke(request, env);
  assert.equal(crashed.code, 85, crashed.stderr);
  await new Promise((resolve) => setTimeout(resolve, 150));
  const reconciled = await invoke({ ...request, type: 'reconcile' }, env);
  assert.equal(reconciled.code, 0, reconciled.stderr);
  const response = JSON.parse(reconciled.stdout);
  assert.equal(response.type, 'failed');
  assert.equal(response.failureCode, 'SUPERVISOR_LOST');
  await assert.rejects(() => readFile(fixture.invocations, 'utf8'), (error) => error.code === 'ENOENT');
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
  assert.equal(attestation.type, 'failed');
  assert.equal(attestation.state, 'failed');
  assert.equal(attestation.failureCode, 'COPILOT_EXIT_NONZERO');
  assert.equal(attestation.workerVerified, true);
  assert.equal(attestation.exitCode, 9);
  assert.equal(attestation.sessionId, acknowledgement.sessionId);
});

test('nonzero exit remains attestable after the child destroys Git inspection evidence', async () => {
  const fixture = await createFixture('nonzero-without-git');
  const env = {
    ...fixture.env,
    RALPH_MAC_WORKER_FAKE_EXIT_CODE: '9',
    RALPH_MAC_WORKER_FAKE_DELETE_GIT: 'true',
  };
  const request = { version: 1, type: 'dispatch', job: fixture.job };
  const accepted = await invoke(request, env);
  assert.equal(accepted.code, 0, accepted.stderr);
  await waitForRecord(
    path.join(fixture.state, `${fixture.job.jobId}.json`),
    (record) => record.state === 'awaiting-terminal-evidence',
    'nonzero result after Git destruction',
  );
  const failed = await invoke({ ...request, type: 'reconcile' }, env);
  assert.equal(failed.code, 0, failed.stderr);
  const response = JSON.parse(failed.stdout);
  assert.equal(response.type, 'failed');
  assert.equal(response.failureCode, 'COPILOT_EXIT_NONZERO');
  assert.equal(response.exitCode, 9);
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
  assert.match(status.stderr, /lacks clean, pushed, ancestry-bound Git evidence/);
  assert.equal(JSON.parse(await readFile(recordFile, 'utf8')).state, 'awaiting-terminal-evidence');
});

test('exit 0 cannot succeed after the child rewrites origin even when the trusted branch was pushed', async () => {
  const fixture = await createFixture('rewritten-origin');
  const env = { ...fixture.env, RALPH_MAC_WORKER_FAKE_REWRITE_ORIGIN: 'true' };
  const request = { version: 1, type: 'dispatch', job: fixture.job };
  const accepted = await invoke(request, env);
  assert.equal(accepted.code, 0, accepted.stderr);
  const recordFile = path.join(fixture.state, `${fixture.job.jobId}.json`);
  await waitForRecord(recordFile, (record) => record.state === 'awaiting-terminal-evidence', 'rewritten origin result');
  const status = await invoke({ ...request, type: 'reconcile' }, env);
  assert.equal(status.code, 1);
  assert.match(status.stderr, /ancestry-bound Git evidence/);
  assert.equal(JSON.parse(await readFile(recordFile, 'utf8')).state, 'awaiting-terminal-evidence');
});

test('exit 0 cannot succeed from unrelated history pushed to the admitted branch', async () => {
  const fixture = await createFixture('unrelated-history');
  const env = { ...fixture.env, RALPH_MAC_WORKER_FAKE_UNRELATED_HISTORY: 'true' };
  const request = { version: 1, type: 'dispatch', job: fixture.job };
  const accepted = await invoke(request, env);
  assert.equal(accepted.code, 0, accepted.stderr);
  const recordFile = path.join(fixture.state, `${fixture.job.jobId}.json`);
  await waitForRecord(recordFile, (record) => record.state === 'awaiting-terminal-evidence', 'unrelated history result');
  const status = await invoke({ ...request, type: 'reconcile' }, env);
  assert.equal(status.code, 1);
  assert.match(status.stderr, /ancestry-bound Git evidence/);
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
  assert.equal(attestation.type, 'failed');
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

test('publishes complete lock ownership atomically before a crash', async () => {
  const fixture = await createFixture('atomic-lock-publication');
  const request = { version: 1, type: 'reconcile', job: fixture.job };
  const crashed = await invoke(
    request,
    { ...fixture.env, RALPH_MAC_WORKER_TEST_CRASH_AT: 'after-lock-publication' },
  );
  assert.equal(crashed.code, 84, crashed.stderr);
  const lockFile = path.join(fixture.state, '.locks', `${fixture.job.jobId}.lock`);
  const lock = JSON.parse(await readFile(lockFile, 'utf8'));
  assert.match(lock.token, /^[0-9a-f-]{36}$/);
  assert.ok(Number.isInteger(lock.pid));
  assert.ok(Number.isFinite(Date.parse(lock.expiresAt)));
  await new Promise((resolve) => setTimeout(resolve, 150));
  const recovered = await invoke(request, fixture.env);
  assert.equal(recovered.code, 0, recovered.stderr);
  assert.equal(JSON.parse(recovered.stdout).failureCode, 'JOB_NOT_FOUND');
});

test('a lock owner cannot delete a replacement generation when it resumes', async () => {
  const fixture = await createFixture('lock-replacement');
  const lockFile = path.join(fixture.state, '.locks', `${fixture.job.jobId}.lock`);
  const request = { version: 1, type: 'reconcile', job: fixture.job };
  const invocation = invoke(
    request,
    { ...fixture.env, RALPH_MAC_WORKER_TEST_LOCK_HOLD_MS: '250' },
  );
  await waitForRecord(lockFile, (lock) => typeof lock.token === 'string', 'published worker lock');
  const replacement = {
    token: 'replacement-generation',
    pid: process.pid,
    createdAt: new Date().toISOString(),
    expiresAt: new Date(Date.now() + 60_000).toISOString(),
  };
  await rm(lockFile);
  await writeFile(lockFile, JSON.stringify(replacement));
  const result = await invocation;
  assert.equal(result.code, 0, result.stderr);
  assert.equal(JSON.parse(await readFile(lockFile, 'utf8')).token, replacement.token);
});

test('recovers stale reclaim guards and orphaned recovery claims before reclaiming a dead lock', async () => {
  const fixture = await createFixture('stale-reclaim-guard');
  const lockDirectory = path.join(fixture.state, '.locks');
  const lockFile = path.join(lockDirectory, `${fixture.job.jobId}.lock`);
  const expired = {
    token: 'dead-main', pid: 2147483647,
    createdAt: '2026-01-01T00:00:00Z', expiresAt: '2026-01-01T00:00:01Z',
  };
  const staleGuard = { ...expired, token: 'dead-guard' };
  const staleClaim = { ...expired, token: 'dead-claim' };
  await mkdir(lockDirectory, { recursive: true });
  await writeFile(lockFile, JSON.stringify(expired));
  await writeFile(`${lockFile}.reclaim`, JSON.stringify(staleGuard));
  await writeFile(`${lockFile}.reclaim.recover.token%3Adead-guard`, JSON.stringify(staleClaim));
  const dispatched = await invoke({ version: 1, type: 'dispatch', job: fixture.job }, fixture.env);
  assert.equal(dispatched.code, 0, dispatched.stderr);
  await waitForRecord(
    path.join(fixture.state, `${fixture.job.jobId}.json`),
    (record) => record.state === 'awaiting-terminal-evidence',
    'post-stale-guard recovery process result',
  );
  assert.equal((await waitForInvocations(fixture.invocations, 1)).length, 1);
});

test('reconcile attests absence without launching a missing job', async () => {
  const fixture = await createFixture('absent-reconcile');
  const result = await invoke({ version: 1, type: 'reconcile', job: fixture.job }, fixture.env);
  assert.equal(result.code, 0, result.stderr);
  const response = JSON.parse(result.stdout);
  assert.equal(response.type, 'failed');
  assert.equal(response.failureCode, 'JOB_NOT_FOUND');
  assert.equal(response.workerVerified, true);
  assert.equal(response.dispatchFenced, true);
  const delayed = await invoke({ version: 1, type: 'dispatch', job: fixture.job }, fixture.env);
  assert.equal(delayed.code, 1);
  assert.match(delayed.stderr, /terminally fenced/);
  await assert.rejects(() => readFile(fixture.invocations, 'utf8'), (error) => error.code === 'ENOENT');
});

test('ledger-only reconciliation verifies absence, rejects unknown host history and known sessions, never dispatches', async () => {
  const fixture = await createFixture('ledger-absent');
  const request = ledgerRequest(fixture.job);
  for (const override of [{ allowNoRecord: false }, { sessionId: 'known-session' }]) {
    const blocked = await invoke(ledgerRequest(fixture.job, override), fixture.env);
    assert.equal(blocked.code, 1);
    assert.match(blocked.stderr, /Missing worker record cannot prove/);
  }
  const result = await invoke(request, fixture.env);
  assert.equal(result.code, 0, result.stderr);
  assert.equal(JSON.parse(result.stdout).failureCode, 'JOB_NOT_FOUND');
  assert.equal(JSON.parse(result.stdout).requestDigest, request.job.requestDigest);
  const replay = await invoke(request, fixture.env);
  assert.deepEqual(JSON.parse(replay.stdout), JSON.parse(result.stdout));
  const tombstoneFile = path.join(fixture.state, `${fixture.job.jobId}.json`);
  const tombstone = await readFile(tombstoneFile, 'utf8');
  for (const override of [{ fence: 999 }, { requestDigest: 'e'.repeat(64) }]) {
    const mismatch = await invoke(ledgerRequest(fixture.job, override), fixture.env);
    assert.equal(mismatch.code, 1);
    assert.match(mismatch.stderr, /tombstone does not match/);
    assert.equal(await readFile(tombstoneFile, 'utf8'), tombstone);
  }
  const delayed = await invoke({ version: 1, type: 'dispatch', job: fixture.job }, fixture.env);
  assert.equal(delayed.code, 1);
  assert.match(delayed.stderr, /terminally fenced/);
  const residualFixture = await createFixture('ledger-residual');
  await mkdir(path.join(residualFixture.worktrees, residualFixture.job.jobId), { recursive: true });
  const residual = await invoke(ledgerRequest(residualFixture.job), residualFixture.env);
  assert.equal(residual.code, 1);
  assert.match(residual.stderr, /residual worktree or process evidence/);
  await assert.rejects(() => readFile(fixture.invocations, 'utf8'), (error) => error.code === 'ENOENT');
});

test('concurrent dispatch and ledger absence serialize: either retain launched work or permanently fence it', async () => {
  const fixture = await createFixture('ledger-race');
  const env = { ...fixture.env, RALPH_MAC_WORKER_FAKE_DELAY_MS: '300' };
  const [dispatch, status] = await Promise.all([
    invoke({ version: 1, type: 'dispatch', job: fixture.job }, env),
    invoke(ledgerRequest(fixture.job), env),
  ]);
  assert.equal(status.code, 0, status.stderr);
  const response = JSON.parse(status.stdout);
  if (response.failureCode === 'JOB_NOT_FOUND') {
    assert.equal(response.dispatchFenced, true);
    assert.equal(dispatch.code, 1);
    await assert.rejects(() => readFile(fixture.invocations, 'utf8'), (error) => error.code === 'ENOENT');
  } else {
    assert.equal(dispatch.code, 0, dispatch.stderr);
    assert.equal(response.type, 'accepted');
    await waitForRecord(path.join(fixture.state, `${fixture.job.jobId}.json`),
      (record) => record.state === 'awaiting-terminal-evidence', 'race winner process completion');
    assert.equal((await waitForInvocations(fixture.invocations, 1)).length, 1);
  }
});

test('ledger-only recovery binds original worker payload, digest, fence, session and host while preserving live work', async () => {
  const fixture = await createFixture('ledger-live');
  const env = { ...fixture.env, RALPH_MAC_WORKER_FAKE_DELAY_MS: '2000' };
  const accepted = await invoke({ version: 1, type: 'dispatch', job: fixture.job }, env);
  assert.equal(accepted.code, 0, accepted.stderr);
  const acknowledgement = JSON.parse(accepted.stdout);
  const recordFile = path.join(fixture.state, `${fixture.job.jobId}.json`);
  await waitForRecord(recordFile, (record) => record.state === 'running', 'live ledger recovery');
  await waitForInvocations(fixture.invocations, 1);
  await trackFixturePid(fixture.pidFile);
  const request = ledgerRequest(fixture.job, { sessionId: acknowledgement.sessionId });
  const live = await invoke(request, env);
  assert.equal(live.code, 0, live.stderr);
  assert.equal(JSON.parse(live.stdout).type, 'accepted');
  assert.equal(JSON.parse(live.stdout).requestDigest, request.job.requestDigest);
  for (const overrides of [
    { fence: 999 }, { requestDigest: 'f'.repeat(64) }, { sessionId: 'wrong-session' },
    { owner: 'other' }, { baseSha: 'c'.repeat(40) }, { expectedHost: 'wrong.local' },
  ]) {
    const mismatch = await invoke(ledgerRequest(fixture.job, overrides), env);
    assert.equal(mismatch.code, 1);
    assert.match(mismatch.stderr, /ledger fence and digests|Malformed ledger/);
  }
  await waitForRecord(recordFile, (record) => record.state === 'awaiting-terminal-evidence', 'ledger job completion');
  const terminal = await invoke(request, env);
  assert.equal(terminal.code, 0, terminal.stderr);
  assert.equal(JSON.parse(terminal.stdout).state, 'completed');
  const replay = await invoke(request, env);
  assert.deepEqual(JSON.parse(replay.stdout), JSON.parse(terminal.stdout));
  assert.equal((await waitForInvocations(fixture.invocations, 1)).length, 1);
});

test('ledger-only recovery refuses corrupt stored wire digest and attests a correlated prelaunch failure', async () => {
  const fixture = await createFixture('ledger-failure');
  const changed = fixture.job;
  execFileSync('git', ['-C', fixture.repository, 'branch', `ralph/${changed.jobId}`, fixture.baseSha], { stdio: 'ignore' });
  const dispatch = await invoke({ version: 1, type: 'dispatch', job: changed }, fixture.env);
  assert.equal(dispatch.code, 1);
  const request = ledgerRequest(changed);
  const failed = await invoke(request, fixture.env);
  assert.equal(failed.code, 0, failed.stderr);
  assert.equal(JSON.parse(failed.stdout).state, 'failed');
  assert.equal(JSON.parse(failed.stdout).requestDigest, request.job.requestDigest);
  const file = path.join(fixture.state, `${changed.jobId}.json`);
  const record = JSON.parse(await readFile(file, 'utf8'));
  record.digest = '0'.repeat(64);
  await writeFile(file, JSON.stringify(record));
  const corrupt = await invoke(request, fixture.env);
  assert.equal(corrupt.code, 1);
  assert.match(corrupt.stderr, /ledger fence and digests/);
});

test('explicit abandonment preserves stopped incomplete work and durably fences dispatch and supervisor replays', async () => {
  const fixture = await createFixture('abandon-incomplete');
  const env = { ...fixture.env, RALPH_MAC_WORKER_FAKE_PUSH: 'false' };
  const request = { version: 1, type: 'dispatch', job: fixture.job };
  assert.equal((await invoke(request, env)).code, 0);
  const file = path.join(fixture.state, `${fixture.job.jobId}.json`);
  const before = await waitForRecord(file,
    (record) => record.state === 'awaiting-terminal-evidence' && processGone(record.supervisorPid),
    'recorded stopped process');
  const dirtyFile = path.join(before.worktree, 'README.md');
  await writeFile(dirtyFile, 'unpublished work must survive abandonment\n');
  const result = await invoke({ ...request, type: 'abandon-incomplete' }, env);
  assert.equal(result.code, 0, result.stderr);
  const response = JSON.parse(result.stdout);
  assert.equal(response.type, 'abandoned');
  assert.equal(response.state, 'abandoned');
  assert.equal(response.failureCode, 'INCOMPLETE_DELIVERY');
  assert.equal(response.dispatchFenced, true);
  assert.equal(response.processCessationVerified, true);
  assert.equal(response.workingTreeClean, false);
  assert.equal(response.allCommitsPushed, false);
  assert.equal(response.processCompletedAt, before.processResult.completedAt);
  const after = JSON.parse(await readFile(file, 'utf8'));
  for (const [key, value] of Object.entries(before)) {
    if (key !== 'state' && key !== 'terminal') assert.deepEqual(after[key], value);
  }
  assert.deepEqual(after.abandonmentEvidence.processResult, before.processResult);
  assert.equal(await readFile(dirtyFile, 'utf8'), 'unpublished work must survive abandonment\n');
  for (const type of ['abandon-incomplete', 'reconcile']) {
    const replay = await invoke({ ...request, type }, env);
    assert.equal(replay.code, 0, replay.stderr);
    assert.deepEqual(JSON.parse(replay.stdout), response);
  }
  const identityRequest = { ...ledgerRequest(fixture.job, { sessionId: before.sessionId }), type: 'abandon-incomplete-ledger' };
  assert.deepEqual(JSON.parse((await invoke(identityRequest, env)).stdout), response);
  const mismatch = await invoke({
    ...identityRequest, job: { ...identityRequest.job, sessionId: 'unrelated-session' },
  }, env);
  assert.equal(mismatch.code, 1);
  assert.match(mismatch.stderr, /ledger fence and digests/);
  const delayed = await invoke(request, env);
  assert.equal(delayed.code, 1);
  assert.match(delayed.stderr, /terminally fenced/);
  const supervisor = await invoke({}, env, ['--run', fixture.job.jobId, before.launchToken]);
  assert.equal(supervisor.code, 1);
  assert.match(supervisor.stderr, /Supervisor launch is not owned/);
  assert.equal((await waitForInvocations(fixture.invocations, 1)).length, 1);
  assert.equal(await readFile(dirtyFile, 'utf8'), 'unpublished work must survive abandonment\n');
});

test('abandonment rejects alive, unknown, malformed and mismatched process/job evidence without changing audit', async () => {
  const fixture = await createFixture('abandon-rejections');
  const env = { ...fixture.env, RALPH_MAC_WORKER_FAKE_PUSH: 'false', RALPH_MAC_WORKER_FAKE_DELAY_MS: '500' };
  const request = { version: 1, type: 'dispatch', job: fixture.job };
  assert.equal((await invoke(request, env)).code, 0);
  const file = path.join(fixture.state, `${fixture.job.jobId}.json`);
  const live = await invoke({ ...request, type: 'abandon-incomplete' }, env);
  assert.equal(live.code, 1);
  const before = await waitForRecord(file,
    (record) => record.state === 'awaiting-terminal-evidence' && processGone(record.supervisorPid),
    'stopped rejection fixture');
  const abandon = { ...request, type: 'abandon-incomplete' };
  const variants = [
    { ...before, processResult: undefined },
    { ...before, launchToken: undefined },
    { ...before, launchToken: 'not-a-worker-token' },
    { ...before, supervisorPid: undefined },
    { ...before, supervisorPid: process.pid },
    { ...before, pid: process.pid },
    { ...before, processResult: { ...before.processResult, completedAt: '2099-01-01T00:00:00Z' } },
    { ...before, digest: 'e'.repeat(64) },
    { ...before, job: { ...before.job, fence: 999 } },
  ];
  for (const record of variants) {
    const serialized = JSON.stringify(record);
    await writeFile(file, serialized);
    const blocked = await invoke(abandon, env);
    assert.equal(blocked.code, 1, 'invalid evidence must never abandon');
    assert.equal(await readFile(file, 'utf8'), serialized);
  }
  await writeFile(file, JSON.stringify(before));
  for (const pidEvidence of ['unknown', String(process.pid)]) {
    await writeFile(fixture.pidFile, pidEvidence);
    const blocked = await invoke(abandon, env);
    assert.equal(blocked.code, 1);
    assert.match(blocked.stderr, /Process probe could not|process remains alive/);
    assert.equal(await readFile(file, 'utf8'), JSON.stringify(before));
  }
  const wrongFence = await invoke({ ...abandon, job: { ...fixture.job, fence: 999 } }, env);
  assert.equal(wrongFence.code, 1);
  assert.match(wrongFence.stderr, /No matching job/);
  const missingJob = await invoke({ ...abandon, job: { ...fixture.job, jobId: 'never-dispatched' } }, env);
  assert.equal(missingJob.code, 1);
  assert.match(missingJob.stderr, /existing worker termination record/);
});

test('complete delivery uses normal status and cannot be downgraded to abandoned', async () => {
  const fixture = await createFixture('abandon-complete');
  const request = { version: 1, type: 'dispatch', job: fixture.job };
  assert.equal((await invoke(request, fixture.env)).code, 0);
  await waitForRecord(path.join(fixture.state, `${fixture.job.jobId}.json`),
    (record) => record.state === 'awaiting-terminal-evidence' && processGone(record.supervisorPid),
    'completed process for normal status');
  const result = await invoke({ ...request, type: 'abandon-incomplete' }, fixture.env);
  assert.equal(result.code, 1);
  assert.match(result.stderr, /Complete Git evidence/);
  const normal = await invoke({ ...request, type: 'reconcile' }, fixture.env);
  assert.equal(normal.code, 0, normal.stderr);
  assert.equal(JSON.parse(normal.stdout).state, 'completed');
});

test('reconcile does not attest absence while residual worktree evidence exists', async () => {
  const fixture = await createFixture('absent-with-worktree');
  await mkdir(path.join(fixture.worktrees, fixture.job.jobId), { recursive: true });
  const result = await invoke({ version: 1, type: 'reconcile', job: fixture.job }, fixture.env);
  assert.equal(result.code, 1);
  assert.match(result.stderr, /residual worktree or process evidence/);
  await assert.rejects(() => readFile(fixture.invocations, 'utf8'), (error) => error.code === 'ENOENT');
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
    { ...fixture.job, jobId: 'x'.repeat(65) },
    { ...fixture.job, jobId: 'invalid:git-ref' },
  ]) {
    const result = await invoke({ version: 1, type: 'dispatch', job: changedJob }, fixture.env);
    assert.equal(result.code, 1);
  }
  execFileSync(
    'git',
    ['-C', fixture.repository, '-c', 'user.name=Fixture', '-c', 'user.email=fixture@example.test',
      'commit', '--allow-empty', '-m', 'advance trusted base'],
    { stdio: 'ignore' },
  );
  execFileSync('git', ['-C', fixture.repository, 'push', 'origin', 'development'], { stdio: 'ignore' });
  execFileSync(
    'git',
    ['-C', fixture.repository, 'update-ref', 'refs/remotes/origin/development', fixture.baseSha],
    { stdio: 'ignore' },
  );
  execFileSync('git', ['-C', fixture.repository, 'reset', '--hard', fixture.baseSha], { stdio: 'ignore' });
  const staleBase = await invoke(
    { version: 1, type: 'dispatch', job: { ...fixture.job, jobId: 'stale-base-2605' } },
    fixture.env,
  );
  assert.equal(staleBase.code, 1);
  assert.match(staleBase.stderr, /repository identity or base commit/);
  const wrongHost = await invoke(
    { version: 1, type: 'dispatch', job: { ...fixture.job, jobId: 'wrong-host-2605' } },
    { ...fixture.env, RALPH_MAC_WORKER_TEST_HOSTNAME: 'impostor.local' },
  );
  assert.equal(wrongHost.code, 1);
  assert.match(wrongHost.stderr, /trusted host/);
  const mismatchedRequestHost = await invoke(
    {
      version: 1,
      type: 'dispatch',
      job: { ...fixture.job, jobId: 'wrong-request-host-2605', expectedHost: 'impostor.local' },
    },
    fixture.env,
  );
  assert.equal(mismatchedRequestHost.code, 1);
  assert.match(mismatchedRequestHost.stderr, /Malformed remote job/);
  await assert.rejects(() => readFile(fixture.invocations, 'utf8'), (error) => error.code === 'ENOENT');
});

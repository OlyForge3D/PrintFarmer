import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { EventEmitter } from 'node:events';
import { mkdir, readFile, rm, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { PassThrough } from 'node:stream';
import test from 'node:test';
import {
  RalphMacSshError, acknowledgeJob, acknowledgeLocalJob, createRemoteRequest, createSshInvocation, dispatchMacJob,
  loadMacSshConfiguration, markUncertain, parseRemoteAcknowledgement, recordDeliveryIntent,
  recordLocalTerminalResult, recordTerminalResult, recoverRemoteDelivery, reserveJob, reserveLocalJob, runSsh,
} from '../ralph-macos-ssh.mjs';

const root = path.resolve('fixtures', 'ralph-macos-ssh-validation');
const options = () => ({
  platform: 'win32',
  env: {
    RALPH_MAC_SSH_ENABLED: 'true', RALPH_MAC_SSH_DESTINATION: 'operator@trusted-mac.local',
    RALPH_MAC_SSH_EXPECTED_HOST: 'trusted-mac.local', RALPH_MAC_SSH_WORKER_PATH: '/opt/printfarmer/ralph-worker',
    RALPH_MAC_SSH_KNOWN_HOSTS: path.join(root, 'known_hosts'), RALPH_ADMISSION_LEDGER_DIR: root,
  },
});
const job = (id = 'job-2605') => ({
  jobId: id, repository: 'OlyForge3D/PrintFarmer', issue: 2605, owner: 'hudson',
  baseSha: 'a'.repeat(40), model: 'gpt-5.6-terra', effort: 'medium', agent: 'squad',
  acceptanceCriteria: ['Run the targeted iOS test'], charter: 'mobile/AGENTS.md',
});
const eligibility = { repository: 'OlyForge3D/PrintFarmer', issue: 2605, open: true, exactClaim: true, held: false, blocked: false, linkedPr: false };

async function reset() {
  await rm(root, { recursive: true, force: true });
  await mkdir(root, { recursive: true });
}

function fakeChild() {
  const child = new EventEmitter();
  child.stdin = new PassThrough();
  child.stdout = new PassThrough();
  child.stderr = new PassThrough();
  child.kill = () => child.emit('close', 143);
  return child;
}

function runAdmission(command, request) {
  return new Promise((resolve, reject) => {
    const child = spawn(process.execPath, ['scripts/ci/ralph-admission.mjs', command], {
      env: { ...process.env, RALPH_ADMISSION_LEDGER_DIR: root },
      stdio: ['pipe', 'pipe', 'pipe'],
    });
    let stdout = '';
    let stderr = '';
    child.stdout.on('data', (chunk) => { stdout += chunk; });
    child.stderr.on('data', (chunk) => { stderr += chunk; });
    child.on('error', reject);
    child.on('close', (code) => resolve({ code, stdout, stderr }));
    child.stdin.end(JSON.stringify(request));
  });
}

test('requires explicit trusted Windows configuration and strict SSH options', () => {
  assert.throws(() => loadMacSshConfiguration({ platform: 'win32', env: {} }), (error) => error.code === 'DISABLED');
  const configuration = loadMacSshConfiguration(options());
  const invocation = createSshInvocation(configuration);
  assert.deepEqual(invocation.args.slice(0, 8), [
    '-o', 'BatchMode=yes', '-o', 'StrictHostKeyChecking=yes', '-o',
    `UserKnownHostsFile=${configuration.knownHosts}`, '-o', 'ConnectTimeout=10',
  ]);
  assert.match(invocation.args.at(-1), /^zsh -lic /);
  assert.throws(() => loadMacSshConfiguration({ ...options(), env: { ...options().env, RALPH_MAC_SSH_DESTINATION: 'x;whoami' } }),
    (error) => error.code === 'INVALID_CONFIGURATION');
});

test('serializes untrusted content only as structured stdin and limits jobs to PrintFarmer', () => {
  const malicious = job();
  malicious.acceptanceCriteria = ['"; rm -rf / #'];
  const input = createRemoteRequest(malicious);
  assert.match(input, /rm -rf/);
  assert.throws(() => createRemoteRequest({ ...job(), repository: 'OlyForge3D/PrintFarmerDesktop' }),
    (error) => error.code === 'UNSUPPORTED_REPOSITORY');
  assert.throws(() => createRemoteRequest({ ...job(), effort: 'high' }),
    (error) => error.code === 'INVALID_REQUEST');
  assert.throws(() => createRemoteRequest({ ...job(), jobId: undefined }),
    (error) => error.code === 'INVALID_REQUEST');
  assert.throws(() => createRemoteRequest({ ...job(), owner: 2605 }),
    (error) => error.code === 'INVALID_REQUEST');
});

test('rejects malformed, wrong-host, and uncorrelated remote acknowledgements', () => {
  const expected = { version: 1, type: 'accepted', jobId: 'job-2605', repository: 'OlyForge3D/PrintFarmer', issue: 2605, owner: 'hudson', baseSha: 'a'.repeat(40), host: 'trusted-mac.local', sessionId: 'session-1' };
  assert.deepEqual(parseRemoteAcknowledgement(JSON.stringify(expected), { ...job(), expectedHost: expected.host }), expected);
  for (const output of ['not-json', `${JSON.stringify(expected)}\nextra`, JSON.stringify({ ...expected, host: 'wrong-host.local' })]) {
    assert.throws(() => parseRemoteAcknowledgement(output, { ...job(), expectedHost: expected.host }),
      (error) => error.code === 'MALFORMED_RESPONSE');
  }
  for (const acknowledgement of [
    { ...expected, sessionId: undefined },
    { ...expected, sessionId: 1 },
  ]) {
    assert.throws(() => parseRemoteAcknowledgement(JSON.stringify(acknowledgement), { ...job(), expectedHost: expected.host }),
      (error) => error.code === 'MALFORMED_RESPONSE');
  }
});

test('enforces five shared slots, one owner per issue, and stable job fencing', async () => {
  await reset();
  const configuration = options();
  await Promise.all(Array.from({ length: 4 }, (_, index) => reserveJob({
    job: { ...job(`job-${index}`), issue: index + 1 }, eligibility: { ...eligibility, issue: index + 1 },
  }, configuration)));
  await reserveLocalJob({
    job: { ...job('local-job'), issue: 5 }, eligibility: { ...eligibility, issue: 5 },
  }, configuration);
  await assert.rejects(() => reserveJob({ job: { ...job('job-overflow'), issue: 99 }, eligibility: { ...eligibility, issue: 99 } }, configuration),
    (error) => error.code === 'SLOT_EXHAUSTED');
  await assert.rejects(() => reserveJob({ job: { ...job('job-other'), issue: 1 }, eligibility: { ...eligibility, issue: 1 } }, configuration),
    (error) => error.code === 'ISSUE_OWNED');
  await assert.rejects(() => reserveJob({ job: { ...job('job-0'), issue: 99 }, eligibility: { ...eligibility, issue: 99 } }, configuration),
    (error) => error.code === 'FENCED');
});

test('fences request changes under an existing job identifier', async () => {
  await reset();
  const configuration = options();
  await reserveJob({ job: job(), eligibility }, configuration);
  await assert.rejects(() => reserveJob({
    job: { ...job(), acceptanceCriteria: ['Different request'] }, eligibility,
  }, configuration), (error) => error.code === 'FENCED');
});

test('preserves the valid backup while repairing a corrupt primary ledger', async () => {
  await reset();
  const configuration = options();
  const ledger = {
    version: 1, repository: 'OlyForge3D/PrintFarmer', generation: 0, jobs: {},
  };
  await writeFile(path.join(root, 'printfarmer-jobs.json'), '{not-json');
  await writeFile(path.join(root, 'printfarmer-jobs.json.bak'), `${JSON.stringify(ledger)}\n`);

  await reserveJob({ job: job(), eligibility }, configuration);

  assert.deepEqual(JSON.parse(await readFile(path.join(root, 'printfarmer-jobs.json.bak'), 'utf8')), ledger);
});

test('rejects array-shaped ledgers before an admission can be silently lost', async () => {
  await reset();
  const configuration = options();
  await writeFile(path.join(root, 'printfarmer-jobs.json'), JSON.stringify({
    version: 1, repository: 'OlyForge3D/PrintFarmer', generation: 0, jobs: [],
  }));

  await assert.rejects(() => reserveJob({ job: job(), eligibility }, configuration),
    (error) => error.code === 'CORRUPT_LEDGER');
});

test('recovers a crashed controller lock without allowing a live controller overlap', async () => {
  await reset();
  const configuration = options();
  await writeFile(path.join(root, 'printfarmer-jobs.json.lock'), JSON.stringify({
    token: 'crashed-controller', pid: 1, expiresAt: '2026-01-01T00:00:00Z',
  }));
  await reserveJob({ job: job(), eligibility }, { ...configuration, isOwnerAlive: () => false });
  await writeFile(path.join(root, 'printfarmer-jobs.json.lock'), JSON.stringify({
    token: 'live-controller', pid: 1, expiresAt: '2026-01-01T00:00:00Z',
  }));
  await assert.rejects(() => reserveJob({ job: { ...job('blocked-job'), issue: 9 }, eligibility: { ...eligibility, issue: 9 } }, {
    ...configuration, retries: 1, isOwnerAlive: () => true,
  }), (error) => error.code === 'LOCK_TIMEOUT');
});

test('retains delivery ownership after lost acknowledgement and reconciles the same job', async () => {
  await reset();
  const configuration = options();
  await reserveJob({ job: job(), eligibility }, configuration);
  await recordDeliveryIntent('job-2605', configuration);
  await markUncertain('job-2605', configuration);
  const child = fakeChild();
  const acknowledged = await dispatchMacJob({ job: job(), eligibility }, {
    ...configuration,
    spawn: () => {
      queueMicrotask(() => {
        child.stdout.end(JSON.stringify({
          version: 1, type: 'accepted', jobId: 'job-2605', repository: 'OlyForge3D/PrintFarmer',
          issue: 2605, owner: 'hudson', baseSha: 'a'.repeat(40), host: 'trusted-mac.local', sessionId: 'session-1',
        }));
        child.emit('close', 0);
      });
      return child;
    },
  });
  assert.equal(acknowledged.state, 'accepted');
  assert.equal(acknowledged.sessionId, 'session-1');
});

test('recovers an expired delivery intent only after its controller is demonstrably dead', async () => {
  await reset();
  const configuration = options();
  await reserveJob({ job: job(), eligibility }, configuration);
  await recordDeliveryIntent('job-2605', { ...configuration, deliveryLeaseMs: 1 });
  await assert.rejects(() => recoverRemoteDelivery('job-2605', {
    ...configuration, now: Date.now() + 1_000, isOwnerAlive: () => true,
  }), (error) => error.code === 'DELIVERY_ACTIVE');
  const recovered = await recoverRemoteDelivery('job-2605', {
    ...configuration, now: Date.now() + 1_000, isOwnerAlive: () => false,
  });
  assert.equal(recovered.state, 'uncertain');
});
test('requires correlated terminal evidence before capacity is released', async () => {
  await reset();
  const configuration = options();
  await reserveJob({ job: job(), eligibility }, configuration);
  await assert.rejects(() => recordTerminalResult({
    jobId: 'job-2605', repository: 'OlyForge3D/PrintFarmer', issue: 2605, baseSha: 'a'.repeat(40),
    host: 'trusted-mac.local', sessionId: 'session-1', headSha: 'b'.repeat(40), exitCode: 0,
    validationEvidence: 'not accepted', workingTreeClean: true, allCommitsPushed: true,
  }, configuration), (error) => error.code === 'INVALID_TRANSITION');
  await recordDeliveryIntent('job-2605', configuration);
  const acknowledgement = { version: 1, type: 'accepted', jobId: 'job-2605', repository: 'OlyForge3D/PrintFarmer', issue: 2605, owner: 'hudson', baseSha: 'a'.repeat(40), host: 'trusted-mac.local', sessionId: 'session-1' };
  await acknowledgeJob('job-2605', acknowledgement, configuration);
  await assert.rejects(() => recordTerminalResult({ jobId: 'job-2605', repository: 'OlyForge3D/PrintFarmer', issue: 2605, baseSha: 'a'.repeat(40), host: 'trusted-mac.local', headSha: 'b'.repeat(40), exitCode: 0 }, configuration),
    (error) => error.code === 'INVALID_TERMINAL_EVIDENCE');
  const completed = await recordTerminalResult({ jobId: 'job-2605', repository: 'OlyForge3D/PrintFarmer', issue: 2605, baseSha: 'a'.repeat(40), host: 'trusted-mac.local', sessionId: 'session-1', headSha: 'b'.repeat(40), exitCode: 0, validationEvidence: 'mobile/PrintFarmerTests: passed', workingTreeClean: true, allCommitsPushed: true }, configuration);
  assert.equal(completed.state, 'completed');
});

test('keeps local reservations out of the remote terminal lifecycle', async () => {
  await reset();
  const configuration = options();
  await reserveLocalJob({ job: job(), eligibility }, configuration);
  await acknowledgeLocalJob('job-2605', 'local-session-1', configuration);

  await assert.rejects(() => recordTerminalResult({
    jobId: 'job-2605', repository: 'OlyForge3D/PrintFarmer', issue: 2605, baseSha: 'a'.repeat(40),
    host: undefined, sessionId: 'local-session-1', headSha: 'b'.repeat(40), exitCode: 0,
    validationEvidence: 'not remote', workingTreeClean: true, allCommitsPushed: true,
  }, configuration), (error) => error.code === 'INVALID_TRANSITION');
});

test('does not reuse a terminal job identifier as an active reservation', async () => {
  await reset();
  const configuration = options();
  await reserveLocalJob({ job: job(), eligibility }, configuration);
  await acknowledgeLocalJob('job-2605', 'local-session-1', configuration);
  await recordLocalTerminalResult({
    jobId: 'job-2605', sessionId: 'local-session-1', headSha: 'b'.repeat(40), exitCode: 0,
    validationEvidence: 'targeted tests passed', workingTreeClean: true, allCommitsPushed: true,
  }, configuration);

  await assert.rejects(() => reserveLocalJob({ job: job(), eligibility }, configuration),
    (error) => error.code === 'FENCED');
});

test('executes the local create-session admission lifecycle through the CLI', async () => {
  await reset();
  const reserve = await runAdmission('reserve-local', { job: job(), eligibility });
  assert.equal(reserve.code, 0, reserve.stderr);
  assert.equal(JSON.parse(reserve.stdout).result.state, 'reserved');

  const acknowledgement = await runAdmission('acknowledge-local', {
    jobId: 'job-2605', sessionId: 'app-session-2605',
  });
  assert.equal(acknowledgement.code, 0, acknowledgement.stderr);
  assert.equal(JSON.parse(acknowledgement.stdout).result.sessionId, 'app-session-2605');

  const terminal = await runAdmission('terminal-local', {
    result: {
      jobId: 'job-2605', sessionId: 'app-session-2605', headSha: 'b'.repeat(40), exitCode: 0,
      validationEvidence: 'targeted tests passed', workingTreeClean: true, allCommitsPushed: true,
    },
  });
  assert.equal(terminal.code, 0, terminal.stderr);
  assert.equal(JSON.parse(terminal.stdout).result.state, 'completed');

  for (const command of ['unknown', 'constructor']) {
    const invalid = await runAdmission(command, {});
    assert.equal(invalid.code, 1);
    assert.equal(JSON.parse(invalid.stderr).code, 'INVALID_COMMAND');
  }
});

test('contains SSH stream errors, nonzero exits, and wall-clock timeout', async () => {
  const invocation = createSshInvocation(loadMacSshConfiguration(options()));
  const broken = fakeChild();
  await assert.rejects(() => runSsh(invocation, '{}', {
    spawn: () => {
      queueMicrotask(() => broken.stdin.emit('error', new Error('EPIPE')));
      return broken;
    },
  }), (error) => error.code === 'SSH_FAILURE');
  const nonzero = fakeChild();
  await assert.rejects(() => runSsh(invocation, '{}', {
    spawn: () => {
      queueMicrotask(() => nonzero.emit('close', 255));
      return nonzero;
    },
  }), (error) => error.code === 'SSH_FAILURE');
  const stalled = fakeChild();
  await assert.rejects(() => runSsh(invocation, '{}', { spawn: () => stalled, timeoutMs: 1 }),
    (error) => error.code === 'SSH_TIMEOUT');
});

test('marks SSH failure uncertain instead of retrying locally or releasing its slot', async () => {
  await reset();
  const configuration = options();
  await assert.rejects(() => dispatchMacJob({ job: job(), eligibility }, {
    ...configuration,
    spawn: () => { throw new RalphMacSshError('offline', 'SSH_FAILURE'); },
  }), (error) => error.code === 'SSH_FAILURE');
  await assert.rejects(() => reserveJob({ job: { ...job('duplicate'), issue: 2605 }, eligibility }, configuration),
    (error) => error.code === 'ISSUE_OWNED');
});

import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { EventEmitter } from 'node:events';
import { mkdir, readFile, rm, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { PassThrough } from 'node:stream';
import test from 'node:test';
import {
  RalphMacSshError, acknowledgeJob, acknowledgeLocalJob, createRemoteRequest, createSshInvocation, dispatchMacJob,
  loadMacSshConfiguration, markUncertain, parseRemoteAcknowledgement, parseRemoteWorkerResponse, reconcileMacJob,
  recordDeliveryIntent, recordLocalTerminalResult, recoverLocalReservation, recoverLostLocalSession,
  recoverRemoteDelivery, reserveJob, reserveLocalJob, runSsh,
} from '../ralph-macos-ssh.mjs';

const root = path.resolve('fixtures', 'ralph-macos-ssh-validation');
const options = () => ({
  platform: 'win32',
  env: {
    RALPH_MAC_SSH_ENABLED: 'true', RALPH_MAC_SSH_DESTINATION: 'operator@10.0.0.72',
    RALPH_MAC_SSH_EXPECTED_HOST: 'trusted-mac.local', RALPH_MAC_SSH_WORKER_PATH: '/opt/printfarmer/ralph-worker',
    RALPH_MAC_SSH_KNOWN_HOSTS: path.join(root, 'known_hosts'), RALPH_ADMISSION_LEDGER_DIR: root,
  },
});
const job = (id = 'job-2605') => ({
  jobId: id, repository: 'OlyForge3D/PrintFarmer', issue: 2605, owner: 'hudson',
  baseSha: 'a'.repeat(40), expectedHost: 'trusted-mac.local',
  model: 'gpt-5.6-terra', effort: 'medium', agent: 'squad',
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
  assert.equal(configuration.destination, 'operator@10.0.0.72');
  assert.equal(configuration.expectedHost, 'trusted-mac.local');
  assert.match(invocation.args.at(-1), /^zsh -lic /);
  assert.throws(() => loadMacSshConfiguration({ ...options(), env: { ...options().env, RALPH_MAC_SSH_DESTINATION: 'x;whoami' } }),
    (error) => error.code === 'INVALID_CONFIGURATION');
});

test('serializes untrusted content only as structured stdin and limits jobs to PrintFarmer', () => {
  const malicious = { ...job(), fence: 7 };
  malicious.acceptanceCriteria = ['"; rm -rf / #'];
  const input = createRemoteRequest(malicious);
  assert.match(input, /rm -rf/);
  assert.equal(JSON.parse(input).job.fence, 7);
  assert.equal(JSON.parse(input).job.expectedHost, 'trusted-mac.local');
  assert.throws(() => createRemoteRequest({ ...job(), fence: 7, repository: 'OlyForge3D/PrintFarmerDesktop' }),
    (error) => error.code === 'UNSUPPORTED_REPOSITORY');
  assert.throws(() => createRemoteRequest({ ...job(), fence: 7, effort: 'high' }),
    (error) => error.code === 'INVALID_REQUEST');
  assert.throws(() => createRemoteRequest({ ...job(), fence: 7, jobId: 'x'.repeat(65) }),
    (error) => error.code === 'INVALID_REQUEST');
  assert.throws(() => createRemoteRequest({ ...job(), fence: 7, jobId: 'invalid:git-ref' }),
    (error) => error.code === 'INVALID_REQUEST');
  assert.throws(() => createRemoteRequest({ ...job(), fence: 7, jobId: undefined }),
    (error) => error.code === 'INVALID_REQUEST');
  assert.throws(() => createRemoteRequest({ ...job(), fence: 7, owner: 2605 }),
    (error) => error.code === 'INVALID_REQUEST');
  assert.throws(() => createRemoteRequest({ ...job(), fence: 7, acceptanceCriteria: [''] }),
    (error) => error.code === 'INVALID_REQUEST');
  assert.throws(() => createRemoteRequest({ ...job(), fence: 7, acceptanceCriteria: [2605] }),
    (error) => error.code === 'INVALID_REQUEST');
  assert.throws(() => createRemoteRequest({ ...job(), fence: 7, charter: '  ' }),
    (error) => error.code === 'INVALID_REQUEST');
  assert.throws(() => createRemoteRequest({ ...job(), fence: 7 }, 'unknown'),
    (error) => error.code === 'INVALID_REQUEST');
  assert.throws(() => createRemoteRequest(job()), (error) => error.code === 'INVALID_REQUEST');
});

test('rejects oversized worker payloads before creating an admission ledger', async () => {
  await reset();
  await assert.rejects(
    () => dispatchMacJob({
      job: { ...job(), acceptanceCriteria: ['x'.repeat(70 * 1024)] },
      eligibility,
      controllerPid: process.pid,
    }, options()),
    (error) => error.code === 'INVALID_REQUEST',
  );
  await assert.rejects(
    () => readFile(path.join(root, 'printfarmer-jobs.json'), 'utf8'),
    (error) => error.code === 'ENOENT',
  );
});

test('rejects malformed, wrong-host, and uncorrelated remote acknowledgements', () => {
  const expected = { version: 1, type: 'accepted', state: 'running', jobId: 'job-2605', fence: 7, repository: 'OlyForge3D/PrintFarmer', issue: 2605, owner: 'hudson', baseSha: 'a'.repeat(40), host: 'trusted-mac.local', sessionId: 'session-1' };
  const expectedJob = { ...job(), fence: 7, expectedHost: expected.host };
  assert.deepEqual(parseRemoteAcknowledgement(JSON.stringify(expected), expectedJob), expected);
  for (const output of ['not-json', `${JSON.stringify(expected)}\nextra`, JSON.stringify({ ...expected, host: 'wrong-host.local' })]) {
    assert.throws(() => parseRemoteAcknowledgement(output, expectedJob),
      (error) => error.code === 'MALFORMED_RESPONSE');
  }
  for (const acknowledgement of [
    { ...expected, sessionId: undefined },
    { ...expected, sessionId: 1 },
    { ...expected, fence: 8 },
    { ...expected, state: 'failed' },
  ]) {
    assert.throws(() => parseRemoteAcknowledgement(JSON.stringify(acknowledgement), expectedJob),
      (error) => error.code === 'MALFORMED_RESPONSE');
  }
});

test('enforces five shared slots, one owner per issue, and stable job fencing', async () => {
  await reset();
  const configuration = options();
  await Promise.all(Array.from({ length: 4 }, (_, index) => reserveJob({
    job: { ...job(`job-${index}`), issue: index + 1, expectedHost: `mac-${index}.local` },
    eligibility: { ...eligibility, issue: index + 1 },
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
  const acknowledged = await dispatchMacJob({ job: job(), eligibility, controllerPid: process.pid }, {
    ...configuration,
    spawn: () => {
      queueMicrotask(() => {
        child.stdout.end(JSON.stringify({
          version: 1, type: 'accepted', state: 'running', jobId: 'job-2605', fence: 1, repository: 'OlyForge3D/PrintFarmer',
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

test('releases an uncertain delivery only from the worker attesting no durable job exists', async () => {
  await reset();
  const configuration = options();
  await reserveJob({ job: job(), eligibility }, configuration);
  await recordDeliveryIntent('job-2605', configuration);
  await markUncertain('job-2605', configuration);
  const child = fakeChild();
  const failed = await dispatchMacJob({ job: job(), eligibility, controllerPid: process.pid }, {
    ...configuration,
    spawn: () => {
      queueMicrotask(() => {
        child.stdout.end(JSON.stringify({
          version: 1, type: 'failed', state: 'failed', workerVerified: true,
          failureCode: 'JOB_NOT_FOUND',
          failureMessage: 'Mac worker verified that no durable job record exists for this fenced request.',
          jobId: 'job-2605', fence: 1, repository: 'OlyForge3D/PrintFarmer',
          issue: 2605, owner: 'hudson', baseSha: 'a'.repeat(40),
          host: 'trusted-mac.local', sessionId: 'absent-request-digest',
        }));
        child.emit('close', 0);
      });
      return child;
    },
  });
  assert.equal(failed.state, 'failed');
  assert.equal(failed.failureCode, 'JOB_NOT_FOUND');
  assert.equal(failed.workerVerified, true);
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
test('releases remote capacity only from a correlated worker terminal attestation', async () => {
  await reset();
  const configuration = options();
  await reserveJob({ job: job(), eligibility }, configuration);
  await recordDeliveryIntent('job-2605', configuration);
  const acknowledgement = { version: 1, type: 'accepted', state: 'running', jobId: 'job-2605', fence: 1, repository: 'OlyForge3D/PrintFarmer', issue: 2605, owner: 'hudson', baseSha: 'a'.repeat(40), host: 'trusted-mac.local', sessionId: 'session-1' };
  await acknowledgeJob('job-2605', acknowledgement, configuration);
  const terminal = {
    ...acknowledgement,
    type: 'terminal',
    state: 'completed',
    workerVerified: true,
    headSha: 'b'.repeat(40),
    exitCode: 0,
    validationEvidence: 'Mac worker verified exit 0 with a clean worktree and matching pushed branch.',
    workingTreeClean: true,
    allCommitsPushed: true,
    repositoryIdentityVerified: true,
    baseAncestor: true,
  };
  const child = fakeChild();
  const completed = await reconcileMacJob({ job: job() }, {
    ...configuration,
    spawn: () => {
      queueMicrotask(() => {
        child.stdout.end(JSON.stringify(terminal));
        child.emit('close', 0);
      });
      return child;
    },
  });
  assert.equal(completed.state, 'completed');
  assert.equal(completed.workerVerified, true);

  const manual = await runAdmission('terminal-remote', { result: terminal });
  assert.equal(manual.code, 1);
  assert.equal(JSON.parse(manual.stderr).code, 'INVALID_COMMAND');
});

test('keeps local reservations out of the remote terminal lifecycle', async () => {
  await reset();
  const configuration = options();
  await reserveLocalJob({ job: job(), eligibility }, configuration);
  await acknowledgeLocalJob('job-2605', 'local-session-1', configuration);

  await assert.rejects(() => reconcileMacJob({ job: job() }, configuration),
    (error) => error.code === 'INVALID_TRANSITION');
});

test('accepts a correlated worker signal attestation only as failure', () => {
  const expectedJob = { ...job(), fence: 3, expectedHost: 'trusted-mac.local' };
  const signalFailure = {
    version: 1, type: 'terminal', state: 'failed', workerVerified: true,
    jobId: 'job-2605', fence: 3, repository: 'OlyForge3D/PrintFarmer', issue: 2605,
    owner: 'hudson', baseSha: 'a'.repeat(40), host: 'trusted-mac.local', sessionId: 'session-1',
    headSha: 'b'.repeat(40), signal: 'SIGTERM', validationEvidence: 'Mac worker verified a non-success process result.',
    workingTreeClean: false, allCommitsPushed: false, repositoryIdentityVerified: true, baseAncestor: true,
  };
  const response = parseRemoteWorkerResponse(JSON.stringify(signalFailure), expectedJob);
  assert.equal(response.state, 'failed');
  assert.equal(response.signal, 'SIGTERM');
  for (const invalid of [
    { ...signalFailure, signal: undefined, exitCode: 0 },
    { ...signalFailure, exitCode: 9 },
    { ...signalFailure, repositoryIdentityVerified: undefined },
    { ...signalFailure, baseAncestor: undefined },
  ]) {
    assert.throws(() => parseRemoteWorkerResponse(JSON.stringify(invalid), expectedJob),
      (error) => error.code === 'MALFORMED_RESPONSE');
  }
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
  const reserve = await runAdmission('reserve-local', {
    job: job(), eligibility, controllerPid: process.pid,
  });
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

test('requires the app Ralph controller PID for CLI reservations and remote dispatch', async () => {
  await reset();
  const local = await runAdmission('reserve-local', { job: job(), eligibility });
  assert.equal(local.code, 1);
  assert.match(local.stderr, /requires the Ralph controller process identifier/);
  const remote = await runAdmission('dispatch-remote', { job: job(), eligibility });
  assert.equal(remote.code, 1);
  assert.match(remote.stderr, /requires the Ralph controller process identifier/);
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
  const controllerPid = process.pid + 100_000;
  await assert.rejects(() => dispatchMacJob({ job: job(), eligibility, controllerPid }, {
    ...configuration,
    spawn: () => { throw new RalphMacSshError('offline', 'SSH_FAILURE'); },
  }), (error) => error.code === 'SSH_FAILURE');
  const ledger = JSON.parse(await readFile(path.join(root, 'printfarmer-jobs.json'), 'utf8'));
  assert.equal(ledger.jobs['job-2605'].reservationOwnerPid, controllerPid);
  await assert.rejects(() => reserveJob({ job: { ...job('duplicate'), issue: 2605 }, eligibility }, configuration),
    (error) => error.code === 'ISSUE_OWNED');
});

// --- Lost-session / stale-claim reconciliation (issues #2577, #2578, #2599, #2582) ---

test('missing durable artifact: a stale reserved-but-never-acknowledged local job recovers only on authoritative absence', async () => {
  await reset();
  const configuration = options();
  await reserveLocalJob({ job: job(), eligibility, controllerPid: process.pid + 200_000 }, configuration);

  // Fails closed while the session might still be created: absence is not yet authoritative.
  await assert.rejects(() => recoverLocalReservation('job-2605', { ...configuration }),
    (error) => error.code === 'INVALID_REQUEST');
  // Fails closed while the reservation owner PID could still be alive.
  await assert.rejects(() => recoverLocalReservation('job-2605', { sessionAbsent: true, isOwnerAlive: () => true, ...configuration }),
    (error) => error.code === 'RESERVATION_ACTIVE');

  const recovered = await recoverLocalReservation('job-2605', {
    sessionAbsent: true, isOwnerAlive: () => false, now: Date.parse('2999-01-01T00:00:00Z'), ...configuration,
  });
  assert.equal(recovered.state, 'failed');
  assert.equal(recovered.failureReason, 'session-creation-absent');

  // The issue slot is free: a fresh jobId (fresh reproduction) may be reserved for the same issue.
  const fresh = await reserveLocalJob({ job: job('job-2605-retry'), eligibility, controllerPid: process.pid }, configuration);
  assert.equal(fresh.state, 'reserved');
  // The old terminal jobId itself remains fenced.
  await assert.rejects(() => reserveLocalJob({ job: job(), eligibility }, configuration),
    (error) => error.code === 'FENCED');
});

test('lost session: an accepted/running local job with a confirmed-absent session reconciles to abandoned, not silently cleaned up', async () => {
  await reset();
  const configuration = options();
  await reserveLocalJob({ job: job(), eligibility }, configuration);
  await acknowledgeLocalJob('job-2605', 'session-370ca864', configuration);

  // Fails closed: caller must authoritatively assert absence, never inferred.
  await assert.rejects(() => recoverLostLocalSession('job-2605', { ...configuration }),
    (error) => error.code === 'INVALID_REQUEST');

  const abandoned = await recoverLostLocalSession('job-2605', { sessionAbsent: true, ...configuration });
  assert.equal(abandoned.state, 'abandoned');
  assert.equal(abandoned.failureReason, 'session-lost');
  // The ledger entry (and its sessionId) is preserved as an audit record, never deleted.
  assert.equal(abandoned.sessionId, 'session-370ca864');

  // Abandonment frees the issue's slot: a genuinely new jobId can be reserved (explicit
  // re-admission / fresh reproduction), while the old identifier stays fenced forever.
  const reAdmitted = await reserveLocalJob({ job: job('job-2605-reproduce'), eligibility }, configuration);
  assert.equal(reAdmitted.state, 'reserved');
  await assert.rejects(() => reserveLocalJob({ job: job(), eligibility }, configuration),
    (error) => error.code === 'FENCED');
});

test('stale pending admission: reconciliation is rejected for jobs that never reached an accepted session', async () => {
  await reset();
  const configuration = options();
  await reserveLocalJob({ job: job(), eligibility }, configuration);

  // Still only 'reserved' (no acknowledged session) — this is the recoverLocalReservation case,
  // not recoverLostLocalSession's. A "pending-admission" claim with no session must not be
  // waved through as an abandoned session.
  await assert.rejects(() => recoverLostLocalSession('job-2605', { sessionAbsent: true, ...configuration }),
    (error) => error.code === 'INVALID_TRANSITION');

  await acknowledgeLocalJob('job-2605', 'session-1', configuration);
  await recordLocalTerminalResult({
    jobId: 'job-2605', sessionId: 'session-1', headSha: 'b'.repeat(40), exitCode: 0,
    validationEvidence: 'targeted tests passed', workingTreeClean: true, allCommitsPushed: true,
  }, configuration);

  // A genuinely completed job is not eligible for lost-session reconciliation either.
  await assert.rejects(() => recoverLostLocalSession('job-2605', { sessionAbsent: true, ...configuration }),
    (error) => error.code === 'INVALID_TRANSITION');
});

test('rejects Xcode/CoreSimulator host over-subscription independent of the shared slot pool', async () => {
  await reset();
  const configuration = options();
  await reserveJob({ job: job(), eligibility }, configuration);

  // A second remote (mac-dispatched) job targeting the SAME host is rejected outright, even
  // though only 1 of 5 shared slots is in use — Xcode/CoreSimulator concurrency=1 per Mac is a
  // stricter, independent constraint. The caller's own poll loop is expected to retry later.
  await assert.rejects(() => reserveJob({
    job: { ...job('job-second-xcode'), issue: 9001 }, eligibility: { ...eligibility, issue: 9001 },
  }, configuration), (error) => error.code === 'XCODE_HOST_BUSY');

  // A local (non-Xcode) job is unaffected by the host lock and still consumes only the shared pool.
  const local = await reserveLocalJob({
    job: { ...job('job-local-unaffected'), issue: 9002 }, eligibility: { ...eligibility, issue: 9002 },
  }, configuration);
  assert.equal(local.state, 'reserved');

  // A remote job targeting a DIFFERENT host is unaffected by the first host's lock.
  const otherHost = await reserveJob({
    job: { ...job('job-other-host'), issue: 9003, expectedHost: 'second-mac.local' },
    eligibility: { ...eligibility, issue: 9003 },
  }, configuration);
  assert.equal(otherHost.state, 'reserved');

  // Once the first host's job reaches a terminal state, the host lock releases.
  await recordDeliveryIntent('job-2605', configuration);
  await markUncertain('job-2605', configuration);
  const releaseChild = fakeChild();
  await dispatchMacJob({ job: job(), eligibility, controllerPid: process.pid }, {
    ...configuration,
    spawn: () => {
      queueMicrotask(() => {
        releaseChild.stdout.end(JSON.stringify({
          version: 1, type: 'failed', state: 'failed', workerVerified: true,
          failureCode: 'JOB_NOT_FOUND',
          failureMessage: 'Mac worker verified that no durable job record exists for this fenced request.',
          jobId: 'job-2605', fence: 1, repository: 'OlyForge3D/PrintFarmer',
          issue: 2605, owner: 'hudson', baseSha: 'a'.repeat(40),
          host: 'trusted-mac.local', sessionId: 'absent-request-digest',
        }));
        releaseChild.emit('close', 0);
      });
      return releaseChild;
    },
  });
  const freed = await reserveJob({
    job: { ...job('job-second-xcode-retry'), issue: 9001, expectedHost: 'trusted-mac.local' },
    eligibility: { ...eligibility, issue: 9001 },
  }, configuration);
  assert.equal(freed.state, 'reserved');
});

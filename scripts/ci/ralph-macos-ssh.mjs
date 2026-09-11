import { createHash, randomUUID } from 'node:crypto';
import { copyFile, mkdir, open, readFile, rename, rm, stat } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { spawn as nodeSpawn } from 'node:child_process';

export const printFarmerRepository = 'OlyForge3D/PrintFarmer';
export const activeJobStates = new Set(['reserved', 'delivery-intent', 'accepted', 'running', 'uncertain']);

export class RalphMacSshError extends Error {
  constructor(message, code = 'RALPH_MAC_SSH_ERROR') {
    super(message);
    this.code = code;
  }
}

function required(value, name) {
  if (!value) throw new RalphMacSshError(`${name} is required.`, 'INVALID_CONFIGURATION');
  return value;
}

function validDestination(value) {
  return /^[A-Za-z0-9._-]+@[A-Za-z0-9][A-Za-z0-9.-]*$/.test(value);
}

function validHost(value) {
  return typeof value === 'string' && /^[A-Za-z0-9][A-Za-z0-9.-]*$/.test(value);
}

function validAbsolutePosixPath(value) {
  return /^\/[A-Za-z0-9._/@+=,:-]+$/.test(value) && !value.includes('//') && !value.includes('/../');
}

function validSha(value) {
  return /^[0-9a-f]{40}$/i.test(value);
}

function validIdentifier(value) {
  return typeof value === 'string' && /^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$/.test(value);
}

function validJobIdentifier(value) {
  return typeof value === 'string' && /^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$/.test(value);
}

function safeJson(value, name) {
  if (!value || typeof value !== 'object' || Array.isArray(value)) {
    throw new RalphMacSshError(`${name} must be an object.`, 'INVALID_REQUEST');
  }
  return value;
}

function validateRemoteJob(job, { requireFence = false } = {}) {
  const request = safeJson(job, 'Job');
  if (request.repository !== printFarmerRepository) {
    throw new RalphMacSshError('Remote macOS dispatch is limited to OlyForge3D/PrintFarmer.', 'UNSUPPORTED_REPOSITORY');
  }
  if (!Number.isSafeInteger(request.issue) || request.issue <= 0 || !validJobIdentifier(request.jobId) ||
      (requireFence && (!Number.isSafeInteger(request.fence) || request.fence <= 0)) ||
      !validIdentifier(request.owner) || !validSha(request.baseSha) || !Array.isArray(request.acceptanceCriteria) ||
      request.acceptanceCriteria.some((criterion) => typeof criterion !== 'string' || !criterion.trim()) ||
      (request.charter !== undefined && (typeof request.charter !== 'string' || !request.charter.trim())) ||
      !validHost(request.expectedHost) ||
      !['gpt-5.6-terra', 'gpt-5.6-luna'].includes(request.model) || request.effort !== 'medium' || request.agent !== 'squad') {
    throw new RalphMacSshError('Remote job is malformed.', 'INVALID_REQUEST');
  }
  return request;
}

export function loadMacSshConfiguration({ env = process.env, platform = process.platform } = {}) {
  if (platform !== 'win32') throw new RalphMacSshError('The macOS SSH dispatcher may run only on Windows.', 'WRONG_PLATFORM');
  if (env.RALPH_MAC_SSH_ENABLED !== 'true') {
    throw new RalphMacSshError('macOS SSH dispatch is disabled.', 'DISABLED');
  }
  const destination = required(env.RALPH_MAC_SSH_DESTINATION, 'RALPH_MAC_SSH_DESTINATION');
  const expectedHost = required(env.RALPH_MAC_SSH_EXPECTED_HOST, 'RALPH_MAC_SSH_EXPECTED_HOST');
  const workerPath = required(env.RALPH_MAC_SSH_WORKER_PATH, 'RALPH_MAC_SSH_WORKER_PATH');
  const knownHosts = required(env.RALPH_MAC_SSH_KNOWN_HOSTS, 'RALPH_MAC_SSH_KNOWN_HOSTS');
  if (!validDestination(destination) || !validHost(expectedHost) || !validAbsolutePosixPath(workerPath)) {
    throw new RalphMacSshError('macOS SSH configuration has an invalid trusted identifier.', 'INVALID_CONFIGURATION');
  }
  if (!path.isAbsolute(knownHosts) || /\s/.test(knownHosts)) {
    throw new RalphMacSshError('RALPH_MAC_SSH_KNOWN_HOSTS must be an absolute local path.', 'INVALID_CONFIGURATION');
  }
  return { destination, expectedHost, workerPath, knownHosts };
}

export function createRemoteWorkerCommand(workerPath) {
  if (!validAbsolutePosixPath(workerPath)) {
    throw new RalphMacSshError('Worker path must be a safe absolute POSIX path.', 'INVALID_CONFIGURATION');
  }
  return `zsh -lic ${JSON.stringify(workerPath)}`;
}

export function createSshInvocation(configuration) {
  return {
    command: 'ssh',
    args: [
      '-o', 'BatchMode=yes',
      '-o', 'StrictHostKeyChecking=yes',
      '-o', `UserKnownHostsFile=${configuration.knownHosts}`,
      '-o', 'ConnectTimeout=10',
      '-o', 'ServerAliveInterval=10',
      '-o', 'ServerAliveCountMax=3',
      configuration.destination,
      createRemoteWorkerCommand(configuration.workerPath),
    ],
  };
}

export function createRemoteRequest(job, type = 'dispatch') {
  const request = validateRemoteJob(job, { requireFence: true });
  if (!['dispatch', 'reconcile', 'terminal'].includes(type)) {
    throw new RalphMacSshError('Remote worker request type is invalid.', 'INVALID_REQUEST');
  }
  const serialized = `${JSON.stringify({
    version: 1,
    type,
    job: {
      jobId: request.jobId,
      fence: request.fence,
      repository: request.repository,
      issue: request.issue,
      owner: request.owner,
      baseSha: request.baseSha,
      expectedHost: request.expectedHost,
      model: request.model,
      effort: request.effort,
      agent: request.agent,
      acceptanceCriteria: request.acceptanceCriteria,
      charter: request.charter,
    },
  })}\n`;
  if (Buffer.byteLength(serialized, 'utf8') > 64 * 1024) {
    throw new RalphMacSshError('Remote worker request exceeds the protocol limit.', 'INVALID_REQUEST');
  }
  return serialized;
}

function requestDigest(job) {
  return createHash('sha256').update(JSON.stringify({
    issue: job.issue, owner: job.owner, baseSha: job.baseSha, expectedHost: job.expectedHost, model: job.model,
    effort: job.effort, agent: job.agent, acceptanceCriteria: job.acceptanceCriteria, charter: job.charter,
  })).digest('hex');
}

export function parseRemoteWorkerResponse(output, job) {
  if (typeof output !== 'string' || output.length > 64 * 1024) {
    throw new RalphMacSshError('Remote worker response is missing or exceeds the protocol limit.', 'MALFORMED_RESPONSE');
  }
  const lines = output.trim().split(/\r?\n/);
  if (lines.length !== 1) throw new RalphMacSshError('Remote worker response must contain exactly one record.', 'MALFORMED_RESPONSE');
  let response;
  try {
    response = JSON.parse(lines[0]);
  } catch {
    throw new RalphMacSshError('Remote worker response is not JSON.', 'MALFORMED_RESPONSE');
  }
  const correlated = response?.version === 1 && response.jobId === job.jobId &&
    response.fence === job.fence && response.repository === printFarmerRepository &&
    response.issue === job.issue && response.owner === job.owner &&
    response.baseSha === job.baseSha && response.host === job.expectedHost &&
    validIdentifier(response.sessionId);
  if (!correlated) throw new RalphMacSshError('Remote worker response does not match the dispatched job.', 'MALFORMED_RESPONSE');
  if (response.type === 'accepted') {
    if (!['preparing', 'accepted', 'supervisor-launching', 'launching', 'running', 'orphan-running'].includes(response.state)) {
      throw new RalphMacSshError('Remote acknowledgement has an invalid live state.', 'MALFORMED_RESPONSE');
    }
    return response;
  }
  if (response.type === 'terminal') {
    const hasExitCode = Number.isInteger(response.exitCode);
    const hasSignal = validIdentifier(response.signal);
    const validProcessResult = hasExitCode !== hasSignal;
    const validEvidence = response.workerVerified === true && ['completed', 'failed'].includes(response.state) &&
      validProcessResult && validSha(response.headSha) && typeof response.validationEvidence === 'string' &&
      response.validationEvidence.trim() && typeof response.workingTreeClean === 'boolean' &&
      typeof response.allCommitsPushed === 'boolean' &&
      typeof response.repositoryIdentityVerified === 'boolean' && typeof response.baseAncestor === 'boolean';
    const validSuccess = response.state !== 'completed' ||
      (response.exitCode === 0 && response.signal === undefined &&
       response.workingTreeClean === true && response.allCommitsPushed === true &&
       response.repositoryIdentityVerified === true && response.baseAncestor === true);
    const validFailure = response.state !== 'failed' || hasSignal || response.exitCode !== 0;
    if (!validEvidence || !validSuccess || !validFailure) {
      throw new RalphMacSshError('Remote terminal attestation is malformed.', 'MALFORMED_RESPONSE');
    }
    return response;
  }
  if (response.type === 'failed') {
    const hasExitCode = Number.isInteger(response.exitCode);
    const hasSignal = validIdentifier(response.signal);
    if (response.state !== 'failed' || response.workerVerified !== true ||
        !validIdentifier(response.failureCode) || typeof response.failureMessage !== 'string' ||
        !response.failureMessage.trim() || response.failureMessage.length > 1024 ||
        (hasExitCode && hasSignal) || (hasExitCode && response.exitCode === 0)) {
      throw new RalphMacSshError('Remote failure attestation is malformed.', 'MALFORMED_RESPONSE');
    }
    return response;
  }
  throw new RalphMacSshError('Remote worker response has an invalid type.', 'MALFORMED_RESPONSE');
}

export function parseRemoteAcknowledgement(output, job) {
  const response = parseRemoteWorkerResponse(output, job);
  if (response.type !== 'accepted') {
    throw new RalphMacSshError('Remote worker response is not a live acknowledgement.', 'MALFORMED_RESPONSE');
  }
  return response;
}

function resolveLedgerDirectory({ env = process.env, platform = process.platform, home = os.homedir() } = {}) {
  if (env.RALPH_ADMISSION_LEDGER_DIR) return path.resolve(env.RALPH_ADMISSION_LEDGER_DIR);
  if (platform === 'win32') return path.join(env.LOCALAPPDATA || path.join(home, 'AppData', 'Local'), 'PrintFarmer', 'ralph-admission');
  return path.join(env.XDG_STATE_HOME || path.join(home, '.local', 'state'), 'printfarmer', 'ralph-admission');
}

function ledgerFile(options) {
  return path.join(resolveLedgerDirectory(options), 'printfarmer-jobs.json');
}

function ownerIsAlive(pid) {
  if (!Number.isInteger(pid) || pid <= 0) return undefined;
  try {
    process.kill(pid, 0);
    return true;
  } catch (error) {
    return error.code === 'ESRCH' ? false : undefined;
  }
}

function lockRecord() {
  return {
    token: randomUUID(),
    pid: process.pid,
    expiresAt: new Date(Date.now() + 5 * 60 * 1000).toISOString(),
  };
}

async function reclaimStaleLock(lockFile, { isOwnerAlive = ownerIsAlive } = {}) {
  let observed;
  try {
    observed = JSON.parse(await readFile(lockFile, 'utf8'));
  } catch {
    try {
      const details = await stat(lockFile);
      if (Date.now() - details.mtimeMs < 5 * 60 * 1000) return false;
      observed = { malformed: true, mtimeMs: details.mtimeMs, size: details.size };
    } catch {
      return false;
    }
  }
  if (!observed.malformed && (
    typeof observed.token !== 'string' || !Number.isInteger(observed.pid) ||
    !Number.isFinite(Date.parse(observed.expiresAt)) || Date.parse(observed.expiresAt) >= Date.now() ||
    isOwnerAlive(observed.pid) !== false
  )) return false;
  const reclaimFile = `${lockFile}.reclaim`;
  let reclaim;
  try {
    reclaim = await open(reclaimFile, 'wx');
  } catch (error) {
    if (error.code === 'EEXIST') {
      try {
        const details = await stat(reclaimFile);
        if (Date.now() - details.mtimeMs >= 5 * 60 * 1000) await rm(reclaimFile, { force: true });
      } catch { /* another controller owns or removed the guard */ }
      return false;
    }
    throw error;
  }
  try {
    await reclaim.writeFile(JSON.stringify({ token: randomUUID(), observed: observed.token }));
    let current;
    try {
      current = JSON.parse(await readFile(lockFile, 'utf8'));
    } catch {
      if (!observed.malformed) return false;
      const details = await stat(lockFile).catch(() => undefined);
      if (!details || details.mtimeMs !== observed.mtimeMs || details.size !== observed.size) return false;
      await rm(lockFile, { force: true });
      return true;
    }
    if (observed.malformed || current.token !== observed.token) return false;
    await rm(lockFile, { force: true });
    return true;
  } finally {
    await reclaim.close();
    await rm(reclaimFile, { force: true });
  }
}

async function acquireLock(file, { retries = 40, retryMs = 10, ...options } = {}) {
  for (let attempt = 0; attempt < retries; attempt += 1) {
    try {
      const handle = await open(`${file}.lock`, 'wx');
      try {
        await handle.writeFile(JSON.stringify(lockRecord()));
        return handle;
      } catch (error) {
        await handle.close();
        await rm(`${file}.lock`, { force: true });
        throw error;
      }
    } catch (error) {
      if (error.code !== 'EEXIST') throw error;
      if (await reclaimStaleLock(`${file}.lock`, options)) continue;
      await new Promise((resolve) => setTimeout(resolve, retryMs));
    }
  }
  throw new RalphMacSshError('Another Windows Ralph controller owns the admission ledger.', 'LOCK_TIMEOUT');
}

async function mutateLedger(mutator, options = {}) {
  const file = ledgerFile(options);
  const backup = `${file}.bak`;
  await mkdir(path.dirname(file), { recursive: true });
  const lock = await acquireLock(file, options);
  const temp = `${file}.${process.pid}.${randomUUID()}.tmp`;
  try {
    let ledger = { version: 1, repository: printFarmerRepository, generation: 0, jobs: {} };
    let recoveredFromBackup = false;
    try {
      ledger = JSON.parse(await readFile(file, 'utf8'));
    } catch (error) {
      if (error.code !== 'ENOENT') {
        try {
          ledger = JSON.parse(await readFile(backup, 'utf8'));
          recoveredFromBackup = true;
        } catch {
          throw new RalphMacSshError('Admission ledger is corrupt.', 'CORRUPT_LEDGER');
        }
      }
    }
    if (ledger.version !== 1 || ledger.repository !== printFarmerRepository ||
        !ledger.jobs || typeof ledger.jobs !== 'object' || Array.isArray(ledger.jobs)) {
      throw new RalphMacSshError('Admission ledger has an invalid schema.', 'CORRUPT_LEDGER');
    }
    const result = await mutator(ledger);
    ledger.generation += 1;
    const temporary = await open(temp, 'wx');
    try {
      await temporary.writeFile(`${JSON.stringify(ledger)}\n`, 'utf8');
      await temporary.sync();
    } finally {
      await temporary.close();
    }
    if (!recoveredFromBackup) {
      try {
        await copyFile(file, backup);
      } catch (error) {
        if (error.code !== 'ENOENT') throw error;
      }
    }
    await rename(temp, file);
    return result;
  } finally {
    await rm(temp, { force: true });
    await lock.close();
    await rm(`${file}.lock`, { force: true });
  }
}

function assertFreshEligibility(eligibility, job) {
  const check = safeJson(eligibility, 'Fresh eligibility');
  if (check.repository !== printFarmerRepository || check.issue !== job.issue || check.open !== true ||
      check.exactClaim !== true || check.held !== false || check.blocked !== false || check.linkedPr !== false) {
    throw new RalphMacSshError('Fresh GitHub eligibility or exact claim verification failed.', 'INELIGIBLE');
  }
}

export async function reserveJob({ job, eligibility, mode = 'remote', now = new Date().toISOString(), reservationLeaseMs = 60_000, reservationOwnerPid = process.pid }, options = {}) {
  validateRemoteJob(job);
  assertFreshEligibility(eligibility, job);
  return mutateLedger((ledger) => {
    const existing = ledger.jobs[job.jobId];
    if (existing) {
      if (existing.requestDigest !== requestDigest(job) || existing.mode !== mode) {
        throw new RalphMacSshError('Job identifier is already fenced to different work.', 'FENCED');
      }
      if (!activeJobStates.has(existing.state)) {
        throw new RalphMacSshError('A terminal job identifier cannot be reserved again.', 'FENCED');
      }
      return existing;
    }
    const active = Object.values(ledger.jobs).filter((entry) => activeJobStates.has(entry.state));
    if (active.some((entry) => entry.issue === job.issue)) throw new RalphMacSshError('Issue already has an active Ralph job.', 'ISSUE_OWNED');
    // A released stranded kickoff (issue #2621) frees the slot but not the issue: the created
    // session was never observed processing anything, so it may still wake up and work the issue.
    // Re-admitting the issue before that session is proven gone would put two sessions on the same
    // work — the ledger enforces that here rather than trusting the caller to remember the policy.
    if (Object.values(ledger.jobs).some((entry) => entry.issue === job.issue &&
      entry.failureReason === 'kickoff-unverified' && validIdentifier(entry.strandedSessionId) &&
      entry.strandedSessionCleared !== true)) {
      throw new RalphMacSshError('Issue has a stranded local session that is not yet reconciled.', 'STRANDED_SESSION');
    }
    if (active.length >= 5) throw new RalphMacSshError('All five PrintFarmer Ralph slots are reserved.', 'SLOT_EXHAUSTED');
    // Xcode/CoreSimulator concurrency is a per-Mac constraint, independent of the shared 5-slot
    // pool: a physical Mac can only run one xcodebuild/simctl invocation at a time no matter how
    // many total slots the pool has free, and a git worktree does not isolate that host-wide
    // state. Every remote (mac-dispatched) job is an Xcode/CoreSimulator job, so reject a second
    // active remote job targeting the same expectedHost outright — the caller (Ralph's own poll
    // loop) naturally retries later, which is the "queued" behavior; nothing here builds a
    // separate in-process queue. This reuses the same active-state/stale-reclamation machinery
    // (recoverRemoteDelivery, reconcileMacJob) other reservations already rely on for freshness.
    if (mode === 'remote' && active.some((entry) => entry.mode === 'remote' && entry.expectedHost === job.expectedHost)) {
      throw new RalphMacSshError(`Mac host ${job.expectedHost} already has an active Xcode/CoreSimulator job.`, 'XCODE_HOST_BUSY');
    }
    const entry = {
      jobId: job.jobId, repository: printFarmerRepository, issue: job.issue, owner: job.owner, baseSha: job.baseSha,
      state: 'reserved', mode, fence: ledger.generation + 1, requestDigest: requestDigest(job), createdAt: now, updatedAt: now,
      expectedHost: job.expectedHost,
      reservationOwnerPid, reservationExpiresAt: new Date(Date.parse(now) + reservationLeaseMs).toISOString(),
    };
    ledger.jobs[job.jobId] = entry;
    return entry;
  }, options);
}

export async function reserveLocalJob({ job, eligibility, now = new Date().toISOString(), controllerPid = process.pid }, options = {}) {
  if (!Number.isInteger(controllerPid) || controllerPid <= 0) {
    throw new RalphMacSshError('Local controller process identifier is invalid.', 'INVALID_REQUEST');
  }
  const localJob = { ...job, repository: printFarmerRepository };
  return reserveJob({ job: localJob, eligibility, mode: 'local', now, reservationOwnerPid: controllerPid }, options);
}

// A created session is not a started session: a local session can be created with its worktree and
// branch and still never process its kickoff, sitting idle with zero turns while it holds a slot
// and the issue's claim (issue #2621). Acknowledgement therefore requires the dispatcher to assert
// that it observed the session actually processing the kickoff, and records that assertion — plus
// whether the kickoff had to be resent — as a durable audit field.
export async function acknowledgeLocalJob(jobId, sessionId, { kickoffVerified, kickoffRetried = false, ...options } = {}) {
  if (!validIdentifier(sessionId)) throw new RalphMacSshError('Local session identifier is invalid.', 'INVALID_REQUEST');
  if (kickoffVerified !== true) throw new RalphMacSshError('Verified kickoff processing is required to acknowledge a local session.', 'INVALID_REQUEST');
  if (typeof kickoffRetried !== 'boolean') throw new RalphMacSshError('Kickoff retry evidence must be boolean.', 'INVALID_REQUEST');
  return mutateLedger((ledger) => {
    const entry = ledger.jobs[jobId];
    if (!entry || entry.mode !== 'local' || entry.state !== 'reserved') throw new RalphMacSshError('Only a reserved local job may be acknowledged.', 'INVALID_TRANSITION');
    entry.state = 'accepted';
    entry.sessionId = sessionId;
    entry.local = true;
    entry.kickoffVerified = true;
    entry.kickoffRetried = kickoffRetried;
    delete entry.reservationOwnerPid;
    delete entry.reservationExpiresAt;
    entry.updatedAt = new Date().toISOString();
    return entry;
  }, options);
}

// The stranded-kickoff counterpart of acknowledgeLocalJob: the session exists but never started,
// so neither existing recovery path applies (recoverLocalReservation needs an expired lease and a
// dead controller — the dispatching controller is alive; recoverLostLocalSession needs the session
// to be genuinely absent — it is present, just idle). Release is authorized either by the
// reservation's own live owner (matching the recorded reservation PID) or, when that controller
// died before it could verify the kickoff, by a later controller under exactly the same
// dead-owner-plus-expired-lease proof recoverLocalReservation already requires — without that
// second branch a crashed round would wedge the reservation in 'reserved' forever, with no legal
// transition out and the issue's slot held permanently. The PID match correlates a caller to its
// own reservation inside this machine-local trusted ledger; it is not an authentication boundary,
// and nothing here is weaker than direct write access to the ledger file itself. The disposition is
// terminal: the stranded sessionId is required and retained as an audit record, the issue's slot is
// freed for a fresh jobId, the old jobId stays fenced, and nothing about the stranded session is
// archived, deleted, or otherwise cleaned up. Freeing the slot is not permission to re-dispatch the
// issue: reserveJob keeps that issue blocked (STRANDED_SESSION) until clearStrandedKickoff proves
// the stranded session is gone.
export async function failLocalKickoff(jobId, { controllerPid, kickoffUnverified, sessionId, isOwnerAlive = ownerIsAlive, now = Date.now(), ...options } = {}) {
  if (kickoffUnverified !== true) throw new RalphMacSshError('An explicit unverified-kickoff assertion is required.', 'INVALID_REQUEST');
  if (!Number.isInteger(controllerPid) || controllerPid <= 0) throw new RalphMacSshError('Local controller process identifier is invalid.', 'INVALID_REQUEST');
  if (!validIdentifier(sessionId)) throw new RalphMacSshError('The stranded local session identifier is required.', 'INVALID_REQUEST');
  return mutateLedger((ledger) => {
    const entry = ledger.jobs[jobId];
    if (!entry || entry.mode !== 'local' || entry.state !== 'reserved') {
      throw new RalphMacSshError('Only a reserved local job may fail for an unverified kickoff.', 'INVALID_TRANSITION');
    }
    const ownsReservation = entry.reservationOwnerPid === controllerPid;
    const ownerIsDead = Number.isInteger(entry.reservationOwnerPid) &&
      Number.isFinite(Date.parse(entry.reservationExpiresAt)) &&
      Date.parse(entry.reservationExpiresAt) <= now &&
      isOwnerAlive(entry.reservationOwnerPid) === false;
    if (!ownsReservation && !ownerIsDead) {
      throw new RalphMacSshError('Local reservation is still owned by another live controller.', 'RESERVATION_ACTIVE');
    }
    entry.state = 'failed';
    entry.failureReason = 'kickoff-unverified';
    entry.kickoffVerified = false;
    entry.strandedSessionId = sessionId;
    delete entry.reservationOwnerPid;
    delete entry.reservationExpiresAt;
    entry.updatedAt = new Date(now).toISOString();
    return entry;
  }, options);
}

// The only way a stranded issue becomes dispatchable again. It requires the same authoritative
// session-absence assertion recoverLostLocalSession uses — the ledger cannot observe an app-managed
// session itself — and it clears the issue-level block without resurrecting the fenced jobId or
// deleting the audit record.
export async function clearStrandedKickoff(jobId, { sessionAbsent, now = Date.now(), ...options } = {}) {
  if (sessionAbsent !== true) throw new RalphMacSshError('Authoritative session absence is required.', 'INVALID_REQUEST');
  return mutateLedger((ledger) => {
    const entry = ledger.jobs[jobId];
    if (!entry || entry.mode !== 'local' || entry.failureReason !== 'kickoff-unverified' || !validIdentifier(entry.strandedSessionId)) {
      throw new RalphMacSshError('Only a released stranded kickoff may be reconciled.', 'INVALID_TRANSITION');
    }
    entry.strandedSessionCleared = true;
    entry.updatedAt = new Date(now).toISOString();
    return entry;
  }, options);
}

export async function recoverLocalReservation(jobId, { isOwnerAlive = ownerIsAlive, now = Date.now(), sessionAbsent, ...options } = {}) {  if (sessionAbsent !== true) throw new RalphMacSshError('Authoritative session absence is required.', 'INVALID_REQUEST');
  return mutateLedger((ledger) => {
    const entry = ledger.jobs[jobId];
    if (!entry || entry.mode !== 'local' || entry.state !== 'reserved' ||
        !Number.isInteger(entry.reservationOwnerPid) || !Number.isFinite(Date.parse(entry.reservationExpiresAt)) ||
        Date.parse(entry.reservationExpiresAt) > now || isOwnerAlive(entry.reservationOwnerPid) !== false) {
      throw new RalphMacSshError('Local reservation is still owned by a live controller.', 'RESERVATION_ACTIVE');
    }
    entry.state = 'failed';
    entry.failureReason = 'session-creation-absent';
    delete entry.reservationOwnerPid;
    delete entry.reservationExpiresAt;
    entry.updatedAt = new Date(now).toISOString();
    return entry;
  }, options);
}

// A local job that reached 'accepted'/'running' has a claimed session, but the ledger has no
// way to observe that session's liveness itself (it is an app-managed Copilot session, not an
// OS process the ledger owns a PID for). If that session is later confirmed gone — e.g. it never
// produced a correlated terminal result and no longer appears in the session store — the claim
// must not block the issue's slot forever, and it must not silently vanish or be deleted either.
// This is a fail-closed, explicit "abandoned" disposition: it requires the caller to assert
// authoritative absence (verified externally, the same contract recoverLocalReservation already
// uses), it never runs any destructive cleanup of the session's worktree or artifacts, and it
// keeps the ledger entry (with its sessionId) as a permanent, inspectable audit record distinct
// from a genuine 'failed' terminal result.
export async function recoverLostLocalSession(jobId, { sessionAbsent, now = Date.now(), ...options } = {}) {
  if (sessionAbsent !== true) throw new RalphMacSshError('Authoritative session absence is required.', 'INVALID_REQUEST');
  return mutateLedger((ledger) => {
    const entry = ledger.jobs[jobId];
    if (!entry || entry.mode !== 'local' || !entry.local || !['accepted', 'running'].includes(entry.state) ||
        !validIdentifier(entry.sessionId)) {
      throw new RalphMacSshError('Only an accepted or running local job with a claimed session may be reconciled as abandoned.', 'INVALID_TRANSITION');
    }
    entry.state = 'abandoned';
    entry.failureReason = 'session-lost';
    entry.updatedAt = new Date(now).toISOString();
    return entry;
  }, options);
}

export async function recordLocalTerminalResult(result, options = {}) {
  const value = safeJson(result, 'Local terminal result');
  return mutateLedger((ledger) => {
    const entry = ledger.jobs[value.jobId];
    if (entry?.mode !== 'local' || !entry?.local || !['accepted', 'running'].includes(entry.state) || value.sessionId !== entry.sessionId ||
        !validSha(value.headSha) || !Number.isInteger(value.exitCode) || !value.validationEvidence ||
        value.workingTreeClean !== true || value.allCommitsPushed !== true) {
      throw new RalphMacSshError('Local terminal result lacks correlated evidence.', 'INVALID_TERMINAL_EVIDENCE');
    }
    entry.state = value.exitCode === 0 ? 'completed' : 'failed';
    entry.headSha = value.headSha;
    entry.exitCode = value.exitCode;
    entry.validationEvidence = value.validationEvidence;
    entry.workingTreeClean = true;
    entry.allCommitsPushed = true;
    entry.updatedAt = new Date().toISOString();
    return entry;
  }, options);
}

export async function recordDeliveryIntent(jobId, { deliveryLeaseMs = 60_000, ...options } = {}) {
  if (!Number.isSafeInteger(deliveryLeaseMs) || deliveryLeaseMs <= 0) {
    throw new RalphMacSshError('Delivery lease duration is invalid.', 'INVALID_CONFIGURATION');
  }
  return mutateLedger((ledger) => {
    const entry = ledger.jobs[jobId];
    if (!entry || entry.mode !== 'remote' || !['reserved', 'uncertain'].includes(entry.state)) throw new RalphMacSshError('Only a reserved or uncertain job may be delivered.', 'INVALID_TRANSITION');
    entry.state = 'delivery-intent';
    entry.deliveryOwnerPid = process.pid;
    entry.deliveryExpiresAt = new Date(Date.now() + deliveryLeaseMs).toISOString();
    entry.updatedAt = new Date().toISOString();
    return entry;
  }, options);
}

export async function acknowledgeJob(jobId, acknowledgement, options = {}) {
  return mutateLedger((ledger) => {
    const entry = ledger.jobs[jobId];
    if (!entry || entry.mode !== 'remote' || !['delivery-intent', 'uncertain'].includes(entry.state)) {
      throw new RalphMacSshError('Only a delivered or uncertain job may be acknowledged.', 'INVALID_TRANSITION');
    }
    if (acknowledgement.jobId !== entry.jobId || acknowledgement.fence !== entry.fence ||
        acknowledgement.issue !== entry.issue || acknowledgement.baseSha !== entry.baseSha) {
      throw new RalphMacSshError('Acknowledgement is fenced to a different job.', 'FENCED');
    }
    entry.state = 'accepted';
    entry.sessionId = acknowledgement.sessionId;
    entry.host = acknowledgement.host;
    delete entry.deliveryOwnerPid;
    delete entry.deliveryExpiresAt;
    entry.updatedAt = new Date().toISOString();
    return entry;
  }, options);
}

export async function markUncertain(jobId, options = {}) {
  return mutateLedger((ledger) => {
    const entry = ledger.jobs[jobId];
    if (!entry || entry.state !== 'delivery-intent') throw new RalphMacSshError('Only an in-flight delivery may become uncertain.', 'INVALID_TRANSITION');
    entry.state = 'uncertain';
    delete entry.deliveryOwnerPid;
    delete entry.deliveryExpiresAt;
    entry.updatedAt = new Date().toISOString();
    return entry;
  }, options);
}

export async function recoverRemoteDelivery(jobId, { isOwnerAlive = ownerIsAlive, now = Date.now(), ...options } = {}) {
  return mutateLedger((ledger) => {
    const entry = ledger.jobs[jobId];
    if (!entry || entry.mode !== 'remote' || entry.state !== 'delivery-intent') {
      throw new RalphMacSshError('Only an in-flight remote delivery may be recovered.', 'INVALID_TRANSITION');
    }
    if (!Number.isInteger(entry.deliveryOwnerPid) || !Number.isFinite(Date.parse(entry.deliveryExpiresAt)) ||
        Date.parse(entry.deliveryExpiresAt) > now || isOwnerAlive(entry.deliveryOwnerPid) !== false) {
      throw new RalphMacSshError('Remote delivery is still owned by a live controller.', 'DELIVERY_ACTIVE');
    }
    entry.state = 'uncertain';
    delete entry.deliveryOwnerPid;
    delete entry.deliveryExpiresAt;
    entry.updatedAt = new Date(now).toISOString();
    return entry;
  }, options);
}

async function recordRemoteWorkerResponse(response, options = {}) {
  return mutateLedger((ledger) => {
    const entry = ledger.jobs[response.jobId];
    if (!entry || entry.mode !== 'remote' ||
        !['delivery-intent', 'uncertain', 'accepted', 'running'].includes(entry.state)) {
      throw new RalphMacSshError('Worker response has no active remote reservation.', 'INVALID_TRANSITION');
    }
    const sessionMatches = entry.sessionId === undefined || entry.sessionId === response.sessionId;
    const hostMatches = entry.host === undefined || entry.host === response.host;
    if (response.repository !== printFarmerRepository || response.issue !== entry.issue ||
        response.baseSha !== entry.baseSha || response.fence !== entry.fence ||
        !hostMatches || !validHost(response.host) || !sessionMatches || !validIdentifier(response.sessionId)) {
      throw new RalphMacSshError('Worker response lacks correlated evidence.', 'INVALID_TERMINAL_EVIDENCE');
    }
    entry.sessionId = response.sessionId;
    entry.host = response.host;
    if (response.type === 'accepted') {
      entry.state = 'accepted';
    } else if (response.type === 'terminal') {
      entry.state = response.state;
      entry.headSha = response.headSha;
      entry.exitCode = response.exitCode;
      entry.signal = response.signal;
      entry.validationEvidence = response.validationEvidence;
      entry.workingTreeClean = response.workingTreeClean;
      entry.allCommitsPushed = response.allCommitsPushed;
      entry.repositoryIdentityVerified = response.repositoryIdentityVerified;
      entry.baseAncestor = response.baseAncestor;
      entry.workerVerified = true;
    } else if (response.type === 'failed') {
      entry.state = 'failed';
      entry.failureCode = response.failureCode;
      entry.failureReason = response.failureMessage;
      entry.exitCode = response.exitCode;
      entry.signal = response.signal;
      entry.workerVerified = true;
    } else {
      throw new RalphMacSshError('Worker response type is invalid.', 'MALFORMED_RESPONSE');
    }
    delete entry.deliveryOwnerPid;
    delete entry.deliveryExpiresAt;
    entry.updatedAt = new Date().toISOString();
    return entry;
  }, options);
}

export function runSsh(invocation, input, { spawn = nodeSpawn, timeoutMs = 45_000 } = {}) {
  return new Promise((resolve, reject) => {
    const child = spawn(invocation.command, invocation.args, { stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true });
    let stdout = '';
    let stderr = '';
    let settled = false;
    const finish = (error, value) => {
      if (settled) return;
      settled = true;
      clearTimeout(timeout);
      if (error) reject(error);
      else resolve(value);
    };
    const timeout = setTimeout(() => {
      finish(new RalphMacSshError('SSH worker timed out.', 'SSH_TIMEOUT'));
      child.kill();
    }, timeoutMs);
    const append = (current, chunk) => {
      const next = current + chunk;
      if (next.length > 64 * 1024) {
        child.kill();
        finish(new RalphMacSshError('SSH protocol output exceeds the limit.', 'MALFORMED_RESPONSE'));
      }
      return next;
    };
    child.stdout.on('data', (chunk) => { stdout = append(stdout, chunk.toString('utf8')); });
    child.stderr.on('data', (chunk) => { stderr = append(stderr, chunk.toString('utf8')); });
    child.stdin.on('error', (error) => finish(new RalphMacSshError(`SSH input failed: ${error.message}`, 'SSH_FAILURE')));
    child.on('error', (error) => finish(new RalphMacSshError(`SSH execution failed: ${error.message}`, 'SSH_FAILURE')));
    child.on('close', (code) => {
      if (code !== 0) finish(new RalphMacSshError(`SSH worker exited with status ${code}: ${stderr}`, 'SSH_FAILURE'));
      else finish(undefined, stdout);
    });
    child.stdin.end(input, 'utf8');
  });
}

export async function dispatchMacJob({ job, eligibility, controllerPid }, options = {}) {
  if (!Number.isInteger(controllerPid) || controllerPid <= 0) {
    throw new RalphMacSshError('Remote dispatch requires the Ralph controller process identifier.', 'INVALID_REQUEST');
  }
  const configuration = loadMacSshConfiguration(options);
  const request = { ...job, expectedHost: configuration.expectedHost };
  if (!['gpt-5.6-terra', 'gpt-5.6-luna'].includes(request.model) || request.effort !== 'medium' || request.agent !== 'squad') {
    throw new RalphMacSshError('Remote jobs must use an approved model, effort, and squad agent.', 'INVALID_REQUEST');
  }
  createRemoteRequest({ ...request, fence: Number.MAX_SAFE_INTEGER });
  let reservation = await reserveJob({ job: request, eligibility, reservationOwnerPid: controllerPid }, options);
  if (reservation.state === 'delivery-intent') {
    await recoverRemoteDelivery(request.jobId, options);
    reservation = await reserveJob({ job: request, eligibility, reservationOwnerPid: controllerPid }, options);
  }
  request.fence = reservation.fence;
  const reconciling = reservation.state === 'uncertain';
  if (['reserved', 'uncertain'].includes(reservation.state)) await recordDeliveryIntent(request.jobId, options);
  else {
    throw new RalphMacSshError('Job is already delivered and must be reconciled by its existing session.', 'INVALID_TRANSITION');
  }
  try {
    const output = await runSsh(
      createSshInvocation(configuration),
      createRemoteRequest(request, reconciling ? 'reconcile' : 'dispatch'),
      options,
    );
    const response = parseRemoteWorkerResponse(output, request);
    return recordRemoteWorkerResponse(response, options);
  } catch (error) {
    await markUncertain(request.jobId, options);
    throw error;
  }
}

export async function reconcileMacJob({ job }, options = {}) {
  const configuration = loadMacSshConfiguration(options);
  validateRemoteJob(job);
  const reservation = await mutateLedger((ledger) => {
    const entry = ledger.jobs[job.jobId];
    if (!entry || entry.mode !== 'remote' || !['accepted', 'running'].includes(entry.state) ||
        entry.requestDigest !== requestDigest(job)) {
      throw new RalphMacSshError('Only the matching accepted remote job may be reconciled.', 'INVALID_TRANSITION');
    }
    return entry;
  }, options);
  const request = {
    ...job,
    fence: reservation.fence,
    expectedHost: configuration.expectedHost,
  };
  const output = await runSsh(
    createSshInvocation(configuration),
    createRemoteRequest(request, 'reconcile'),
    options,
  );
  const response = parseRemoteWorkerResponse(output, request);
  return recordRemoteWorkerResponse(response, options);
}

import { randomUUID } from 'node:crypto';
import { mkdir, open, readFile, rename, rm, writeFile } from 'node:fs/promises';
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
  return /^[A-Za-z0-9][A-Za-z0-9.-]*$/.test(value);
}

function validAbsolutePosixPath(value) {
  return /^\/[A-Za-z0-9._/@+=,:-]+$/.test(value) && !value.includes('//') && !value.includes('/../');
}

function validSha(value) {
  return /^[0-9a-f]{40}$/i.test(value);
}

function validIdentifier(value) {
  return /^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$/.test(value);
}

function safeJson(value, name) {
  if (!value || typeof value !== 'object' || Array.isArray(value)) {
    throw new RalphMacSshError(`${name} must be an object.`, 'INVALID_REQUEST');
  }
  return value;
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
  if (!path.isAbsolute(knownHosts)) {
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
      configuration.destination,
      createRemoteWorkerCommand(configuration.workerPath),
    ],
  };
}

export function createRemoteRequest(job) {
  const request = safeJson(job, 'Job');
  if (request.repository !== printFarmerRepository) {
    throw new RalphMacSshError('Remote macOS dispatch is limited to OlyForge3D/PrintFarmer.', 'UNSUPPORTED_REPOSITORY');
  }
  if (!Number.isSafeInteger(request.issue) || request.issue <= 0 || !validIdentifier(request.jobId) ||
      !validIdentifier(request.owner) || !validSha(request.baseSha) || !Array.isArray(request.acceptanceCriteria)) {
    throw new RalphMacSshError('Remote job is malformed.', 'INVALID_REQUEST');
  }
  if (request.model !== 'gpt-5.6-terra' || request.effort !== 'medium' || request.agent !== 'squad') {
    throw new RalphMacSshError('Remote implementation jobs must use the approved model, effort, and squad agent.', 'INVALID_REQUEST');
  }
  return `${JSON.stringify({
    version: 1,
    type: 'dispatch',
    job: {
      jobId: request.jobId,
      repository: request.repository,
      issue: request.issue,
      owner: request.owner,
      baseSha: request.baseSha,
      model: request.model,
      effort: request.effort,
      agent: request.agent,
      acceptanceCriteria: request.acceptanceCriteria,
      charter: request.charter,
    },
  })}\n`;
}

export function parseRemoteAcknowledgement(output, job) {
  if (typeof output !== 'string' || output.length > 64 * 1024) {
    throw new RalphMacSshError('Remote acknowledgement is missing or exceeds the protocol limit.', 'MALFORMED_RESPONSE');
  }
  const lines = output.trim().split(/\r?\n/);
  if (lines.length !== 1) throw new RalphMacSshError('Remote acknowledgement must contain exactly one record.', 'MALFORMED_RESPONSE');
  let acknowledgement;
  try {
    acknowledgement = JSON.parse(lines[0]);
  } catch {
    throw new RalphMacSshError('Remote acknowledgement is not JSON.', 'MALFORMED_RESPONSE');
  }
  if (
    acknowledgement?.version !== 1 || acknowledgement.type !== 'accepted' ||
    acknowledgement.jobId !== job.jobId || acknowledgement.repository !== printFarmerRepository ||
    acknowledgement.issue !== job.issue || acknowledgement.owner !== job.owner ||
    acknowledgement.baseSha !== job.baseSha || acknowledgement.host !== job.expectedHost ||
    !validIdentifier(acknowledgement.sessionId)
  ) throw new RalphMacSshError('Remote acknowledgement does not match the dispatched job.', 'MALFORMED_RESPONSE');
  return acknowledgement;
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
    return false;
  }
  if (
    typeof observed.token !== 'string' || !Number.isInteger(observed.pid) ||
    !Number.isFinite(Date.parse(observed.expiresAt)) || Date.parse(observed.expiresAt) >= Date.now() ||
    isOwnerAlive(observed.pid) !== false
  ) return false;
  const reclaimFile = `${lockFile}.reclaim`;
  let reclaim;
  try {
    reclaim = await open(reclaimFile, 'wx');
  } catch (error) {
    if (error.code === 'EEXIST') return false;
    throw error;
  }
  try {
    await reclaim.writeFile(JSON.stringify({ token: randomUUID(), observed: observed.token }));
    const current = JSON.parse(await readFile(lockFile, 'utf8'));
    if (current.token !== observed.token) return false;
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
  await mkdir(path.dirname(file), { recursive: true });
  const lock = await acquireLock(file, options);
  const temp = `${file}.${process.pid}.${randomUUID()}.tmp`;
  try {
    let ledger = { version: 1, repository: printFarmerRepository, generation: 0, jobs: {} };
    try {
      ledger = JSON.parse(await readFile(file, 'utf8'));
    } catch (error) {
      if (error.code !== 'ENOENT') throw new RalphMacSshError('Admission ledger is corrupt.', 'CORRUPT_LEDGER');
    }
    if (ledger.version !== 1 || ledger.repository !== printFarmerRepository || !ledger.jobs || typeof ledger.jobs !== 'object') {
      throw new RalphMacSshError('Admission ledger has an invalid schema.', 'CORRUPT_LEDGER');
    }
    const result = await mutator(ledger);
    ledger.generation += 1;
    await writeFile(temp, `${JSON.stringify(ledger)}\n`, { encoding: 'utf8', flag: 'wx' });
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

export async function reserveJob({ job, eligibility, now = new Date().toISOString() }, options = {}) {
  createRemoteRequest(job);
  assertFreshEligibility(eligibility, job);
  return mutateLedger((ledger) => {
    const existing = ledger.jobs[job.jobId];
    if (existing) {
      if (existing.issue !== job.issue || existing.owner !== job.owner || existing.baseSha !== job.baseSha) {
        throw new RalphMacSshError('Job identifier is already fenced to different work.', 'FENCED');
      }
      return existing;
    }
    const active = Object.values(ledger.jobs).filter((entry) => activeJobStates.has(entry.state));
    if (active.some((entry) => entry.issue === job.issue)) throw new RalphMacSshError('Issue already has an active Ralph job.', 'ISSUE_OWNED');
    if (active.length >= 5) throw new RalphMacSshError('All five PrintFarmer Ralph slots are reserved.', 'SLOT_EXHAUSTED');
    const entry = {
      jobId: job.jobId, repository: printFarmerRepository, issue: job.issue, owner: job.owner, baseSha: job.baseSha,
      state: 'reserved', fence: ledger.generation + 1, createdAt: now, updatedAt: now,
    };
    ledger.jobs[job.jobId] = entry;
    return entry;
  }, options);
}

export async function recordDeliveryIntent(jobId, options = {}) {
  return mutateLedger((ledger) => {
    const entry = ledger.jobs[jobId];
    if (!entry || entry.state !== 'reserved') throw new RalphMacSshError('Only a reserved job may be delivered.', 'INVALID_TRANSITION');
    entry.state = 'delivery-intent';
    entry.updatedAt = new Date().toISOString();
    return entry;
  }, options);
}

export async function acknowledgeJob(jobId, acknowledgement, options = {}) {
  return mutateLedger((ledger) => {
    const entry = ledger.jobs[jobId];
    if (!entry || !['delivery-intent', 'uncertain'].includes(entry.state)) {
      throw new RalphMacSshError('Only a delivered or uncertain job may be acknowledged.', 'INVALID_TRANSITION');
    }
    if (acknowledgement.jobId !== entry.jobId || acknowledgement.issue !== entry.issue || acknowledgement.baseSha !== entry.baseSha) {
      throw new RalphMacSshError('Acknowledgement is fenced to a different job.', 'FENCED');
    }
    entry.state = 'accepted';
    entry.sessionId = acknowledgement.sessionId;
    entry.host = acknowledgement.host;
    entry.updatedAt = new Date().toISOString();
    return entry;
  }, options);
}

export async function markUncertain(jobId, options = {}) {
  return mutateLedger((ledger) => {
    const entry = ledger.jobs[jobId];
    if (!entry || entry.state !== 'delivery-intent') throw new RalphMacSshError('Only an in-flight delivery may become uncertain.', 'INVALID_TRANSITION');
    entry.state = 'uncertain';
    entry.updatedAt = new Date().toISOString();
    return entry;
  }, options);
}

export async function recordTerminalResult(result, options = {}) {
  const value = safeJson(result, 'Terminal result');
  return mutateLedger((ledger) => {
    const entry = ledger.jobs[value.jobId];
    if (!entry || !activeJobStates.has(entry.state)) throw new RalphMacSshError('Terminal result has no active reservation.', 'INVALID_TRANSITION');
    if (value.repository !== printFarmerRepository || value.issue !== entry.issue || value.baseSha !== entry.baseSha ||
        value.host !== entry.host || !validSha(value.headSha) || !Number.isInteger(value.exitCode) ||
        !value.validationEvidence || value.workingTreeClean !== true || value.allCommitsPushed !== true) {
      throw new RalphMacSshError('Terminal result lacks correlated evidence.', 'INVALID_TERMINAL_EVIDENCE');
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

export function runSsh(invocation, input, { spawn = nodeSpawn } = {}) {
  return new Promise((resolve, reject) => {
    const child = spawn(invocation.command, invocation.args, { stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true });
    let stdout = '';
    let stderr = '';
    const append = (current, chunk) => {
      const next = current + chunk;
      if (next.length > 64 * 1024) {
        child.kill();
        reject(new RalphMacSshError('SSH protocol output exceeds the limit.', 'MALFORMED_RESPONSE'));
      }
      return next;
    };
    child.stdout.on('data', (chunk) => { stdout = append(stdout, chunk.toString('utf8')); });
    child.stderr.on('data', (chunk) => { stderr = append(stderr, chunk.toString('utf8')); });
    child.on('error', (error) => reject(new RalphMacSshError(`SSH execution failed: ${error.message}`, 'SSH_FAILURE')));
    child.on('close', (code) => {
      if (code !== 0) reject(new RalphMacSshError(`SSH worker exited with status ${code}: ${stderr}`, 'SSH_FAILURE'));
      else resolve(stdout);
    });
    child.stdin.end(input, 'utf8');
  });
}

export async function dispatchMacJob({ job, eligibility }, options = {}) {
  const configuration = loadMacSshConfiguration(options);
  const request = { ...job, expectedHost: configuration.expectedHost };
  await reserveJob({ job: request, eligibility }, options);
  await recordDeliveryIntent(request.jobId, options);
  try {
    const output = await runSsh(createSshInvocation(configuration), createRemoteRequest(request), options);
    const acknowledgement = parseRemoteAcknowledgement(output, request);
    return acknowledgeJob(request.jobId, acknowledgement, options);
  } catch (error) {
    await markUncertain(request.jobId, options);
    throw error;
  }
}

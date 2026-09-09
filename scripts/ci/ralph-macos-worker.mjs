#!/usr/bin/env node
import { createHash, randomUUID } from 'node:crypto';
import { spawn } from 'node:child_process';
import { closeSync, openSync } from 'node:fs';
import { mkdir, open, readFile, rename, rm, stat } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';

const repository = 'OlyForge3D/PrintFarmer';
const stateRoot = process.env.RALPH_MAC_WORKER_STATE_ROOT;
const repositoryRoot = process.env.RALPH_MAC_WORKER_REPOSITORY_ROOT;
const worktreeRoot = process.env.RALPH_MAC_WORKER_WORKTREE_ROOT;
const copilotPath = process.env.RALPH_MAC_WORKER_COPILOT_PATH;
const expectedOrigin = process.env.RALPH_MAC_WORKER_EXPECTED_ORIGIN;
const baseRef = process.env.RALPH_MAC_WORKER_BASE_REF;
const expectedHost = process.env.RALPH_MAC_WORKER_EXPECTED_HOST;
const testMode = process.env.RALPH_MAC_WORKER_TEST_MODE === 'true';
const actualHost = testMode ? process.env.RALPH_MAC_WORKER_TEST_HOSTNAME : os.hostname();

function fail(message) {
  throw new Error(message);
}

function identifier(value) {
  return typeof value === 'string' && /^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$/.test(value);
}

function sha(value) {
  return typeof value === 'string' && /^[0-9a-f]{40}$/i.test(value);
}

function absolute(value) {
  return typeof value === 'string' && path.isAbsolute(value) &&
    !value.split(/[\\/]/).includes('..') && !/[\0\r\n]/.test(value);
}

function host(value) {
  return typeof value === 'string' && /^[A-Za-z0-9][A-Za-z0-9.-]{0,252}$/.test(value);
}

function outsideRepository(value) {
  const relative = path.relative(repositoryRoot, value);
  return relative.startsWith('..') && !path.isAbsolute(relative);
}

function validateConfiguration() {
  if ((!testMode && process.platform !== 'darwin') ||
      ![stateRoot, repositoryRoot, worktreeRoot, copilotPath].every(absolute) ||
      !outsideRepository(stateRoot) || !outsideRepository(worktreeRoot) ||
      !host(expectedHost) || actualHost !== expectedHost ||
      typeof expectedOrigin !== 'string' || !expectedOrigin || /[\r\n]/.test(expectedOrigin) ||
      typeof baseRef !== 'string' || !/^[A-Za-z0-9][A-Za-z0-9._/-]{0,127}$/.test(baseRef) ||
      baseRef.includes('..') || baseRef.includes('//')) {
    fail('Mac worker configuration does not match its trusted host, repository, or absolute paths.');
  }
}

function validateJob(job) {
  if (!job || job.repository !== repository || !Number.isSafeInteger(job.issue) || job.issue <= 0 ||
      !identifier(job.jobId) || !Number.isSafeInteger(job.fence) || job.fence <= 0 ||
      !identifier(job.owner) || !sha(job.baseSha) ||
      !['gpt-5.6-terra', 'gpt-5.6-luna'].includes(job.model) || job.effort !== 'medium' ||
      job.agent !== 'squad' || !Array.isArray(job.acceptanceCriteria) ||
      job.acceptanceCriteria.some((criterion) => typeof criterion !== 'string' || !criterion.trim()) ||
      (job.charter !== undefined && (typeof job.charter !== 'string' || !job.charter.trim()))) {
    fail('Malformed remote job.');
  }
}

function digest(job) {
  return createHash('sha256').update(JSON.stringify(job)).digest('hex');
}

function fileFor(jobId) {
  return path.join(stateRoot, `${jobId}.json`);
}

function lockFor(jobId) {
  return path.join(stateRoot, '.locks', `${jobId}.lock`);
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

async function replaceFile(target, content) {
  const temporary = `${target}.${randomUUID()}.tmp`;
  const handle = await open(temporary, 'wx', 0o600);
  try {
    await handle.writeFile(`${JSON.stringify(content)}\n`, 'utf8');
    await handle.sync();
  } finally {
    await handle.close();
  }
  if (testMode && process.platform === 'win32') await rm(target, { force: true });
  await rename(temporary, target);
  if (!testMode) {
    const directory = await open(path.dirname(target), 'r');
    try {
      await directory.sync();
    } finally {
      await directory.close();
    }
  }
}

async function reclaimDeadLock(lockFile) {
  const leaseMs = testMode ? 100 : 30_000;
  let observed;
  try {
    observed = JSON.parse(await readFile(lockFile, 'utf8'));
    if (!identifier(observed?.token) || !Number.isInteger(observed?.pid) ||
        !Number.isFinite(Date.parse(observed?.createdAt))) {
      throw new Error('Lock schema is incomplete.');
    }
  } catch {
    try {
      const details = await stat(lockFile);
      if (Date.now() - details.mtimeMs < leaseMs) return false;
      observed = { malformed: true, mtimeMs: details.mtimeMs, size: details.size };
    } catch {
      return false;
    }
  }
  if (!observed.malformed && (
    Date.now() - Date.parse(observed.createdAt) < leaseMs ||
    ownerIsAlive(observed.pid) !== false
  )) {
    return false;
  }
  const guardFile = `${lockFile}.reclaim`;
  let guard;
  try {
    guard = await open(guardFile, 'wx', 0o600);
  } catch (error) {
    if (error.code === 'EEXIST') return false;
    throw error;
  }
  try {
    let current;
    try {
      current = JSON.parse(await readFile(lockFile, 'utf8'));
      if (!identifier(current?.token) || !Number.isInteger(current?.pid) ||
          !Number.isFinite(Date.parse(current?.createdAt))) {
        throw new Error('Lock schema is incomplete.');
      }
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
    await guard.close();
    await rm(guardFile, { force: true });
  }
}

async function withJobLock(jobId, action) {
  await mkdir(path.dirname(lockFor(jobId)), { recursive: true, mode: 0o700 });
  const lockFile = lockFor(jobId);
  let handle;
  for (let attempt = 0; attempt < 200; attempt += 1) {
    try {
      handle = await open(lockFile, 'wx', 0o600);
      await handle.writeFile(JSON.stringify({ token: randomUUID(), pid: process.pid, createdAt: new Date().toISOString() }));
      await handle.sync();
      break;
    } catch (error) {
      if (error.code !== 'EEXIST') throw error;
      if (await reclaimDeadLock(lockFile)) continue;
      await new Promise((resolve) => setTimeout(resolve, 10));
    }
  }
  if (!handle) fail('Another worker process owns this job record.');
  try {
    return await action();
  } finally {
    await handle.close();
    await rm(lockFile, { force: true });
  }
}

async function persist(jobId, record) {
  await mkdir(stateRoot, { recursive: true, mode: 0o700 });
  await replaceFile(fileFor(jobId), record);
}

async function load(jobId) {
  try {
    const record = JSON.parse(await readFile(fileFor(jobId), 'utf8'));
    if (!record || record.version !== 1 || record.job?.jobId !== jobId) fail('Worker job record is corrupt.');
    return record;
  } catch (error) {
    if (error.code === 'ENOENT') return undefined;
    throw error;
  }
}

async function mutate(jobId, mutator) {
  return withJobLock(jobId, async () => {
    const record = await load(jobId);
    const result = await mutator(record);
    if (result) await persist(jobId, result);
    return result;
  });
}

function acknowledgement(record) {
  if (!record || !identifier(record.sessionId) ||
      !['accepted', 'supervisor-launching', 'launching', 'running'].includes(record.state)) {
    fail('Job has not reached durable acceptance.');
  }
  return {
    ...correlation(record),
    version: 1,
    type: 'accepted',
    state: record.state,
  };
}

function correlation(record) {
  return {
    jobId: record.job.jobId,
    fence: record.job.fence,
    repository,
    issue: record.job.issue,
    owner: record.job.owner,
    baseSha: record.job.baseSha,
    host: expectedHost,
    sessionId: record.sessionId,
  };
}

function failedResponse(record) {
  if (!record || record.state !== 'failed' || !identifier(record.sessionId)) fail('Job is not a correlated worker failure.');
  return {
    ...correlation(record),
    version: 1,
    type: 'failed',
    state: 'failed',
    workerVerified: true,
    failureCode: identifier(record.processResult?.errorCode) ? record.processResult.errorCode : 'WORKER_FAILURE',
    failureMessage: 'Mac worker failed before a terminal process result was available.',
  };
}

function copilotPrompt(job) {
  return [
    `Ralph job ${job.jobId}, fence ${job.fence}. Work only in this isolated PrintFarmer worktree.`,
    `Issue #${job.issue}; base SHA ${job.baseSha}. Read ${job.charter || '.github/copilot-instructions.md'} before work.`,
    'Keep the job marker and actual session identity in the final report. Do not create another session or worktree.',
    ...job.acceptanceCriteria,
  ].join('\n');
}

function runProcess(command, args, options = {}) {
  return new Promise((resolve, reject) => {
    const { timeoutMs = 30_000, ...spawnOptions } = options;
    const child = spawn(command, args, { ...spawnOptions, windowsHide: true });
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
      child.kill();
      finish(new Error(`${command} timed out after ${timeoutMs}ms.`));
    }, timeoutMs);
    child.stdout?.on('data', (chunk) => {
      stdout += chunk;
      if (stdout.length > 64 * 1024) child.kill();
    });
    child.stderr?.on('data', (chunk) => {
      stderr += chunk;
      if (stderr.length > 64 * 1024) child.kill();
    });
    child.once('error', (error) => finish(error));
    child.once('close', (code, signal) => {
      if (code === 0) finish(undefined, stdout.trim());
      else finish(new Error(`${command} exited with status ${code ?? signal}: ${stderr.trim()}`));
    });
  });
}

function runGit(args, options = {}) {
  return runProcess('git', args, { stdio: ['ignore', 'pipe', 'pipe'], ...options });
}

async function validateRepository(job) {
  const [inside, origin, resolvedBase] = await Promise.all([
    runGit(['-C', repositoryRoot, 'rev-parse', '--is-inside-work-tree']),
    runGit(['-C', repositoryRoot, 'remote', 'get-url', 'origin']),
    runGit(['-C', repositoryRoot, 'rev-parse', `${job.baseSha}^{commit}`]),
  ]);
  if (inside !== 'true' || origin !== expectedOrigin || resolvedBase.toLowerCase() !== job.baseSha.toLowerCase()) {
    fail('Configured repository identity or base commit does not match the job.');
  }
  await runGit(['-C', repositoryRoot, 'merge-base', '--is-ancestor', job.baseSha, baseRef]);
}

function copilotCommand() {
  if (!testMode || !process.env.RALPH_MAC_WORKER_COPILOT_PREFIX_JSON) {
    return { command: copilotPath, prefix: [] };
  }
  let prefix;
  try {
    prefix = JSON.parse(process.env.RALPH_MAC_WORKER_COPILOT_PREFIX_JSON);
  } catch {
    fail('Test Copilot executable prefix must be JSON.');
  }
  if (!Array.isArray(prefix) || prefix.length > 8 ||
      prefix.some((argument) => typeof argument !== 'string' || argument.length > 4096)) {
    fail('Test Copilot executable prefix is invalid.');
  }
  return { command: copilotPath, prefix };
}

function childEnvironment() {
  const allowed = [
    'HOME', 'PATH', 'SHELL', 'TMPDIR', 'TMP', 'TEMP', 'LANG', 'LC_ALL', 'LC_CTYPE',
    'TERM', 'USER', 'LOGNAME', 'SSH_AUTH_SOCK', 'XDG_CONFIG_HOME', 'XDG_DATA_HOME',
    'XDG_STATE_HOME', 'NVM_DIR',
  ];
  const env = Object.fromEntries(allowed.filter((name) => process.env[name] !== undefined).map((name) => [name, process.env[name]]));
  if (testMode) {
    for (const [name, value] of Object.entries(process.env)) {
      if (name.startsWith('RALPH_MAC_WORKER_FAKE_')) env[name] = value;
    }
  }
  return env;
}

function waitForSpawn(child) {
  return new Promise((resolve, reject) => {
    child.once('spawn', resolve);
    child.once('error', reject);
  });
}

function waitForExit(child) {
  return new Promise((resolve) => {
    child.once('error', (error) => resolve({ error }));
    child.once('close', (exitCode, signal) => resolve({ exitCode, signal }));
  });
}

async function runJob(jobId, launchToken) {
  validateConfiguration();
  let record = await mutate(jobId, (current) => {
    if (!current || current.state !== 'supervisor-launching' || current.launchToken !== launchToken) {
      fail('Supervisor launch is not owned by this worker.');
    }
    current.state = 'launching';
    current.supervisorPid = process.pid;
    current.launchStartedAt = new Date().toISOString();
    return current;
  });
  if (testMode && process.env.RALPH_MAC_WORKER_TEST_CRASH_AT === 'after-launch-intent') process.exit(86);

  const logs = path.join(stateRoot, 'logs');
  await mkdir(logs, { recursive: true, mode: 0o700 });
  const stdoutPath = path.join(logs, `${jobId}.stdout.log`);
  const stderrPath = path.join(logs, `${jobId}.stderr.log`);
  record = await mutate(jobId, (current) => {
    if (!current || current.state !== 'launching' || current.launchToken !== launchToken) fail('Worker launch ownership changed.');
    current.stdoutPath = stdoutPath;
    current.stderrPath = stderrPath;
    return current;
  });

  let stdout;
  let stderr;
  let child;
  try {
    stdout = openSync(stdoutPath, 'a', 0o600);
    stderr = openSync(stderrPath, 'a', 0o600);
    const { command, prefix } = copilotCommand();
    child = spawn(command, [
      ...prefix,
      '-C', record.worktree,
      '--agent', 'squad',
      '--model', record.job.model,
      '--reasoning-effort', record.job.effort,
      '--mode', 'autopilot',
      '--allow-all-tools',
      '--no-ask-user',
      '--no-remote',
      '--no-remote-export',
      '--silent',
      '--prompt', copilotPrompt(record.job),
    ], {
      cwd: record.worktree,
      detached: true,
      stdio: ['ignore', stdout, stderr],
      env: childEnvironment(),
      shell: false,
      windowsHide: true,
    });
    const outcomePromise = waitForExit(child);
    await waitForSpawn(child);
    if (testMode && process.env.RALPH_MAC_WORKER_TEST_CRASH_AT === 'after-child-spawn-before-pid') process.exit(87);
    record = await mutate(jobId, (current) => {
      if (!current || current.state !== 'launching' || current.launchToken !== launchToken) fail('Worker launch ownership changed.');
      current.state = 'running';
      current.pid = child.pid;
      current.startedAt = new Date().toISOString();
      return current;
    });
    const outcome = await outcomePromise;
    record = await mutate(jobId, (current) => {
      if (!current || current.launchToken !== launchToken || !['launching', 'running'].includes(current.state)) {
        fail('Worker process result has no owned launch.');
      }
      current.state = outcome.error ? 'failed' : 'awaiting-terminal-evidence';
      current.processResult = outcome.error
        ? {
            error: outcome.error.message,
            errorCode: typeof outcome.error.code === 'string' ? outcome.error.code : undefined,
            completedAt: new Date().toISOString(),
          }
        : {
            ...(Number.isInteger(outcome.exitCode) ? { exitCode: outcome.exitCode } : {}),
            ...(typeof outcome.signal === 'string' ? { signal: outcome.signal } : {}),
            completedAt: new Date().toISOString(),
          };
      delete current.pid;
      return current;
    });
  } catch (error) {
    await mutate(jobId, (current) => {
      if (!current || current.launchToken !== launchToken ||
          !['supervisor-launching', 'launching', 'running'].includes(current.state)) return current;
      current.state = 'failed';
      current.processResult = {
        error: error instanceof Error ? error.message : String(error),
        errorCode: typeof error?.code === 'string' ? error.code : undefined,
        completedAt: new Date().toISOString(),
      };
      delete current.pid;
      return current;
    });
    throw error;
  } finally {
    if (stdout !== undefined) closeSync(stdout);
    if (stderr !== undefined) closeSync(stderr);
  }
}

async function startSupervisor(jobId, launchToken) {
  const child = spawn(process.execPath, [process.argv[1], '--run', jobId, launchToken], {
    detached: true,
    stdio: 'ignore',
    env: process.env,
    shell: false,
    windowsHide: true,
  });
  try {
    await waitForSpawn(child);
  } catch (error) {
    await mutate(jobId, (record) => {
      if (record?.state === 'supervisor-launching' && record.launchToken === launchToken) {
        record.state = 'failed';
        record.processResult = {
          error: error.message,
          errorCode: typeof error.code === 'string' ? error.code : undefined,
          completedAt: new Date().toISOString(),
        };
      }
      return record;
    });
    throw error;
  }
  child.unref();
}

async function dispatch(job) {
  validateJob(job);
  const jobDigest = digest(job);
  const prepared = await withJobLock(job.jobId, async () => {
    const existing = await load(job.jobId);
    if (existing) {
      if (existing.digest !== jobDigest) fail('Job identifier is fenced to a different request.');
      return { response: await workerResponse(existing) };
    }
    await validateRepository(job);
    const worktree = path.join(worktreeRoot, job.jobId);
    const branch = `ralph/${job.jobId}`;
    const record = {
      version: 1,
      job,
      digest: jobDigest,
      sessionId: `mac-${job.jobId}-${randomUUID()}`,
      worktree,
      branch,
      state: 'preparing',
      preparedAt: new Date().toISOString(),
      terminal: undefined,
    };
    await persist(job.jobId, record);
    try {
      await mkdir(worktreeRoot, { recursive: true, mode: 0o700 });
      await runGit(['-C', repositoryRoot, 'worktree', 'add', '-b', branch, worktree, job.baseSha]);
      record.state = 'accepted';
      record.acceptedAt = new Date().toISOString();
      await persist(job.jobId, record);
      record.state = 'supervisor-launching';
      record.launchToken = randomUUID();
      record.supervisorRequestedAt = new Date().toISOString();
      await persist(job.jobId, record);
      return { record, launchToken: record.launchToken };
    } catch (error) {
      record.state = 'failed';
      record.processResult = { error: error.message, completedAt: new Date().toISOString() };
      await persist(job.jobId, record);
      throw error;
    }
  });
  if (prepared.response) return prepared.response;
  await startSupervisor(job.jobId, prepared.launchToken);
  return acknowledgement(prepared.record);
}

async function inspectTerminalRepository(record) {
  const [headSha, status, remoteHead] = await Promise.all([
    runGit(['-C', record.worktree, 'rev-parse', 'HEAD']),
    runGit(['-C', record.worktree, 'status', '--porcelain']),
    runGit(['-C', record.worktree, 'ls-remote', '--exit-code', 'origin', `refs/heads/${record.branch}`])
      .catch(() => ''),
  ]);
  const pushedSha = remoteHead.split(/\s+/)[0];
  return {
    headSha,
    workingTreeClean: status === '',
    allCommitsPushed: pushedSha?.toLowerCase() === headSha.toLowerCase(),
  };
}

async function terminalResponse(record) {
  if (record.terminal) return record.terminal;
  if (record.state !== 'awaiting-terminal-evidence' ||
      (!Number.isInteger(record.processResult?.exitCode) && typeof record.processResult?.signal !== 'string')) {
    fail('Job does not have a terminal process result.');
  }
  const gitEvidence = await inspectTerminalRepository(record);
  const completed = record.processResult.exitCode === 0 && record.processResult.signal === undefined;
  if (completed && (!gitEvidence.workingTreeClean || !gitEvidence.allCommitsPushed)) {
    fail('Successful worker process lacks clean, pushed Git evidence.');
  }
  record.state = completed ? 'completed' : 'failed';
  record.terminal = {
    ...correlation(record),
    ...gitEvidence,
    version: 1,
    type: 'terminal',
    state: record.state,
    workerVerified: true,
    ...(Number.isInteger(record.processResult.exitCode) ? { exitCode: record.processResult.exitCode } : {}),
    ...(typeof record.processResult.signal === 'string' ? { signal: record.processResult.signal } : {}),
    validationEvidence: completed
      ? 'Mac worker verified exit 0 with a clean worktree and matching pushed branch.'
      : 'Mac worker verified a non-success process result.',
    recordedAt: new Date().toISOString(),
  };
  await persist(record.job.jobId, record);
  return record.terminal;
}

async function workerResponse(record) {
  if (record.state === 'awaiting-terminal-evidence' || record.terminal) return terminalResponse(record);
  if (record.state === 'failed') return failedResponse(record);
  return acknowledgement(record);
}

async function readRequest() {
  const input = await new Promise((resolve, reject) => {
    let content = '';
    process.stdin.setEncoding('utf8');
    process.stdin.on('data', (chunk) => {
      content += chunk;
      if (content.length > 64 * 1024) process.stdin.destroy(new Error('Request exceeds protocol limit.'));
    });
    process.stdin.on('end', () => resolve(content));
    process.stdin.on('error', reject);
  });
  let request;
  try {
    request = JSON.parse(input);
  } catch {
    fail('Worker request must be one JSON object.');
  }
  if (!request || request.version !== 1 || !['dispatch', 'reconcile', 'terminal'].includes(request.type)) {
    fail('Malformed worker request.');
  }
  return request;
}

async function main() {
  if (process.argv[2] === '--run' && process.argv.length === 5) {
    await runJob(process.argv[3], process.argv[4]);
    return;
  }
  if (process.argv.length !== 2) fail('Worker accepts requests only on stdin.');
  validateConfiguration();
  const request = await readRequest();
  validateJob(request.job);
  const answer = ['reconcile', 'terminal'].includes(request.type)
    ? await withJobLock(request.job.jobId, async () => {
        const record = await load(request.job.jobId);
        if (!record || record.digest !== digest(request.job)) fail('No matching job to reconcile.');
        return workerResponse(record);
      })
    : await dispatch(request.job);
  process.stdout.write(`${JSON.stringify(answer)}\n`);
}

main().catch((error) => {
  process.stderr.write(`${error instanceof Error ? error.message : String(error)}\n`);
  process.exitCode = 1;
});

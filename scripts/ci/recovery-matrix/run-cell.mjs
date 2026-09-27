#!/usr/bin/env node
import { execFileSync, spawnSync } from 'node:child_process';
import { chmodSync, cpSync, existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { basename, dirname, join, resolve } from 'node:path';

import { createFixtureSigstoreRoot } from './fixture-sigstore.mjs';
import { buildC2ImageLayout } from './oci-layout-builder.mjs';
import {
  baseEvidence,
  createCheckpoints,
  defaultCell,
  defaultFaultHooks,
  detectUbuntuHost,
  directorySnapshot,
  lastJournalPhase,
  parseToolVersions,
  provisionFixtureHostState,
  writeHostUpdateConfig,
  writeThrowawayEnv,
  writeValidatedEvidence,
} from './cell-runtime.mjs';
import {
  assembleFixtureBundle,
  buildFixtureRelease,
  fixtureRelease,
  fixtureSourceCommit,
  hostUpdateCliToolIdentity,
  protectedBackupReference,
  releaseEvidenceIdentity,
  writeJson,
  writeTrustedRootApproval,
} from './fixture-release-builder.mjs';

const args = parseArgs(process.argv.slice(2));
const repo = resolve(required(args.repo, '--repo'));
const runRoot = resolve(required(args['run-root'], '--run-root'));
const evidencePath = resolve(required(args.evidence, '--evidence'));
const cosign = resolve(required(args.cosign, '--cosign'));
const network = required(args.network, '--network');
const appStaticIp = required(args['app-ip'], '--app-ip');
const egressSink = required(args['egress-sink'], '--egress-sink');
const egressSinkIp = required(args['egress-sink-ip'], '--egress-sink-ip');
const networkAttemptsPath = required(args['network-attempts'], '--network-attempts');

const startedAt = new Date();
const checkpoints = createCheckpoints();
const cell = { ...defaultCell };
const faultHooks = { ...defaultFaultHooks };
void faultHooks;

const run = {
  id: runRoot.split(/[\\/]/).at(-1),
  startedAt: startedAt.toISOString(),
  finishedAt: startedAt.toISOString(),
  harnessCommit: git(repo, ['rev-parse', 'HEAD']).trim(),
  entryPoint: 'bash',
};

const host = detectUbuntuHost();
const tools = parseToolVersions({ cosign });
const root = createFixtureSigstoreRoot({ now: startedAt });
const sourceCommit = fixtureSourceCommit('c2-release-source');
const prior = fixtureRelease({
  version: '1.0.0-insider.1',
  sourceCommit,
  buildId: '3099001',
});
const target = fixtureRelease({
  version: '1.0.0-insider.2',
  sourceCommit,
  buildId: '3099002',
});
let bundlePath;

let evidence;
try {
  mkdirSync(runRoot, { recursive: true });
  const trustedRootPath = join(runRoot, 'trusted-root.json');
  root.writeTrustedRoot(trustedRootPath);
  const trustedRootBytes = readFileSync(trustedRootPath);
  const trustedRootApprovalPath = join(runRoot, 'trusted-root-approval.json');
  writeTrustedRootApproval(trustedRootApprovalPath, trustedRootBytes, {
    approvedAt: startedAt.toISOString(),
    approvedBy: 'recovery-matrix',
  });
  checkpoints.ok('trusted-root-created');

  const protectedBackup = protectedBackupReference(prior);
  const protectedBackupPath = join(runRoot, 'protected-backup.json');
  writeJson(protectedBackupPath, protectedBackup);

  const { layout: imageLayout, priorImages, targetImages, infrastructureLock } = buildC2ImageLayout({
    repo,
    runRoot,
    prior,
    target,
    run: (name, commandArgs, options = {}) => execFileSync(name, commandArgs, { encoding: 'utf8', ...options }),
  });
  writeJson(join(runRoot, 'image-details-prior.json'), priorImages);
  writeJson(join(runRoot, 'image-details-target.json'), targetImages);
  checkpoints.ok('oci-layout-built');

  const releaseRoot = join(runRoot, 'releases');
  const scratch = join(runRoot, 'scratch');
  mkdirSync(scratch, { recursive: true });
  const commandRunner = (name, commandArgs, options = {}) => {
    if (name === 'cosign') {
      return execFileSync(cosign, commandArgs, { encoding: 'utf8', ...options });
    }
    if (name === 'dotnet' && !commandExists('dotnet')) {
      const cwd = options.cwd ?? repo;
      return execFileSync('docker', [
        'run',
        '--rm',
        '-v', `${repo}:${repo}`,
        '-w', cwd,
        'mcr.microsoft.com/dotnet/sdk:10.0-noble',
        'dotnet',
        ...commandArgs,
      ], { encoding: 'utf8', stdio: options.stdio, env: options.env });
    }
    return execFileSync(name, commandArgs, { encoding: 'utf8', ...options });
  };
  const priorRelease = buildFixtureRelease({
    source: repo,
    output: join(releaseRoot, prior.version),
    release: prior,
    imageDetails: priorImages,
    infrastructureLock,
    sigstoreRoot: root,
    run: commandRunner,
    scratch,
  });
  const targetRelease = buildFixtureRelease({
    source: repo,
    output: join(releaseRoot, target.version),
    release: target,
    imageDetails: targetImages,
    infrastructureLock,
    sigstoreRoot: root,
    run: commandRunner,
    scratch,
  });
  checkpoints.ok('fixture-releases-built');

  const toolsDir = join(runRoot, 'tools');
  mkdirSync(toolsDir, { recursive: true });
  prepareOfflineTools({ repo, toolsDir, cosign, run: commandRunner });

  bundlePath = join(runRoot, `printfarmer-offline-bundle-v${target.version}.tar`);
  assembleFixtureBundle({
    releaseAssets: targetRelease.assets,
    priorReleaseAssets: priorRelease.assets,
    channel: target.channel,
    output: bundlePath,
    run: commandRunner,
    trustedRoot: trustedRootPath,
    cosign,
    imageLayout,
    priorImages: imageLayout,
    tools: toolsDir,
    protectedBackup,
  });
  checkpoints.ok('offline-bundle-assembled');

  const priorBundlePath = join(runRoot, `printfarmer-offline-bundle-v${prior.version}.tar`);
  assembleFixtureBundle({
    releaseAssets: priorRelease.assets,
    channel: prior.channel,
    output: priorBundlePath,
    run: commandRunner,
    trustedRoot: trustedRootPath,
    cosign,
    imageLayout,
    tools: toolsDir,
  });
  checkpoints.ok('prior-offline-bundle-assembled');

  const deploymentRoot = join(runRoot, 'deployment');
  mkdirSync(join(deploymentRoot, 'volumes'), { recursive: true });
  const envPath = join(deploymentRoot, '.env');
  const env = writeThrowawayEnv(envPath, {
    COMPOSE_PROJECT_NAME: run.id,
    POSTGRES_PORT: String(15432 + (process.pid % 1000)),
    PRINTFARMER_IMAGE: priorImages.monolith.reference,
  });
  const configPath = join(deploymentRoot, 'host-update.json');
  const hostStateRoot = process.env.HOME
    ? join(process.env.HOME, '.cache', 'printfarmer-recovery-matrix', basename(runRoot), 'host-state')
    : join(runRoot, 'host-state');
  provisionFixtureHostState(hostStateRoot, { channel: 'insider'   });
  const dockerShim = writeDockerShim(runRoot, deploymentRoot);
  writeCompose(deploymentRoot, network, egressSinkIp, 'database', appStaticIp);
  checkpoints.ok('deployment-root-prepared');
  execFileSync('/usr/bin/docker', [
    'compose',
    '-f', join(deploymentRoot, 'docker-compose.recovery.yml'),
    '-p', env.COMPOSE_PROJECT_NAME,
    'up',
    '-d',
    '--no-build',
    '--pull',
    'never',
    'database',
  ], {
    cwd: deploymentRoot,
    encoding: 'utf8',
    env: { ...process.env, ...env },
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  waitForDatabaseReady(deploymentRoot, env);
  const databaseContainer = `${env.COMPOSE_PROJECT_NAME}-database-1`;
  const databaseHost = dockerContainerIp(databaseContainer);
  writeCompose(deploymentRoot, network, egressSinkIp, databaseHost, appStaticIp);
  const postgresTools = writePostgresToolShims(runRoot, databaseContainer);
  writeHostUpdateConfig(configPath, {
    rootDirectory: join(runRoot, 'host-update'),
    deploymentRoot,
    projectName: env.COMPOSE_PROJECT_NAME,
    hostStateRoot,
    databaseConnectionString: `Host=${databaseHost};Port=5432;Database=${env.POSTGRES_DB};Username=${env.POSTGRES_USER};Password=${env.POSTGRES_PASSWORD}`,
    jwtKey: env.Jwt__Key,
    jwtIssuer: env.Jwt__Issuer,
    jwtAudience: env.Jwt__Audience,
    docker: dockerShim,
    pgDump: postgresTools.pgDump,
    pgRestore: postgresTools.pgRestore,
    healthBaseUrl: `http://${appStaticIp}:5000`,
  });

  const cli = installHostUpdateCli({
    repo,
    runRoot,
    release: target,
    assetDir: targetRelease.assets,
    trustedRootPath,
    cosign,
  });
  tools.cli = hostUpdateCliToolIdentity({ assets: targetRelease.assets, version: target.version });
  checkpoints.ok('cli-installed');

  const priorStaging = join(runRoot, 'staging-prior');
  const targetStaging = join(runRoot, 'staging-target');
  const decisionRecords = join(runRoot, 'decision-records');
  mkdirSync(decisionRecords, { recursive: true });

  executePackagedStep({
    checkpointName: 'import-prior',
    cli,
    repo,
    cosign,
    instructionsPath: join(priorRelease.assets, 'offline-recovery-instructions.json'),
    operationId: 'offline-bundle-import',
    replacements: {
      '<host-update.json>': configPath,
      '<bundle.tar>': priorBundlePath,
      '<trusted_root.json>': trustedRootPath,
      '<trusted-root-approval.json>': trustedRootApprovalPath,
      '<new-staging-dir>': priorStaging,
      '<decision-records-dir>': decisionRecords,
      '<operator>': 'recovery-matrix',
    },
  });
  executePackagedStep({
    checkpointName: 'activate-prior',
    cli,
    repo,
    cosign,
    instructionsPath: join(priorRelease.assets, 'offline-recovery-instructions.json'),
    operationId: 'offline-activate',
    replacements: {
      '<host-update.json>': configPath,
      '<staging-dir>': priorStaging,
      '<trusted_root.json>': trustedRootPath,
    },
  });

  const beforeRecovery = stateContinuitySnapshot({ env, deploymentRoot, hostStateRoot });

  writeHostUpdateConfig(configPath, {
    rootDirectory: join(runRoot, 'host-update'),
    deploymentRoot,
    projectName: env.COMPOSE_PROJECT_NAME,
    hostStateRoot,
    databaseConnectionString: `Host=${databaseHost};Port=5432;Database=${env.POSTGRES_DB};Username=${env.POSTGRES_USER};Password=${env.POSTGRES_PASSWORD}`,
    jwtKey: env.Jwt__Key,
    jwtIssuer: env.Jwt__Issuer,
    jwtAudience: env.Jwt__Audience,
    docker: dockerShim,
    pgDump: postgresTools.pgDump,
    pgRestore: postgresTools.pgRestore,
    healthBaseUrl: `http://${appStaticIp}:5000`,
  });

  executePackagedStep({
    checkpointName: 'import-target',
    cli,
    repo,
    cosign,
    instructionsPath: join(targetRelease.assets, 'offline-recovery-instructions.json'),
    operationId: 'offline-bundle-import-with-prior',
    replacements: {
      '<host-update.json>': configPath,
      '<bundle.tar>': bundlePath,
      '<trusted_root.json>': trustedRootPath,
      '<trusted-root-approval.json>': trustedRootApprovalPath,
      '<new-staging-dir>': targetStaging,
      '<decision-records-dir>': decisionRecords,
      '<operator>': 'recovery-matrix',
      '<protected-backup.json>': protectedBackupPath,
    },
  });
  const activationStarted = Date.now();
  const targetActivation = executePackagedStep({
    checkpointName: 'activate-target',
    autoOk: false,
    cli,
    repo,
    cosign,
    instructionsPath: join(targetRelease.assets, 'offline-recovery-instructions.json'),
    operationId: 'offline-activate',
    replacements: {
      '<host-update.json>': configPath,
      '<staging-dir>': targetStaging,
      '<trusted_root.json>': trustedRootPath,
    },
  });
  if (!targetActivation.stdout.includes('Completed') && !targetActivation.stdout.includes('Activated')) {
    checkpoints.failed('activate-target');
    throw cellFailure('target_activation_did_not_complete', { actual: 'RecoveryRequired', exitCode: targetActivation.exitCode });
  }
  const activationSeconds = Math.max(1, Math.round((Date.now() - activationStarted) / 1000));
  checkpoints.ok('activate-target');

  const recoveryStarted = Date.now();
  for (const [operationId, checkpointName] of [
    ['offline-recover-preview', 'recover-preview'],
    ['offline-recover-confirm', 'recover-confirm'],
  ]) {
    executePackagedStep({
      checkpointName,
      cli,
      repo,
      cosign,
      instructionsPath: join(targetRelease.assets, 'offline-recovery-instructions.json'),
      operationId,
      replacements: {
        '<host-update.json>': configPath,
        '<staging-dir>': targetStaging,
        '<trusted_root.json>': trustedRootPath,
        '<protected-backup.json>': protectedBackupPath,
      },
    });
  }
  const recoverySeconds = Math.max(1, Math.round((Date.now() - recoveryStarted) / 1000));
  checkpoints.ok('recovery-rolled-back');

  const afterRecovery = stateContinuitySnapshot({ env, deploymentRoot, hostStateRoot });
  assertEqualJson('migration-heads-continuous', beforeRecovery.migrationHeads, afterRecovery.migrationHeads);
  checkpoints.ok(`migration-heads-continuous:${afterRecovery.migrationHeads.join(',') || 'empty'}`);
  assertEqualJson('volume-hashes-continuous', beforeRecovery.volumeHashes, afterRecovery.volumeHashes);
  checkpoints.ok('blob-config-key-volume-hashes-continuous');
  assertEqualJson('protected-replay-history-continuous', beforeRecovery.hostState, afterRecovery.hostState);
  checkpoints.ok('protected-replay-history-continuous');
  const runningDigest = runningComposeImageDigest(env, 'printfarmer');
  if (runningDigest !== priorImages.monolith.indexDigest) {
    throw new Error(`running_digest_mismatch:expected=${priorImages.monolith.indexDigest}:actual=${runningDigest}`);
  }
  checkpoints.ok(`running-digest-prior:${runningDigest}`);
  const healthz = httpGetFromNetwork(network, appStaticIp, '/healthz');
  const health = httpGetFromNetwork(network, appStaticIp, '/health');
  if (!/Healthy|OK|"status"\s*:\s*"ok"|^\s*$/.test(healthz) && !healthz.includes('healthy')) {
    throw new Error(`healthz_not_green:${healthz.slice(0, 120)}`);
  }
  checkpoints.ok('healthz-green');
  const healthEntries = discoverHealthEntries(health);
  checkpoints.ok(`health-green:${healthEntries.join(',') || 'plain'}`);
  const queueEntries = healthEntries.filter(entry => /queue|dispatch|outbox|consumer/i.test(entry));
  if (queueEntries.length === 0) {
    checkpoints.failed('queue-consumers-running:not-exposed');
    throw cellFailure('queue_consumers_not_exposed', { actual: 'RolledBack', exitCode: 1 });
  }
  checkpoints.ok(`queue-consumers-running:${queueEntries.join(',')}`);

  run.finishedAt = new Date().toISOString();
  const journalPath = join(runRoot, 'host-update', 'state', 'journal.ndjson');
  const journalPhase = lastJournalPhase(journalPath);
  checkpoints.skipped('fault-injected');
  evidence = baseEvidence({
    run,
    host,
    cell,
    identities: {
      source: releaseEvidenceIdentity(prior),
      target: releaseEvidenceIdentity(target),
      prior: releaseEvidenceIdentity(prior),
      bundleSha256: evidenceBundleSha256(bundlePath),
      signingRootFingerprint: root.fingerprint,
    },
    tools,
    checkpoints: checkpoints.checkpoints,
    outcome: {
      expected: 'RolledBack',
      actual: 'RolledBack',
      reason: null,
      exitCode: 0,
      journalPhase,
    },
    timings: { activationSeconds, recoverySeconds },
    verdict: 'pass',
    networkAttempts: readNetworkAttempts(networkAttemptsPath),
  });
  writeValidatedEvidence(evidencePath, evidence);
} catch (error) {
  run.finishedAt = new Date().toISOString();
  writeFileSync(join(runRoot, 'error.txt'), formatError(error), { mode: 0o600 });
  checkpoints.skipped('fault-injected');
  checkpoints.failed('e2e-complete');
  const failure = classifyFailure(error, join(runRoot, 'host-update', 'state', 'journal.ndjson'));
  evidence = baseEvidence({
    run,
    host,
    cell,
    identities: {
      source: releaseEvidenceIdentity(prior),
      target: releaseEvidenceIdentity(target),
      prior: releaseEvidenceIdentity(prior),
      bundleSha256: evidenceBundleSha256(bundlePath),
      signingRootFingerprint: root.fingerprint,
    },
    tools,
    checkpoints: checkpoints.checkpoints,
    outcome: {
      expected: 'RolledBack',
      actual: failure.actual,
      reason: failure.reason,
      exitCode: failure.exitCode,
      journalPhase: failure.journalPhase,
    },
    timings: { activationSeconds: 0, recoverySeconds: 0 },
    verdict: 'fail',
    networkAttempts: readNetworkAttempts(networkAttemptsPath),
  });
  writeValidatedEvidence(evidencePath, evidence);
  console.error(error.message);
  process.exitCode = 1;
} finally {
  root.dispose();
}

function formatError(error) {
  const pieces = [
    `message=${error?.message ?? String(error)}`,
    error?.stack ? `stack=\n${error.stack}` : undefined,
    error?.stdout ? `stdout=\n${String(error.stdout)}` : undefined,
    error?.stderr ? `stderr=\n${String(error.stderr)}` : undefined,
  ].filter(Boolean);
  return `${pieces.join('\n\n')}\n`;
}

function installHostUpdateCli({ repo, runRoot, release, assetDir, trustedRootPath, cosign }) {
  const nativeRoot = process.env.HOME
    ? join(process.env.HOME, '.cache', 'printfarmer-recovery-matrix', basename(runRoot))
    : join(runRoot, 'native-cache');
  const installRoot = join(nativeRoot, 'installed-cli');
  const installerWork = join(nativeRoot, 'installer-work');
  mkdirSync(installRoot, { recursive: true, mode: 0o755 });
  mkdirSync(installerWork, { recursive: true, mode: 0o700 });
  chmodSync(installRoot, 0o755);
  chmodSync(installerWork, 0o700);
  const output = execFileSync('bash', [
    join(repo, 'scripts/install-host-update-cli.sh'),
    'install',
    '--version', release.version,
    '--asset-dir', assetDir,
    '--install-root', installRoot,
    '--runtime', 'linux-x64',
    '--trusted-root', trustedRootPath,
  ], {
    cwd: repo,
    encoding: 'utf8',
    env: {
      ...process.env,
      PATH: `${dirname(cosign)}:${process.env.PATH ?? ''}`,
      TMPDIR: installerWork,
    },
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  return output.trim().split(/\r?\n/).at(-1);
}

function runPackagedOperation({ cli, repo, cosign, instructionsPath, operationId, replacements, allowedExitCodes = [0] }) {
  const instructions = JSON.parse(readFileSync(instructionsPath, 'utf8'));
  const operation = instructions.operations.find(candidate => candidate.id === operationId);
  if (!operation) throw new Error(`missing_packaged_operation:${operationId}`);
  const argv = operation.bash.map((argument, index) => {
    const value = replacements[argument] ?? argument;
    return index === 0 ? cli : value;
  });
  const result = spawnSync(argv[0], argv.slice(1), {
    cwd: dirname(cli),
    encoding: 'utf8',
    stdio: ['ignore', 'pipe', 'pipe'],
    env: {
      ...process.env,
      PATH: `${dirname(cosign)}:${process.env.PATH ?? ''}`,
      PRINTFARMER_OFFLINE_BUNDLE_TOOL: join(repo, 'scripts/ci/offline-update-bundle.mjs'),
    },
  });
  if (result.stdout) process.stdout.write(result.stdout);
  if (result.stderr) process.stderr.write(result.stderr);
  const status = result.status ?? 1;
  if (!allowedExitCodes.includes(status)) {
    const error = new Error(`Command failed (${status}): ${argv.join(' ')}`);
    error.stdout = result.stdout;
    error.stderr = result.stderr;
    error.exitCode = status;
    error.operationId = operationId;
    throw error;
  }

  return { exitCode: status, stdout: result.stdout ?? '', stderr: result.stderr ?? '' };
}

function executePackagedStep({ checkpointName, autoOk = true, ...operation }) {
  try {
    const result = runPackagedOperation(operation);
    if (autoOk) {
      checkpoints.ok(checkpointName);
    }
    return result;
  } catch (error) {
    checkpoints.failed(checkpointName);
    error.stepName = checkpointName;
    throw error;
  }
}

function evidenceBundleSha256(path) {
  if (!path || !existsSync(path)) {
    throw new Error('target_offline_bundle_not_assembled');
  }
  return sha256LargeFile(path);
}

function cellFailure(reason, { actual = 'RecoveryRequired', exitCode = 1, journalPhase } = {}) {
  const error = new Error(reason);
  error.reason = reason;
  error.actual = actual;
  error.exitCode = exitCode;
  if (journalPhase) {
    error.journalPhase = journalPhase;
  }
  return error;
}

function classifyFailure(error, journalPath) {
  const combined = `${error?.stdout ?? ''}\n${error?.stderr ?? ''}\n${error?.message ?? String(error)}`;
  const code = matchLineValue(combined, 'code');
  const reason =
    error?.reason ??
    code ??
    matchLineValue(combined, 'reason') ??
    matchLineValue(combined, 'detail') ??
    (error?.operationId ? `${error.operationId}_failed` : undefined) ??
    String(error?.message ?? error).split(/\s+/)[0];
  const outcome = matchLineValue(combined, 'outcome');
  const state = matchLineValue(combined, 'state');
  const decision = matchLineValue(combined, 'decision');
  const step = typeof error?.stepName === 'string' ? error.stepName : null;
  const actual = normalizeOutcome(error?.actual ?? outcome ?? state ?? (decision === 'refused' || code ? 'Refused' : 'RecoveryRequired'));
  return {
    actual,
    reason: String(step ? `${step}:${reason}` : reason).slice(0, 80),
    exitCode: Number.isInteger(error?.exitCode) ? error.exitCode : 1,
    journalPhase: error?.journalPhase ?? safeJournalPhase(journalPath),
  };
}

function matchLineValue(text, key) {
  const match = new RegExp(`^${key}:\\s*(.+)$`, 'im').exec(text);
  return match?.[1]?.trim();
}

function normalizeOutcome(value) {
  return ['Activated', 'RolledBack', 'NeedsOperator', 'RecoveryRequired', 'FenceReleasePending', 'Refused']
    .includes(value) ? value : 'RecoveryRequired';
}

function safeJournalPhase(journalPath) {
  try {
    return existsSync(journalPath) ? lastJournalPhase(journalPath) : 'not-started';
  } catch {
    return 'not-started';
  }
}

function parseArgs(argv) {
  const parsed = {};
  for (let index = 0; index < argv.length; index += 2) {
    const key = argv[index];
    if (!key?.startsWith('--') || argv[index + 1] === undefined) {
      throw new Error(`Invalid argument at ${index}: ${key}`);
    }
    parsed[key.slice(2)] = argv[index + 1];
  }
  return parsed;
}

function required(value, name) {
  if (!value) throw new Error(`${name} is required`);
  return value;
}

function git(cwd, commandArgs) {
  try {
    return execFileSync('git', ['--no-pager', ...commandArgs], { cwd, encoding: 'utf8' });
  } catch (error) {
    if (existsSync('/proc/version')) {
      try {
        return execFileSync('git.exe', ['--no-pager', ...commandArgs], { cwd, encoding: 'utf8' });
      } catch {
        throw error;
      }
    }
    throw error;
  }
}

function commandExists(name) {
  try {
    execFileSync('bash', ['-lc', `command -v ${JSON.stringify(name)} >/dev/null 2>&1`], { stdio: 'ignore' });
    return true;
  } catch {
    return false;
  }
}

function prepareOfflineTools({ repo, toolsDir, cosign, run }) {
  const lock = JSON.parse(readFileSync(join(repo, 'scripts/docker/offline-tools.lock.json'), 'utf8'));
  for (const artifact of lock.artifacts) {
    const output = join(toolsDir, artifact.name);
    if (artifact.name === 'cosign-linux-amd64') {
      cpSync(cosign, output);
    } else {
      run('curl', ['-fsSL', artifact.url, '-o', output], { stdio: ['ignore', 'inherit', 'pipe'] });
    }
    chmodSync(output, artifact.name.endsWith('.exe') ? 0o644 : 0o755);
    const actual = createHash('sha256').update(readFileSync(output)).digest('hex');
    if (actual !== artifact.sha256) {
      throw new Error(`offline_tool_checksum_mismatch:${artifact.name}`);
    }
  }
}

function readNetworkAttempts(path) {
  if (!existsSync(path)) return [];
  return readFileSync(path, 'utf8')
    .split(/\r?\n/)
    .filter(Boolean)
    .map((line) => {
      const attempt = JSON.parse(line);
      return {
        at: attempt.at,
        destination: attempt.destination,
        protocol: attempt.protocol,
      };
    });
}

function writeCompose(deploymentRoot, network, egressSinkIp, databaseHost = 'database', appIp = undefined) {
  const compose = {
    name: '${COMPOSE_PROJECT_NAME}',
    services: {
      database: {
        image: '${POSTGRES_IMAGE:-postgres:16-alpine}',
        environment: {
          POSTGRES_DB: '${POSTGRES_DB}',
          POSTGRES_USER: '${POSTGRES_USER}',
          POSTGRES_PASSWORD: '${POSTGRES_PASSWORD}',
        },
        ports: ['127.0.0.1:${POSTGRES_PORT}:5432'],
        networks: appIp ? { [network]: { ipv4_address: appIp } } : [network],
        volumes: ['postgres-data:/var/lib/postgresql/data'],
      },
      printfarmer: {
        image: '${PRINTFARMER_IMAGE}',
        depends_on: { database: { condition: 'service_started' } },
        dns: [egressSinkIp],
        ports: ['127.0.0.1:${PRINTFARMER_PORT:-5245}:5000'],
        environment: [
          'DEPLOYMENT_MODE=monolith',
          'DB_PROVIDER=Postgres',
          `ConnectionStrings__Default=Host=${databaseHost};Port=5432;Database=\${POSTGRES_DB};Username=\${POSTGRES_USER};Password=\${POSTGRES_PASSWORD}`,
          'ASPNETCORE_ENVIRONMENT=${ASPNETCORE_ENVIRONMENT}',
          'ASPNETCORE_URLS=http://+:5000',
          'Jwt__Key=${Jwt__Key}',
          'Jwt__Issuer=${Jwt__Issuer}',
          'Jwt__Audience=${Jwt__Audience}',
          'WorkerAuth__SharedKey=${WORKER_SHARED_API_KEY}',
          'SlicerPromotion__SharedKey=${PROMOTION_SHARED_API_KEY}',
          'DiscoveryAuth__SharedKey=${DISCOVERY_SHARED_API_KEY}',
          'HostUpdates__VerifiedReleaseDiscovery__Enabled=false',
          'WebAuthn__RelyingPartyId=${WebAuthn__RelyingPartyId}',
          'WebAuthn__RelyingPartyName=${WebAuthn__RelyingPartyName}',
          'WebAuthn__Origin=${WebAuthn__Origin}',
          'GCODE_STORAGE_PATH=/app/gcode',
          'MODEL_UPLOAD_PATH=/app/models',
          'SLICER_PROFILES_PATH=/app/profiles',
          'DATAPROTECTION_KEYS_PATH=/app/data-protection-keys',
        ],
        networks: [network],
        volumes: [
          'app-data:/data',
          'models:/app/models',
          'gcode:/app/gcode',
          'profiles:/app/profiles',
          'keys:/app/data-protection-keys',
        ],
      },
    },
    networks: {
      [network]: { external: true },
    },
    volumes: {
      'postgres-data': {},
      'app-data': {},
      models: {},
      gcode: {},
      profiles: {},
      keys: {},
    },
  };
  writeFileSync(join(deploymentRoot, 'docker-compose.recovery.yml'), `${JSON.stringify(compose, undefined, 2)}\n`);
}

function waitForDatabaseReady(deploymentRoot, env) {
  const args = [
    'compose',
    '-f', join(deploymentRoot, 'docker-compose.recovery.yml'),
    '-p', env.COMPOSE_PROJECT_NAME,
    'exec',
    '-T',
    'database',
    'pg_isready',
    '-U',
    env.POSTGRES_USER,
    '-d',
    env.POSTGRES_DB,
  ];
  const deadline = Date.now() + 45_000;
  let lastError;
  while (Date.now() < deadline) {
    try {
      execFileSync('/usr/bin/docker', args, {
        cwd: deploymentRoot,
        encoding: 'utf8',
        env: { ...process.env, ...env },
        stdio: ['ignore', 'pipe', 'pipe'],
      });
      return;
    } catch (error) {
      lastError = error;
      Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, 1000);
    }
  }

  throw lastError ?? new Error('database did not become ready');
}

function dockerContainerIp(containerName) {
  const json = execFileSync('/usr/bin/docker', ['inspect', containerName, '--format', '{{json .NetworkSettings.Networks}}'], { encoding: 'utf8' });
  const networks = JSON.parse(json);
  for (const network of Object.values(networks)) {
    if (network?.IPAddress) {
      return network.IPAddress;
    }
  }

  throw new Error(`database_container_ip_unavailable:${containerName}`);
}

function writeDockerShim(runRoot, deploymentRoot) {
  const shim = join(runRoot, 'docker');
  const log = join(runRoot, 'docker-commands.ndjson');
  const envFile = join(deploymentRoot, '.env');
  writeFileSync(shim, `#!/usr/bin/env bash
set -euo pipefail
args=("$@")
if [[ "\${args[0]:-}" == "compose" ]]; then
  has_env_file=0
  for arg in "\${args[@]}"; do
    if [[ "$arg" == "--env-file" ]]; then
      has_env_file=1
      break
    fi
  done
  if [[ "$has_env_file" == 0 ]]; then
    args=("compose" "--env-file" ${JSON.stringify(envFile)} "\${args[@]:1}")
  fi
fi
printf '{"at":"%s","args":%s}\\n' "$(date -u +%FT%TZ)" "$(node -e 'process.stdout.write(JSON.stringify(process.argv.slice(1)))' "$@")" >> ${JSON.stringify(log)}
exec /usr/bin/docker "\${args[@]}"
`);
  chmodSync(shim, 0o755);
  return shim;
}

function writePostgresToolShims(runRoot, databaseContainer) {
  const pgDump = join(runRoot, 'pg_dump');
  const pgRestore = join(runRoot, 'pg_restore');
  writeFileSync(pgDump, `#!/usr/bin/env bash
set -euo pipefail
out=""
args=()
while [[ $# -gt 0 ]]; do
  case "$1" in
    -f)
      out="$2"
      shift 2
      ;;
    *)
      args+=("$1")
      shift
      ;;
  esac
done
if [[ -z "$out" ]]; then
  echo "pg_dump shim requires -f <output>" >&2
  exit 64
fi
mkdir -p "$(dirname "$out")"
/usr/bin/docker exec -e "PGPASSWORD=\${PGPASSWORD:-}" ${databaseContainer} pg_dump "\${args[@]}" > "$out"
`);
  chmodSync(pgDump, 0o755);
  writeFileSync(pgRestore, `#!/usr/bin/env bash
set -euo pipefail
args=("$@")
last_index=$((\${#args[@]} - 1))
input="\${args[$last_index]}"
unset "args[$last_index]"
/usr/bin/docker exec -i -e "PGPASSWORD=\${PGPASSWORD:-}" ${databaseContainer} pg_restore "\${args[@]}" < "$input"
`);
  chmodSync(pgRestore, 0o755);
  return { pgDump, pgRestore };
}

function stateContinuitySnapshot({ env, deploymentRoot, hostStateRoot }) {
  const migrationHeads = psqlQuery(deploymentRoot, env, 'SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId";')
    .split(/\r?\n/)
    .map((line) => line.trim())
    .filter(Boolean);
  const volumeHashes = Object.fromEntries(['app-data', 'models', 'gcode', 'profiles', 'keys']
    .map((volume) => [volume, dockerVolumeHash(`${env.COMPOSE_PROJECT_NAME}_${volume}`)]));
  return {
    migrationHeads,
    volumeHashes,
    hostState: directorySnapshot({ hostState: hostStateRoot }),
  };
}

function psqlQuery(deploymentRoot, env, sql) {
  return execFileSync('/usr/bin/docker', [
    'compose',
    '-f', join(deploymentRoot, 'docker-compose.recovery.yml'),
    '-p', env.COMPOSE_PROJECT_NAME,
    'exec',
    '-T',
    'database',
    'psql',
    '-U', env.POSTGRES_USER,
    '-d', env.POSTGRES_DB,
    '-tAc', sql,
  ], {
    cwd: deploymentRoot,
    encoding: 'utf8',
    env: { ...process.env, ...env },
    stdio: ['ignore', 'pipe', 'pipe'],
  });
}

function dockerVolumeHash(volume) {
  return execFileSync('/usr/bin/docker', [
    'run',
    '--rm',
    '--network', 'none',
    '-v', `${volume}:/data:ro`,
    'python:3.12-alpine',
    'sh',
    '-c',
    "cd /data && find . -type f -print0 | sort -z | xargs -0 sha256sum 2>/dev/null | sha256sum | awk '{print $1}'",
  ], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }).trim();
}

function runningComposeImageDigest(env, service) {
  const container = `${env.COMPOSE_PROJECT_NAME}-${service}-1`;
  const image = execFileSync('/usr/bin/docker', ['inspect', container, '--format', '{{.Config.Image}}'], { encoding: 'utf8' }).trim();
  return image.includes('@') ? image.split('@').at(-1) : image;
}

function httpGetFromNetwork(network, ip, path) {
  const script = `import urllib.request; print(urllib.request.urlopen('http://${ip}:5000${path}', timeout=20).read().decode())`;
  let lastError;
  for (let attempt = 1; attempt <= 12; attempt += 1) {
    try {
      return execFileSync('/usr/bin/docker', [
        'run',
        '--rm',
        '--network', network,
        'python:3.12-alpine',
        'python',
        '-c',
        script,
      ], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }).trim();
    } catch (error) {
      lastError = error;
      execFileSync('/usr/bin/sleep', ['5'], { stdio: ['ignore', 'ignore', 'ignore'] });
    }
  }

  throw lastError;
}

function discoverHealthEntries(body) {
  try {
    const parsed = JSON.parse(body);
    if (parsed?.entries && typeof parsed.entries === 'object') {
      return Object.keys(parsed.entries).sort();
    }
    if (parsed?.results && typeof parsed.results === 'object') {
      return Object.keys(parsed.results).sort();
    }
  } catch {
    // Plain-text health responses are still valid for the endpoint check.
  }
  return [];
}

function sha256LargeFile(path) {
  return execFileSync('/usr/bin/sha256sum', [path], { encoding: 'utf8' }).trim().split(/\s+/)[0];
}

function assertEqualJson(label, expected, actual) {
  const left = JSON.stringify(expected);
  const right = JSON.stringify(actual);
  if (left !== right) {
    throw new Error(`${label}:expected=${left}:actual=${right}`);
  }
}

#!/usr/bin/env node
import { execFileSync, spawnSync } from 'node:child_process';
import { chmodSync, cpSync, existsSync, mkdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { basename, dirname, join, resolve } from 'node:path';

import { waitForDuringActivationPoint } from './activation-runner.mjs';
import { createFixtureSigstoreRoot } from './fixture-sigstore.mjs';
import { resolveCell } from './cells.mjs';
import { buildCellImageLayout } from './oci-layout-builder.mjs';
import { schemaDeltaFixtureStateSql } from './schema-delta-fixture.mjs';
import { writeRecoveryCompose } from './compose-config.mjs';
import { dockerFaultFiles, wrapToolWithPauseGate, writeDockerShim } from './docker-shim.mjs';
import { runFaultScenario } from './fault-scenarios.mjs';
import { hasFaultHooks, invokeFaultHook, parseFaultHooks } from './fault-hooks.mjs';
import { assertHostStateContinuity, readHostStateSnapshotFromBoundary } from './host-state-continuity.mjs';
import { canaryDnsName, hasCanaryAttempt } from './network-denial.mjs';
import { providerFor } from './providers.mjs';
import { serviceMappingsFor } from './topologies.mjs';
import {
  activeServiceDigestExpectations,
  assertActiveServiceDigests,
  assertExpectedNeedsOperator,
  assertExpectedRefusal,
  assertNoMutation,
  evaluateQueueConsumersHealth,
} from './runtime-assertions.mjs';
import {
  baseEvidence,
  createCheckpoints,
  detectUbuntuHost,
  lastJournalPhase,
  parseToolVersions,
  recoveryHostStateRoot,
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
const cellSpec = resolveCell(args.cell ?? 'c2');
const repo = resolve(required(args.repo, '--repo'));
const runRoot = resolve(required(args['run-root'], '--run-root'));
const evidencePath = resolve(required(args.evidence, '--evidence'));
const cosign = resolve(required(args.cosign, '--cosign'));
const network = required(args.network, '--network');
const appStaticIp = required(args['app-ip'], '--app-ip');
required(args['egress-sink'], '--egress-sink');
const egressSinkIp = required(args['egress-sink-ip'], '--egress-sink-ip');
const networkAttemptsPath = required(args['network-attempts'], '--network-attempts');
const hostContainer = required(args['host-container'], '--host-container');
const faultHooks = parseFaultHooks(toArray(args.fault));
const provider = providerFor(cellSpec.cell.provider);

const startedAt = new Date();
const checkpoints = createCheckpoints();
const cell = { ...cellSpec.cell };
const priorCell = cellSpec.id === 'split-database'
  ? { ...cell, databaseLayout: 'shared' }
  : cell;
const faultState = { injected: false };

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
  proveNetworkDenialBoundary({ hostContainer, networkAttemptsPath });
  checkpoints.ok('network-denial-canary-proven');
  const protectedBackup = protectedBackupReference(prior);
  const protectedBackupPath = join(runRoot, 'protected-backup.json');
  writeJson(protectedBackupPath, protectedBackup);

  const { layout: imageLayout, priorImages, targetImages, infrastructureLock, schemaDelta } = buildCellImageLayout({
    repo,
    runRoot,
    prior,
    target,
    cell,
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
  const boundaryCosign = join(toolsDir, 'cosign-linux-amd64');

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
  const databaseStaticIp = siblingIp(appStaticIp, 11);
  const databaseContainer = `${run.id}-database-1`;
  const imageEnv = imageEnvironment({ cell, priorImages, infrastructureLock });
  const env = writeThrowawayEnv(envPath, {
    COMPOSE_PROJECT_NAME: run.id,
    POSTGRES_PORT: String(15432 + (process.pid % 1000)),
    MSSQL_PORT: String(16433 + (process.pid % 1000)),
    DB_PROVIDER: provider.dbProvider,
    ...provider.env(),
    ...imageEnv,
  });
  const configPath = join(deploymentRoot, 'host-update.json');
  const hostStateRoot = recoveryHostStateRoot(runRoot, { hostBoundary: true });
  provisionBoundaryHostState(hostContainer, repo, hostStateRoot, { channel: 'insider' });
  const dockerShim = writeDockerShim(runRoot, deploymentRoot, networkAttemptsPath);
  writeRecoveryCompose({
    deploymentRoot,
    network,
    egressSinkIp,
    databaseHost: databaseStaticIp,
    databaseIp: databaseStaticIp,
    appIp: appStaticIp,
    runId: run.id,
    cell: priorCell,
    hostUpdateBackupsRoot: join(runRoot, 'host-update', 'backups'),
  });
  const databaseTools = provider.writeToolShims({ runRoot, databaseContainer });
  const toolGates = cellSpec.scenario === 'fault' && databaseTools.pgDump && databaseTools.pgRestore
    ? {
      pgDump: wrapToolWithPauseGate(databaseTools.pgDump, { name: 'pg_dump' }),
      pgRestore: wrapToolWithPauseGate(databaseTools.pgRestore, { name: 'pg_restore' }),
    }
    : null;
  writeHostUpdateConfig(configPath, {
    rootDirectory: join(runRoot, 'host-update'),
    deploymentRoot,
    projectName: env.COMPOSE_PROJECT_NAME,
    hostStateRoot,
    databaseConnectionString: provider.connectionString({ host: databaseStaticIp, env }),
    slicerConnectionString: priorCell.databaseLayout === 'split' ? provider.slicerConnectionString({ host: databaseStaticIp, env }) : undefined,
    jwtKey: env.Jwt__Key,
    jwtIssuer: env.Jwt__Issuer,
    jwtAudience: env.Jwt__Audience,
    docker: dockerShim,
    pgDump: databaseTools.pgDump ?? 'pg_dump',
    pgRestore: databaseTools.pgRestore ?? 'pg_restore',
    sqlcmd: databaseTools.sqlcmd ?? 'sqlcmd',
    healthBaseUrl: `http://${appStaticIp}:5000`,
    cell: priorCell,
    databaseProvider: provider,
    databaseExternallyOwned: false,
    storageExternallyOwned: false,
    createHostStateRoot: false,
  });
  checkpoints.ok('deployment-root-prepared');

  const cli = installHostUpdateCli({
    repo,
    runRoot,
    release: target,
    assetDir: targetRelease.assets,
    trustedRootPath,
    cosign: boundaryCosign,
    hostContainer,
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
    cosign: boundaryCosign,
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
  const loadedInfrastructure = assertInfrastructureLoadedFromBundle(decisionRecords, infrastructureLock);
  checkpoints.ok(`infrastructure-loaded-from-bundle:${loadedInfrastructure.join(',')}`);
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
  waitForDatabaseReady(deploymentRoot, env, provider);
  ensureHarnessDatabases(deploymentRoot, env, provider, priorCell);
  executePackagedStep({
    checkpointName: 'activate-prior',
    cli,
    repo,
    cosign: boundaryCosign,
    instructionsPath: join(priorRelease.assets, 'offline-recovery-instructions.json'),
    operationId: 'offline-activate',
    replacements: {
      '<host-update.json>': configPath,
      '<staging-dir>': priorStaging,
      '<trusted_root.json>': trustedRootPath,
    },
  });

  const beforeRecovery = stateContinuitySnapshot({ env, deploymentRoot, hostStateRoot, hostContainer, provider });
  if (cellSpec.scenario === 'refuse-activation' && cell.workers === 'remote') {
    seedRemoteWorkerRegistration(deploymentRoot, env, provider);
    checkpoints.ok('remote-worker-registration-seeded');
  }

  if (cellSpec.id === 'split-database') {
    writeRecoveryCompose({
      deploymentRoot,
      network,
      egressSinkIp,
      databaseHost: databaseStaticIp,
      databaseIp: databaseStaticIp,
      appIp: appStaticIp,
      runId: run.id,
      cell,
      hostUpdateBackupsRoot: join(runRoot, 'host-update', 'backups'),
    });
  }

  writeHostUpdateConfig(configPath, {
    rootDirectory: join(runRoot, 'host-update'),
    deploymentRoot,
    projectName: env.COMPOSE_PROJECT_NAME,
    hostStateRoot,
    databaseConnectionString: provider.connectionString({ host: databaseStaticIp, env }),
    slicerConnectionString: cell.databaseLayout === 'split' ? provider.slicerConnectionString({ host: databaseStaticIp, env }) : undefined,
    jwtKey: env.Jwt__Key,
    jwtIssuer: env.Jwt__Issuer,
    jwtAudience: env.Jwt__Audience,
    docker: dockerShim,
    pgDump: databaseTools.pgDump ?? 'pg_dump',
    pgRestore: databaseTools.pgRestore ?? 'pg_restore',
    sqlcmd: databaseTools.sqlcmd ?? 'sqlcmd',
    healthBaseUrl: `http://${appStaticIp}:5000`,
    cell,
    databaseProvider: provider,
    databaseExternallyOwned: false,
    storageExternallyOwned: false,
    createHostStateRoot: false,
  });

  executePackagedStep({
    checkpointName: 'import-target',
    cli,
    repo,
    cosign: boundaryCosign,
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
  const targetReplayIdentity = readImportDecisionRecords(decisionRecords).at(-1)?.replay?.correlationId?.replace(/^decision:/, '');
  runFaultHook('before-activate', { runRoot, deploymentRoot, stagingDir: targetStaging, bundlePath });
  const assertRecoveredToPrior = () => {
    const afterRecovery = stateContinuitySnapshot({ env, deploymentRoot, hostStateRoot, hostContainer, provider });
    assertEqualJson('migration-heads-continuous', beforeRecovery.migrationHeads, afterRecovery.migrationHeads);
    if (afterRecovery.migrationHeads.length === 0) {
      throw new Error('migration-heads-continuous: no migration history rows observed');
    }
    checkpoints.ok(`migration-heads-continuous:${afterRecovery.migrationHeads.join(',')}`);
    assertEqualJson('volume-hashes-continuous', beforeRecovery.volumeHashes, afterRecovery.volumeHashes);
    checkpoints.ok('blob-config-key-volume-hashes-continuous');
    assertHostStateContinuity(beforeRecovery.hostState, afterRecovery.hostState, { targetIdentity: targetReplayIdentity });
    checkpoints.ok('protected-replay-history-continuous');
    const checkedDigests = assertActiveServiceDigests({
      cell,
      priorImages,
      inspectDigest: (composeServiceName) => runningComposeImageDigest(env, composeServiceName),
    });
    checkpoints.ok(`running-digest-prior:${checkedDigests.map((digest) => `${digest.serviceId}=${digest.actualDigest}`).join(',')}`);
    assertApplicationHealthy('RolledBack');
  };
  const assertApplicationHealthy = (actual) => {
    const healthz = httpGetFromNetwork(network, appStaticIp, '/healthz');
    const health = httpGetFromNetwork(network, appStaticIp, '/health');
    if (!/Healthy|OK|"status"\s*:\s*"ok"|^\s*$/.test(healthz) && !healthz.includes('healthy')) {
      throw new Error(`healthz_not_green:${healthz.slice(0, 120)}`);
    }
    checkpoints.ok('healthz-green');
    const healthEntries = discoverHealthEntries(health);
    checkpoints.ok(`health-green:${healthEntries.join(',') || 'plain'}`);
    const queueConsumers = evaluateQueueConsumersHealth(health);
    if (!queueConsumers.ok) {
      checkpoints.failed(`queue-consumers-running:${queueConsumers.detail}`);
      throw cellFailure(queueConsumers.reason, { actual, exitCode: 1 });
    }
    checkpoints.ok(`queue-consumers-running:${queueConsumers.consumers.join(',')}`);
  };
  if (cellSpec.scenario === 'fault') {
    const journalPath = join(runRoot, 'host-update', 'state', 'journal.ndjson');
    const instructionsPath = join(targetRelease.assets, 'offline-recovery-instructions.json');
    const replacements = {
      '<host-update.json>': configPath,
      '<staging-dir>': targetStaging,
      '<trusted_root.json>': trustedRootPath,
      '<protected-backup.json>': protectedBackupPath,
    };
    const anyExitCode = Array.from({ length: 256 }, (_, code) => code);
    const composeArgs = ['compose', '-f', join(deploymentRoot, 'docker-compose.recovery.yml'), '-p', env.COMPOSE_PROJECT_NAME];
    const composeServices = activeServiceDigestExpectations(cell, priorImages).map((entry) => entry.composeServiceName);
    const faultSnapshot = () => {
      const snapshot = stateContinuitySnapshot({ env, deploymentRoot, hostStateRoot, hostContainer, provider });
      return {
        ...snapshot,
        serviceDigests: Object.fromEntries(composeServices.map((service) => {
          try {
            return [service, runningComposeImageDigest(env, service)];
          } catch {
            return [service, 'absent'];
          }
        })),
      };
    };
    const fault = runFaultScenario({
      cellSpec,
      runRoot,
      journalPath,
      hostStateRoot,
      checkpoints,
      dockerLogPath: join(runRoot, 'docker-commands.ndjson'),
      dockerGate: {
        spec: join(runRoot, dockerFaultFiles.spec),
        pause: join(runRoot, dockerFaultFiles.pause),
        decision: join(runRoot, dockerFaultFiles.decision),
      },
      toolGates,
      admissionClosedPath: join(runRoot, 'host-update', 'state', 'admission.closed'),
      markInjected: () => {
        faultState.injected = true;
      },
      op: (operationId, { extraArgs = [], allowedExitCodes = anyExitCode } = {}) => runPackagedOperation({
        cli,
        repo,
        cosign: boundaryCosign,
        instructionsPath,
        operationId,
        replacements,
        allowedExitCodes,
        extraArgs,
      }),
      launchOp: (operationId) => launchPackagedOperation({
        cli,
        repo,
        cosign: boundaryCosign,
        instructionsPath,
        operationId,
        replacements,
        workRoot: join(runRoot, 'fault-ops'),
      }),
      restartHost: () => restartHostContainer(hostContainer),
      hostShell: (script) => hostExecFileSync(hostContainer, ['bash', '-lc', script], { cwd: repo }),
      dbQuery: (sql) => databaseQuery(deploymentRoot, env, provider, sql),
      schemaDelta,
      schemaDeltaState: (context) => parseSchemaDeltaState(
        databaseQuery(deploymentRoot, env, provider, schemaDeltaFixtureStateSql(provider.id, context))),
      seedPrinter: () => databaseQuery(deploymentRoot, env, provider, seedPrinterSql()),
      snapshot: faultSnapshot,
      assertNoMutation,
      assertRolledBack: assertRecoveredToPrior,
      assertActivated: () => {
        const checked = assertActiveServiceDigests({
          cell,
          priorImages: targetImages,
          inspectDigest: (composeServiceName) => runningComposeImageDigest(env, composeServiceName),
        });
        checkpoints.ok(`running-digest-target:${checked.map((digest) => `${digest.serviceId}=${digest.actualDigest}`).join(',')}`);
        assertApplicationHealthy('Activated');
      },
      runningDigest: () => runningComposeImageDigest(env, composeServices[0]),
      enableComposeFault: (mode) => {
        enableManagedActivationFault(runRoot, { mode });
        faultState.injected = true;
      },
      stopApplication: () => execFileSync('/usr/bin/docker', [...composeArgs, 'stop', ...composeServices], {
        cwd: deploymentRoot,
        encoding: 'utf8',
        env: { ...process.env, ...env },
        stdio: ['ignore', 'pipe', 'pipe'],
      }),
    });
    run.finishedAt = new Date().toISOString();
    recordFaultCheckpoint();
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
        schemaDelta,
      },
      tools,
      checkpoints: checkpoints.checkpoints,
      outcome: {
        expected: cellSpec.expected.outcome,
        expectedReason: null,
        actual: fault.actual,
        reason: fault.actual === 'Activated' || fault.actual === 'RolledBack' ? null : fault.reason,
        exitCode: fault.exitCode,
        journalPhase: safeJournalPhase(journalPath),
      },
      timings: { activationSeconds: fault.activationSeconds, recoverySeconds: fault.recoverySeconds },
      verdict: 'pass',
      networkAttempts: readNetworkAttempts(networkAttemptsPath),
    });
    writeValidatedEvidence(evidencePath, evidence);
    const complete = new Error('fault cell evidence written');
    complete.evidenceWritten = true;
    complete.success = true;
    throw complete;
  }
  const activationStarted = Date.now();
  const managedActivationFault = shouldInjectManagedActivationFault(cellSpec);
  const customDuringActivationHook = Boolean(faultHooks['during-activate']);
  const activationFaultPaths = managedActivationFault ? enableManagedActivationFault(runRoot, {
    mode: customDuringActivationHook ? 'pause' : 'fail-now',
  }) : null;
  const activationOperation = {
    checkpointName: 'activate-target',
    autoOk: false,
    cli,
    repo,
    cosign: boundaryCosign,
    instructionsPath: join(targetRelease.assets, 'offline-recovery-instructions.json'),
    operationId: 'offline-activate',
    replacements: {
      '<host-update.json>': configPath,
      '<staging-dir>': targetStaging,
      '<trusted_root.json>': trustedRootPath,
    },
    allowedExitCodes: cellSpec.scenario === 'refuse-activation' ? [6] : managedActivationFault ? [0, 6] : [0],
  };
  const beforeRefusal = cellSpec.scenario === 'refuse-activation'
    ? mutationSnapshot({ env, deploymentRoot, hostStateRoot, hostContainer, provider, cell, priorImages })
    : null;
  const targetActivation = customDuringActivationHook
    ? executePackagedStepDuringActivation({
      ...activationOperation,
      markerPath: activationFaultPaths?.pausePath ?? join(runRoot, 'host-update', 'state', 'journal.ndjson'),
      hookContext: {
        runRoot,
        deploymentRoot,
        stagingDir: targetStaging,
        bundlePath,
        faultDecisionPath: activationFaultPaths?.decisionPath,
        faultPausePath: activationFaultPaths?.pausePath,
      },
      managedActivationFault,
      faultDecisionPath: activationFaultPaths?.decisionPath,
    })
    : executePackagedStep(activationOperation);
  if (managedActivationFault && !customDuringActivationHook) {
    faultState.injected = true;
  }
  if (cellSpec.scenario === 'refuse-activation') {
    const failure = classifyFailure({
      stdout: targetActivation.stdout,
      stderr: targetActivation.stderr,
      exitCode: targetActivation.exitCode,
      operationId: 'offline-activate',
    }, join(runRoot, 'host-update', 'state', 'journal.ndjson'));
    try {
      assertExpectedRefusal(failure, cellSpec.expected);
    } catch (error) {
      checkpoints.failed('activation-refused');
      throw cellFailure(`unexpected_refusal:${failure.actual}:${failure.reason}`, {
        actual: failure.actual,
        exitCode: targetActivation.exitCode,
        journalPhase: failure.journalPhase,
      });
    }
    const afterRefusal = mutationSnapshot({ env, deploymentRoot, hostStateRoot, hostContainer, provider, cell, priorImages });
    assertNoMutation('activation-refusal', beforeRefusal, afterRefusal);
    checkpoints.ok('activation-refusal-no-mutation');
    checkpoints.ok(`activation-refused:${failure.reason}`);
    recordFaultCheckpoint();
    run.finishedAt = new Date().toISOString();
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
        expected: cellSpec.expected.outcome,
        expectedReason: cellSpec.expected.reason,
        actual: failure.actual,
        reason: failure.reason,
        exitCode: targetActivation.exitCode,
        journalPhase: failure.journalPhase,
      },
      timings: { activationSeconds: Math.max(1, Math.round((Date.now() - activationStarted) / 1000)), recoverySeconds: 0 },
      verdict: 'pass',
      networkAttempts: readNetworkAttempts(networkAttemptsPath),
    });
    writeValidatedEvidence(evidencePath, evidence);
    const complete = new Error('expected fail-closed evidence written');
    complete.evidenceWritten = true;
    complete.success = true;
    throw complete;
  }
  const activationReachedExpectedState = targetActivation.stdout.includes('Completed')
    || targetActivation.stdout.includes('Activated')
    || (managedActivationFault && targetActivation.stdout.includes('RecoveryRequired'));
  if (!activationReachedExpectedState) {
    checkpoints.failed('activate-target');
    throw cellFailure('target_activation_did_not_complete', { actual: 'RecoveryRequired', exitCode: targetActivation.exitCode });
  }
  const activationSeconds = Math.max(1, Math.round((Date.now() - activationStarted) / 1000));
  checkpoints.ok('activate-target');

  const recoveryStarted = Date.now();
  runFaultHook('before-recover', { runRoot, deploymentRoot, stagingDir: targetStaging, bundlePath });
  if (cellSpec.scenario === 'needs-operator-recover') {
    const externalOwner = cell.databaseOwner === 'external' ? 'database' : 'storage';
    writeHostUpdateConfig(configPath, {
      rootDirectory: join(runRoot, 'host-update'),
      deploymentRoot,
      projectName: env.COMPOSE_PROJECT_NAME,
      hostStateRoot,
      databaseConnectionString: provider.connectionString({ host: databaseStaticIp, env }),
      slicerConnectionString: cell.databaseLayout === 'split' ? provider.slicerConnectionString({ host: databaseStaticIp, env }) : undefined,
      jwtKey: env.Jwt__Key,
      jwtIssuer: env.Jwt__Issuer,
      jwtAudience: env.Jwt__Audience,
      docker: dockerShim,
      pgDump: databaseTools.pgDump ?? 'pg_dump',
      pgRestore: databaseTools.pgRestore ?? 'pg_restore',
      sqlcmd: databaseTools.sqlcmd ?? 'sqlcmd',
      healthBaseUrl: `http://${appStaticIp}:5000`,
      cell,
      databaseProvider: provider,
      databaseExternallyOwned: cell.databaseOwner === 'external',
      storageExternallyOwned: cell.storageOwner === 'external',
      createHostStateRoot: false,
    });
    checkpoints.ok(`external-${externalOwner}-owner-flipped`);
    const preview = executePackagedStep({
      checkpointName: 'recover-preview',
      cli,
      repo,
      cosign: boundaryCosign,
      instructionsPath: join(targetRelease.assets, 'offline-recovery-instructions.json'),
      operationId: 'offline-recover-preview',
      allowedExitCodes: [10],
      replacements: {
        '<host-update.json>': configPath,
        '<staging-dir>': targetStaging,
        '<trusted_root.json>': trustedRootPath,
        '<protected-backup.json>': protectedBackupPath,
      },
    });
    try {
      assertExpectedNeedsOperator(preview, cellSpec.expected.reason);
    } catch (error) {
      checkpoints.failed(`needs-operator-recover:${cellSpec.expected.reason}:not-observed`);
      throw cellFailure('needs_operator_evidence_unavailable', { actual: 'NeedsOperator', exitCode: preview.exitCode });
    }
    const afterPreview = stateContinuitySnapshot({ env, deploymentRoot, hostStateRoot, hostContainer, provider });
    assertEqualJson(`external-${externalOwner}-migration-heads-unchanged`, beforeRecovery.migrationHeads, afterPreview.migrationHeads);
    assertEqualJson(`external-${externalOwner}-volume-hashes-unchanged`, beforeRecovery.volumeHashes, afterPreview.volumeHashes);
    checkpoints.ok(`needs-operator-recover:${cellSpec.expected.reason}`);
    run.finishedAt = new Date().toISOString();
    const journalPath = join(runRoot, 'host-update', 'state', 'journal.ndjson');
    const journalPhase = lastJournalPhase(journalPath);
    recordFaultCheckpoint();
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
        expected: cellSpec.expected.outcome,
        expectedReason: cellSpec.expected.reason,
        actual: 'NeedsOperator',
        reason: cellSpec.expected.reason,
        exitCode: preview.exitCode,
        journalPhase,
      },
      timings: { activationSeconds, recoverySeconds: Math.max(1, Math.round((Date.now() - recoveryStarted) / 1000)) },
      verdict: 'pass',
      networkAttempts: readNetworkAttempts(networkAttemptsPath),
    });
    writeValidatedEvidence(evidencePath, evidence);
    const complete = new Error('expected needs-operator evidence written');
    complete.evidenceWritten = true;
    complete.success = true;
    throw complete;
  }
  for (const [operationId, checkpointName] of [
    ['offline-recover-preview', 'recover-preview'],
    ['offline-recover-confirm', 'recover-confirm'],
  ]) {
    executePackagedStep({
      checkpointName,
      cli,
      repo,
      cosign: boundaryCosign,
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

  assertRecoveredToPrior();

  run.finishedAt = new Date().toISOString();
  const journalPath = join(runRoot, 'host-update', 'state', 'journal.ndjson');
  const journalPhase = lastJournalPhase(journalPath);
  recordFaultCheckpoint();
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
      expectedReason: null,
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
  if (error?.evidenceWritten) {
    if (!error.success) {
      console.error(error.message);
    }
    process.exitCode = error.success ? 0 : 1;
  } else {
  run.finishedAt = new Date().toISOString();
  writeFileSync(join(runRoot, 'error.txt'), formatError(error), { mode: 0o600 });
  recordFaultCheckpoint();
  checkpoints.failed('e2e-complete');
  const failure = classifyFailure(error, join(runRoot, 'host-update', 'state', 'journal.ndjson'));
  const expectedOutcome = cellSpec.expected.outcome ?? 'RolledBack';
  const expectedReason = cellSpec.scenario === 'fault' ? null : cellSpec.expected.reason ?? null;
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
      expected: expectedOutcome,
      expectedReason,
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
  }
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

function installHostUpdateCli({ repo, runRoot, release, assetDir, trustedRootPath, cosign, hostContainer }) {
  const nativeRoot = hostContainer
    ? `/root/.cache/printfarmer-recovery-matrix/${basename(runRoot)}`
    : process.env.HOME
    ? join(process.env.HOME, '.cache', 'printfarmer-recovery-matrix', basename(runRoot))
    : join(runRoot, 'native-cache');
  const installRoot = join(nativeRoot, 'installed-cli');
  const installerWork = join(nativeRoot, 'installer-work');
  if (hostContainer) {
    hostExecFileSync(hostContainer, ['bash', '-lc', `mkdir -p "${installRoot}" "${installerWork}" && chmod 0755 "${installRoot}" && chmod 0700 "${installerWork}"`], {
      cwd: repo,
      stdio: ['ignore', 'pipe', 'pipe'],
    });
  } else {
    mkdirSync(installRoot, { recursive: true, mode: 0o755 });
    mkdirSync(installerWork, { recursive: true, mode: 0o700 });
    chmodSync(installRoot, 0o755);
    chmodSync(installerWork, 0o700);
  }
  const output = hostExecFileSync(hostContainer, [
    'bash',
    join(repo, 'scripts/install-host-update-cli.sh'),
    'install',
    '--version', release.version,
    '--asset-dir', assetDir,
    '--install-root', installRoot,
    '--runtime', 'linux-x64',
    '--trusted-root', trustedRootPath,
  ], {
    cwd: repo,
    env: {
      PATH: containerPath(dirname(cosign)),
      TMPDIR: installerWork,
    },
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  return output.trim().split(/\r?\n/).at(-1);
}

function provisionBoundaryHostState(container, repo, rootPath, { channel }) {
  const script = [
    "import { provisionFixtureHostState } from './scripts/ci/recovery-matrix/cell-runtime.mjs';",
    `provisionFixtureHostState(${JSON.stringify(rootPath)}, { channel: ${JSON.stringify(channel)} });`,
  ].join('\n');
  hostExecFileSync(container, ['node', '--input-type=module', '-e', script], {
    cwd: repo,
    stdio: ['ignore', 'pipe', 'pipe'],
  });
}

function packagedOperationArgv({ cli, instructionsPath, operationId, replacements, extraArgs = [] }) {
  const instructions = JSON.parse(readFileSync(instructionsPath, 'utf8'));
  const operation = instructions.operations.find(candidate => candidate.id === operationId);
  if (!operation) throw new Error(`missing_packaged_operation:${operationId}`);
  return [...operation.bash.map((argument, index) => {
    const value = replacements[argument] ?? argument;
    return index === 0 ? cli : value;
  }), ...extraArgs];
}

function runPackagedOperation({ cli, repo, cosign, instructionsPath, operationId, replacements, allowedExitCodes = [0], extraArgs = [] }) {
  const argv = packagedOperationArgv({ cli, instructionsPath, operationId, replacements, extraArgs });
  const result = hostSpawnSync(hostContainer, argv, {
    cwd: dirname(cli),
    env: {
      PATH: containerPath(dirname(cosign)),
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
function executePackagedStepDuringActivation({ checkpointName, markerPath, hookContext, autoOk = true, managedActivationFault = false, faultDecisionPath, ...operation }) {
  try {
    const result = runPackagedOperationDuringActivation({ ...operation, markerPath, hookContext, managedActivationFault, faultDecisionPath });
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

function runPackagedOperationDuringActivation({
  cli,
  repo,
  cosign,
  instructionsPath,
  operationId,
  replacements,
  markerPath,
  hookContext,
  managedActivationFault = false,
  faultDecisionPath,
  allowedExitCodes = [0],
}) {
  const argv = packagedOperationArgv({ cli, instructionsPath, operationId, replacements });
  const workDir = join(dirname(markerPath), `during-${Date.now()}-${process.pid}`);
  mkdirSync(workDir, { recursive: true });
  const stdoutPath = join(workDir, 'stdout.log');
  const stderrPath = join(workDir, 'stderr.log');
  const exitPath = join(workDir, 'exit-code');
  const baseline = fileMarker(markerPath);
  const dockerArgs = [
    'exec',
    '-w', dirname(cli),
    '-e', `PATH=${containerPath(dirname(cosign))}`,
    '-e', `PRINTFARMER_OFFLINE_BUNDLE_TOOL=${join(repo, 'scripts/ci/offline-update-bundle.mjs')}`,
    hostContainer,
    ...argv,
  ].map(shellQuote).join(' ');
  const launch = `(${shellQuote('/usr/bin/docker')} ${dockerArgs} >${shellQuote(stdoutPath)} 2>${shellQuote(stderrPath)}; printf '%s' "$?" >${shellQuote(exitPath)}) & echo $!`;
  const pid = Number(execFileSync('bash', ['-lc', launch], { encoding: 'utf8' }).trim());
  if (!Number.isInteger(pid) || pid <= 0) {
    throw new Error('during_activate_spawn_failed');
  }
  try {
    waitForDuringActivationPoint({
      markerAdvanced: () => fileMarkerAdvanced(markerPath, baseline),
      isComplete: () => existsSync(exitPath) || !processAlive(pid),
      runHook: () => {
        runFaultHook('during-activate', { ...hookContext, activationPid: pid });
        if (managedActivationFault && faultDecisionPath) {
          try {
            writeFileSync(faultDecisionPath, 'fail', { flag: 'wx' });
            faultState.injected = true;
          } catch (error) {
            if (error?.code !== 'EEXIST') throw error;
          }
        }
      },
      timeoutMs: 60_000,
    });
    const status = waitForBackgroundExit(pid, exitPath);
    const stdout = existsSync(stdoutPath) ? readFileSync(stdoutPath, 'utf8') : '';
    const stderr = existsSync(stderrPath) ? readFileSync(stderrPath, 'utf8') : '';
    if (stdout) process.stdout.write(stdout);
    if (stderr) process.stderr.write(stderr);
    if (!allowedExitCodes.includes(status)) {
      const error = new Error(`Command failed (${status}): ${argv.join(' ')}`);
      error.stdout = stdout;
      error.stderr = stderr;
      error.exitCode = status;
      error.operationId = operationId;
      throw error;
    }
    return { exitCode: status, stdout, stderr };
  } catch (error) {
    try {
      process.kill(pid, 'SIGTERM');
    } catch {
      // Process already exited.
    }
    throw error;
  }
}

// Starts a packaged operation in the host container without waiting, so a fault scenario can
// hold it at a pause gate and kill the host underneath it.
function launchPackagedOperation({ cli, repo, cosign, instructionsPath, operationId, replacements, workRoot }) {
  const argv = packagedOperationArgv({ cli, instructionsPath, operationId, replacements });
  const workDir = join(workRoot, `${operationId}-${Date.now()}-${process.pid}`);
  mkdirSync(workDir, { recursive: true });
  const stdoutPath = join(workDir, 'stdout.log');
  const stderrPath = join(workDir, 'stderr.log');
  const exitPath = join(workDir, 'exit-code');
  const dockerArgs = [
    'exec',
    '-w', dirname(cli),
    '-e', `PATH=${containerPath(dirname(cosign))}`,
    '-e', `PRINTFARMER_OFFLINE_BUNDLE_TOOL=${join(repo, 'scripts/ci/offline-update-bundle.mjs')}`,
    hostContainer,
    ...argv,
  ].map(shellQuote).join(' ');
  const launch = `(${shellQuote('/usr/bin/docker')} ${dockerArgs} >${shellQuote(stdoutPath)} 2>${shellQuote(stderrPath)}; printf '%s' "$?" >${shellQuote(exitPath)}) >/dev/null 2>&1 & echo $!`;
  const pid = Number(execFileSync('bash', ['-lc', launch], { encoding: 'utf8' }).trim());
  if (!Number.isInteger(pid) || pid <= 0) {
    throw new Error(`fault_launch_failed:${operationId}`);
  }
  return {
    pid,
    finished: () => existsSync(exitPath) || !processAlive(pid),
    wait: () => {
      const exitCode = waitForBackgroundExit(pid, exitPath);
      const stdout = existsSync(stdoutPath) ? readFileSync(stdoutPath, 'utf8') : '';
      const stderr = existsSync(stderrPath) ? readFileSync(stderrPath, 'utf8') : '';
      if (stdout) process.stdout.write(stdout);
      if (stderr) process.stderr.write(stderr);
      return { exitCode, stdout, stderr };
    },
  };
}

// Simulates power loss: every process in the host boundary (CLI, docker shim, tool shims) dies
// with SIGKILL, and only durable state (bind-mounted run root, container filesystem) survives.
function restartHostContainer(container) {
  const startedAt = () => execFileSync('/usr/bin/docker', ['inspect', '--format', '{{.State.StartedAt}}', container], { encoding: 'utf8' }).trim();
  const before = startedAt();
  // A failed kill throws, so a container that was never killed cannot pass as a power loss.
  execFileSync('/usr/bin/docker', ['kill', '--signal', 'KILL', container], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] });
  execFileSync('/usr/bin/docker', ['start', container], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] });
  const after = startedAt();
  if (after === before) throw new Error(`host_restart_not_observed:${container}:${before}`);
  const deadline = Date.now() + 60_000;
  for (;;) {
    const probe = spawnSync('/usr/bin/docker', ['exec', container, 'true'], { stdio: 'ignore' });
    if (probe.status === 0) return;
    if (Date.now() > deadline) throw new Error(`host_restart_timeout:${container}`);
    Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, 250);
  }
}

// Inserts one printer row using only required columns, so the physical reconciliation gate sees
// a non-empty inventory regardless of optional schema columns.
// Inserts one placeholder printer by filling every required column generically. A required
// foreign key takes an existing parent row, and an empty parent table (e.g. `Manufacturers`,
// which only the runtime seed service populates) is seeded the same way first.
function seedPrinterSql() {
  return `CREATE FUNCTION pg_temp.recovery_matrix_seed(tbl text) RETURNS void LANGUAGE plpgsql AS $fn$
DECLARE cols text; vals text; parent record; parent_rows bigint;
BEGIN
  FOR parent IN
    SELECT DISTINCT ccu.table_name AS ref_table
      FROM information_schema.table_constraints tc
      JOIN information_schema.key_column_usage kcu
        ON kcu.constraint_name = tc.constraint_name AND kcu.table_schema = tc.table_schema
      JOIN information_schema.constraint_column_usage ccu
        ON ccu.constraint_name = tc.constraint_name AND ccu.table_schema = tc.table_schema
      JOIN information_schema.columns c
        ON c.table_schema = kcu.table_schema AND c.table_name = kcu.table_name AND c.column_name = kcu.column_name
     WHERE tc.constraint_type = 'FOREIGN KEY' AND tc.table_schema = 'public' AND tc.table_name = tbl
       AND c.is_nullable = 'NO' AND ccu.table_name <> tbl
  LOOP
    EXECUTE format('SELECT count(*) FROM public.%I', parent.ref_table) INTO parent_rows;
    IF parent_rows = 0 THEN
      PERFORM pg_temp.recovery_matrix_seed(parent.ref_table);
    END IF;
  END LOOP;

  SELECT string_agg(quote_ident(c.column_name), ',' ORDER BY c.ordinal_position),
         string_agg(CASE
           WHEN fk.ref_table IS NOT NULL THEN format('(SELECT %I FROM public.%I LIMIT 1)', fk.ref_column, fk.ref_table)
           WHEN c.data_type IN ('integer', 'bigint', 'smallint', 'numeric', 'double precision', 'real') THEN '0'
           WHEN c.data_type = 'boolean' THEN 'false'
           WHEN c.data_type = 'uuid' THEN quote_literal(gen_random_uuid()::text) || '::uuid'
           WHEN c.data_type LIKE 'timestamp%' THEN 'now()'
           WHEN c.data_type IN ('json', 'jsonb') THEN quote_literal('{}')
           WHEN c.data_type = 'bytea' THEN quote_literal('') || '::bytea'
           ELSE quote_literal('recovery-matrix-' || lower(tbl))
         END, ',' ORDER BY c.ordinal_position)
    INTO cols, vals
    FROM information_schema.columns c
    LEFT JOIN LATERAL (
      SELECT ccu.table_name AS ref_table, ccu.column_name AS ref_column
        FROM information_schema.table_constraints tc
        JOIN information_schema.key_column_usage kcu
          ON kcu.constraint_name = tc.constraint_name AND kcu.table_schema = tc.table_schema
        JOIN information_schema.constraint_column_usage ccu
          ON ccu.constraint_name = tc.constraint_name AND ccu.table_schema = tc.table_schema
       WHERE tc.constraint_type = 'FOREIGN KEY' AND tc.table_schema = 'public' AND tc.table_name = tbl
         AND kcu.column_name = c.column_name AND ccu.table_name <> tbl
       LIMIT 1) fk ON true
   WHERE c.table_schema = 'public' AND c.table_name = tbl
     AND c.is_nullable = 'NO' AND c.column_default IS NULL AND c.is_identity = 'NO';
  EXECUTE format('INSERT INTO public.%I (%s) VALUES (%s)', tbl, cols, vals);
END $fn$;
SELECT pg_temp.recovery_matrix_seed('Printers');`;
}

function waitForBackgroundExit(pid, exitPath) {
  const deadline = Date.now() + 600_000;
  while (!existsSync(exitPath)) {
    if (!processAlive(pid)) {
      break;
    }
    if (Date.now() > deadline) {
      try {
        process.kill(pid, 'SIGTERM');
      } catch {
        // Process already exited.
      }
      throw new Error('during_activate_timeout');
    }
    Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, 250);
  }
  if (!existsSync(exitPath)) {
    throw new Error('during_activate_exit_missing');
  }
  return Number(readFileSync(exitPath, 'utf8').trim() || '1');
}

function fileMarker(path) {
  if (!existsSync(path)) {
    return { exists: false, size: 0, mtimeMs: 0 };
  }
  const stat = statSync(path);
  return { exists: true, size: stat.size, mtimeMs: stat.mtimeMs };
}

function fileMarkerAdvanced(path, baseline) {
  if (!existsSync(path)) {
    return false;
  }
  const stat = statSync(path);
  return !baseline.exists || stat.size > baseline.size || stat.mtimeMs > baseline.mtimeMs;
}

function processAlive(pid) {
  try {
    process.kill(pid, 0);
    return true;
  } catch {
    return false;
  }
}

function shellQuote(value) {
  return `'${String(value).replace(/'/g, `'\\''`)}'`;
}

function containerPath(prefix) {
  return `${prefix}:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin`;
}

function hostExecFileSync(container, argv, { cwd, env = {}, stdio = ['ignore', 'pipe', 'pipe'] } = {}) {
  return execFileSync('/usr/bin/docker', [
    'exec',
    '-w', cwd ?? '/',
    ...Object.entries(env).flatMap(([key, value]) => ['-e', `${key}=${value}`]),
    container,
    ...argv,
  ], { encoding: 'utf8', stdio });
}

function hostSpawnSync(container, argv, { cwd, env = {} } = {}) {
  return spawnSync('/usr/bin/docker', [
    'exec',
    '-w', cwd ?? '/',
    ...Object.entries(env).flatMap(([key, value]) => ['-e', `${key}=${value}`]),
    container,
    ...argv,
  ], {
    encoding: 'utf8',
    stdio: ['ignore', 'pipe', 'pipe'],
  });
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
    return null;
  }
  return sha256LargeFile(path);
}

function runFaultHook(point, context) {
  if (!hasFaultHooks(faultHooks) || !faultHooks[point]) {
    return false;
  }
  try {
    invokeFaultHook({
      hooks: faultHooks,
      point,
      context: { ...context, hook: point },
      run: (command, { env = {} } = {}) => hostExecFileSync(hostContainer, ['bash', '-lc', command], {
        cwd: repo,
        env,
        stdio: ['ignore', 'inherit', 'pipe'],
      }),
    });
    faultState.injected = true;
    return true;
  } catch (error) {
    error.reason = `fault_hook_failed:${point}`;
    throw error;
  }
}

function shouldInjectManagedActivationFault(spec) {
  return spec.scenario === 'recover' || spec.scenario === 'needs-operator-recover';
}

function enableManagedActivationFault(root, { mode }) {
  const enablePath = join(root, 'fault-compose-up.enable');
  const pausePath = join(root, 'fault-compose-up.pause');
  const decisionPath = join(root, 'fault-compose-up.decision');
  writeFileSync(enablePath, mode);
  return { enablePath, pausePath, decisionPath };
}

function recordFaultCheckpoint() {
  if (faultState.injected) {
    checkpoints.ok('fault-injected');
  } else {
    checkpoints.skipped('fault-injected');
  }
}

function proveNetworkDenialBoundary({ hostContainer, networkAttemptsPath }) {
  writeFileSync(networkAttemptsPath, '');
  const result = hostSpawnSync(hostContainer, [
    'bash',
    '-lc',
    [
      'set +e',
      "timeout 3 bash -lc 'cat </dev/null >/dev/tcp/1.1.1.1/443' >/dev/null 2>&1",
      'direct=$?',
      `getent hosts ${canaryDnsName} >/dev/null 2>&1`,
      'dns=$?',
      'test "$direct" -ne 0',
      'test "$dns" -ne 0',
    ].join('\n'),
  ], { cwd: repo });
  if (result.status !== 0) {
    throw new Error(`network_denial_canary_failed:exit=${result.status}:stdout=${result.stdout}:stderr=${result.stderr}`);
  }
  const deadline = Date.now() + 5000;
  while (Date.now() < deadline) {
    const attempts = readNetworkAttempts(networkAttemptsPath);
    if (hasCanaryAttempt(attempts)) {
      writeFileSync(networkAttemptsPath, '');
      return;
    }
    Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, 100);
  }
  throw new Error('network_denial_canary_not_recorded');
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
    matchJsonStringValue(combined, 'reason') ??
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
    reason: String(step ? `${step}:${reason}` : reason).slice(0, 200),
    exitCode: Number.isInteger(error?.exitCode) ? error.exitCode : 1,
    journalPhase: error?.journalPhase ?? safeJournalPhase(journalPath),
  };
}

function matchJsonStringValue(text, key) {
  const escapedKey = key.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const match = new RegExp(`"${escapedKey}"\\s*:\\s*"((?:[^"\\\\]|\\\\.)*)"`, 'u').exec(text);
  if (!match) return null;
  try {
    return JSON.parse(`"${match[1]}"`);
  } catch {
    return match[1];
  }
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
    const name = key.slice(2);
    if (name === 'fault') {
      parsed.fault = [...toArray(parsed.fault), argv[index + 1]];
    } else {
      parsed[name] = argv[index + 1];
    }
  }
  return parsed;
}

function toArray(value) {
  if (value === undefined) return [];
  return Array.isArray(value) ? value : [value];
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
      cpSync(cosign, join(toolsDir, 'cosign'));
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
        ...(attempt.source ? { source: attempt.source } : {}),
        ...(attempt.query ? { query: attempt.query } : {}),
      };
    });
}

function siblingIp(ip, hostOctet) {
  const parts = ip.split('.');
  if (parts.length !== 4) {
    throw new Error(`invalid_ipv4_address:${ip}`);
  }
  return [...parts.slice(0, 3), String(hostOctet)].join('.');
}

function imageEnvironment({ cell, priorImages, infrastructureLock }) {
  const values = {};
  for (const mapping of serviceMappingsFor(cell)) {
    const image = priorImages[mapping.serviceId];
    if (!image) {
      throw new Error(`missing_prior_image:${mapping.serviceId}`);
    }
    values[mapping.imageEnvironmentVariable] = `${image.reference}@${image.indexDigest}`;
  }
  for (const image of infrastructureLock.images) {
    const key = image.id === 'postgres'
      ? 'POSTGRES_IMAGE'
      : image.id === 'mssql'
      ? 'MSSQL_IMAGE'
      : `${image.id.toUpperCase().replaceAll('-', '_')}_IMAGE`;
    values[key] = `${image.reference}@${image.digest}`;
  }
  return values;
}

function assertInfrastructureLoadedFromBundle(decisionRecords, infrastructureLock) {
  const records = readImportDecisionRecords(decisionRecords);
  const latest = records.at(-1);
  if (!latest) {
    throw new Error('infrastructure_import_decision_missing');
  }
  const loaded = new Map((latest.loadedImages ?? []).map((image) => [image.member, image.digest]));
  const names = [];
  for (const image of infrastructureLock.images) {
    const member = `infrastructure-${image.id}.oci.tar`;
    if (loaded.get(member) !== image.digest) {
      throw new Error(`infrastructure_not_loaded_from_bundle:${image.id}`);
    }
    names.push(`${image.id}@${image.digest}`);
  }
  return names;
}

function readImportDecisionRecords(directory) {
  if (!existsSync(directory)) {
    return [];
  }
  const script = [
    "const { readdirSync, readFileSync, statSync } = require('fs');",
    "const { join } = require('path');",
    "const root = process.argv[1];",
    "const files = [];",
    "function walk(dir) { for (const name of readdirSync(dir)) { const file = join(dir, name); const stat = statSync(file); if (stat.isDirectory()) walk(file); else if (name.endsWith('.json')) files.push(file); } }",
    "walk(root);",
    "const records = files.map(file => { try { return JSON.parse(readFileSync(file, 'utf8')); } catch { return undefined; } }).filter(record => record && record.kind === 'printfarmer-offline-import-decision');",
    "records.sort((a, b) => String(a.decidedAt).localeCompare(String(b.decidedAt)));",
    "process.stdout.write(JSON.stringify(records));",
  ].join('\n');
  return JSON.parse(execFileSync('node', ['-e', script, directory], { encoding: 'utf8' }));
}

function waitForDatabaseReady(deploymentRoot, env, provider) {
  const args = [
    'compose',
    '-f', join(deploymentRoot, 'docker-compose.recovery.yml'),
    '-p', env.COMPOSE_PROJECT_NAME,
    'exec',
    '-T',
    'database',
    ...provider.readinessArgs(env),
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

function ensureHarnessDatabases(deploymentRoot, env, provider, cell) {
  if (provider.id === 'postgres') {
    if (cell.databaseLayout === 'split') {
      createPostgresDatabaseIfMissing(deploymentRoot, env, `${env.POSTGRES_DB}_slicer`);
    }
    return;
  }

  const databaseNames = [env.MSSQL_DB, ...(cell.databaseLayout === 'split' ? [`${env.MSSQL_DB}_slicer`] : [])];
  for (const databaseName of databaseNames) {
    databaseQuery(deploymentRoot, env, provider, `IF DB_ID(N'${databaseName}') IS NULL CREATE DATABASE [${databaseName}];`, { database: 'master' });
  }
}

function createPostgresDatabaseIfMissing(deploymentRoot, env, databaseName) {
  try {
    execFileSync('/usr/bin/docker', [
      'compose',
      '-f', join(deploymentRoot, 'docker-compose.recovery.yml'),
      '-p', env.COMPOSE_PROJECT_NAME,
      'exec',
      '-T',
      'database',
      'createdb',
      '-U',
      env.POSTGRES_USER,
      databaseName,
    ], {
      cwd: deploymentRoot,
      encoding: 'utf8',
      env: { ...process.env, ...env },
      stdio: ['ignore', 'pipe', 'pipe'],
    });
  } catch (error) {
    const stderr = String(error.stderr ?? '');
    if (!stderr.includes('already exists')) {
      throw error;
    }
  }
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

function stateContinuitySnapshot({ env, deploymentRoot, hostStateRoot, hostContainer, provider }) {
  const migrationHeads = databaseQuery(deploymentRoot, env, provider, provider.migrationHeadsSql)
    .split(/\r?\n/)
    .map((line) => line.trim())
    .filter(Boolean);
  const volumeHashes = Object.fromEntries(['app-data', 'models', 'gcode', 'profiles', 'keys']
    .map((volume) => [volume, dockerVolumeHash(`${env.COMPOSE_PROJECT_NAME}_${volume}`)]));
  return {
    migrationHeads,
    volumeHashes,
    hostState: readHostStateSnapshotFromBoundary(hostStateRoot, {
      exec: (argv) => hostExecFileSync(hostContainer, argv, { cwd: repo }),
    }),
  };
}

function mutationSnapshot({ env, deploymentRoot, hostStateRoot, hostContainer, provider, cell, priorImages }) {
  const snapshot = stateContinuitySnapshot({ env, deploymentRoot, hostStateRoot, hostContainer, provider });
  return {
    ...snapshot,
    serviceDigests: Object.fromEntries(activeServiceDigestExpectations(cell, priorImages)
      .map(({ serviceId, composeServiceName }) => [serviceId, runningComposeImageDigest(env, composeServiceName)])),
  };
}

// psql -tA prints `1|1`; sqlcmd prints the two columns separated by whitespace.
function parseSchemaDeltaState(output) {
  const values = String(output).trim().split(/[\s|]+/).filter(Boolean).map(Number);
  if (values.length !== 2 || values.some((value) => !Number.isInteger(value))) {
    throw new Error(`schema-delta fixture state unreadable: ${JSON.stringify(output)}`);
  }
  return { historyRows: values[0], tableExists: values[1] };
}

function databaseQuery(deploymentRoot, env, provider, sql, { database } = {}) {
  return execFileSync('/usr/bin/docker', [
    'compose',
    '-f', join(deploymentRoot, 'docker-compose.recovery.yml'),
    '-p', env.COMPOSE_PROJECT_NAME,
    'exec',
    '-T',
    'database',
    ...provider.queryArgs(env, sql, { database }),
  ], {
    cwd: deploymentRoot,
    encoding: 'utf8',
    env: { ...process.env, ...env },
    stdio: ['ignore', 'pipe', 'pipe'],
  });
}

function seedRemoteWorkerRegistration(deploymentRoot, env, provider) {
  const sql = provider.id === 'postgres'
    ? `INSERT INTO slicer."SlicerServices" ("Id", "Name", "SlicerType", "Host", "MaxConcurrentJobs", "Status", "LastSeen", "CreatedAt", "UpdatedAt") VALUES ('00000000-0000-0000-0000-000000003100', 'remote-matrix-worker', 0, 'http://10.99.0.5:5000', 1, 'Online', NOW(), NOW(), NOW());`
    : `INSERT INTO [slicer].[SlicerServices] ([Id], [Name], [SlicerType], [Host], [MaxConcurrentJobs], [Status], [LastSeen], [CreatedAt], [UpdatedAt]) VALUES (NEWID(), 'remote-matrix-worker', 0, 'http://10.99.0.5:5000', 1, 'Online', SYSUTCDATETIME(), SYSUTCDATETIME(), SYSUTCDATETIME());`;
  databaseQuery(deploymentRoot, env, provider, sql);
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

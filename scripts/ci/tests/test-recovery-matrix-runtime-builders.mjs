import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { appendFileSync, existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import test from 'node:test';

import { waitForDuringActivationPoint } from '../recovery-matrix/activation-runner.mjs';
import { containerizedDotnetArgs, recoveryHostStateRoot, writeHostUpdateConfig } from '../recovery-matrix/cell-runtime.mjs';
import { writeRecoveryCompose } from '../recovery-matrix/compose-config.mjs';
import { writeDockerShim } from '../recovery-matrix/docker-shim.mjs';
import { validateRecoveryEvidence } from '../recovery-matrix/evidence.mjs';
import { hasFaultHooks, invokeFaultHook, parseFaultHooks } from '../recovery-matrix/fault-hooks.mjs';
import { assertHostStateContinuity, readHostStateSnapshot, readHostStateSnapshotFromBoundary } from '../recovery-matrix/host-state-continuity.mjs';
import {
  canaryDnsLookupCommand,
  canaryDnsName,
  hasCanaryAttempt,
  withoutCanaryAttempts,
} from '../recovery-matrix/network-denial.mjs';

const scratchRoot = path.resolve('.recovery-matrix-test-work');

test('compose and host-update config target the monolith static IP for health checks', () => {
  const scratch = path.join(scratchRoot, `compose-${process.pid}-${Date.now()}`);
  mkdirSync(scratch, { recursive: true });
  try {
    const appIp = '172.30.44.20';
    const compose = writeRecoveryCompose({
      deploymentRoot: scratch,
      network: 'c2-test-network',
      egressSinkIp: '172.30.44.10',
      databaseHost: '172.30.44.11',
      appIp,
      runId: 'c2-test',
    });
    assert.deepEqual(compose.services.database.networks, ['c2-test-network']);
    assert.equal(compose.services.printfarmer.networks['c2-test-network'].ipv4_address, appIp);

    const configPath = path.join(scratch, 'host-update.json');
    writeHostUpdateConfig(configPath, {
      rootDirectory: path.join(scratch, 'host-update'),
      deploymentRoot: scratch,
      projectName: 'c2-test',
      databaseConnectionString: 'Host=database;Database=printfarmer',
      healthBaseUrl: `http://${appIp}:5000`,
    });
    const config = JSON.parse(readFileSync(configPath, 'utf8'));
    assert.equal(config.HostUpdateExecution.HealthCheckBaseUrl, `http://${appIp}:5000`);
  } finally {
    rmSync(scratch, { recursive: true, force: true });
  }
});

test('host-state root uses mounted run root normally and secure host-private storage in boundary mode', () => {
  const runRoot = path.join(scratchRoot, `run-root-${process.pid}-${Date.now()}`);
  const deploymentRoot = path.join(runRoot, 'deployment');
  mkdirSync(deploymentRoot, { recursive: true });
  try {
    assert.equal(recoveryHostStateRoot(runRoot), path.join(runRoot, 'host-state'));
    const hostStateRoot = recoveryHostStateRoot(runRoot, { hostBoundary: true });
    assert.equal(hostStateRoot, `/root/.cache/printfarmer-recovery-matrix/${path.basename(runRoot)}/host-state`);
    const configPath = path.join(deploymentRoot, 'host-update.json');
    writeHostUpdateConfig(configPath, {
      rootDirectory: path.join(runRoot, 'host-update'),
      deploymentRoot,
      projectName: 'c2-test',
      hostStateRoot,
      databaseConnectionString: 'Host=database;Database=printfarmer',
      createHostStateRoot: false,
    });
    const config = JSON.parse(readFileSync(configPath, 'utf8'));
    assert.equal(config.HostUpdates.HostState.RootPath, hostStateRoot);
  } finally {
    rmSync(runRoot, { recursive: true, force: true });
  }
});

test('fault hooks parse, no-op by default, and invoke each supported point', () => {
  const none = parseFaultHooks([]);
  assert.equal(hasFaultHooks(none), false);
  assert.equal(invokeFaultHook({ hooks: none, point: 'before-activate' }), false);

  for (const point of ['before-activate', 'during-activate', 'before-recover']) {
    const calls = [];
    const hooks = parseFaultHooks([`${point}=echo ${point}`]);
    assert.equal(hasFaultHooks(hooks), true);
    assert.equal(invokeFaultHook({
      hooks,
      point,
      context: { hook: point },
      run: (command, options) => calls.push({ command, options }),
    }), true);
    assert.equal(calls[0].command, `echo ${point}`);
    assert.equal(calls[0].options.env.PF_RECOVERY_HOOK, point);
  }
});

test('semantic host-state continuity allows advancing replay history but rejects regression', () => {
  const scratch = path.join(scratchRoot, `host-state-${process.pid}-${Date.now()}`);
  const beforeRoot = path.join(scratch, 'before');
  const afterRoot = path.join(scratch, 'after');
  mkdirSync(beforeRoot, { recursive: true });
  mkdirSync(afterRoot, { recursive: true });
  try {
    writeHostState(beforeRoot, { epoch: 1, highWater: { release: 1 }, identities: { prior: { version: '1.0.0-insider.1' } } });
    writeHostState(afterRoot, {
      epoch: 2,
      highWater: { release: 2 },
      identities: {
        prior: { version: '1.0.0-insider.1' },
        target: { version: '1.0.0-insider.2' },
      },
    });

    const before = readHostStateSnapshot(beforeRoot);
    const after = readHostStateSnapshot(afterRoot);
    assertHostStateContinuity(before, after, { targetVersion: '1.0.0-insider.2' });

    writeHostState(afterRoot, { epoch: 0, highWater: { release: 0 }, identities: {} });
    assert.throws(
      () => assertHostStateContinuity(before, readHostStateSnapshot(afterRoot), { targetVersion: '1.0.0-insider.2' }),
      /host_state_epoch_regressed|host_state_high_water_regressed|host_state_identities_missing/,
    );
  } finally {
    rmSync(scratch, { recursive: true, force: true });
  }
});

test('host-state snapshot reads a garbled replay store without crashing and fails continuity closed', () => {
  const scratch = path.join(scratchRoot, `host-state-garbled-${process.pid}-${Date.now()}`);
  const beforeRoot = path.join(scratch, 'before');
  const afterRoot = path.join(scratch, 'after');
  mkdirSync(beforeRoot, { recursive: true });
  mkdirSync(afterRoot, { recursive: true });
  try {
    writeHostState(beforeRoot, { epoch: 1, highWater: { release: 1 }, identities: { prior: { version: '1.0.0-insider.1' } } });
    writeHostState(afterRoot, { epoch: 1, highWater: { release: 1 }, identities: { prior: { version: '1.0.0-insider.1' } } });
    const garbled = '{"Version":1,"Epoch":0,"Checksum":"sha256:tampered"';
    writeFileSync(path.join(afterRoot, 'host-update-replay.json'), garbled);
    appendFileSync(path.join(afterRoot, 'replay-anchor.journal'), 'not-json\n');

    const after = readHostStateSnapshot(afterRoot);
    assert.equal(after.replay, null);
    assert.equal(after.rawReplay, garbled);
    assert.equal(after.replayChecksumValid, false);
    assert.equal(after.anchorValid, false);
    assert.deepEqual(after.journalEntries.at(-1), { unparsable: 'not-json' });
    assert.notDeepEqual(after, readHostStateSnapshot(beforeRoot));
    assert.throws(() => assertHostStateContinuity(readHostStateSnapshot(beforeRoot), after), /host_state_replay_checksum_invalid/);
  } finally {
    rmSync(scratch, { recursive: true, force: true });
  }
});

test('network-denial canary accounting is segregated from real attempts', () => {
  const attempts = [
    { at: '2026-09-27T00:00:00Z', destination: canaryDnsName, protocol: 'udp/53', source: '172.30.1.2:45555' },
    { at: '2026-09-27T00:00:01Z', destination: 'api.github.com', protocol: 'udp/53', source: '172.30.1.2:45556' },
  ];
  assert.equal(hasCanaryAttempt(attempts), true);
  assert.deepEqual(withoutCanaryAttempts(attempts), [attempts[1]]);
});

test('network-denial canary lookup passes an absolute name to the resolver', () => {
  const scratch = path.join(scratchRoot, `resolver-canary-${process.pid}-${Date.now()}`);
  const capturePath = path.join(scratch, 'getent-arguments.txt');
  mkdirSync(scratch, { recursive: true });
  try {
    execFileSync('bash', ['-c', [
      'getent() { printf "%s\\n" "$*" > "$CAPTURE_PATH"; return 2; }',
      canaryDnsLookupCommand,
      'test "$?" -eq 2',
    ].join('\n')], {
      env: { ...process.env, CAPTURE_PATH: capturePath },
      stdio: ['ignore', 'pipe', 'pipe'],
    });
    assert.equal(readFileSync(capturePath, 'utf8'), `hosts ${canaryDnsName}.\n`);
  } finally {
    rmSync(scratch, { recursive: true, force: true });
  }
});

test('docker shim denies daemon-mediated pull and records it as egress evidence', () => {
  const scratch = path.join(scratchRoot, `docker-shim-${process.pid}-${Date.now()}`);
  const deploymentRoot = path.join(scratch, 'deployment');
  const attemptsPath = path.join(scratch, 'attempts.ndjson');
  mkdirSync(deploymentRoot, { recursive: true });
  writeFileSync(path.join(deploymentRoot, '.env'), 'COMPOSE_PROJECT_NAME=test\n');
  try {
    const shim = writeDockerShim(scratch, deploymentRoot, attemptsPath);
    assert.throws(
      () => execFileSync('bash', [shim, 'pull', 'alpine:latest'], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }),
      /docker shim denied daemon-mediated network command: pull/,
    );
    const attempts = readFileSync(attemptsPath, 'utf8').trim().split(/\r?\n/).map((line) => JSON.parse(line));
    assert.equal(attempts.length, 1);
    assert.equal(attempts[0].protocol, 'docker-daemon');
    assert.equal(attempts[0].destination, 'docker:pull');
    assert.equal(attempts[0].source, 'docker-shim');
    const errors = validateRecoveryEvidence(minimalPassingEvidence({ attempts }));
    assert.match(errors.join('\n'), /networkDenial\.attempts/);
  } finally {
    rmSync(scratch, { recursive: true, force: true });
  }
});

test('docker shim records failed compose commands with their stderr', { skip: !existsSync('/usr/bin/docker') }, () => {
  const scratch = path.join(scratchRoot, `docker-shim-failure-${process.pid}-${Date.now()}`);
  const deploymentRoot = path.join(scratch, 'deployment');
  mkdirSync(deploymentRoot, { recursive: true });
  try {
    const shim = writeDockerShim(scratch, deploymentRoot, path.join(scratch, 'attempts.ndjson'));
    assert.throws(() => execFileSync('bash', [shim, 'compose', '-f', '/nonexistent.yml', 'ps'], { stdio: ['ignore', 'pipe', 'pipe'] }));
    const failures = readFileSync(path.join(scratch, 'docker-command-failures.ndjson'), 'utf8').trim().split(/\r?\n/).map((line) => JSON.parse(line));
    assert.equal(failures.length, 1);
    assert.notEqual(failures[0].exitCode, 0);
    assert.deepEqual(failures[0].args, ['compose', '-f', '/nonexistent.yml', 'ps']);
    assert.ok(failures[0].stderr.length > 0);
  } finally {
    rmSync(scratch, { recursive: true, force: true });
  }
});

test('boundary host-state snapshot is exported through the host container command', () => {
  const scratch = path.join(scratchRoot, `boundary-state-${process.pid}-${Date.now()}`);
  mkdirSync(scratch, { recursive: true });
  try {
    writeHostState(scratch, { epoch: 3, highWater: { release: 7 }, identities: { target: { version: '1.0.0-insider.2' } } });
    const snapshot = readHostStateSnapshotFromBoundary('/root/.cache/printfarmer-recovery-matrix/run/host-state', {
      exec(argv) {
        assert.deepEqual(argv.slice(0, 3), ['node', '--input-type=module', '-e']);
        assert.match(argv[3], /\/root\/\.cache\/printfarmer-recovery-matrix\/run\/host-state/);
        return JSON.stringify(readHostStateSnapshot(scratch));
      },
    });
    assert.equal(snapshot.replay.Epoch, 3);
    assert.equal(snapshot.anchorValid, true);
  } finally {
    rmSync(scratch, { recursive: true, force: true });
  }
});

test('during-activate hook runs after marker and before activation completion, and fails closed', () => {
  const order = [];
  let polls = 0;
  waitForDuringActivationPoint({
    markerAdvanced() {
      order.push('marker-check');
      polls += 1;
      return polls > 1;
    },
    isComplete() {
      order.push('complete-check');
      return false;
    },
    runHook() {
      order.push('hook');
    },
    sleep() {
      order.push('sleep');
    },
  });
  assert.equal(order.at(-1), 'hook');

  assert.throws(() => waitForDuringActivationPoint({
    markerAdvanced: () => false,
    isComplete: () => true,
    runHook: () => {},
  }), /during_activate_marker_not_observed/);
  assert.throws(() => waitForDuringActivationPoint({
    markerAdvanced: () => true,
    isComplete: () => false,
    runHook: () => { throw new Error('hook failed'); },
  }), /hook failed/);
});

function writeHostState(root, { epoch, highWater, identities }) {
  const replay = {
    Version: 1,
    Epoch: epoch,
    HighWaterByNamespace: highWater,
    Identities: identities,
  };
  replay.Checksum = sha256Json(replay);
  const replayPath = path.join(root, 'host-update-replay.json');
  writeFileSync(replayPath, JSON.stringify(replay));
  const raw = readFileSync(replayPath, 'utf8');
  const stateHash = createHash('sha256').update(raw).digest('hex');
  const anchor = {
    Version: 1,
    Epoch: epoch,
    PreviousHash: '',
    StateHash: stateHash,
  };
  anchor.Hash = createHash('sha256')
    .update(`${anchor.Version}|${anchor.Epoch}|${anchor.PreviousHash}|${anchor.StateHash}`, 'utf8')
    .digest('hex');
  writeFileSync(path.join(root, 'replay-anchor.json'), JSON.stringify(anchor));
  writeFileSync(path.join(root, 'replay-anchor.journal'), `${JSON.stringify(anchor)}\n`);
}

function minimalPassingEvidence({ attempts }) {
  const now = '2026-09-27T00:00:00.000Z';
  const release = {
    tag: 'v1.0.0-insider.1',
    version: '1.0.0-insider.1',
    channel: 'insider',
    sourceBranch: 'development',
    sourceCommit: 'a'.repeat(40),
    buildId: '1',
    sequence: 1,
  };
  return {
    schema: 2,
    kind: 'printfarmer-recovery-matrix-evidence',
    run: {
      id: 'c2-test',
      startedAt: now,
      finishedAt: now,
      harnessCommit: 'b'.repeat(40),
      entryPoint: 'bash',
    },
    host: {
      distribution: 'ubuntu',
      distributionVersion: '24.04',
      arch: 'x64',
      kernel: 'test',
    },
    cell: {
      topology: 'monolith',
      provider: 'postgres',
      databaseLayout: 'shared',
      databaseOwner: 'host',
      storageOwner: 'host',
      workers: 'managed',
    },
    identities: {
      source: release,
      prior: release,
      target: { ...release, tag: 'v1.0.0-insider.2', version: '1.0.0-insider.2', sequence: 2 },
      signingRoot: 'fixture-ephemeral',
      signingRootFingerprint: 'c'.repeat(64),
      schemaDelta: 'identical',
      bundleSha256: 'd'.repeat(64),
    },
    tools: {
      cli: '1.0.0/linux-x64 sha256:' + 'e'.repeat(64),
      docker: '29.0.0',
      compose: '2.40.0',
      cosign: 'v3.0.6',
      node: '24.0.0',
      shell: 'bash',
    },
    networkDenial: {
      mechanism: 'docker-internal-network+default-deny-egress-sink',
      egressSinkActive: true,
      attempts,
    },
    checkpoints: [{ name: 'ok', at: now, result: 'ok' }],
    outcome: {
      expected: 'RolledBack',
      actual: 'RolledBack',
      reason: null,
      exitCode: 0,
      journalPhase: 'Completed/recovery rolled-back',
    },
    timings: { activationSeconds: 1, recoverySeconds: 1 },
    verdict: 'pass',
  };
}

function sha256Json(value) {
  return `sha256:${createHash('sha256').update(JSON.stringify(value), 'utf8').digest('hex')}`;
}

test('containerized dotnet fallback runs as a non-root invoking user so cleanup can remove its output', () => {
  const args = containerizedDotnetArgs({
    commandArgs: ['publish', 'x.csproj'],
    cwd: '/repo',
    mounts: ['/repo', '/work/run', '/repo'],
    uid: 1000,
    gid: 1001,
  });
  assert.deepEqual(args.slice(0, 4), ['run', '--rm', '--user', '1000:1001']);
  assert.ok(args.includes('DOTNET_CLI_HOME=/tmp/pf-dotnet-home'));
  assert.equal(args.filter((arg) => arg === '-v').length, 2);
  assert.ok(args.includes('/work/run:/work/run'));
  assert.deepEqual(args.slice(-6), ['-w', '/repo', 'mcr.microsoft.com/dotnet/sdk:10.0-noble', 'dotnet', 'publish', 'x.csproj']);

  const rootArgs = containerizedDotnetArgs({ commandArgs: ['--info'], cwd: '/repo', mounts: ['/repo'], uid: 0, gid: 0 });
  assert.ok(!rootArgs.includes('--user'));
  // `undefined` would fall back to process.getuid() on Linux; null simulates Windows, which has no uid.
  const windowsArgs = containerizedDotnetArgs({ commandArgs: ['--info'], cwd: '/repo', mounts: ['/repo'], uid: null, gid: null });
  assert.ok(!windowsArgs.includes('--user'));
});

import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import test from 'node:test';

import { recoveryHostStateRoot, writeHostUpdateConfig } from '../recovery-matrix/cell-runtime.mjs';
import { writeRecoveryCompose } from '../recovery-matrix/compose-config.mjs';
import { hasFaultHooks, invokeFaultHook, parseFaultHooks } from '../recovery-matrix/fault-hooks.mjs';
import { assertHostStateContinuity, readHostStateSnapshot } from '../recovery-matrix/host-state-continuity.mjs';
import { canaryDnsName, hasCanaryAttempt, withoutCanaryAttempts } from '../recovery-matrix/network-denial.mjs';

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

test('network-denial canary accounting is segregated from real attempts', () => {
  const attempts = [
    { at: '2026-09-27T00:00:00Z', destination: canaryDnsName, protocol: 'udp/53', source: '172.30.1.2:45555' },
    { at: '2026-09-27T00:00:01Z', destination: 'api.github.com', protocol: 'udp/53', source: '172.30.1.2:45556' },
  ];
  assert.equal(hasCanaryAttempt(attempts), true);
  assert.deepEqual(withoutCanaryAttempts(attempts), [attempts[1]]);
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

function sha256Json(value) {
  return `sha256:${createHash('sha256').update(JSON.stringify(value), 'utf8').digest('hex')}`;
}

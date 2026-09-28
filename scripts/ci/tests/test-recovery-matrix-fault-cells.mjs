import assert from 'node:assert/strict';
import { spawn, spawnSync } from 'node:child_process';
import { chmodSync, existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import test from 'node:test';

import { cellIds, cellsById, resolveCell, resolveCellList, runnableCellIds } from '../recovery-matrix/cells.mjs';
import { dockerFaultFiles, wrapToolWithPauseGate, writeDockerShim } from '../recovery-matrix/docker-shim.mjs';
import { evidenceKind, evidenceSchema, networkDenialMechanism, validateRecoveryEvidence } from '../recovery-matrix/evidence.mjs';
import { faultCellIds, faultCells, faultCheckpointName, faultKinds } from '../recovery-matrix/fault-cells.mjs';
import {
  classifyCliResult,
  countCalls,
  countPhase,
  dockerCommandCounts,
  outcomeMatches,
  parseCliText,
  readDockerCommands,
  readJournalTolerant,
  runFaultScenario,
  uncertainPhases,
  waitFor,
} from '../recovery-matrix/fault-scenarios.mjs';

const repoRoot = path.resolve(import.meta.dirname, '../../..');
const hasBash = spawnSync('bash', ['-c', 'true']).status === 0 && process.platform !== 'win32';

function scratch(name) {
  const dir = path.join(repoRoot, '.recovery-matrix-work', 'unit', `${name}-${process.pid}`);
  rmSync(dir, { recursive: true, force: true });
  mkdirSync(dir, { recursive: true });
  return dir;
}

test('fault cells cover every #3101 fault kind on the supported c2 shape', () => {
  assert.deepEqual([...new Set(faultCells.map((entry) => entry.fault.kind))].sort(), [...faultKinds].sort());
  const c2 = resolveCell('c2').cell;
  for (const entry of faultCells) {
    assert.equal(entry.scenario, 'fault');
    assert.deepEqual(entry.cell, c2, entry.id);
    assert.match(entry.id, /^fault-/);
    assert.ok(['Activated', 'RolledBack', 'NeedsOperator', 'RecoveryRequired'].includes(entry.expected.outcome), entry.id);
    assert.equal(entry.expected.failClosed, false);
    if (!['Activated', 'RolledBack'].includes(entry.expected.outcome)) {
      assert.ok(entry.expected.reason, `${entry.id} needs a stable reason`);
    }
    assert.match(faultCheckpointName(entry.fault), /^fault:[a-z-]+:[a-z:-]+$/);
  }
  const powerLossPoints = faultCells.filter((entry) => entry.fault.kind === 'power-loss').map((entry) => entry.fault.point);
  assert.deepEqual(powerLossPoints, [
    'backup:before',
    'migration:before',
    'migration:after-side-effect',
    'apply:before',
    'apply:after-side-effect',
  ]);
});

test('fault cells are runnable individually and as a group without changing `all`', () => {
  assert.equal(resolveCellList('all').length, cellIds.length);
  assert.ok(cellIds.every((id) => !id.startsWith('fault-')));
  assert.deepEqual(resolveCellList('faults').map((entry) => entry.id), faultCellIds);
  assert.deepEqual(runnableCellIds, [...cellIds, ...faultCellIds]);
  for (const id of faultCellIds) {
    assert.equal(resolveCell(id), cellsById[id]);
    assert.ok(resolveCell(id).fault);
  }
});

test('parseCliText and classifyCliResult map CLI text to recovery outcomes', () => {
  assert.deepEqual(parseCliText('exitCode: 0\noutcome: RolledBack\ndetail: coordinated_restore\noutcome: Ignored'), {
    exitCode: '0',
    outcome: 'RolledBack',
    detail: 'coordinated_restore',
  });
  assert.deepEqual(
    pick(classifyCliResult({ exitCode: 0, stdout: 'exitCode: 0\noutcome: AlreadyRolledBack\ndetail: coordinated_restore' })),
    { actual: 'RolledBack', reason: 'coordinated_restore' },
  );
  assert.deepEqual(
    pick(classifyCliResult({ exitCode: 0, stdout: 'plan.kind: CoordinatedRestore\nplan.detail: coordinated_restore' })),
    { actual: 'RecoveryRequired', reason: 'coordinated_restore' },
  );
  assert.deepEqual(
    pick(classifyCliResult({ exitCode: 10, stdout: 'plan.kind: NeedsOperator\nplan.detail: no_backup_available' })),
    { actual: 'NeedsOperator', reason: 'no_backup_available' },
  );
  assert.deepEqual(
    pick(classifyCliResult({ exitCode: 6, stdout: 'decision: refused\nreason: HostUpdateApplyFailedException\nstate: RecoveryRequired' })),
    { actual: 'RecoveryRequired', reason: 'HostUpdateApplyFailedException' },
  );
  assert.deepEqual(pick(classifyCliResult({ exitCode: 0, stdout: 'state: Completed' })), { actual: 'Activated', reason: null });
  assert.deepEqual(pick(classifyCliResult({ exitCode: 4, stdout: '', stderr: 'code: journal_integrity_failure' })), {
    actual: 'RecoveryRequired',
    reason: 'journal_integrity_failure',
  });
  assert.deepEqual(pick(classifyCliResult({ exitCode: 6, stdout: 'decision: refused\nreason: not_in_recovery' })), {
    actual: 'Refused',
    reason: 'not_in_recovery',
  });
});

test('outcomeMatches compares the outcome and a stable reason prefix', () => {
  const result = { actual: 'FenceReleasePending', reason: 'physical_reconciliation_pending|fence_release_failed:IOException' };
  assert.ok(outcomeMatches(result, { outcome: 'FenceReleasePending', reason: 'physical_reconciliation_pending' }));
  assert.ok(outcomeMatches(result, { outcome: 'FenceReleasePending', reason: null }));
  assert.ok(!outcomeMatches(result, { outcome: 'RolledBack' }));
  assert.ok(!outcomeMatches({ actual: 'NeedsOperator', reason: null }, { outcome: 'NeedsOperator', reason: 'no_backup_available' }));
});

test('journal helpers find fenced steps and tolerate torn lines', () => {
  const dir = scratch('journal');
  const journal = path.join(dir, 'journal.ndjson');
  const record = (phase) => JSON.stringify({ Payload: JSON.stringify({ Phase: phase }) });
  writeFileSync(journal, [
    record('backup:before'),
    record('backup:after'),
    record('migration:before'),
    record('migration:after'),
    record('apply:before'),
    '{"Payload": "torn',
  ].join('\n'));
  const { lines, activities, unreadable } = readJournalTolerant(journal);
  assert.equal(lines, 6);
  assert.equal(unreadable, 1);
  assert.deepEqual(uncertainPhases(activities), ['apply']);
  assert.equal(countPhase(activities, 'migration:before'), 1);
  assert.equal(readJournalTolerant(journal, { fromLine: 4 }).activities.length, 1);
  assert.deepEqual(readJournalTolerant(path.join(dir, 'missing.ndjson')), { lines: 0, activities: [], unreadable: 0 });
});

test('docker command counts distinguish migration probe/apply and compose up', () => {
  const dir = scratch('docker-log');
  const log = path.join(dir, 'docker-commands.ndjson');
  const entry = (args) => JSON.stringify({ at: 'now', args });
  writeFileSync(log, [
    entry(['run', '--rm', 'img', '--host-update-migration', 'AppDbContext', 'probe', 'Npgsql']),
    entry(['run', '--rm', 'img', '--host-update-migration', 'AppDbContext', 'apply', 'Npgsql']),
    entry(['run', '--rm', 'img', '--host-update-migration', 'SlicerDbContext', 'probe', 'Npgsql']),
    entry(['compose', '-f', 'x.yml', 'up', '-d', 'printfarmer']),
    entry(['compose', '-f', 'x.yml', 'ps']),
    '{"torn',
  ].join('\n'));
  const { lines, commands } = readDockerCommands(log);
  assert.equal(lines, 6);
  assert.deepEqual(dockerCommandCounts(commands), {
    migrationApply: { AppDbContext: 1 },
    migrationProbe: { AppDbContext: 1, SlicerDbContext: 1 },
    composeUp: 1,
  });
  assert.equal(readDockerCommands(log, { fromLine: 3 }).commands.length, 2);
});

test('waitFor returns the first truthy value and fails with a stable reason', () => {
  let calls = 0;
  assert.equal(waitFor(() => (++calls >= 2 ? 'ready' : null), { intervalMs: 1 }), 'ready');
  assert.throws(() => waitFor(() => null, { timeoutMs: 5, intervalMs: 1, label: 'never' }), /fault_wait_timeout:never/);
});

function pick({ actual, reason }) {
  return { actual, reason };
}

function fakeDocker(dir) {
  const fake = path.join(dir, 'real-docker');
  writeFileSync(fake, `#!/usr/bin/env bash\nprintf '%s\\n' "$*" >> ${JSON.stringify(path.join(dir, 'real-calls'))}\nexit "\${FAKE_STATUS:-0}"\n`);
  chmodSync(fake, 0o755);
  return fake;
}

function writeShim(dir) {
  const deploymentRoot = path.join(dir, 'deployment');
  mkdirSync(deploymentRoot, { recursive: true });
  return writeDockerShim(dir, deploymentRoot, path.join(dir, 'egress', 'attempts.ndjson'), { realDocker: fakeDocker(dir) });
}

function realCalls(dir) {
  const file = path.join(dir, 'real-calls');
  return existsSync(file) ? readFileSync(file, 'utf8').split('\n').filter(Boolean) : [];
}

// Launches the shim, waits for the pause marker, writes the decision and returns the exit code.
async function runPaused(shim, args, { pausePath, decisionPath, decision, env = {} }) {
  const child = spawn(shim, args, { env: { ...process.env, ...env }, stdio: 'ignore' });
  const exited = new Promise((resolve) => child.on('exit', (code) => resolve(code)));
  const deadline = Date.now() + 20_000;
  while (!existsSync(pausePath)) {
    if (Date.now() > deadline) {
      child.kill('SIGKILL');
      throw new Error('pause marker never written');
    }
    await new Promise((resolve) => setTimeout(resolve, 25));
  }
  let pause;
  try {
    pause = JSON.parse(readFileSync(pausePath, 'utf8'));
  } finally {
    writeFileSync(decisionPath, decision);
  }
  return { code: await exited, pause };
}

test('docker shim pause-before gate holds the side effect until the harness decides', { skip: !hasBash }, async () => {
  const dir = scratch('shim-before');
  const shim = writeShim(dir);
  const files = Object.fromEntries(Object.entries(dockerFaultFiles).map(([key, name]) => [key, path.join(dir, name)]));
  const migration = ['run', '--rm', 'img', '--host-update-migration', 'AppDbContext', 'apply', 'Npgsql'];

  writeFileSync(files.spec, JSON.stringify({ tokens: ['--host-update-migration', 'AppDbContext', 'apply'], mode: 'pause-before' }));
  assert.equal(spawnSync(shim, ['run', '--rm', 'img', '--host-update-migration', 'AppDbContext', 'probe', 'Npgsql']).status, 0);
  assert.equal(realCalls(dir).length, 1, 'non-matching command passes through without claiming the gate');
  assert.ok(existsSync(files.spec));

  const failed = await runPaused(shim, migration, { pausePath: files.pause, decisionPath: files.decision, decision: 'fail' });
  assert.equal(failed.code, 70);
  assert.equal(failed.pause.mode, 'pause-before');
  assert.equal(realCalls(dir).length, 1, 'a failed pause-before never executes the side effect');
  assert.ok(!existsSync(files.spec), 'the gate is one-shot');

  assert.equal(spawnSync(shim, migration).status, 0, 'a disarmed gate passes through');
  assert.equal(realCalls(dir).length, 2);

  writeFileSync(files.spec, JSON.stringify({ tokens: ['compose', 'up'], mode: 'pause-before' }));
  rmSync(files.pause, { force: true });
  const ran = await runPaused(shim, ['compose', '-f', 'x.yml', 'up', '-d', 'printfarmer'], {
    pausePath: files.pause,
    decisionPath: files.decision,
    decision: 'run',
  });
  assert.equal(ran.code, 0);
  assert.equal(realCalls(dir).length, 3);
  const logged = readDockerCommands(path.join(dir, 'docker-commands.ndjson')).commands;
  assert.equal(dockerCommandCounts(logged).composeUp, 1);
});

test('docker shim pause-after gate runs the side effect and can report failure', { skip: !hasBash }, async () => {
  const dir = scratch('shim-after');
  const shim = writeShim(dir);
  const files = Object.fromEntries(Object.entries(dockerFaultFiles).map(([key, name]) => [key, path.join(dir, name)]));
  writeFileSync(files.spec, JSON.stringify({ tokens: ['compose', 'up'], mode: 'pause-after' }));
  const result = await runPaused(shim, ['compose', '-f', 'x.yml', 'up', '-d'], {
    pausePath: files.pause,
    decisionPath: files.decision,
    decision: 'fail',
  });
  assert.equal(result.pause.mode, 'pause-after');
  assert.equal(result.pause.status, 0);
  assert.equal(result.code, 70);
  assert.equal(realCalls(dir).length, 1, 'the side effect executed before the injected failure');

  writeFileSync(files.spec, JSON.stringify({ tokens: ['compose', 'up'], mode: 'pause-after' }));
  rmSync(files.pause, { force: true });
  const passthrough = await runPaused(shim, ['compose', 'up'], {
    pausePath: files.pause,
    decisionPath: files.decision,
    decision: 'return',
    env: { FAKE_STATUS: '3' },
  });
  assert.equal(passthrough.pause.status, 3);
  assert.equal(passthrough.code, 3, 'the real exit status is returned unless the harness fails the call');
});

test('wrapped tool records every call and supports a pause-before gate', { skip: !hasBash }, async () => {
  const dir = scratch('tool');
  const tool = path.join(dir, 'pg_dump');
  writeFileSync(tool, `#!/usr/bin/env bash\nprintf 'ran %s\\n' "$*" >> ${JSON.stringify(path.join(dir, 'tool-ran'))}\n`);
  chmodSync(tool, 0o755);
  const gate = wrapToolWithPauseGate(tool, { name: 'pg_dump' });
  assert.ok(existsSync(`${tool}.real`));
  assert.equal(spawnSync(tool, ['-Fc']).status, 0);
  assert.equal(countCalls(gate.calls), 1);

  writeFileSync(gate.spec, JSON.stringify({ tokens: [], mode: 'pause-before' }));
  const failed = await runPaused(tool, ['-Fc'], { pausePath: gate.pause, decisionPath: gate.decision, decision: 'fail' });
  assert.equal(failed.code, 70);
  assert.equal(countCalls(gate.calls), 2);
  assert.equal(readFileSync(path.join(dir, 'tool-ran'), 'utf8').split('\n').filter(Boolean).length, 1);
});

test('fault evidence validates for every expected fault outcome', () => {
  for (const entry of faultCells) {
    const operatorVisible = !['Activated', 'RolledBack'].includes(entry.expected.outcome);
    const record = {
      schema: evidenceSchema,
      kind: evidenceKind,
      run: {
        id: `${entry.id}-run`,
        startedAt: '2026-09-28T10:00:00Z',
        finishedAt: '2026-09-28T10:20:00Z',
        harnessCommit: 'b'.repeat(40),
        entryPoint: 'bash',
      },
      host: { distribution: 'ubuntu', distributionVersion: '24.04', arch: 'x64', kernel: '6.8.0-45-generic' },
      cell: { ...entry.cell },
      identities: {
        source: { tag: 'v0.2.3', version: '0.2.3', channel: 'insider', sourceCommit: 'a'.repeat(40), buildId: 'b-3', sequence: 3 },
        target: { tag: 'v0.2.4', version: '0.2.4', channel: 'insider', sourceCommit: 'a'.repeat(40), buildId: 'b-4', sequence: 4 },
        prior: { tag: 'v0.2.3', version: '0.2.3', channel: 'insider', sourceCommit: 'a'.repeat(40), buildId: 'b-3', sequence: 3 },
        bundleSha256: 'c'.repeat(64),
        signingRoot: 'fixture-ephemeral',
        signingRootFingerprint: 'e'.repeat(64),
        schemaDelta: 'identical',
      },
      tools: { cli: '0.2.4', docker: '29.1.3', compose: '2.29.7', cosign: '2.4.1', node: '24.0.0', shell: 'bash 5.2.21' },
      networkDenial: { mechanism: networkDenialMechanism, egressSinkActive: true, attempts: [] },
      checkpoints: [
        { name: faultCheckpointName(entry.fault), at: '2026-09-28T10:05:00Z', result: 'ok' },
        { name: 'fault-injected', at: '2026-09-28T10:05:00Z', result: 'ok' },
      ],
      outcome: {
        expected: entry.expected.outcome,
        expectedReason: null,
        actual: entry.expected.outcome,
        reason: operatorVisible ? entry.expected.reason : null,
        exitCode: entry.expected.outcome === 'NeedsOperator' ? 10 : entry.expected.outcome === 'RecoveryRequired' ? 4 : 0,
        journalPhase: 'RecoveryRequired',
      },
      timings: { activationSeconds: 60, recoverySeconds: 60 },
      verdict: 'pass',
    };
    assert.deepEqual(validateRecoveryEvidence(record), [], entry.id);
  }
});

// Scenario-level harness proof: a fake ctx drives the api-down scenario end to end so the
// terminal durability and fence assertions are exercised without a live deployment.
function fakeScenarioCtx(name, { replayComposeUpAfterRestart = false, releaseFenceEarly = false } = {}) {
  const dir = scratch(name);
  const ctx = {
    cellSpec: faultCells.find((entry) => entry.id === 'fault-api-down'),
    journalPath: path.join(dir, 'journal.jsonl'),
    dockerLogPath: path.join(dir, 'docker.jsonl'),
    admissionClosedPath: path.join(dir, 'admission.closed'),
    toolGates: { pgRestore: { calls: path.join(dir, 'pg_restore.calls') } },
    passed: [],
    failedCheckpoints: [],
    restarts: 0,
    rolledBack: false,
  };
  ctx.checkpoints = { ok: (checkpoint) => ctx.passed.push(checkpoint), failed: (checkpoint) => ctx.failedCheckpoints.push(checkpoint) };
  ctx.markInjected = () => {};
  ctx.enableComposeFault = () => {};
  ctx.stopApplication = () => {};
  ctx.assertRolledBack = () => {};
  ctx.restartHost = () => { ctx.restarts += 1; };
  const docker = (args) => writeFileSync(ctx.dockerLogPath, `${JSON.stringify({ args })}\n`, { flag: 'a' });
  ctx.op = (operationId) => {
    if (operationId === 'offline-activate') {
      writeFileSync(ctx.admissionClosedPath, '');
      return { exitCode: 6, stdout: 'state: RecoveryRequired\nreason: compose_up_failed\n', stderr: '' };
    }
    if (operationId === 'host-update-status') return { exitCode: 0, stdout: '', stderr: '' };
    if (operationId === 'offline-recover-preview') {
      return { exitCode: 0, stdout: 'plan.kind: RestoreBackup\n', stderr: '' };
    }
    if (!ctx.rolledBack) {
      ctx.rolledBack = true;
      writeFileSync(ctx.toolGates.pgRestore.calls, 'restore\n');
      rmSync(ctx.admissionClosedPath, { force: true });
    }
    if (replayComposeUpAfterRestart && ctx.restarts > 0) docker(['compose', 'up', '-d', '--remove-orphans']);
    if (releaseFenceEarly) rmSync(ctx.admissionClosedPath, { force: true });
    return { exitCode: 0, stdout: 'outcome: RolledBack\n', stderr: '' };
  };
  return ctx;
}

test('api-down scenario passes when the rollback is durable and the fence tracks the outcome', () => {
  const ctx = fakeScenarioCtx('scenario-pass');
  const result = runFaultScenario(ctx);
  assert.equal(result.actual, 'RolledBack');
  assert.deepEqual(ctx.failedCheckpoints, []);
  assert.ok(ctx.passed.includes('fence-held:activation-fault'));
  assert.ok(ctx.passed.includes('fence-released:durable'));
  assert.ok(ctx.passed.includes('restart-no-apply-or-migration-replay'));
});

test('api-down scenario fails when a post-restart repeat replays compose up', () => {
  const ctx = fakeScenarioCtx('scenario-replay', { replayComposeUpAfterRestart: true });
  assert.throws(() => runFaultScenario(ctx), (error) => error.reason.startsWith('restart-no-apply-or-migration-replay'));
  assert.ok(ctx.failedCheckpoints.includes('restart-no-apply-or-migration-replay'));
});

test('api-down scenario fails when the fence is released while RecoveryRequired', () => {
  const ctx = fakeScenarioCtx('scenario-fence');
  const activate = ctx.op;
  ctx.op = (operationId, options) => {
    const result = activate(operationId, options);
    if (operationId === 'offline-activate') rmSync(ctx.admissionClosedPath, { force: true });
    return result;
  };
  assert.throws(() => runFaultScenario(ctx), (error) => error.reason.startsWith('fence-held:activation-fault'));
});
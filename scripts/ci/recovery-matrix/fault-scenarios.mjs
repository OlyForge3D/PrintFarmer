// Live fault-injection scenarios for issue #3101. run-cell.mjs builds a context (`ctx`) around
// one prepared c2 deployment (prior activated, target imported) and hands control here for any
// cell with `scenario: 'fault'`. Each scenario injects exactly one fault, proves the product
// fenced the uncertainty truthfully, redrives the operator path, and proves the final outcome is
// durable across a host restart without blindly replaying restore, apply or migration.
import { existsSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';

import { composeUpTokens, faultCheckpointName, migrationApplyTokens } from './fault-cells.mjs';

export const faultExitCodes = Object.freeze({
  success: 0,
  stateUnreadable: 4,
  refused: 6,
  needsOperator: 10,
  fenceReleasePending: 11,
  physicalReconciliationPending: 13,
});

const physicalTokenPattern = /physical-[0-9a-f]{32}/;

export function parseCliText(stdout) {
  const fields = {};
  for (const line of String(stdout ?? '').split(/\r?\n/)) {
    const match = /^([A-Za-z][A-Za-z0-9_.[\]-]*): (.*)$/.exec(line.trim());
    if (match && !(match[1] in fields)) {
      fields[match[1]] = match[2].trim();
    }
  }
  return fields;
}

// Maps one CLI invocation to the recovery-matrix outcome vocabulary. The CLI prints `key: value`
// lines; recover confirm reports `outcome`, recover preview reports `plan.kind`, activation reports
// `state`, and state failures (exit 4) report only a `code`.
export function classifyCliResult({ exitCode, stdout = '', stderr = '' }) {
  const fields = parseCliText(stdout);
  const errorFields = parseCliText(stderr);
  const code = fields.code ?? errorFields.code ?? null;
  if (fields.outcome) {
    const actual = fields.outcome === 'AlreadyRolledBack' ? 'RolledBack' : fields.outcome;
    return { actual, reason: fields.detail ?? fields.reason ?? code, exitCode, fields };
  }
  if (fields['plan.kind']) {
    const actual = fields['plan.kind'] === 'NeedsOperator' ? 'NeedsOperator' : 'RecoveryRequired';
    return { actual, reason: fields['plan.detail'] ?? code, exitCode, fields };
  }
  if (fields.state) {
    const actual = fields.state === 'Completed' ? 'Activated' : fields.state;
    return { actual, reason: fields.reason ?? code, exitCode, fields };
  }
  if (exitCode === faultExitCodes.stateUnreadable) {
    return { actual: 'RecoveryRequired', reason: code, exitCode, fields };
  }
  if (exitCode === faultExitCodes.refused || fields.decision === 'refused') {
    return { actual: 'Refused', reason: fields.reason ?? code, exitCode, fields };
  }
  return { actual: exitCode === 0 ? 'Activated' : 'RecoveryRequired', reason: code, exitCode, fields };
}

export function outcomeMatches(result, expected) {
  if (result.actual !== expected.outcome) return false;
  if (!expected.reason) return true;
  return typeof result.reason === 'string' && result.reason.startsWith(expected.reason);
}

export function readJournalTolerant(journalPath, { fromLine = 0 } = {}) {
  if (!existsSync(journalPath)) return { lines: 0, activities: [], unreadable: 0 };
  const lines = readFileSync(journalPath, 'utf8').split(/\r?\n/).filter(Boolean);
  const activities = [];
  let unreadable = 0;
  for (const line of lines.slice(fromLine)) {
    try {
      const record = JSON.parse(line);
      const activity = typeof record.Payload === 'string' ? JSON.parse(record.Payload) : record.Activity ?? record;
      activities.push(activity);
    } catch {
      unreadable += 1;
    }
  }
  return { lines: lines.length, activities, unreadable };
}

export function phaseOf(activity) {
  return activity?.Phase ?? activity?.phase ?? '';
}

// Steps whose `:before` marker has no `:after`: the side effect may or may not have happened.
export function uncertainPhases(activities) {
  const phases = activities.map(phaseOf);
  return phases
    .filter((phase) => phase.endsWith(':before'))
    .map((phase) => phase.slice(0, -':before'.length))
    .filter((step) => !phases.includes(`${step}:after`));
}

export function countPhase(activities, phase) {
  return activities.filter((activity) => phaseOf(activity) === phase).length;
}

export function readDockerCommands(logPath, { fromLine = 0 } = {}) {
  if (!existsSync(logPath)) return { lines: 0, commands: [] };
  const lines = readFileSync(logPath, 'utf8').split(/\r?\n/).filter(Boolean);
  const commands = [];
  for (const line of lines.slice(fromLine)) {
    try {
      const entry = JSON.parse(line);
      if (Array.isArray(entry.args)) commands.push(entry.args);
    } catch {
      // A torn final line from a killed shim is not a command that ran.
    }
  }
  return { lines: lines.length, commands };
}

const hasTokens = (args, tokens) => tokens.every((token) => args.includes(token));

export function dockerCommandCounts(commands) {
  const migrationApply = {};
  const migrationProbe = {};
  let composeUp = 0;
  for (const args of commands) {
    const index = args.indexOf('--host-update-migration');
    if (index >= 0) {
      const context = args[index + 1];
      const verb = args[index + 2];
      const bucket = verb === 'apply' ? migrationApply : verb === 'probe' ? migrationProbe : null;
      if (bucket) bucket[context] = (bucket[context] ?? 0) + 1;
      continue;
    }
    if (args[0] === 'compose' && hasTokens(args, composeUpTokens)) composeUp += 1;
  }
  return { migrationApply, migrationProbe, composeUp };
}

export function countCalls(callsPath) {
  if (!existsSync(callsPath)) return 0;
  return readFileSync(callsPath, 'utf8').split(/\r?\n/).filter(Boolean).length;
}

export function faultScenarioFailure(reason, { actual = 'RecoveryRequired', exitCode = 1 } = {}) {
  const error = new Error(reason);
  error.reason = reason;
  error.actual = actual;
  error.exitCode = exitCode;
  return error;
}

export function waitFor(predicate, { timeoutMs = 300_000, intervalMs = 250, label = 'condition' } = {}) {
  const deadline = Date.now() + timeoutMs;
  for (;;) {
    const value = predicate();
    if (value) return value;
    if (Date.now() > deadline) throw faultScenarioFailure(`fault_wait_timeout:${label}`);
    Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, intervalMs);
  }
}

export function runFaultScenario(ctx) {
  const scenario = scenarios[ctx.cellSpec.fault.kind];
  if (!scenario) throw faultScenarioFailure(`unknown_fault_kind:${ctx.cellSpec.fault.kind}`);
  const harness = createHarness(ctx);
  const result = scenario(harness, ctx.cellSpec);
  if (!outcomeMatches(result, ctx.cellSpec.expected)) {
    harness.failed(`final-outcome:${result.actual}`);
    throw faultScenarioFailure(`unexpected_fault_outcome:${result.actual}:${result.reason}`, result);
  }
  harness.ok(`final-outcome:${result.actual}${result.reason ? `:${result.reason}` : ''}`);
  return { ...result, activationSeconds: harness.activationSeconds(), recoverySeconds: harness.recoverySeconds() };
}

function createHarness(ctx) {
  const journalBaseline = readJournalTolerant(ctx.journalPath).lines;
  const dockerBaseline = readDockerCommands(ctx.dockerLogPath).lines;
  let activationStarted;
  let activationFinished;
  let recoveryStarted;
  let recoveryFinished;
  const harness = {
    ok: (name) => ctx.checkpoints.ok(name),
    failed: (name) => ctx.checkpoints.failed(name),
    injected(fault) {
      ctx.markInjected();
      ctx.checkpoints.ok(faultCheckpointName(fault));
    },
    journal: () => readJournalTolerant(ctx.journalPath, { fromLine: journalBaseline }),
    dockerMark: () => readDockerCommands(ctx.dockerLogPath).lines,
    dockerSince: (fromLine = dockerBaseline) => dockerCommandCounts(readDockerCommands(ctx.dockerLogPath, { fromLine }).commands),
    restoreCalls: () => countCalls(ctx.toolGates.pgRestore.calls),
    activate({ allowedExitCodes, extraArgs } = {}) {
      activationStarted ??= Date.now();
      const result = ctx.op('offline-activate', { allowedExitCodes, extraArgs });
      activationFinished = Date.now();
      return classifyCliResult(result);
    },
    launchActivate() {
      activationStarted ??= Date.now();
      return ctx.launchOp('offline-activate');
    },
    recover(operationId, { extraArgs } = {}) {
      recoveryStarted ??= Date.now();
      const result = ctx.op(operationId, { extraArgs });
      recoveryFinished = Date.now();
      return { ...classifyCliResult(result), stdout: result.stdout, stderr: result.stderr };
    },
    status() {
      const result = ctx.op('host-update-status', {});
      return result.exitCode;
    },
    expect(label, result, expected) {
      if (!outcomeMatches(result, expected)) {
        ctx.checkpoints.failed(`${label}:${result.actual}`);
        throw faultScenarioFailure(`${label}_unexpected:${result.actual}:${result.reason}:exit=${result.exitCode}`, result);
      }
      if (expected.exitCode !== undefined && result.exitCode !== expected.exitCode) {
        ctx.checkpoints.failed(`${label}:exit=${result.exitCode}`);
        throw faultScenarioFailure(`${label}_exit_code:${result.exitCode}`, result);
      }
      ctx.checkpoints.ok(`${label}:${result.actual}${result.reason ? `:${result.reason}` : ''}:exit=${result.exitCode}`);
      return result;
    },
    require(condition, label, detail = '') {
      if (!condition) {
        ctx.checkpoints.failed(label);
        throw faultScenarioFailure(`${label}${detail ? `:${detail}` : ''}`);
      }
      ctx.checkpoints.ok(label);
    },
    activationSeconds: () => seconds(activationStarted, activationFinished),
    recoverySeconds: () => seconds(recoveryStarted, recoveryFinished),
    ctx,
  };
  return harness;
}

function seconds(start, end) {
  if (!start) return 0;
  return Math.max(1, Math.round(((end ?? Date.now()) - start) / 1000));
}

function readPause(path) {
  if (!existsSync(path)) return null;
  try {
    return JSON.parse(readFileSync(path, 'utf8'));
  } catch {
    return null;
  }
}

function armGate(spec, { tokens = [], mode = 'pause-before' }) {
  writeFileSync(spec, JSON.stringify({ tokens, mode }));
}

function clearGate(gate) {
  for (const path of [gate.spec, gate.pause, gate.decision]) rmSync(path, { force: true });
}

function gateFor(harness, trigger) {
  if (trigger.tool === 'pg_dump') return { gate: harness.ctx.toolGates.pgDump, tokens: [], mode: 'pause-before' };
  if (trigger.docker) return { gate: harness.ctx.dockerGate, tokens: trigger.docker.tokens, mode: trigger.docker.mode };
  throw faultScenarioFailure('fault_trigger_unknown');
}

// Starts target activation, holds it at the armed gate and returns once the pause marker proves
// the executor reached the fault point.
function activateUntilPaused(harness, trigger, label) {
  const { gate, tokens, mode } = gateFor(harness, trigger);
  clearGate(gate);
  armGate(gate.spec, { tokens, mode });
  const running = harness.launchActivate();
  const pause = waitFor(() => readPause(gate.pause) ?? (running.finished() ? 'exited' : null), {
    timeoutMs: 600_000,
    label: `${label}-pause`,
  });
  if (pause === 'exited') {
    const early = classifyCliResult(running.wait());
    harness.failed(`${label}-paused`);
    throw faultScenarioFailure(`fault_point_not_reached:${label}:${early.actual}:${early.reason}`, early);
  }
  harness.ok(`${label}-paused:${pause.mode}`);
  return { gate, running, pause };
}

function assertFenced(harness, step) {
  const { activities } = harness.journal();
  const uncertain = uncertainPhases(activities);
  harness.require(uncertain.includes(step), `journal-fenced:${step}:before-without-after`, uncertain.join(','));
}

function powerLoss(harness, running, label) {
  harness.ctx.restartHost();
  running.wait();
  harness.ok(`${label}-host-restarted`);
}

// Terminal proof shared by every cell: a repeat of the final operator action must not add a
// restore, compose up or migration apply, and the outcome must survive a host restart.
function proveDurable(harness, final, repeat) {
  const restoresBefore = harness.restoreCalls();
  const dockerMark = harness.dockerMark();
  const again = repeat();
  harness.expect('repeat-operator-action', again, { outcome: final.actual, reason: final.reason ? final.reason.split('|')[0] : null });
  const replay = harness.dockerSince(dockerMark);
  harness.require(harness.restoreCalls() === restoresBefore, 'repeat-no-restore-replay');
  harness.require(replay.composeUp === 0 && Object.keys(replay.migrationApply).length === 0, 'repeat-no-apply-or-migration-replay',
    JSON.stringify(replay));
  harness.ctx.restartHost();
  harness.ok('durability-host-restarted');
  harness.ok(`status-after-restart:exit=${harness.status()}`);
  const durable = repeat();
  harness.expect('outcome-durable-after-restart', durable, { outcome: final.actual, reason: final.reason ? final.reason.split('|')[0] : null });
  harness.require(harness.restoreCalls() === restoresBefore, 'restart-no-restore-replay');
  return final;
}

function recoverToRolledBack(harness) {
  const preview = harness.recover('offline-recover-preview');
  harness.expect('recover-preview', preview, { outcome: 'RecoveryRequired' });
  const confirm = harness.recover('offline-recover-confirm');
  harness.expect('recover-confirm', confirm, { outcome: 'RolledBack', exitCode: 0 });
  harness.ctx.assertRolledBack();
  return proveDurable(harness, confirm, () => harness.recover('offline-recover-confirm'));
}

function activatedDurably(harness, result) {
  harness.ctx.assertActivated();
  return proveDurable(harness, result, () => {
    const again = harness.activate({ allowedExitCodes: [0, 6] });
    return again.actual === 'Activated' || again.reason === 'replay_accepted'
      ? { ...again, actual: 'Activated', reason: null }
      : again;
  });
}

// Fails the next target activation at compose up without executing it and returns the
// RecoveryRequired activation result.
function activateIntoRecoveryRequired(harness, label = 'activation-fault') {
  harness.ctx.enableComposeFault('fail-now');
  const result = harness.activate({ allowedExitCodes: [0, 6] });
  harness.expect(label, result, { outcome: 'RecoveryRequired' });
  return result;
}

const scenarios = {
  'power-loss'(harness, spec) {
    const { fault } = spec;
    const step = fault.point.split(':')[0];
    const label = `power-loss-${step}`;
    const { gate, running, pause } = activateUntilPaused(harness, fault.trigger, label);
    if (pause.mode === 'pause-after') {
      harness.require(pause.status === 0, `${label}-side-effect-completed`, `status=${pause.status}`);
    }
    assertFenced(harness, step);
    const restoresBeforeKill = harness.restoreCalls();
    powerLoss(harness, running, label);
    harness.injected(fault);
    clearGate(gate);
    assertFenced(harness, step);

    const beforeRedrive = harness.journal();
    const dockerMark = harness.dockerMark();
    const redrive = harness.activate({ allowedExitCodes: [0, 4, 6, 10, 11, 13] });
    harness.expect('redrive-activate', redrive, fault.redrive);
    const redriveCommands = harness.dockerSince(dockerMark);
    const whole = harness.dockerSince();
    const after = harness.journal();
    for (const [context, count] of Object.entries(whole.migrationApply)) {
      harness.require(count <= 1, `migration-apply-at-most-once:${context}`, `count=${count}`);
    }
    harness.require(whole.composeUp <= 1, 'compose-up-at-most-once', `count=${whole.composeUp}`);
    harness.require(harness.restoreCalls() === restoresBeforeKill, 'redrive-no-restore');
    if (step !== 'backup') {
      harness.require(countPhase(after.activities, `${step}:before`) === countPhase(beforeRedrive.activities, `${step}:before`),
        `redrive-did-not-restart-unsafe-step:${step}`);
    }
    if (redrive.actual === 'RecoveryRequired') {
      harness.require(redriveCommands.composeUp === 0 && Object.keys(redriveCommands.migrationApply).length === 0,
        'uncertain-redrive-no-side-effects', JSON.stringify(redriveCommands));
      return recoverToRolledBack(harness);
    }
    return activatedDurably(harness, redrive);
  },

  'partial-migration'(harness, spec) {
    const trigger = { docker: { tokens: migrationApplyTokens, mode: 'pause-after' } };
    const { gate, running } = activateUntilPaused(harness, trigger, 'partial-migration');
    harness.ctx.dbQuery(`INSERT INTO "__EFMigrationsHistory" ("MigrationId","ProductVersion") VALUES ('29990101000000_RecoveryMatrixPartialMigration','10.0.0');`);
    harness.ok('partial-migration-row-written');
    writeFileSync(gate.decision, 'fail');
    harness.injected(spec.fault);
    const failed = classifyCliResult(running.wait());
    harness.expect('partial-migration-activation', failed, { outcome: 'RecoveryRequired' });
    clearGate(gate);
    return recoverToRolledBack(harness);
  },

  'partial-apply'(harness, spec) {
    const trigger = { docker: { tokens: composeUpTokens, mode: 'pause-after' } };
    const { gate, running, pause } = activateUntilPaused(harness, trigger, 'partial-apply');
    harness.require(pause.status === 0, 'partial-apply-compose-up-executed', `status=${pause.status}`);
    harness.ok(`partial-apply-running-digest:${harness.ctx.runningDigest()}`);
    writeFileSync(gate.decision, 'fail');
    harness.injected(spec.fault);
    const failed = classifyCliResult(running.wait());
    harness.expect('partial-apply-activation', failed, { outcome: 'RecoveryRequired' });
    clearGate(gate);
    return recoverToRolledBack(harness);
  },

  'api-down'(harness, spec) {
    activateIntoRecoveryRequired(harness);
    harness.ctx.stopApplication();
    harness.injected(spec.fault);
    harness.ok(`status-while-api-down:exit=${harness.status()}`);
    return recoverToRolledBack(harness);
  },

  'missing-backup'(harness, spec) {
    activateIntoRecoveryRequired(harness);
    const backups = join(harness.ctx.runRoot, 'host-update', 'backups');
    rmSync(backups, { recursive: true, force: true });
    harness.injected(spec.fault);
    const before = harness.ctx.snapshot();
    const expected = { outcome: 'NeedsOperator', reason: 'no_backup_available', exitCode: faultExitCodes.needsOperator };
    harness.expect('recover-preview', harness.recover('offline-recover-preview'), expected);
    const confirm = harness.expect('recover-confirm', harness.recover('offline-recover-confirm'), expected);
    harness.ctx.assertNoMutation('missing-backup', before, harness.ctx.snapshot());
    harness.ok('missing-backup-no-mutation');
    return proveDurable(harness, confirm, () => harness.recover('offline-recover-preview'));
  },

  'corrupt-journal'(harness, spec) {
    activateIntoRecoveryRequired(harness);
    const lines = readFileSync(harness.ctx.journalPath, 'utf8').split(/\r?\n/);
    const record = JSON.parse(lines[0]);
    const payload = JSON.parse(record.Payload);
    payload.Phase = `${payload.Phase}-tampered`;
    record.Payload = JSON.stringify(payload);
    lines[0] = JSON.stringify(record);
    writeFileSync(harness.ctx.journalPath, lines.join('\n'));
    harness.injected(spec.fault);
    const before = harness.ctx.snapshot();
    const expected = { outcome: 'RecoveryRequired', reason: spec.expected.reason, exitCode: faultExitCodes.stateUnreadable };
    harness.expect('recover-preview', harness.recover('offline-recover-preview'), expected);
    const confirm = harness.expect('recover-confirm', harness.recover('offline-recover-confirm'), expected);
    harness.ctx.assertNoMutation('corrupt-journal', before, harness.ctx.snapshot());
    harness.ok('corrupt-journal-no-mutation');
    return proveDurable(harness, confirm, () => harness.recover('offline-recover-confirm'));
  },

  'corrupt-replay'(harness, spec) {
    const replayPath = `${harness.ctx.hostStateRoot}/host-update-replay.json`;
    harness.ctx.hostShell(`printf '{"Version":1,"Epoch":0,"Checksum":"sha256:tampered"' > '${replayPath}'`);
    harness.injected(spec.fault);
    const before = harness.ctx.snapshot();
    const journalBefore = harness.journal().lines;
    const result = harness.activate({ allowedExitCodes: [4, 6] });
    const expected = { outcome: 'RecoveryRequired', reason: spec.expected.reason, exitCode: faultExitCodes.stateUnreadable };
    harness.expect('activate-with-corrupt-replay', result, expected);
    harness.require(harness.journal().lines === journalBefore, 'corrupt-replay-journal-unchanged');
    harness.ctx.assertNoMutation('corrupt-replay', before, harness.ctx.snapshot());
    harness.ok('corrupt-replay-no-mutation');
    return proveDurable(harness, result, () => harness.activate({ allowedExitCodes: [4, 6] }));
  },

  'fence-release'(harness, spec) {
    harness.ctx.seedPrinter();
    harness.ok('printer-inventory-seeded');
    activateIntoRecoveryRequired(harness);
    const preview = harness.recover('offline-recover-preview');
    harness.expect('recover-preview', preview, { outcome: 'RecoveryRequired' });
    const pending = harness.recover('offline-recover-confirm');
    harness.injected(spec.fault);
    harness.expect('recover-confirm-pending', pending, {
      outcome: 'FenceReleasePending',
      reason: 'physical_reconciliation_pending',
      exitCode: faultExitCodes.physicalReconciliationPending,
    });
    harness.require(existsSync(harness.ctx.admissionClosedPath), 'fence-held-while-pending');
    const restores = harness.restoreCalls();
    harness.ctx.restartHost();
    harness.ok('pending-host-restarted');
    const pendingPreview = harness.recover('offline-recover-preview');
    const token = physicalTokenPattern.exec(`${pendingPreview.stdout}\n${pendingPreview.stderr}`)?.[0];
    harness.require(Boolean(token), 'physical-reconciliation-token-offered');
    const released = harness.recover('offline-recover-confirm', { extraArgs: ['--printers-reconciled', token] });
    harness.expect('recover-confirm-release', released, { outcome: 'RolledBack', exitCode: 0 });
    harness.require(harness.restoreCalls() === restores, 'release-redrive-no-restore-replay');
    harness.require(!existsSync(harness.ctx.admissionClosedPath), 'fence-released');
    harness.ctx.assertRolledBack();
    return proveDurable(harness, released, () => harness.recover('offline-recover-confirm'));
  },
};

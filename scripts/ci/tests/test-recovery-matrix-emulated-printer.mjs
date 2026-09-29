import assert from 'node:assert/strict';
import { createHmac } from 'node:crypto';
import { existsSync, mkdirSync, rmSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import test from 'node:test';

import {
  classifyDispatchProbe,
  commandsSince,
  emulatorIpFor,
  mintHarnessJwt,
  networkRequestScript,
  parseEmulatorRequests,
  parseNetworkResponse,
  pointPrinterAtEmulatorSql,
} from '../recovery-matrix/emulated-printer.mjs';
import { faultCells } from '../recovery-matrix/fault-cells.mjs';
import { runFaultScenario } from '../recovery-matrix/fault-scenarios.mjs';

const repoRoot = path.resolve(import.meta.dirname, '../../..');

function scratch(name) {
  const dir = path.join(repoRoot, '.recovery-matrix-work', 'unit', `${name}-${process.pid}`);
  rmSync(dir, { recursive: true, force: true });
  mkdirSync(dir, { recursive: true });
  return dir;
}

test('emulator address stays on the run subnet and never collides with the app', () => {
  assert.equal(emulatorIpFor('172.30.7.20'), '172.30.7.40');
  assert.throws(() => emulatorIpFor('172.30.7.40'), /emulator_ip_collides_with_app/);
  assert.throws(() => emulatorIpFor('not-an-ip'), /invalid_app_ip/);
  assert.throws(() => emulatorIpFor('172.30.7.999'), /invalid_app_ip/);
});

test('printer attach SQL points the seeded printer at the emulator as a Moonraker backend', () => {
  const sql = pointPrinterAtEmulatorSql('172.30.7.40');
  assert.match(sql, /"ServerUrl" = 'http:\/\/172\.30\.7\.40'/);
  assert.match(sql, /"BackendPort" = 7125/);
  assert.match(sql, /"Backend" = 1/);
  assert.match(sql, /RETURNING "Id"/);
  assert.throws(() => pointPrinterAtEmulatorSql("1.2.3.4'; DROP TABLE x;--"), /invalid_emulator_ip/);
});

test('harness JWT is an HS256 farm_admin token signed with the run key', () => {
  const key = 'k'.repeat(48);
  const token = mintHarnessJwt({ key, issuer: 'iss', audience: 'aud', nowSeconds: 1000 });
  const [header, payload, signature] = token.split('.');
  assert.equal(signature, createHmac('sha256', key).update(`${header}.${payload}`).digest('base64url'));
  const claims = JSON.parse(Buffer.from(payload, 'base64url').toString());
  assert.equal(claims.role, 'farm_admin');
  assert.equal(claims['http://schemas.microsoft.com/ws/2008/06/identity/claims/role'], 'farm_admin');
  assert.equal(claims.iss, 'iss');
  assert.equal(claims.aud, 'aud');
  assert.equal(claims.exp, 1600);
  assert.throws(() => mintHarnessJwt({ key: 'short', issuer: 'i', audience: 'a' }), /harness_jwt_key_missing/);
});

test('dispatch probe classification distinguishes the fence from auth, outage and admission', () => {
  assert.equal(classifyDispatchProbe({ status: 409, body: '{"error":"host_update_admission_closed"}' }), 'admission-closed');
  assert.equal(classifyDispatchProbe({ status: 409, body: '{"error":"other"}' }), 'admitted');
  assert.equal(classifyDispatchProbe({ status: 428, body: '' }), 'admitted');
  assert.equal(classifyDispatchProbe({ status: 401, body: '' }), 'unauthenticated');
  assert.equal(classifyDispatchProbe({ status: 403, body: '' }), 'unauthenticated');
  assert.equal(classifyDispatchProbe({ status: 503, body: '' }), 'server-error');
  assert.equal(classifyDispatchProbe({ status: -1, body: 'URLError' }), 'unreachable');
});

test('network request helper reads its spec from the environment and parses the last line', () => {
  assert.match(networkRequestScript, /os\.environ\['REQ'\]/);
  assert.deepEqual(parseNetworkResponse('noise\n{"status":428,"body":"x"}\n'), { status: 428, body: 'x' });
  assert.throws(() => parseNetworkResponse('{"status":"x","body":1}'), /network_response_invalid/);
});

test('emulator command delta uses the cumulative counter and names offenders', () => {
  const baseline = parseEmulatorRequests(JSON.stringify({ total: 3, commands: 0, entries: [] }));
  const current = parseEmulatorRequests(JSON.stringify({
    total: 6,
    commands: 1,
    entries: [
      { sequence: 4, transport: 'jsonrpc', method: 'printer.objects.query', target: '', isCommand: false },
      { sequence: 5, transport: 'jsonrpc', method: 'printer.gcode.script', target: 'G28', isCommand: true },
      { sequence: 6, transport: 'http', method: 'GET', target: '/server/info', isCommand: false },
    ],
  }));
  assert.deepEqual(commandsSince(baseline, current), { count: 1, reads: 2, offenders: ['jsonrpc:printer.gcode.script:G28'] });
  assert.throws(() => commandsSince(current, baseline), /emulator_request_log_regressed/);
  assert.throws(() => parseEmulatorRequests('{"total":1}'), /emulator_request_log_invalid/);
});

// Scenario-level proof with a fake ctx: pending recovery holds the fence, the dispatch probe is
// admission-closed until release, and any emulator command fails the cell.
function fakeCtx(name, { admittedWhilePending = false, commandDuringRecovery = false, fencedAfterRelease = false } = {}) {
  const dir = scratch(name);
  const log = { total: 2, commands: 0, entries: [] };
  const ctx = {
    cellSpec: faultCells.find((entry) => entry.id === 'fault-emulated-printer-reconciliation'),
    journalPath: path.join(dir, 'journal.jsonl'),
    dockerLogPath: path.join(dir, 'docker.jsonl'),
    admissionClosedPath: path.join(dir, 'admission.closed'),
    toolGates: { pgRestore: { calls: path.join(dir, 'pg_restore.calls') } },
    passed: [],
    failedCheckpoints: [],
    state: 'idle',
  };
  ctx.checkpoints = { ok: (checkpoint) => ctx.passed.push(checkpoint), failed: (checkpoint) => ctx.failedCheckpoints.push(checkpoint) };
  ctx.markInjected = () => {};
  ctx.enableComposeFault = () => {};
  ctx.assertRolledBack = () => {};
  ctx.seedPrinter = () => {};
  ctx.restartHost = () => {};
  ctx.emulatedPrinter = {
    start: () => {},
    attach: () => '00000000-0000-0000-0000-000000000001',
    requests: () => {
      log.total += 1;
      if (commandDuringRecovery && ctx.state === 'pending' && log.commands === 0) {
        log.commands += 1;
        log.entries.push({ sequence: log.total, transport: 'jsonrpc', method: 'printer.gcode.script', target: 'G28', isCommand: true });
      }
      return { ...log, entries: [...log.entries] };
    },
    dispatchProbe: () => {
      const fenced = existsSync(ctx.admissionClosedPath) && !admittedWhilePending;
      if (fenced || (fencedAfterRelease && ctx.state === 'released')) {
        return { status: 409, classification: 'admission-closed' };
      }
      return { status: 428, classification: 'admitted' };
    },
  };
  ctx.op = (operationId, { extraArgs = [] } = {}) => {
    if (operationId === 'offline-activate') {
      writeFileSync(ctx.admissionClosedPath, '');
      return { exitCode: 6, stdout: 'state: RecoveryRequired\nreason: compose_up_failed\n', stderr: '' };
    }
    if (operationId === 'host-update-status') return { exitCode: 0, stdout: '', stderr: '' };
    if (operationId === 'offline-recover-preview') {
      return { exitCode: 0, stdout: `plan.kind: RestoreBackup\ntoken: physical-${'a'.repeat(32)}\n`, stderr: '' };
    }
    if (ctx.state === 'idle') {
      ctx.state = 'pending';
      writeFileSync(ctx.toolGates.pgRestore.calls, 'restore\n');
      return { exitCode: 13, stdout: 'outcome: FenceReleasePending\nreason: physical_reconciliation_pending\n', stderr: '' };
    }
    if (ctx.state === 'pending') {
      if (!extraArgs.includes('--printers-reconciled')) {
        return { exitCode: 13, stdout: 'outcome: FenceReleasePending\nreason: physical_reconciliation_pending\n', stderr: '' };
      }
      ctx.state = 'released';
      rmSync(ctx.admissionClosedPath, { force: true });
    }
    return { exitCode: 0, stdout: 'outcome: RolledBack\n', stderr: '' };
  };
  return ctx;
}

test('emulated-printer scenario passes when dispatch is fenced until release and no command reaches the printer', () => {
  const ctx = fakeCtx('emulated-pass');
  const result = runFaultScenario(ctx);
  assert.equal(result.actual, 'RolledBack');
  assert.deepEqual(ctx.failedCheckpoints, []);
  for (const checkpoint of [
    'dispatch-fenced:pending',
    'dispatch-fenced:pending-after-restart',
    'dispatch-reopened-after-reconciliation',
    'fence-released:durable',
  ]) {
    assert.ok(ctx.passed.includes(checkpoint), checkpoint);
  }
  assert.ok(ctx.passed.some((checkpoint) => checkpoint.startsWith('emulator-commands-during-recovery:0:')));
  assert.ok(ctx.passed.some((checkpoint) => checkpoint.startsWith('emulator-commands-total:0:')));
});

test('emulated-printer scenario fails when dispatch is admitted while reconciliation is pending', () => {
  const ctx = fakeCtx('emulated-admitted', { admittedWhilePending: true });
  assert.throws(() => runFaultScenario(ctx), (error) => error.reason.startsWith('dispatch-fenced:pending:admitted'));
});

test('emulated-printer scenario fails when recovery commands the printer', () => {
  const ctx = fakeCtx('emulated-command', { commandDuringRecovery: true });
  assert.throws(() => runFaultScenario(ctx), (error) =>
    error.reason.includes('emulator-commands-during-recovery') && error.reason.includes('printer.gcode.script'));
});

test('emulated-printer scenario fails when dispatch stays fenced after reconciliation', () => {
  const ctx = fakeCtx('emulated-still-fenced', { fencedAfterRelease: true });
  assert.throws(() => runFaultScenario(ctx), (error) => error.reason.startsWith('dispatch-reopened-after-reconciliation'));
});

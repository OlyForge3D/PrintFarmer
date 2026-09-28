// Live fault-injection cells for issue #3101. Every cell runs on the supported c2 shape
// (monolith / postgres / host-owned database and storage / managed workers) so the only
// variable is the injected fault. `expected` is the final, durable outcome an operator sees;
// `redrive` describes what the first operator redrive after the fault must report.

const c2Shape = Object.freeze({
  topology: 'monolith',
  provider: 'postgres',
  databaseLayout: 'shared',
  databaseOwner: 'host',
  storageOwner: 'host',
  workers: 'managed',
});

// The executor probes then applies each context in turn; AppDbContext apply is the first unsafe
// database side effect of an activation.
export const migrationApplyTokens = Object.freeze(['--host-update-migration', 'AppDbContext', 'apply']);
export const composeUpTokens = Object.freeze(['compose', 'up']);

export const faultKinds = Object.freeze([
  'power-loss',
  'partial-migration',
  'partial-apply',
  'api-down',
  'missing-backup',
  'corrupt-journal',
  'corrupt-replay',
  'fence-release',
]);

const defineFaultCell = ({ id, fault, expected }) => Object.freeze({
  id,
  scenario: 'fault',
  cell: c2Shape,
  fault: Object.freeze(fault),
  expected: Object.freeze({ failClosed: false, reason: null, ...expected }),
});

export const faultCells = Object.freeze([
  defineFaultCell({
    id: 'fault-power-loss-backup',
    fault: {
      kind: 'power-loss',
      point: 'backup:before',
      trigger: { tool: 'pg_dump' },
      redrive: { outcome: 'Activated' },
    },
    expected: { outcome: 'Activated' },
  }),
  defineFaultCell({
    id: 'fault-power-loss-migration-before',
    fault: {
      kind: 'power-loss',
      point: 'migration:before',
      trigger: { docker: { tokens: migrationApplyTokens, mode: 'pause-before' } },
      redrive: { outcome: 'Activated' },
    },
    expected: { outcome: 'Activated' },
  }),
  defineFaultCell({
    id: 'fault-power-loss-migration-after',
    fault: {
      kind: 'power-loss',
      point: 'migration:after-side-effect',
      trigger: { docker: { tokens: migrationApplyTokens, mode: 'pause-after' } },
      redrive: { outcome: 'Activated' },
    },
    expected: { outcome: 'Activated' },
  }),
  defineFaultCell({
    id: 'fault-power-loss-apply-before',
    fault: {
      kind: 'power-loss',
      point: 'apply:before',
      trigger: { docker: { tokens: composeUpTokens, mode: 'pause-before' } },
      redrive: { outcome: 'RecoveryRequired', reason: 'uncertain_side_effect:apply:running_digests_unverified' },
    },
    expected: { outcome: 'RolledBack' },
  }),
  defineFaultCell({
    id: 'fault-power-loss-apply-after',
    fault: {
      kind: 'power-loss',
      point: 'apply:after-side-effect',
      trigger: { docker: { tokens: composeUpTokens, mode: 'pause-after' } },
      redrive: { outcome: 'Activated' },
    },
    expected: { outcome: 'Activated' },
  }),
  defineFaultCell({
    id: 'fault-partial-migration',
    fault: { kind: 'partial-migration', point: 'migration:after-side-effect' },
    expected: { outcome: 'RolledBack' },
  }),
  defineFaultCell({
    id: 'fault-partial-apply',
    fault: { kind: 'partial-apply', point: 'apply:after-side-effect' },
    expected: { outcome: 'RolledBack' },
  }),
  defineFaultCell({
    id: 'fault-api-down',
    fault: { kind: 'api-down', point: 'recover:before' },
    expected: { outcome: 'RolledBack' },
  }),
  defineFaultCell({
    id: 'fault-missing-backup',
    fault: { kind: 'missing-backup', point: 'recover:before' },
    expected: { outcome: 'NeedsOperator', reason: 'no_backup_available' },
  }),
  defineFaultCell({
    id: 'fault-corrupt-journal',
    fault: { kind: 'corrupt-journal', point: 'recover:before' },
    expected: { outcome: 'RecoveryRequired', reason: 'journal_integrity_failure' },
  }),
  defineFaultCell({
    id: 'fault-corrupt-replay',
    fault: { kind: 'corrupt-replay', point: 'activate:before' },
    expected: { outcome: 'RecoveryRequired', reason: 'host_update_replay_state_invalid' },
  }),
  defineFaultCell({
    id: 'fault-fence-release',
    fault: { kind: 'fence-release', point: 'fence-release:pending' },
    expected: { outcome: 'RolledBack' },
  }),
]);

export const faultCellIds = Object.freeze(faultCells.map((entry) => entry.id));

export function faultCheckpointName(fault) {
  return `fault:${fault.kind}:${fault.point}`;
}

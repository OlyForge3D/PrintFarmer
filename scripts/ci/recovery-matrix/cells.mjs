import { expectedCellOutcome } from './evidence.mjs';
import { faultCellIds, faultCells } from './fault-cells.mjs';
import { importCellIds, importCells } from './import-cells.mjs';

const defineCell = ({ id, cell, scenario, expected }) => Object.freeze({
  id,
  cell: Object.freeze(cell),
  scenario,
  expected: Object.freeze(expected ?? expectedFor(cell, scenario)),
});

function expectedFor(cell, scenario) {
  const expectation = expectedCellOutcome(cell);
  if (expectation.failClosed) {
    return expectation;
  }
  return {
    failClosed: false,
    outcome: scenario === 'refuse-activation' ? 'Refused' : 'RolledBack',
    reason: undefined,
  };
}

export const cells = Object.freeze([
  defineCell({
    id: 'c2',
    scenario: 'recover',
    cell: {
      topology: 'monolith',
      provider: 'postgres',
      databaseLayout: 'shared',
      databaseOwner: 'host',
      storageOwner: 'host',
      workers: 'managed',
    },
  }),
  defineCell({
    id: 'monolith-sqlserver',
    scenario: 'recover',
    cell: {
      topology: 'monolith',
      provider: 'sqlserver',
      databaseLayout: 'shared',
      databaseOwner: 'host',
      storageOwner: 'host',
      workers: 'managed',
    },
  }),
  defineCell({
    id: 'split-postgres',
    scenario: 'recover',
    cell: {
      topology: 'split',
      provider: 'postgres',
      databaseLayout: 'shared',
      databaseOwner: 'host',
      storageOwner: 'host',
      workers: 'managed',
    },
  }),
  defineCell({
    id: 'split-postgres-no-worker',
    scenario: 'recover',
    cell: {
      topology: 'split',
      provider: 'postgres',
      databaseLayout: 'shared',
      databaseOwner: 'host',
      storageOwner: 'host',
      workers: 'none',
    },
  }),
  defineCell({
    id: 'split-sqlserver',
    scenario: 'recover',
    cell: {
      topology: 'split',
      provider: 'sqlserver',
      databaseLayout: 'shared',
      databaseOwner: 'host',
      storageOwner: 'host',
      workers: 'managed',
    },
  }),
  defineCell({
    id: 'external-database',
    scenario: 'needs-operator-recover',
    cell: {
      topology: 'monolith',
      provider: 'postgres',
      databaseLayout: 'shared',
      databaseOwner: 'external',
      storageOwner: 'host',
      workers: 'managed',
    },
  }),
  defineCell({
    id: 'external-storage',
    scenario: 'needs-operator-recover',
    cell: {
      topology: 'monolith',
      provider: 'postgres',
      databaseLayout: 'shared',
      databaseOwner: 'host',
      storageOwner: 'external',
      workers: 'managed',
    },
  }),
  defineCell({
    id: 'remote-worker',
    scenario: 'refuse-activation',
    cell: {
      topology: 'monolith',
      provider: 'postgres',
      databaseLayout: 'shared',
      databaseOwner: 'host',
      storageOwner: 'host',
      workers: 'remote',
    },
  }),
  defineCell({
    id: 'split-database',
    scenario: 'refuse-activation',
    cell: {
      topology: 'split',
      provider: 'postgres',
      databaseLayout: 'split',
      databaseOwner: 'host',
      storageOwner: 'host',
      workers: 'managed',
    },
  }),
]);

export const cellIds = Object.freeze(cells.map((entry) => entry.id));
// Fault cells (#3101) run separately from the topology matrix: `all` keeps its meaning and
// `faults` selects every fault cell. Import cells (#3102) are selected together by `imports`.
export const faultCellList = faultCells;
export const importCellList = importCells;
export const runnableCellIds = Object.freeze([...cellIds, ...faultCellIds, ...importCellIds]);
export const cellsById = Object.freeze(Object.fromEntries([...cells, ...faultCellList, ...importCellList].map((entry) => [entry.id, entry])));

export function resolveCell(id) {
  const cell = cellsById[id];
  if (!cell) {
    throw new Error(`unknown recovery matrix cell '${id}'. Expected one of: ${runnableCellIds.join(', ')}`);
  }
  return cell;
}

export function resolveCellList(value) {
  if (value === 'all') {
    return cells;
  }
  if (value === 'faults') {
    return faultCellList;
  }
  if (value === 'imports') {
    return importCellList;
  }
  return [resolveCell(value)];
}

#!/usr/bin/env node
import { readdirSync, readFileSync } from 'node:fs';
import path from 'node:path';
import { isDeepStrictEqual } from 'node:util';

import { validateEvidenceParity } from './evidence.mjs';
import { cellIds, cellsById, runnableCellIds } from './cells.mjs';
import { faultCellIds } from './fault-cells.mjs';
import { importCellIds } from './import-cells.mjs';

const [group, evidenceDirectory, ...extra] = process.argv.slice(2);
const groupCells = {
  all: cellIds,
  faults: faultCellIds,
  imports: importCellIds,
}[group];
const expectedCells = groupCells ?? (runnableCellIds.includes(group) ? [group] : undefined);

if (!expectedCells || !evidenceDirectory || extra.length > 0) {
  console.error('Usage: compare-evidence.mjs <cell|all|faults|imports> <evidence-directory>');
  process.exit(2);
}

const errors = [];
const records = new Map();
for (const entryPoint of ['bash', 'powershell']) {
  const prefix = `evidence-parity-${group}-${entryPoint}-`;
  const cellIdsFound = new Map();
  let entries;
  try {
    entries = readdirSync(evidenceDirectory, { withFileTypes: true });
  } catch (error) {
    errors.push(`${entryPoint}: failed to read evidence directory: ${error.message}`);
    continue;
  }
  for (const entry of entries) {
    if (!entry.isFile() || !entry.name.startsWith(prefix) || !entry.name.endsWith('.json')) {
      continue;
    }

    const cellId = entry.name.slice(prefix.length, -'.json'.length);
    if (!expectedCells.includes(cellId)) {
      errors.push(`${entryPoint}: unexpected evidence record for cell '${cellId}'`);
      continue;
    }
    cellIdsFound.set(cellId, (cellIdsFound.get(cellId) ?? 0) + 1);
  }

  for (const cellId of expectedCells) {
    const count = cellIdsFound.get(cellId) ?? 0;
    if (count !== 1) {
      errors.push(`${entryPoint}: expected exactly one '${cellId}' evidence record, found ${count}`);
    }
  }
}

if (errors.length === 0) {
  for (const cellId of expectedCells) {
    const bashPath = path.join(
      evidenceDirectory,
      `evidence-parity-${group}-bash-${cellId}.json`,
    );
    const powershellPath = path.join(
      evidenceDirectory,
      `evidence-parity-${group}-powershell-${cellId}.json`,
    );
    let bashEvidence;
    let powershellEvidence;
    try {
      bashEvidence = JSON.parse(readFileSync(bashPath, 'utf8'));
      powershellEvidence = JSON.parse(readFileSync(powershellPath, 'utf8'));
    } catch (error) {
      errors.push(`${cellId}: failed to read evidence pair: ${error.message}`);
      continue;
    }

    if (bashEvidence.verdict !== 'pass' || powershellEvidence.verdict !== 'pass') {
      errors.push(`${cellId}: both entry points must have a passing cell verdict`);
      continue;
    }

    const spec = cellsById[cellId];
    const expectedCell = Object.fromEntries(
      ['topology', 'provider', 'databaseLayout', 'databaseOwner', 'storageOwner', 'workers']
        .map((field) => [field, spec.cell[field]]),
    );
    if (!isDeepStrictEqual(bashEvidence.cell, expectedCell)) {
      errors.push(`${cellId}: Bash evidence does not match the expected cell definition`);
    }
    if (!isDeepStrictEqual(powershellEvidence.cell, expectedCell)) {
      errors.push(`${cellId}: PowerShell evidence does not match the expected cell definition`);
    }

    for (const error of validateEvidenceParity(bashEvidence, powershellEvidence)) {
      errors.push(`${cellId}: ${error}`);
    }
    records.set(cellId, { bashPath, powershellPath });
  }
}

if (errors.length > 0) {
  console.error(`Recovery-matrix evidence parity failed:\n${errors.join('\n')}`);
  process.exit(1);
}

console.log(`Evidence parity passed for all ${records.size} ${group} cells`);

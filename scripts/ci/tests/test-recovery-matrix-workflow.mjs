// Contract test: .github/workflows/recovery-matrix.yml must expose every runnable recovery
// matrix cell on dispatch and run every topology and import cell (#3102) nightly. The workflow
// lists cells as literals, so this guards it against drift from the cell catalog.

import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import yaml from 'js-yaml';

import { cellIds, runnableCellIds } from '../recovery-matrix/cells.mjs';
import { faultCellIds } from '../recovery-matrix/fault-cells.mjs';
import { importCellIds } from '../recovery-matrix/import-cells.mjs';

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..', '..');
const workflowPath = path.join(repositoryRoot, '.github', 'workflows', 'recovery-matrix.yml');
const workflow = yaml.load(readFileSync(workflowPath, 'utf8'));
// YAML 1.1 parses the bare `on` key as boolean true.
const triggers = workflow.on ?? workflow.true;
const cellGroups = ['all', 'faults', 'imports'];
const publishedBundleOption = 'published-bundle';

function scheduledCells() {
  const expression = workflow.jobs.cell.strategy.matrix.cell;
  const match = /github\.event_name == 'schedule' && fromJSON\('(\[[^']*\])'\)/.exec(expression);
  assert.ok(match, 'the cell matrix must select a literal cell list on schedule');
  return JSON.parse(match[1]);
}

test('recovery matrix runs only nightly and on manual dispatch, never per PR', () => {
  assert.deepEqual(Object.keys(triggers).sort(), ['schedule', 'workflow_dispatch']);
  assert.equal(triggers.schedule.length, 1);
});

test('dispatch offers exactly every runnable cell, every cell group and the published bundle', () => {
  const options = triggers.workflow_dispatch.inputs.cell.options;
  const expected = [...runnableCellIds, ...cellGroups, publishedBundleOption];
  assert.equal(new Set(options).size, options.length, 'dispatch options must not repeat');
  assert.deepEqual([...options].sort(), [...expected].sort());
});

test('nightly runs every topology cell and every import cell once, and no fault cell', () => {
  const scheduled = scheduledCells();
  assert.equal(new Set(scheduled).size, scheduled.length, 'scheduled cells must not repeat');
  assert.deepEqual([...scheduled].sort(), [...cellIds, ...importCellIds].sort());
  for (const id of faultCellIds) {
    assert.ok(!scheduled.includes(id), `fault cell ${id} runs on dispatch only`);
  }
});

test('the published-bundle job runs nightly and only on its own dispatch option', () => {
  assert.equal(workflow.jobs['published-bundle'].if,
    "github.event_name == 'schedule' || inputs.cell == 'published-bundle'");
  assert.equal(workflow.jobs.cell.if,
    "github.event_name == 'schedule' || inputs.cell != 'published-bundle'");
});

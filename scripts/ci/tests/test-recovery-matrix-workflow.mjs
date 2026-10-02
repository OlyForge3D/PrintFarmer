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
const triggers = workflow.on;
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
  // Nightly: a fixed minute and hour, every day of every month and weekday.
  assert.match(triggers.schedule[0].cron, /^([0-5]?\d) ([01]?\d|2[0-3]) \* \* \*$/);
});

test('dispatch runs exactly the selected cell, defaulting to c2', () => {
  const expression = workflow.jobs.cell.strategy.matrix.cell;
  assert.match(expression, /\|\| fromJSON\(format\('\["\{0\}"\]', inputs\.cell \|\| 'c2'\)\) \}\}$/);
  assert.equal(triggers.workflow_dispatch.inputs.cell.default, 'c2');
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

test('group dispatches run Bash and PowerShell once in the parity step and upload evidence', () => {
  const steps = workflow.jobs.cell.steps;
  const bashStep = steps.find(step => step.name === 'Run recovery cell');
  const parityStep = steps.find(step => step.name === 'Run and compare Bash and PowerShell recovery cells');
  const importParityStep = steps.find(step => step.name === 'Reuse #3102 Bash evidence and run PowerShell import cells');
  const uploadStep = steps.find(step => step.name === 'Upload evidence');

  assert.match(bashStep.if, /inputs\.cell != 'all'/);
  assert.match(bashStep.if, /inputs\.cell != 'faults'/);
  assert.match(bashStep.if, /inputs\.cell != 'imports'/);
  assert.match(parityStep.if, /github\.event_name == 'workflow_dispatch'/);
  assert.match(parityStep.if, /inputs\.cell == 'all'/);
  assert.match(parityStep.if, /inputs\.cell == 'faults'/);
  assert.match(parityStep.run, /run-cell\.sh/);
  assert.match(parityStep.run, /run-cell\.ps1/);
  assert.match(parityStep.run, /compare-evidence\.mjs/);
  assert.match(parityStep.run, /bash_status/);
  assert.match(parityStep.run, /powershell_status/);
  assert.match(importParityStep.if, /inputs\.cell == 'imports'/);
  assert.match(importParityStep.run, /gh run download 37028604300/);
  assert.match(importParityStep.run, /run-cell\.ps1/);
  assert.match(importParityStep.run, /compare-evidence\.mjs imports/);
  assert.match(importParityStep.run, /import-replay-supersede/);
  assert.deepEqual(workflow.jobs.cell.permissions, { actions: 'read', contents: 'read' });
  assert.equal(uploadStep.if, 'always()');
  assert.match(uploadStep.with.path, /\.recovery-matrix-work\/evidence-\*\.json/);
});

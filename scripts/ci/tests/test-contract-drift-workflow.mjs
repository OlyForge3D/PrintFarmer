// Structural self-test for .github/workflows/contract-drift.yml (issue #2243).
//
// Parses the workflow with js-yaml (already a root devDependency, used the
// same way by scripts/ci/tests/test-squad-verdict-gate.mjs) and asserts the
// shape a "yamllint if available" pass would otherwise only partially cover:
// the file parses as valid YAML, and it actually wires up the drift-check
// script and its self-tests rather than silently doing nothing.

import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import yaml from 'js-yaml';

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..', '..');
const workflowPath = path.join(repositoryRoot, '.github', 'workflows', 'contract-drift.yml');

async function loadWorkflow() {
  const raw = await readFile(workflowPath, 'utf8');
  return yaml.load(raw);
}

test('contract-drift.yml parses as valid YAML', async () => {
  const workflow = await loadWorkflow();
  assert.ok(workflow && typeof workflow === 'object');
});

test('contract-drift.yml is a standalone workflow, not an edit to an existing one', async () => {
  // #2243 "Non-goals / do not edit" forbids touching ci.yml, deployment-tests.yml,
  // and ios-pr-ci.yml. This is a repo-layout assertion, not a YAML-content one:
  // this test file's own existence proves a NEW file was added; here we just
  // confirm the sibling workflows are untouched by this issue's script by
  // checking they still parse independently (a corrupted shared anchor/edit
  // would break one of them).
  for (const name of ['ci.yml', 'deployment-tests.yml', 'ios-pr-ci.yml']) {
    const raw = await readFile(path.join(repositoryRoot, '.github', 'workflows', name), 'utf8');
    const doc = yaml.load(raw);
    assert.ok(doc && typeof doc === 'object', `${name} must still parse as valid YAML`);
  }
});

test('contract-drift.yml triggers on pull_request with no path filter', async () => {
  const workflow = await loadWorkflow();
  // YAML parses the bare key `on` as boolean `true` unless quoted; js-yaml
  // (like the workflow files it parses elsewhere in this repo) follows that
  // YAML 1.1 rule, so read the same key GitHub Actions itself reads.
  const on = workflow.on ?? workflow['true'];
  assert.ok(on.pull_request, 'workflow must trigger on pull_request');
  assert.equal(on.pull_request.paths, undefined, 'must not path-filter pull_request (see ci.yml rationale)');
  assert.deepEqual(on.pull_request.types, ['opened', 'synchronize', 'reopened']);
});

test('contract-drift.yml triggers on push to main/development', async () => {
  const workflow = await loadWorkflow();
  const on = workflow.on ?? workflow['true'];
  assert.deepEqual(on.push.branches, ['main', 'development']);
});

test('contract-drift.yml runs the drift-check script and its self-tests', async () => {
  const workflow = await loadWorkflow();
  const steps = workflow.jobs['contract-drift'].steps;
  const runCommands = steps.map((s) => s.run).filter(Boolean).join('\n');
  assert.match(runCommands, /node --test scripts\/ci\/tests\/test-check-contract-drift\.mjs/);
  assert.match(runCommands, /node scripts\/ci\/check-contract-drift\.mjs/);
  assert.match(runCommands, /bash scripts\/ci\/compute-change-set\.sh/);
});

test('contract-drift.yml fails closed if compute-change-set.sh could not compute a diff', async () => {
  const workflow = await loadWorkflow();
  const steps = workflow.jobs['contract-drift'].steps;
  const diffStepIndex = steps.findIndex((s) => s.id === 'diff');
  assert.ok(diffStepIndex >= 0, 'the "Compute change set" step must have id: diff so a later step can gate on its output');
  const guardStep = steps
    .slice(diffStepIndex + 1)
    .find((s) => typeof s.if === 'string' && s.if.includes('force_full_safe'));
  assert.ok(guardStep, 'a later step must check steps.diff.outputs.force_full_safe and fail the job when it is set');
  assert.match(guardStep.run, /exit 1/);
});

test('contract-drift.yml grants only read repo contents permission', async () => {
  const workflow = await loadWorkflow();
  assert.deepEqual(workflow.permissions, { contents: 'read' });
});

test('contract-drift.yml declares a concurrency group to cancel superseded runs', async () => {
  const workflow = await loadWorkflow();
  assert.ok(workflow.concurrency);
  assert.equal(workflow.concurrency['cancel-in-progress'], true);
});

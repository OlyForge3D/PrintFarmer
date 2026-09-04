import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import yaml from 'js-yaml';

const repositoryRoot = path.resolve(
  path.dirname(fileURLToPath(import.meta.url)),
  '..', '..', '..',
);
const workflowPath = path.join(
  repositoryRoot,
  '.github',
  'workflows',
  'epic-dependency-gate.yml',
);
const ciWorkflowPath = path.join(
  repositoryRoot,
  '.github',
  'workflows',
  'ci.yml',
);

async function loadWorkflow() {
  return yaml.load(await readFile(workflowPath, 'utf8'));
}

test('epic dependency workflow parses as valid YAML', async () => {
  assert.ok(await loadWorkflow());
});

test('workflow checks issue changes, closures, schedules, and manual dispatches', async () => {
  const workflow = await loadWorkflow();
  const on = workflow.on ?? workflow['true'];
  assert.deepEqual(on.issues.types, [
    'opened', 'edited', 'labeled', 'unlabeled', 'reopened', 'closed',
  ]);
  assert.equal(on.schedule[0].cron, '17 9 * * *');
  assert.ok(on.workflow_dispatch);
});

test('workflow uses least privilege and serializes each epic', async () => {
  const workflow = await loadWorkflow();
  assert.deepEqual(workflow.permissions, {
    contents: 'read',
    issues: 'write',
  });
  assert.equal(
    workflow.jobs.verify.concurrency.group,
    'epic-dependency-gate-${{ matrix.issue }}',
  );
  assert.equal(
    workflow.jobs.verify.concurrency['cancel-in-progress'],
    false,
  );
  assert.deepEqual(
    workflow.jobs.verify.strategy.matrix.issue,
    '${{ fromJSON(needs.targets.outputs.issues) }}',
  );
  const targetScript = workflow.jobs.targets.steps[0].with.script;
  assert.match(targetScript, /Number\.isSafeInteger/);
  assert.match(targetScript, /JSON\.stringify\(\[input\]\)/);
});

test('workflow loads default-branch logic and fails graph violations', async () => {
  const workflow = await loadWorkflow();
  const steps = workflow.jobs.verify.steps;
  assert.equal(
    steps[0].with.ref,
    '${{ github.event.repository.default_branch }}',
  );
  const script = steps.find((step) => step.with?.script)?.with.script;
  assert.match(script, /verify-epic-dependencies\.mjs/);
  assert.match(script, /core\.setFailed/);
  assert.match(script, /formatGateComment/);
  assert.match(script, /if \(!gate\.hasEpicLabel\(issue\.labels\)\)/);
  assert.match(script, /Matrix issue number must be a positive safe integer/);
});

test('ci-tools runs both epic dependency regression files', async () => {
  const ci = yaml.load(await readFile(ciWorkflowPath, 'utf8'));
  const commands = ci.jobs['ci-tools'].steps
    .map((step) => step.run)
    .filter(Boolean)
    .join('\n');
  assert.match(commands, /test-verify-epic-dependencies\.mjs/);
  assert.match(commands, /test-epic-dependency-workflow\.mjs/);
});

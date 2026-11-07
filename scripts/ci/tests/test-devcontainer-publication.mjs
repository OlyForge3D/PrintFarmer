import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import yaml from 'js-yaml';

const source = readFileSync(new URL('../../../.github/workflows/devcontainer-multiarch.yml', import.meta.url), 'utf8');
const workflow = yaml.load(source);

function publicationGate(raw) {
  const condition = yaml.load(raw).jobs['build-push'].if;
  assert.equal(condition, "${{ github.event_name != 'release' || !github.event.release.prerelease }}");
  // This expression uses only string equality and boolean operators shared by
  // JavaScript and Actions; non-release payloads deliberately omit release.
  return new Function('github', `return (${condition.slice(3, -2)});`);
}

const shouldPublish = publicationGate(source);
const cases = [
  ['published prerelease', 'release', { action: 'published', release: { prerelease: true } }, false],
  ['published stable release', 'release', { action: 'published', release: { prerelease: false } }, true],
  ['prerelease published from draft', 'release', { action: 'published', release: { prerelease: true, draft: false } }, false],
  ['main branch push', 'push', { ref: 'refs/heads/main' }, true],
  ['semver tag push', 'push', { ref: 'refs/tags/v1.2.3' }, true],
  ['manual dispatch without tag', 'workflow_dispatch', { inputs: {} }, true],
  ['manual dispatch with tag', 'workflow_dispatch', { inputs: { tag: 'v1.2.3' } }, true],
];

for (const [name, eventName, event, expected] of cases) {
  test(`build-push publication: ${name}`, () => {
    assert.equal(shouldPublish({ event_name: eventName, event }), expected);
  });
}

test('publication triggers remain unchanged and every publishing step is inside the guarded job', () => {
  assert.deepEqual(workflow.on, {
    push: {
      branches: ['main'],
      tags: ['v[0-9]+.[0-9]+.[0-9]+'],
      paths: ['.devcontainer/**', 'global.json', '.github/workflows/devcontainer-multiarch.yml'],
    },
    workflow_dispatch: {
      inputs: {
        tag: {
          description: 'Optional semver tag to apply to the built image (e.g. v1.2.3)',
          required: false,
        },
      },
    },
    release: { types: ['published'] },
  });
  assert.deepEqual(Object.keys(workflow.jobs), ['build-push']);
  assert.ok(workflow.jobs['build-push'].steps.some(step => step.uses === 'docker/login-action@v4'));
  assert.ok(workflow.jobs['build-push'].steps.some(step => step.run?.includes('--push .')));
});

test('removing or inverting the real workflow guard fails the contract', () => {
  for (const mutation of [
    source.replace(/^    if:.*\r?\n/m, ''),
    source.replace('!github.event.release.prerelease', 'github.event.release.prerelease'),
  ]) {
    assert.notEqual(mutation, source);
    assert.throws(() => publicationGate(mutation), assert.AssertionError);
  }
});

test('ordinary CI runs the publication regression without executing the publisher', () => {
  const ci = yaml.load(readFileSync(new URL('../../../.github/workflows/ci.yml', import.meta.url), 'utf8'));
  assert.ok(ci.jobs['ci-tools'].steps.some(step =>
    step.run === 'node --test scripts/ci/tests/test-devcontainer-publication.mjs'));
});

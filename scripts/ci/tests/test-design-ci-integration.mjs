import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import yaml from 'js-yaml';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..', '..');
const read = (file) => fs.readFileSync(path.join(root, file), 'utf8');

test('design reviewer is an executable, read-only source reviewer with exact-head evidence', () => {
  const source = read('.github/agents/design-reviewer.agent.md');
  const [, frontmatter, body] = source.match(/^---\r?\n([\s\S]*?)\r?\n---\r?\n([\s\S]*)$/);
  const metadata = yaml.load(frontmatter);
  assert.equal(metadata.name, 'Design Reviewer');
  assert.deepEqual(metadata.tools, ['*']);
  for (const requirement of [
    'charter.md', 'critique', 'audit', 'mobile/DESIGN.md', 'Copilot-Processing.md',
    'Do not run builds, installs, tests, linters, simulators',
    '<!-- squad-verdict -->', 'Squad-Reviewer: design-reviewer', 'Squad-Head-SHA:',
    'prior-SHA -> new-head delta', 'stable finding IDs', 'self-attested',
  ]) {
    assert.ok(body.includes(requirement), `Missing reviewer contract: ${requirement}`);
  }
});

test('required CI tooling job runs the detector, not only an optional workflow', () => {
  const workflow = yaml.load(read('.github/workflows/ci.yml'));
  const job = workflow.jobs['ci-tools'];
  assert.equal(job.name, 'CI tooling tests');
  assert.equal(job.if, undefined);
  const detection = job.steps.find((step) => step.run?.includes('scripts/design/check-ui-design.mjs'));
  assert.ok(detection, 'Existing required job must execute UI detection');
  assert.equal(detection['continue-on-error'], undefined);
  assert.ok(detection.env.DESIGN_BASE_SHA, 'Base SHA must be passed as data');
  assert.ok(detection.env.DESIGN_HEAD_SHA, 'Head SHA must be passed as data');
  assert.match(detection.run, /--base "\$DESIGN_BASE_SHA"/);
  assert.match(detection.run, /--head "\$DESIGN_HEAD_SHA"/);
  assert.doesNotMatch(detection.run, /\$\{\{/);
  assert.ok(job.steps.some((step) => step.run?.includes('test-design-ci-integration.mjs')));
});

test('automatic design ceremony and Ralph shaping are wired to the reviewer', () => {
  assert.match(read('.squad/team.md'), /design-reviewer\/charter\.md/);
  assert.match(read('.squad/routing.md'), /design-reviewer/i);
  const ceremony = read('.squad/ceremonies.md');
  assert.doesNotMatch(ceremony, /No automatic ceremonies/);
  for (const text of ['shape', 'critique', 'audit', 'design-reviewer']) {
    assert.ok(ceremony.includes(text), `Missing automatic ceremony requirement: ${text}`);
  }
  const ralph = read('.squad/ralph-instructions.md');
  assert.match(ralph, /shape/);
  assert.match(ralph, /design-reviewer/i);
});

test('native design authority exists and documents incumbent iOS semantics', () => {
  const design = read('mobile/DESIGN.md');
  for (const heading of ['Overview', 'Colors', 'Typography', 'Layout', 'Components']) {
    assert.ok(design.includes(`## ${heading}`), `Missing native design section: ${heading}`);
  }
  assert.match(design, /SwiftUI|iOS/);
  assert.match(design, /Dynamic Type/);
  assert.match(design, /44/);
  assert.match(design, /ThemeColors/);
});

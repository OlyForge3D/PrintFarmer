import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import yaml from 'js-yaml';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..', '..');
const read = (file) => fs.readFileSync(path.join(root, file), 'utf8');

test('CodeQL config keeps the pinned vendor folder query-eligible', () => {
  const source = read('.github/codeql/codeql-config.yml');
  const config = yaml.load(source);
  assert.deepEqual(Object.keys(config).sort(), ['name', 'paths-ignore', 'queries']);
  assert.equal(config.name, 'PrintFarmer CodeQL Config');
  assert.deepEqual(config.queries, [{ uses: 'security-and-quality' }]);
  assert.deepEqual(config['paths-ignore'], [
    '**/__tests__/**',
    '**/*.test.js', '**/*.test.jsx', '**/*.test.ts', '**/*.test.tsx',
    '**/*.spec.js', '**/*.spec.jsx', '**/*.spec.ts', '**/*.spec.tsx',
    'src/Web/ReactApp/e2e', 'src/Web/ReactApp/src/test',
  ]);
  assert.ok(!config['paths-ignore'].some((pattern) =>
    pattern.includes('scripts/design/vendor/impeccable')));
  const workflow = yaml.load(read('.github/workflows/codeql.yml'));
  const init = workflow.jobs.analyze.steps.find((step) => step.uses?.startsWith('github/codeql-action/init@'));
  assert.equal(init.with['config-file'], './.github/codeql/codeql-config.yml');
});

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
  const setupNode = job.steps.findIndex((step) => step.name === 'Setup Node');
  const installDependencies = job.steps.findIndex((step) => step.name === 'Install design detector dependencies');
  const detection = job.steps.find((step) => step.run?.includes('scripts/design/check-ui-design.mjs'));
  assert.ok(detection, 'Existing required job must execute UI detection');
  assert.ok(setupNode >= 0 && installDependencies > setupNode,
    'Node and design detector dependencies must be set up before UI detection');
  assert.equal(job.steps[installDependencies].run, 'npm ci --ignore-scripts');
  assert.ok(installDependencies < job.steps.indexOf(detection));
  assert.equal(detection['continue-on-error'], undefined);
  assert.ok(detection.env.DESIGN_BASE_SHA, 'Base SHA must be passed as data');
  assert.ok(detection.env.DESIGN_HEAD_SHA, 'Head SHA must be passed as data');
  assert.match(detection.run, /--base "\$DESIGN_BASE_SHA"/);
  assert.match(detection.run, /--head "\$DESIGN_HEAD_SHA"/);
  assert.doesNotMatch(detection.run, /\$\{\{/);
  assert.ok(job.steps.some((step) => step.run?.includes('test-design-ci-integration.mjs')));
  const checkout = job.steps[0];
  assert.equal(checkout.with.ref, '${{ github.event.pull_request.head.sha || env.CI_SOURCE_SHA }}');
  assert.equal(checkout.with['persist-credentials'], false);
  const restoreIndex = job.steps.findIndex((step) => step.name === 'Checkout CI integration source');
  assert.ok(restoreIndex > job.steps.indexOf(detection));
  const restore = job.steps[restoreIndex];
  assert.equal(restore.if, "github.event_name == 'pull_request'");
  assert.equal(restore.with.ref, '${{ env.CI_SOURCE_SHA }}');
  assert.equal(restore.with['persist-credentials'], false);
  for (const name of ['Install tooling dependencies', 'Test automatic design gate integration', 'Enforce authoritative SDK selection']) {
    assert.ok(job.steps.findIndex((step) => step.name === name) > restoreIndex,
      `${name} must validate the integration source, not the unmerged PR head`);
  }

  const detectorWorkflow = yaml.load(read('.github/workflows/design-detector.yml'));
  const detectorSteps = detectorWorkflow.jobs['design-detector'].steps;
  const detectorSetupNode = detectorSteps.findIndex((step) => step.name === 'Set up Node');
  const detectorInstallDependencies = detectorSteps.findIndex((step) => step.name === 'Install design detector dependencies');
  const detectorScan = detectorSteps.findIndex((step) => step.name === 'Run changed UI design checks');
  assert.ok(detectorSetupNode >= 0 && detectorInstallDependencies > detectorSetupNode,
    'Standalone detector must set up Node and install dependencies');
  assert.equal(detectorSteps[detectorSetupNode].uses,
    'actions/setup-node@820762786026740c76f36085b0efc47a31fe5020',
    'Standalone detector must use the verified immutable setup-node action pin');
  assert.equal(detectorSteps[detectorInstallDependencies].run, 'npm ci --ignore-scripts');
  assert.ok(detectorInstallDependencies < detectorScan,
    'Standalone detector must install parser dependencies before scanning');
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
  assert.match(design, /caps its rendered Dynamic[\s\S]*?\.xxxLarge/);
  assert.match(design, /does not certify that support/);
});

test('rendered UI evidence is required before draft creation and reviewed without running apps', () => {
  const policy = read('.github/copilot-instructions.md');
  const section = policy.split('### UI visual evidence')[1]?.split('\n### ')[0];
  assert.ok(section, 'Canonical visual evidence policy must exist');
  for (const requirement of [
    'Before opening a PR (including a draft)', 'desktop and mobile widths',
    'simulator screenshots when a simulator is available', 'full source SHA',
    'do not operate real heaters or motion', 'Refresh evidence',
    'they do not launch apps', 'not applicable',
  ]) {
    assert.ok(section.includes(requirement), `Missing evidence policy: ${requirement}`);
  }
  for (const file of [
    '.squad/ceremonies.md', '.squad/ralph-instructions.md',
    '.squad/agents/design-reviewer/charter.md', '.github/agents/design-reviewer.agent.md',
    '.github/pull_request_template.md', 'docs/CI.md',
  ]) {
    assert.ok(read(file).includes('copilot-instructions.md#ui-visual-evidence'),
      `${file} must reference the canonical evidence policy`);
  }
  const template = read('.github/pull_request_template.md');
  assert.match(template, /## UI visual evidence/);
  assert.match(template, /- \[ \] Required UI visual evidence attached and reviewed/);
  assert.match(read('.squad/ceremonies.md'), /missing required captures or visible unresolved defects block approval/);
});

test('design authorities preserve glyphs, explanation access and behavioral parity without claiming heater alignment', () => {
  for (const file of [
    'DESIGN.md', 'mobile/DESIGN.md',
    'src/Web/ReactApp/src/design-system/DESIGN-LANGUAGE.md',
  ]) {
    const design = read(file);
    assert.match(design, /#+ Cross-surface conventions/);
    assert.match(design, /incumbent.*glyphs?/i);
    assert.match(design, /[Ss]econdary explanat/);
    assert.match(design, /[Ee]ssential.*status/);
    assert.match(design, /permissions/);
    assert.match(design, /outcomes/);
  }
  const web = read('src/Web/ReactApp/src/design-system/DESIGN-LANGUAGE.md');
  assert.match(web, /isPrinterHeaterHot/);
  assert.match(web, /above 50 C.*hotend or 35 C/);
  assert.match(web, /not a requested target/);
  assert.match(web, /Neither this policy nor a design review certifies identical/);
  assert.match(web, /falsify the design/);
  assert.match(web, /physical actions may not be/);
  const mobile = read('mobile/DESIGN.md');
  assert.match(mobile, /incumbent semantic[\s\S]*difference, not proven parity/);
});

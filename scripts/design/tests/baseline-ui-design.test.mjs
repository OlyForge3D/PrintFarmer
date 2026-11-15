import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

import { classifySource, parseTrackedTree, runBaseline, serializeBaseline,
  validateDesignFrontmatter } from '../baseline-ui-design.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..', '..');

function fixture(t) {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'printfarmer-baseline-test-'));
  t.after(() => fs.rmSync(directory, { recursive: true, force: true }));
  fs.cpSync(path.join(root, 'scripts', 'design'), path.join(directory, 'scripts', 'design'), { recursive: true });
  const write = (file, content) => {
    fs.mkdirSync(path.dirname(path.join(directory, file)), { recursive: true });
    fs.writeFileSync(path.join(directory, file), content);
  };
  const git = args => {
    const result = spawnSync('git', args, { cwd: directory, encoding: 'utf8', windowsHide: true });
    assert.equal(result.status, 0, result.stderr);
    return result.stdout.trim();
  };
  git(['init', '--quiet']);
  git(['config', 'user.name', 'Baseline Fixture']);
  git(['config', 'user.email', 'baseline@example.invalid']);
  git(['config', 'core.autocrlf', 'false']);
  write('.impeccable/config.json', '{"hook":{"limits":{"maxFindings":1,"maxChars":64}}}');
  write('DESIGN.md', '---\nname: Fixture\ncolors:\n  accent: "#ffffff"\n---\n');
  write('src/Web/ReactApp/package.json', '{}');
  write('src/Web/ReactApp/src/a with space.tsx', '<div className="border-l-4 rounded-lg">A</div>');
  write('src/Web/ReactApp/src/waived.tsx',
    '// impeccable-disable side-tab -- fixture warning rail\n<div className="border-l-4 rounded-lg">B</div>');
  write('src/Web/ReactApp/src/a.test.tsx', '<div className="border-l-4 rounded-lg">Test</div>');
  write('src/Web/ReactApp/public/info.html', '<div class="border-l-4 rounded-lg">Public</div>');
  write('mobile/PrintFarmer/Views/Button.swift', 'Button("Save") {}.frame(width: 32, height: 32)');
  write('mobile/PrintFarmer/Helpers.swift', 'Button("Help") {}.frame(width: 30, height: 30)');
  write('site/index.html', '<div class="border-l-4 rounded-lg">Site</div>');
  write('site/styles.css', 'div { color: #123456; }');
  write('site/assets/logo.svg', '<svg/>');
  const commit = () => {
    git(['add', '--all']);
    git(['commit', '--quiet', '-m', 'Baseline fixture']);
    return git(['rev-parse', 'HEAD']);
  };
  commit();
  return { directory, write, git, commit };
}

test('enumeration is NUL-safe, sorted and rejects unsafe, duplicate and incomplete paths', () => {
  const oid = 'a'.repeat(40);
  const records = parseTrackedTree(`100644 blob ${oid}\tsite/z\nname.html\0` +
    `100644 blob ${oid}\tsite/a with space.css\0`);
  assert.deepEqual(records.map(record => record.file), ['site/a with space.css', 'site/z\nname.html']);
  for (const value of [`100644 blob ${oid}\t../escape\0`, `100644 blob ${oid}\tC:/outside\0`,
    `100644 blob ${oid}\tx`, 'garbage\0',
    `100644 blob ${oid}\tx\0` + `100644 blob ${oid}\tx\0`]) {
    assert.throws(() => parseTrackedTree(value), /Git tree/);
  }
});

test('enumerates production source broadly while explaining tests, assets and unsupported types', () => {
  for (const file of ['src/Web/ReactApp/src/App.tsx', 'src/Web/ReactApp/public/info.html',
    'src/Web/ReactApp/index.html', 'site/styles.css', 'site/privacy/index.html']) {
    assert.equal(classifySource(file).engine, 'impeccable-regex');
  }
  assert.equal(classifySource('mobile/PrintFarmer/Helpers.swift').engine, 'swiftui-same-line-target');
  assert.equal(classifySource('src/Web/ReactApp/src/__tests__/App.tsx').disposition, 'excluded');
  assert.equal(classifySource('src/Web/ReactApp/e2e/test.ts').disposition, 'unsupported');
  assert.equal(classifySource('site/assets/image.svg').disposition, 'unsupported');
  assert.equal(classifySource('mobile/PrintFarmer/Assets.xcassets/Contents.json').disposition, 'unsupported');
  assert.equal(classifySource('src/api/Api.cs'), undefined);
});

test('rejects malformed/unsupported design frontmatter instead of silently dropping constraints', () => {
  for (const source of ['name: Fixture', '---\nname: Fixture', '---\ncolors: false\n---',
    '---\nname: "unterminated\n---', '---\nname: A\nname: B\n---',
    '---\ncolors:\n - red\n---', '---\ncolors:\n   accent: "#ffffff"\n---',
    '---\ncolors:\n  accent:\n    nested: red\n---', '---\ncolors:\n  accent: 42\n---',
    '---\ntypography:\n  body:\n    fontFamily: 42\n---']) {
    assert.throws(() => validateDesignFrontmatter(source, 'fixture'), /design|Design/);
  }
});

test('real Git snapshots are stable, include every platform and separate rule families and debt dispositions', t => {
  const { directory, write, git } = fixture(t);
  const first = runBaseline({ root: directory });
  assert.equal(first.inspectedCommit, git(['rev-parse', 'HEAD']));
  assert.equal(first.status, 'observed');
  assert.deepEqual(first.files.map(file => file.file), [...first.files.map(file => file.file)].sort());
  assert.equal(first.summary.platforms.ios.scanned, 2);
  assert.equal(first.summary.platforms.ios.blockers, 2);
  assert.equal(first.summary.platforms.site.scanned, 2);
  assert.equal(first.summary.platforms.site.unsupported, 1);
  assert.equal(first.summary.excluded, 1);
  assert.equal(first.summary.suppressed, 1);
  assert.ok(first.findings.some(finding => finding.platform === 'site' &&
    finding.ruleFamily === 'design-system' && finding.disposition === 'advisory'));
  assert.ok(first.findings.some(finding => finding.platform === 'web' &&
    finding.ruleFamily === 'generic-static' && finding.disposition === 'blocker'));
  assert.ok(first.files.filter(file => file.platform === 'web' && file.disposition === 'scanned')
    .every(file => file.designSystemChecks.length === 0));
  assert.ok(first.provenance.tooling.files.length > 10);
  assert.ok(first.provenance.config.inputs.some(input => input.file === 'DESIGN.md'));
  assert.ok(first.summary.blockers > first.provenance.config.effective.hook.limits.maxFindings);
  write('site/index.html', 'Working-tree edit that must not affect immutable scan');
  write('site/untracked.html', '<div>Untracked</div>');
  assert.equal(serializeBaseline(runBaseline({ root: directory })), serializeBaseline(first));
});

test('shared scoped waivers are retained as concrete suppressed observations', t => {
  const { directory, write, commit } = fixture(t);
  write('.impeccable/config.json', JSON.stringify({
    detector: { ignoreValues: [{ rule: 'side-tab', value: '*',
      files: ['site/index.html'], reason: 'Fixture scoped warning rail' }] },
  }));
  commit();
  const result = runBaseline({ root: directory });
  assert.ok(result.findings.some(finding => finding.file === 'site/index.html' && finding.disposition === 'suppressed'));
  assert.equal(result.provenance.config.effective.detector.ignoreValues[0].reason, 'Fixture scoped warning rail');
});

test('invalid config, design sidecars, UTF-8, symlink inputs and incomplete platform scope fail explicitly', t => {
  const f = fixture(t);
  const original = f.git(['rev-parse', 'HEAD']);
  const cases = [
    ['.impeccable/config.json', '{', /Malformed config/],
    ['.impeccable/config.json', '{"detector":{"unknown":true}}', /Unsupported config/],
    ['.impeccable/config.json', '{"hook":{"designSystem":{"enabled":"no"}}}', /boolean/],
    ['.impeccable/config.json', '{"detector":{"ignoreFiles":["*"]}}', /Whole-file/],
    ['DESIGN.md', '---\ncolors: false\n---', /Malformed design/],
    ['.impeccable/design.json', '{"extensions":{"colorMeta":[]}}', /colorMeta/],
    ['.impeccable/design.json', '{"extensions":{"colorMeta":{"accent":{"canonical":42}}}}', /colorMeta/],
    ['site/styles.css', Buffer.from([0xff]), /Unreadable UTF-8/],
  ];
  for (const [file, source, error] of cases) {
    f.write(file, source);
    f.commit();
    assert.throws(() => runBaseline({ root: f.directory }), error);
    // Restore only our isolated temporary fixture, never the workspace.
    f.git(['read-tree', '--reset', '-u', original]);
  }
  const blob = f.git(['hash-object', '-w', '--stdin']);
  f.git(['update-index', '--cacheinfo', `120000,${blob},site/styles.css`]);
  f.git(['commit', '--quiet', '-m', 'Symlink fixture']);
  assert.throws(() => runBaseline({ root: f.directory }), /symlink/);
  f.git(['read-tree', '--reset', '-u', original]);
  f.git(['rm', '--quiet', 'mobile/PrintFarmer/Views/Button.swift', 'mobile/PrintFarmer/Helpers.swift']);
  f.git(['commit', '--quiet', '-m', 'Missing native fixture']);
  assert.throws(() => runBaseline({ root: f.directory }), /no supported tracked ios source/);
  assert.throws(() => runBaseline({ root: f.directory, commit: 'development' }), /full commit SHA/);
});

test('CLI persists deterministic reports and does not replace them on scanner, integrity or output errors', t => {
  const f = fixture(t);
  const script = path.join(f.directory, 'scripts', 'design', 'baseline-ui-design.mjs');
  const output = path.join(f.directory, 'report.json');
  const run = args => spawnSync(process.execPath, [script, ...args],
    { cwd: f.directory, encoding: 'utf8', windowsHide: true });
  const success = run(['--output', output]);
  assert.equal(success.status, 0, success.stderr);
  const bytes = fs.readFileSync(output, 'utf8');
  assert.equal(JSON.parse(bytes).status, 'observed');
  f.write('scripts/design/swiftui-targets.mjs',
    'export function detectSmallSwiftUiTargets() { throw new Error("Fixture scanner failure"); }');
  const failure = run(['--output', output]);
  assert.equal(failure.status, 1);
  assert.match(failure.stderr, /Fixture scanner failure/);
  assert.equal(fs.readFileSync(output, 'utf8'), bytes);
  f.write('scripts/design/vendor/impeccable/scripts/detector/shared/constants.mjs', 'corrupt');
  // An import-time error is also a nonzero scanner failure; no report is written.
  assert.notEqual(run(['--output', output]).status, 0);
  assert.equal(fs.readFileSync(output, 'utf8'), bytes);
});

test('CLI rejects missing options and unwritable output and vendor verification fails closed', t => {
  const f = fixture(t);
  const script = path.join(f.directory, 'scripts', 'design', 'baseline-ui-design.mjs');
  const run = args => spawnSync(process.execPath, [script, ...args],
    { cwd: f.directory, encoding: 'utf8', windowsHide: true });
  for (const args of [[], ['--unknown', 'x'], ['--commit'], ['--output', path.join(f.directory, 'missing', 'report.json')]]) {
    assert.equal(run(args).status, 1);
  }
  f.write('scripts/design/vendor/impeccable/NOTICE', 'Corrupted integrity fixture');
  assert.throws(() => runBaseline({ root: f.directory }), /integrity/);
});

test('existing changed-file CI entry points are not replaced by the baseline', () => {
  for (const file of ['.github/workflows/ci.yml', '.github/workflows/design-detector.yml']) {
    const source = fs.readFileSync(path.join(root, file), 'utf8');
    assert.ok(source.includes('scripts/design/check-ui-design.mjs'));
    assert.ok(!source.includes('scripts/design/baseline-ui-design.mjs'));
  }
});

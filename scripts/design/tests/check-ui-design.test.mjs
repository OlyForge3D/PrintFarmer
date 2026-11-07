import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';

import {
  enumerateChangedPaths,
  parseNameStatus,
  readSharedConfig,
  runDesignCheck,
  scanChangedUiFiles,
  verifyVendorIntegrity,
} from '../check-ui-design.mjs';
import { detectSmallSwiftUiTargets } from '../swiftui-targets.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
const configuredRoot = path.join(here, 'fixtures', 'configured-repo');
const malformedRoot = path.join(here, 'fixtures', 'malformed-config-repo');

test('parses changed, deleted, and renamed filenames without shell-style splitting', () => {
  const changes = parseNameStatus([
    'M', 'src/Web/ReactApp/src/with space.tsx',
    'R100', 'src/Web/ReactApp/src/old.tsx', 'src/Web/ReactApp/src/new\nname.tsx',
    'D', 'mobile/PrintFarmer/Views/RemovedView.swift',
    '',
  ].join('\0'));
  assert.deepEqual(changes, [
    {
      status: 'M',
      paths: ['src/Web/ReactApp/src/with space.tsx'],
      previousPath: undefined,
      currentPath: 'src/Web/ReactApp/src/with space.tsx',
    },
    {
      status: 'R100',
      paths: ['src/Web/ReactApp/src/old.tsx', 'src/Web/ReactApp/src/new\nname.tsx'],
      previousPath: 'src/Web/ReactApp/src/old.tsx',
      currentPath: 'src/Web/ReactApp/src/new\nname.tsx',
    },
    {
      status: 'D',
      paths: ['mobile/PrintFarmer/Views/RemovedView.swift'],
      previousPath: undefined,
      currentPath: undefined,
    },
  ]);
});

test('rejects incomplete or unsafe changed-path enumeration', () => {
  assert.throws(() => parseNameStatus('R100\0old.tsx\0'), /Incomplete/);
  assert.throws(() => parseNameStatus('M\0new.tsx'), /missing final delimiter/);
  assert.throws(() => parseNameStatus('M\0../escape.tsx\0'), /Invalid changed path/);
  assert.throws(() => parseNameStatus('M\0C:\\outside.tsx\0'), /Invalid changed path/);
});

test('strictly reads shared config and rejects malformed JSON', () => {
  const config = readSharedConfig(configuredRoot);
  assert.equal(config.detector.ignoreValues[0].reason.includes('warning state'), true);
  assert.throws(() => readSharedConfig(malformedRoot), /Cannot parse/);
});

test('rejects unreasoned, unscoped, and broad suppression config', async () => {
  const unreasoned = structuredClone(readSharedConfig(configuredRoot));
  assert.equal(unreasoned.detector.ignoreValues[0].reason.length > 0, true);

  const rules = parseNameStatus('M\0src/Web/ReactApp/src/new-ui.tsx\0');
  const configPath = path.join(configuredRoot, '.impeccable', 'config.json');
  const original = JSON.parse(fs.readFileSync(configPath, 'utf8'));
  try {
    fs.writeFileSync(configPath, JSON.stringify({
      detector: { ignoreRules: ['side-tab'] },
    }));
    await assert.rejects(() => scanChangedUiFiles(configuredRoot, rules), /Broad detector\.ignoreRules/);
    fs.writeFileSync(configPath, JSON.stringify({
      detector: { ignoreValues: [{ rule: 'side-tab', value: 'border-l-4', files: ['src/Web/ReactApp/src/ignored.tsx'] }] },
    }));
    await assert.rejects(() => scanChangedUiFiles(configuredRoot, rules), /requires a non-empty reason/);
    fs.writeFileSync(configPath, JSON.stringify({
      detector: { ignoreValues: [{ rule: 'side-tab', value: 'border-l-4', reason: 'Too broad' }] },
    }));
    await assert.rejects(() => scanChangedUiFiles(configuredRoot, rules), /requires one or more file globs/);
  } finally {
    fs.writeFileSync(configPath, JSON.stringify(original, null, 2) + '\n');
  }
});

test('scans full changed React files and applies narrow justified ignores', async () => {
  const changes = [
    {
      status: 'M',
      paths: ['src/Web/ReactApp/src/new-ui.tsx'],
      currentPath: 'src/Web/ReactApp/src/new-ui.tsx',
    },
    {
      status: 'M',
      paths: ['src/Web/ReactApp/src/ignored.tsx'],
      currentPath: 'src/Web/ReactApp/src/ignored.tsx',
    },
    {
      status: 'M',
      paths: ['src/Web/ReactApp/src/clean-ui.tsx'],
      currentPath: 'src/Web/ReactApp/src/clean-ui.tsx',
    },
  ];
  const result = await scanChangedUiFiles(configuredRoot, changes);
  assert.deepEqual(result.scannedFiles, [
    'src/Web/ReactApp/src/clean-ui.tsx',
    'src/Web/ReactApp/src/ignored.tsx',
    'src/Web/ReactApp/src/new-ui.tsx',
  ]);
  assert.equal(result.findings.some(finding => finding.file.endsWith('new-ui.tsx')), true);
  assert.equal(result.findings.some(finding => finding.file.endsWith('ignored.tsx')), false);
  assert.equal(result.ignores[0].reason.includes('warning state'), true);
});

test('preserves reason-bearing upstream inline ignores for line, next-line, and whole-file scopes', async () => {
  const paths = [
    'inline-line-ui.tsx',
    'inline-next-line-ui.tsx',
    'inline-file-ui.tsx',
  ].map(name => `src/Web/ReactApp/src/${name}`);
  const result = await scanChangedUiFiles(configuredRoot, paths.map(filePath => ({
    status: 'M',
    paths: [filePath],
    currentPath: filePath,
  })));
  assert.deepEqual(result.findings, []);
  assert.deepEqual(result.scannedFiles, paths.sort());
});

test('scans the narrow SwiftUI target rule without claiming full SwiftUI coverage', async () => {
  const changes = [{
    status: 'M',
    paths: ['mobile/PrintFarmer/Views/SmallButton.swift'],
    currentPath: 'mobile/PrintFarmer/Views/SmallButton.swift',
  }];
  const result = await scanChangedUiFiles(configuredRoot, changes);
  assert.equal(result.scannedFiles.length, 1);
  assert.equal(result.findings[0].antipattern, 'swiftui-small-interactive-target');
  assert.equal(result.findings[0].value, '32x32');

  const waived = await scanChangedUiFiles(configuredRoot, [{
    status: 'M',
    paths: ['mobile/PrintFarmer/Views/InlineIgnoredButton.swift'],
    currentPath: 'mobile/PrintFarmer/Views/InlineIgnoredButton.swift',
  }]);
  assert.deepEqual(waived.findings, []);

  const clean = detectSmallSwiftUiTargets(
    'Button("Save") {}.frame(width: 48, height: 48)',
    'mobile/PrintFarmer/Views/AccessibleButton.swift',
  );
  assert.deepEqual(clean, []);
});

test('preserves deleted UI paths in scope without attempting to read them', async () => {
  const result = await scanChangedUiFiles(configuredRoot, [{
    status: 'D',
    paths: ['mobile/PrintFarmer/Views/RemovedView.swift'],
    currentPath: undefined,
  }]);
  assert.deepEqual(result.changedScopePaths, ['mobile/PrintFarmer/Views/RemovedView.swift']);
  assert.deepEqual(result.scannedFiles, []);
});

test('fails when an enumerated changed UI file is unavailable at the requested head', async () => {
  await assert.rejects(() => scanChangedUiFiles(configuredRoot, [{
    status: 'M',
    paths: ['src/Web/ReactApp/src/missing.tsx'],
    currentPath: 'src/Web/ReactApp/src/missing.tsx',
  }]), /missing at the requested head/);
});

test('ignores comments, string examples, and padded-after-frame SwiftUI examples', () => {
  const findings = detectSmallSwiftUiTargets([
    '// Button("sample") {}.frame(width: 20, height: 20)',
    'let example = "Button(\\"sample\\") {}.frame(width: 20, height: 20)"',
    'Button("Save") {}.frame(width: 32, height: 32).padding(8)',
  ].join('\n'), 'mobile/PrintFarmer/Views/Example.swift');
  assert.deepEqual(findings, []);
});

test('reports non-UI pull requests as an explicit skip', async () => {
  const head = spawnSync('git', ['rev-parse', 'HEAD'], { encoding: 'utf8' }).stdout.trim();
  const root = path.resolve(here, '..', '..', '..');
  const result = await runDesignCheck({ root, base: head, head });
  assert.equal(result.status, 'skipped');
  assert.match(result.reason, /No UI source or design-policy paths changed/);
});

test('validates the vendored detector integrity pin and fails when the head differs', () => {
  const manifest = verifyVendorIntegrity();
  assert.equal(manifest.version, '4.0.3');
  const head = spawnSync('git', ['rev-parse', 'HEAD'], { encoding: 'utf8' }).stdout.trim();
  assert.throws(
    () => enumerateChangedPaths(path.resolve(here, '..', '..', '..'), head, '0'.repeat(40)),
    /does not match requested --head|Could not enumerate changed/,
  );
});

import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
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

function makeBoundaryFixture(t) {
  const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'printfarmer-design-boundary-'));
  t.after(() => fs.rmSync(tempRoot, { recursive: true, force: true }));
  const root = path.join(tempRoot, 'repo');
  const sourceRoot = path.join(root, 'src', 'Web', 'ReactApp', 'src');
  const outsideRoot = path.join(tempRoot, 'outside');
  fs.mkdirSync(path.join(root, '.git'), { recursive: true });
  fs.mkdirSync(sourceRoot, { recursive: true });
  fs.mkdirSync(outsideRoot, { recursive: true });
  return { tempRoot, root, sourceRoot, outsideRoot };
}

function makeDirectoryLink(target, linkPath) {
  fs.symlinkSync(target, linkPath, process.platform === 'win32' ? 'junction' : 'dir');
}

function uiChange(relativePath) {
  return [{
    status: 'M',
    paths: [relativePath],
    currentPath: relativePath,
  }];
}

async function assertStaticHtmlParserAvailable() {
  const [htmlparser2, cssSelect, cssTree, domutils] = await Promise.all([
    import('htmlparser2'),
    import('css-select'),
    import('css-tree'),
    import('domutils'),
  ]);
  assert.equal(typeof htmlparser2.parseDocument, 'function');
  assert.equal(typeof cssSelect.selectAll, 'function');
  assert.equal(typeof cssTree.parse, 'function');
  assert.equal(typeof cssTree.walk, 'function');
  assert.equal(typeof domutils.textContent, 'function');
}

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
  assert.deepEqual(config.hook.limits, { maxFindings: 5, maxChars: 8000 });
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
    fs.writeFileSync(configPath, JSON.stringify({
      hook: { limits: { maxFindings: 1, maxChars: 128 } },
    }));
    assert.deepEqual(readSharedConfig(configuredRoot).hook.limits, { maxFindings: 1, maxChars: 128 });
    fs.writeFileSync(configPath, JSON.stringify({
      hook: { limits: { maxFindings: 1, unknown: 2 } },
    }));
    assert.throws(() => readSharedConfig(configuredRoot), /not a supported limit/);
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

test('keeps public design assets in scope without claiming they were scanned', async () => {
  const assetPaths = [
    'src/Web/ReactApp/public/printfarmer-logo.svg',
    'src/Web/ReactApp/public/icons/apple-touch-icon.png',
    'src/Web/ReactApp/public/manifest.webmanifest',
    'src/Web/ReactApp/public/assets/printers/generic-printer.svg',
  ];
  const result = await scanChangedUiFiles(configuredRoot, assetPaths.map(filePath => ({
    status: 'M',
    paths: [filePath],
    currentPath: filePath,
  })));
  assert.deepEqual(result.changedScopePaths, [...assetPaths].sort());
  assert.deepEqual(result.scannedFiles, []);
  assert.deepEqual(result.nonScannedUiPaths, [...assetPaths].sort());
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

test('rejects changed source files whose symlink target is outside the checkout', async (t) => {
  const { root, sourceRoot, outsideRoot } = makeBoundaryFixture(t);
  fs.writeFileSync(path.join(outsideRoot, 'page.tsx'), 'export const page = true;');
  makeDirectoryLink(outsideRoot, path.join(sourceRoot, 'linked'));

  await assert.rejects(
    () => scanChangedUiFiles(root, uiChange('src/Web/ReactApp/src/linked/page.tsx')),
    /changed UI source outside the checkout/,
  );
});

test('accepts a checkout root passed through a directory symlink', async (t) => {
  const { tempRoot, root, sourceRoot } = makeBoundaryFixture(t);
  const rootAlias = path.join(tempRoot, 'repo-alias');
  fs.writeFileSync(path.join(sourceRoot, 'page.tsx'), 'export const page = true;');
  makeDirectoryLink(root, rootAlias);

  const result = await scanChangedUiFiles(rootAlias, uiChange('src/Web/ReactApp/src/page.tsx'));
  assert.deepEqual(result.scannedFiles, ['src/Web/ReactApp/src/page.tsx']);
});

test('rejects DESIGN.md discovered through an out-of-checkout fallback directory symlink', async (t) => {
  const { root, sourceRoot, outsideRoot } = makeBoundaryFixture(t);
  fs.writeFileSync(path.join(sourceRoot, 'page.tsx'), 'export const page = true;');
  fs.writeFileSync(path.join(outsideRoot, 'DESIGN.md'), '---\ncolors: {}\n---\n');
  makeDirectoryLink(outsideRoot, path.join(root, 'docs'));

  await assert.rejects(
    () => scanChangedUiFiles(root, uiChange('src/Web/ReactApp/src/page.tsx')),
    /design metadata outside the checkout/,
  );
});

test('rejects a sidecar resolved through an out-of-checkout directory symlink', async (t) => {
  const { root, sourceRoot, outsideRoot } = makeBoundaryFixture(t);
  fs.writeFileSync(path.join(sourceRoot, 'page.tsx'), 'export const page = true;');
  fs.writeFileSync(path.join(root, 'DESIGN.md'), '---\ncolors: {}\n---\n');
  fs.writeFileSync(path.join(outsideRoot, 'design.json'), '{"colors":{}}');
  makeDirectoryLink(outsideRoot, path.join(root, '.impeccable'));

  await assert.rejects(
    () => scanChangedUiFiles(root, uiChange('src/Web/ReactApp/src/page.tsx')),
    /design sidecar outside the checkout/,
  );
});

test('rejects an Impeccable config whose symlink target is outside the checkout', async (t) => {
  const { root, sourceRoot, outsideRoot } = makeBoundaryFixture(t);
  fs.writeFileSync(path.join(sourceRoot, 'page.tsx'), 'export const page = true;');
  fs.writeFileSync(path.join(outsideRoot, 'config.json'), '{"detector":{}}');
  makeDirectoryLink(outsideRoot, path.join(root, '.impeccable'));

  await assert.rejects(
    () => scanChangedUiFiles(root, uiChange('src/Web/ReactApp/src/page.tsx')),
    /Impeccable config outside the checkout/,
  );
});

test('rejects parser-enabled linked stylesheets whose symlink target is outside the checkout', async (t) => {
  await assertStaticHtmlParserAvailable();
  const { root, sourceRoot, outsideRoot } = makeBoundaryFixture(t);
  const htmlPath = path.join(sourceRoot, 'page.html');
  fs.writeFileSync(path.join(outsideRoot, 'theme.css'), 'body { color: red; }');
  makeDirectoryLink(outsideRoot, path.join(sourceRoot, 'assets'));
  fs.writeFileSync(htmlPath, '<html><head><link rel="stylesheet" href="assets/theme.css"></head><body></body></html>');

  await assert.rejects(
    () => scanChangedUiFiles(root, uiChange('src/Web/ReactApp/src/page.html')),
    /linked stylesheet outside the checkout/,
  );
});

test('accepts parser-enabled linked stylesheets with local imports and remote references', async (t) => {
  await assertStaticHtmlParserAvailable();
  const { root, sourceRoot } = makeBoundaryFixture(t);
  const htmlPath = path.join(sourceRoot, 'page.html');
  const stylesDirectory = path.join(sourceRoot, 'styles');
  const partsDirectory = path.join(stylesDirectory, 'parts');
  fs.mkdirSync(partsDirectory, { recursive: true });
  fs.writeFileSync(path.join(partsDirectory, 'tokens.css'), ':root { --color: red; }');
  fs.writeFileSync(
    path.join(stylesDirectory, 'theme.css'),
    '@import "parts/tokens.css"; @import "tailwindcss"; @import url("https://example.test/theme.css");',
  );
  fs.writeFileSync(
    htmlPath,
    '<html><head><link rel="stylesheet" href="styles/theme.css"><link rel="stylesheet" href="https://example.test/remote.css"></head><body></body></html>',
  );

  const result = await scanChangedUiFiles(root, uiChange('src/Web/ReactApp/src/page.html'));
  assert.deepEqual(result.scannedFiles, ['src/Web/ReactApp/src/page.html']);
});

test('rejects parser-enabled uppercase href followed by an escaping lowercase duplicate', async (t) => {
  await assertStaticHtmlParserAvailable();
  const { root, sourceRoot, outsideRoot } = makeBoundaryFixture(t);
  const htmlPath = path.join(sourceRoot, 'page.html');
  const outsideStylesheetPath = path.join(outsideRoot, 'private.css');
  const href = path.relative(sourceRoot, outsideStylesheetPath).replaceAll(path.sep, '/');
  fs.writeFileSync(outsideStylesheetPath, 'body { color: red; }');
  fs.writeFileSync(
    htmlPath,
    `<html><head><link rel="stylesheet" HREF="styles/safe.css" href="${href}"></head><body></body></html>`,
  );

  await assert.rejects(
    () => scanChangedUiFiles(root, uiChange('src/Web/ReactApp/src/page.html')),
    /linked stylesheet outside the checkout/,
  );
});

test('rejects parser-enabled lowercase stylesheet rel after a non-stylesheet uppercase duplicate', async (t) => {
  await assertStaticHtmlParserAvailable();
  const { root, sourceRoot, outsideRoot } = makeBoundaryFixture(t);
  const htmlPath = path.join(sourceRoot, 'page.html');
  const outsideStylesheetPath = path.join(outsideRoot, 'private.css');
  const href = path.relative(sourceRoot, outsideStylesheetPath).replaceAll(path.sep, '/');
  fs.writeFileSync(outsideStylesheetPath, 'body { color: red; }');
  fs.writeFileSync(
    htmlPath,
    `<html><head><link REL="alternate" rel="stylesheet" href="${href}"></head><body></body></html>`,
  );

  await assert.rejects(
    () => scanChangedUiFiles(root, uiChange('src/Web/ReactApp/src/page.html')),
    /linked stylesheet outside the checkout/,
  );
});

test('rejects parser-enabled stylesheet href traversal outside the checkout', async (t) => {
  await assertStaticHtmlParserAvailable();
  const { root, sourceRoot, outsideRoot } = makeBoundaryFixture(t);
  const htmlPath = path.join(sourceRoot, 'page.html');
  const outsideStylesheetPath = path.join(outsideRoot, 'private.css');
  const href = path.relative(sourceRoot, outsideStylesheetPath).replaceAll(path.sep, '/');
  fs.writeFileSync(outsideStylesheetPath, 'body { color: red; }');
  fs.writeFileSync(htmlPath, `<html><head><link rel="stylesheet" href="${href}"></head><body></body></html>`);

  await assert.rejects(
    () => scanChangedUiFiles(root, uiChange('src/Web/ReactApp/src/page.html')),
    /linked stylesheet outside the checkout/,
  );
});

test('rejects parser-enabled local stylesheet imports that resolve outside the checkout', async (t) => {
  await assertStaticHtmlParserAvailable();
  const { root, sourceRoot, outsideRoot } = makeBoundaryFixture(t);
  const htmlPath = path.join(sourceRoot, 'page.html');
  const stylesDirectory = path.join(sourceRoot, 'styles');
  const nestedStylesDirectory = path.join(stylesDirectory, 'parts');
  const stylesheetPath = path.join(stylesDirectory, 'theme.css');
  const nestedStylesheetPath = path.join(nestedStylesDirectory, 'tokens.css');
  const outsideStylesheetPath = path.join(outsideRoot, 'private.css');
  fs.mkdirSync(nestedStylesDirectory, { recursive: true });
  fs.writeFileSync(outsideStylesheetPath, 'body { color: red; }');
  fs.writeFileSync(stylesheetPath, '@import "parts/tokens.css";');
  fs.writeFileSync(
    nestedStylesheetPath,
    `@import url("${path.relative(nestedStylesDirectory, outsideStylesheetPath).replaceAll(path.sep, '/')}");`,
  );
  fs.writeFileSync(htmlPath, '<html><head><link rel="stylesheet" href="styles/theme.css"></head><body></body></html>');

  await assert.rejects(
    () => scanChangedUiFiles(root, uiChange('src/Web/ReactApp/src/page.html')),
    /stylesheet @import outside the checkout/,
  );
});

test('fails closed when the local stylesheet import graph exceeds its file limit', async (t) => {
  await assertStaticHtmlParserAvailable();
  const { root, sourceRoot } = makeBoundaryFixture(t);
  const htmlPath = path.join(sourceRoot, 'page.html');
  const stylesDirectory = path.join(sourceRoot, 'styles');
  const importsDirectory = path.join(stylesDirectory, 'imports');
  fs.mkdirSync(importsDirectory, { recursive: true });
  const imports = Array.from({ length: 257 }, (_, index) => {
    const name = `style-${index}.css`;
    fs.writeFileSync(path.join(importsDirectory, name), 'body { color: red; }');
    return `@import "imports/${name}";`;
  });
  fs.writeFileSync(path.join(stylesDirectory, 'theme.css'), imports.join('\n'));
  fs.writeFileSync(
    htmlPath,
    '<html><head><link rel="stylesheet" href="styles/theme.css"></head><body></body></html>',
  );

  await assert.rejects(
    () => scanChangedUiFiles(root, uiChange('src/Web/ReactApp/src/page.html')),
    /Too many local stylesheets to validate filesystem boundaries safely/,
  );
});

test('fails closed when an imported stylesheet exceeds its byte limit', async (t) => {
  await assertStaticHtmlParserAvailable();
  const { root, sourceRoot } = makeBoundaryFixture(t);
  const htmlPath = path.join(sourceRoot, 'page.html');
  const stylesDirectory = path.join(sourceRoot, 'styles');
  fs.mkdirSync(stylesDirectory, { recursive: true });
  fs.writeFileSync(path.join(stylesDirectory, 'theme.css'), '@import "oversized.css";');
  fs.writeFileSync(
    path.join(stylesDirectory, 'oversized.css'),
    `/*${'x'.repeat(1024 * 1024)}*/`,
  );
  fs.writeFileSync(
    htmlPath,
    '<html><head><link rel="stylesheet" href="styles/theme.css"></head><body></body></html>',
  );

  await assert.rejects(
    () => scanChangedUiFiles(root, uiChange('src/Web/ReactApp/src/page.html')),
    /A local stylesheet exceeds the filesystem-boundary validation size limit/,
  );
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

test('uses the real merge-base and excludes UI edits made only on the advanced base', () => {
  const root = path.resolve(here, '..', '..', '..');
  const head = spawnSync('git', ['rev-parse', 'HEAD'], { cwd: root, encoding: 'utf8' }).stdout.trim();
  const runGit = (args, input) => {
    const result = spawnSync('git', args, { cwd: root, encoding: 'utf8', input });
    assert.equal(result.status, 0, String(result.stderr));
    return result.stdout.trim();
  };
  const updateTree = (tree, segments, blob) => {
    const entries = runGit(['ls-tree', '-z', tree]).split('\0').filter(Boolean).map(entry => {
      const [metadata, name] = entry.split('\t');
      const [mode, type, object] = metadata.split(' ');
      return { mode, type, object, name };
    });
    const index = entries.findIndex(entry => entry.name === segments[0]);
    assert.notEqual(index, -1, `Missing test path segment ${segments[0]}`);
    const current = entries[index];
    if (segments.length === 1) {
      assert.equal(current.type, 'blob');
      entries[index] = { ...current, object: blob };
    } else {
      assert.equal(current.type, 'tree');
      entries[index] = {
        ...current,
        object: updateTree(current.object, segments.slice(1), blob),
      };
    }
    const treeInput = entries
      .map(entry => `${entry.mode} ${entry.type} ${entry.object}\t${entry.name}\0`)
      .join('');
    return runGit(['mktree', '-z'], treeInput);
  };

  const sourceFiles = runGit(['ls-tree', '-r', '--name-only', head, '--', 'src/Web/ReactApp/src'])
    .split('\n')
    .filter(filePath => /\.(?:css|html|js|jsx|scss|ts|tsx)$/i.test(filePath));
  assert.ok(sourceFiles.length > 0, 'Expected a tracked React UI source file for the divergence fixture.');
  const baseOnlyPath = sourceFiles[0];
  const tree = runGit(['rev-parse', `${head}^{tree}`]);
  const alternateBlob = runGit(['hash-object', '-w', '--stdin'], 'base-only advanced UI change');
  const baseOnlyTree = updateTree(tree, baseOnlyPath.split('/'), alternateBlob);
  const advancedBase = runGit([
    '-c', 'user.name=Design detector test',
    '-c', 'user.email=design-detector-test@example.invalid',
    'commit-tree', baseOnlyTree, '-p', head, '-m', 'test base-only UI change',
  ]);
  const endpointDiff = runGit(['diff', '--name-status', advancedBase, head]);

  assert.match(endpointDiff, new RegExp(`^M\\t${baseOnlyPath.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}$`, 'm'));
  assert.deepEqual(enumerateChangedPaths(root, advancedBase, head), []);
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

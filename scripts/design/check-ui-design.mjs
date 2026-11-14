#!/usr/bin/env node

import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

import {
  filterDetectionFindings,
} from './vendor/impeccable/scripts/lib/impeccable-config.mjs';
import { applyInlineIgnores } from './vendor/impeccable/scripts/detector/shared/inline-ignores.mjs';
import { detectHtml } from './vendor/impeccable/scripts/detector/engines/static-html/detect-html.mjs';
import { detectUiText } from './source-adapter.mjs';
import {
  findDesignRoot,
  loadDesignSystemForTarget,
} from './vendor/impeccable/scripts/detector/design-system.mjs';
import { detectSmallSwiftUiTargets } from './swiftui-targets.mjs';
import { isUiPath } from './ui-paths.mjs';

const scriptPath = fileURLToPath(import.meta.url);
const scriptDirectory = path.dirname(scriptPath);
const repositoryRoot = path.resolve(scriptDirectory, '..', '..');
const webSourceRoot = 'src/Web/ReactApp/src/';
const webRootIndex = 'src/Web/ReactApp/index.html';
const nativeSourceRoots = [
  'mobile/PrintFarmer/Views/',
  'mobile/PrintFarmer/Theme/',
];
const detectorExtensions = new Set([
  '.astro',
  '.css',
  '.htm',
  '.html',
  '.js',
  '.jsx',
  '.less',
  '.sass',
  '.scss',
  '.svelte',
  '.ts',
  '.tsx',
  '.vue',
]);
const designFileNames = ['DESIGN.md', 'Design.md', 'design.md'];
const designFallbackDirectories = ['.agents/context', 'docs'];
const remoteStylesheetPattern = /^(https?:)?\/\//i;
const maxImportedStylesheets = 256;
const maxImportedStylesheetBytes = 1024 * 1024;

function fail(message) {
  throw new Error(message);
}

function isObject(value) {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}

function assertStringArray(value, name) {
  if (!Array.isArray(value) || value.some(item => typeof item !== 'string' || !item.trim())) {
    fail(`.impeccable/config.json: ${name} must be an array of non-empty strings.`);
  }
}

export function readSharedConfig(root) {
  const configPath = path.resolve(root, '.impeccable', 'config.json');
  if (!fs.existsSync(configPath)) {
    return {
      hook: { enabled: true, quiet: false, limits: { maxFindings: 5, maxChars: 8000 } },
      detector: { ignoreRules: [], ignoreFiles: [], ignoreValues: [], designSystem: { enabled: true } },
    };
  }

  if (!realPathWithinRoot(root, configPath, 'Impeccable config')) {
    fail('Cannot resolve Impeccable config inside the checkout.');
  }

  let raw;
  try {
    raw = JSON.parse(fs.readFileSync(configPath, 'utf8'));
  } catch (error) {
    fail(`Cannot parse .impeccable/config.json: ${error.message}`);
  }
  if (!isObject(raw)) fail('.impeccable/config.json must contain a JSON object.');
  if ('hook' in raw && !isObject(raw.hook)) fail('.impeccable/config.json: hook must be an object.');
  if ('detector' in raw && !isObject(raw.detector)) fail('.impeccable/config.json: detector must be an object.');

  const hookConfig = raw.hook || {};
  const detectorConfig = raw.detector || {};
  for (const [name, value] of [
    ['hook.enabled', hookConfig.enabled],
    ['hook.quiet', hookConfig.quiet],
    ['detector.designSystem.enabled', detectorConfig.designSystem?.enabled],
  ]) {
    if (value !== undefined && typeof value !== 'boolean') {
      fail(`.impeccable/config.json: ${name} must be a boolean.`);
    }
  }
  if ('limits' in hookConfig) {
    if (!isObject(hookConfig.limits)) fail('.impeccable/config.json: hook.limits must be an object.');
    const supportedLimitNames = new Set(['maxFindings', 'maxChars']);
    for (const [name, value] of Object.entries(hookConfig.limits)) {
      if (!supportedLimitNames.has(name)) {
        fail(`.impeccable/config.json: hook.limits.${name} is not a supported limit.`);
      }
      if (!Number.isInteger(value) || value <= 0) {
        fail(`.impeccable/config.json: hook.limits.${name} must be a positive integer.`);
      }
    }
  }

  const hookLimits = {
    maxFindings: hookConfig.limits?.maxFindings ?? 5,
    maxChars: hookConfig.limits?.maxChars ?? 8000,
  };
  if (hookLimits.maxChars < 64) {
    fail('.impeccable/config.json: hook.limits.maxChars must be at least 64 to report bounded output safely.');
  }

  const configArray = (section, key) => {
    const value = section[key];
    if (value === undefined) return [];
    if (!Array.isArray(value)) fail(`.impeccable/config.json: detector.${key} must be an array.`);
    return value;
  };
  const ignoreRules = [
    ...configArray(hookConfig, 'ignoreRules'),
    ...configArray(detectorConfig, 'ignoreRules'),
  ];
  const ignoreFiles = [
    ...configArray(hookConfig, 'ignoreFiles'),
    ...configArray(detectorConfig, 'ignoreFiles'),
  ];
  const ignoreValues = [
    ...configArray(hookConfig, 'ignoreValues'),
    ...configArray(detectorConfig, 'ignoreValues'),
  ];
  assertStringArray(ignoreRules, 'detector.ignoreRules');
  assertStringArray(ignoreFiles, 'detector.ignoreFiles');

  if (ignoreRules.length > 0) {
    fail('Broad detector.ignoreRules are not accepted in CI; use a justified, file-scoped detector.ignoreValues entry.');
  }
  if (ignoreFiles.length > 0) {
    fail('Whole-file detector.ignoreFiles are not accepted in CI; use a justified, file-scoped detector.ignoreValues entry.');
  }

  if (!Array.isArray(ignoreValues)) fail('.impeccable/config.json: detector.ignoreValues must be an array.');
  for (const [index, entry] of ignoreValues.entries()) {
    if (!isObject(entry) || typeof entry.rule !== 'string' || !entry.rule.trim() ||
      typeof entry.value !== 'string' || !entry.value.trim()) {
      fail(`.impeccable/config.json: detector.ignoreValues[${index}] requires non-empty rule and value strings.`);
    }
    if (typeof entry.reason !== 'string' || !entry.reason.trim()) {
      fail(`.impeccable/config.json: detector.ignoreValues[${index}] requires a non-empty reason.`);
    }
    if (!Array.isArray(entry.files) || entry.files.length === 0 ||
      entry.files.some(file => typeof file !== 'string' || !file.trim())) {
      fail(`.impeccable/config.json: detector.ignoreValues[${index}] requires one or more file globs.`);
    }
  }

  if ('designSystem' in detectorConfig && !isObject(detectorConfig.designSystem)) {
    fail('.impeccable/config.json: detector.designSystem must be an object.');
  }
  if ('designSystem' in hookConfig && !isObject(hookConfig.designSystem)) {
    fail('.impeccable/config.json: hook.designSystem must be an object.');
  }
  const designSystem = detectorConfig.designSystem || hookConfig.designSystem || {};
  if (!isObject(designSystem)) fail('.impeccable/config.json: detector.designSystem must be an object.');

  return {
    hook: {
      enabled: hookConfig.enabled !== false,
      quiet: hookConfig.quiet === true,
      limits: hookLimits,
    },
    detector: {
      ignoreRules,
      ignoreFiles,
      ignoreValues,
      designSystem: { enabled: designSystem.enabled !== false },
    },
  };
}

export async function scanSingleUiFile(root, rawPath) {
  const absolutePath = path.resolve(root, rawPath);
  const relativePath = path.relative(root, absolutePath).split(path.sep).join('/');
  if (!isInsideRoot(root, relativePath)) fail(`Edited file is outside the repository: ${rawPath}`);
  const normalizedPath = normalizeRepoPath(relativePath);
  if (!isUiPath(normalizedPath)) {
    return { status: 'skipped', reason: 'Edited path is outside UI/design scope.', scannedFiles: [] };
  }
  return scanChangedUiFiles(root, [{
    status: 'M',
    paths: [normalizedPath],
    currentPath: normalizedPath,
  }]);
}

export function verifyVendorIntegrity(root = repositoryRoot) {
  const vendorRoot = path.join(root, 'scripts', 'design', 'vendor', 'impeccable');
  const manifestPath = path.join(vendorRoot, 'integrity.json');
  let manifest;
  try {
    manifest = JSON.parse(fs.readFileSync(manifestPath, 'utf8'));
  } catch (error) {
    fail(`Cannot read Impeccable integrity pin: ${error.message}`);
  }
  if (!isObject(manifest) || manifest.version !== '4.0.3' ||
    manifest.license !== 'Apache-2.0' || !isObject(manifest.files)) {
    fail('Impeccable integrity pin is malformed or has an unexpected version/license.');
  }

  const actualFiles = [];
  const visit = directory => {
    for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
      const absolutePath = path.join(directory, entry.name);
      if (entry.isDirectory()) visit(absolutePath);
      else if (absolutePath !== manifestPath) {
        actualFiles.push(path.relative(vendorRoot, absolutePath).split(path.sep).join('/'));
      }
    }
  };
  visit(vendorRoot);
  actualFiles.sort();
  const pinnedFiles = Object.keys(manifest.files).sort();
  if (actualFiles.length !== pinnedFiles.length ||
    actualFiles.some((filePath, index) => filePath !== pinnedFiles[index])) {
    fail('Vendored Impeccable files do not match the integrity pin file list.');
  }

  for (const filePath of actualFiles) {
    const actualHash = crypto.createHash('sha256')
      .update(fs.readFileSync(path.join(vendorRoot, filePath)))
      .digest('hex');
    if (actualHash !== manifest.files[filePath]) {
      fail(`Vendored Impeccable integrity check failed for ${filePath}.`);
    }
  }
  return manifest;
}

function normalizeRepoPath(filePath) {
  const normalized = String(filePath ?? '').replaceAll('\\', '/');
  if (!normalized || normalized.startsWith('/') || /^[a-z]:/i.test(normalized) ||
    normalized.split('/').some(segment => segment === '..' || segment === '.')) {
    fail(`Invalid changed path from git: ${JSON.stringify(filePath)}`);
  }
  return normalized;
}

export function parseNameStatus(output) {
  const raw = String(output);
  if (raw && !raw.endsWith('\0')) fail('Incomplete git diff name-status output: missing final delimiter.');
  const tokens = raw.split('\0');
  if (tokens.at(-1) === '') tokens.pop();
  const changes = [];

  for (let index = 0; index < tokens.length;) {
    const status = tokens[index++];
    if (!/^(?:A|C\d+|D|M|R\d+|T|U|X|B)$/.test(status || '')) {
      fail(`Incomplete git diff name-status output: unexpected status ${JSON.stringify(status)}.`);
    }
    const isRenameOrCopy = /^[RC]\d+$/.test(status);
    const pathCount = isRenameOrCopy ? 2 : 1;
    if (index + pathCount > tokens.length) {
      fail(`Incomplete git diff name-status output after ${status}.`);
    }
    const paths = tokens.slice(index, index + pathCount).map(normalizeRepoPath);
    index += pathCount;
    changes.push({
      status,
      paths,
      previousPath: isRenameOrCopy ? paths[0] : undefined,
      currentPath: status === 'D' ? undefined : paths.at(-1),
    });
  }

  return changes;
}

function runGit(root, args, { maxBuffer = 32 * 1024 * 1024 } = {}) {
  const result = spawnSync('git', args, { cwd: root, encoding: 'utf8', maxBuffer, windowsHide: true });
  if (result.error) fail(`Could not enumerate changed UI paths: ${result.error.message}`);
  if (result.status !== 0) {
    fail(`Could not enumerate changed UI paths: ${String(result.stderr || '').trim() || `git exited ${result.status}`}`);
  }
  return result.stdout;
}

function resolveCommit(root, sha, name) {
  if (!/^[0-9a-f]{40,64}$/i.test(sha || '')) fail(`--${name} must be a full Git commit SHA.`);
  const resolved = runGit(root, ['rev-parse', '--verify', `${sha}^{commit}`]).trim();
  if (!/^[0-9a-f]{40,64}$/i.test(resolved)) fail(`Could not resolve --${name} as a commit SHA.`);
  return resolved;
}

export function enumerateChangedPaths(root, base, head) {
  const baseSha = resolveCommit(root, base, 'base');
  const headSha = resolveCommit(root, head, 'head');
  const currentHead = runGit(root, ['rev-parse', '--verify', 'HEAD']).trim();
  if (currentHead !== headSha) {
    fail(`Checked-out HEAD ${currentHead} does not match requested --head ${headSha}.`);
  }
  const mergeBase = runGit(root, ['merge-base', baseSha, headSha]).trim();
  if (!/^[0-9a-f]{40,64}$/i.test(mergeBase)) {
    fail(`Could not resolve a merge-base for --base ${baseSha} and --head ${headSha}.`);
  }
  const raw = runGit(root, [
    'diff',
    '--no-ext-diff',
    '--no-color',
    '--name-status',
    '-z',
    '--find-renames=50%',
    mergeBase,
    headSha,
    '--',
  ]);
  return parseNameStatus(raw);
}

function sourceKind(filePath) {
  if (!isUiPath(filePath)) return undefined;
  const extension = path.posix.extname(filePath).toLowerCase();
  if ((filePath.startsWith(webSourceRoot) || filePath === webRootIndex) &&
    detectorExtensions.has(extension)) return 'web';
  if ((nativeSourceRoots.some(prefix => filePath.startsWith(prefix)) ||
    /(?:^|\/)[^/]*View\.swift$/i.test(filePath)) && extension === '.swift') {
    return 'swiftui';
  }
  return undefined;
}

function isInsideRoot(root, filePath) {
  const absolutePath = path.resolve(root, filePath);
  const relativePath = path.relative(root, absolutePath);
  return relativePath !== '' && !relativePath.startsWith(`..${path.sep}`) &&
    relativePath !== '..' && !path.isAbsolute(relativePath);
}

function isWithinRoot(root, absolutePath) {
  const relativePath = path.relative(root, absolutePath);
  return relativePath === '' || (!relativePath.startsWith(`..${path.sep}`) &&
    relativePath !== '..' && !path.isAbsolute(relativePath));
}

function realPathWithinRoot(root, filePath, description) {
  const lexicalRoot = path.resolve(root);
  let realRoot;
  try {
    realRoot = fs.realpathSync(root);
  } catch {
    fail(`Cannot resolve the checkout root while validating ${description}.`);
  }

  const absolutePath = path.resolve(lexicalRoot, filePath);
  if (!isWithinRoot(lexicalRoot, absolutePath)) {
    fail(`Refusing ${description} outside the checkout.`);
  }

  let resolvedPath;
  try {
    resolvedPath = fs.realpathSync(absolutePath);
  } catch (error) {
    if (error.code === 'ENOENT') return undefined;
    fail(`Cannot resolve ${description} for containment validation.`);
  }

  if (!isWithinRoot(realRoot, resolvedPath)) {
    fail(`Refusing ${description} outside the checkout.`);
  }
  return resolvedPath;
}

function assertDesignSystemPathsContained(root, targetPath) {
  const found = findDesignRoot(path.dirname(targetPath));
  if (!found?.hasDesign) return;

  if (!realPathWithinRoot(root, found.dir, 'design system root')) {
    fail('Cannot resolve the design system root inside the checkout.');
  }

  const metadataCandidates = [
    ...designFileNames.map(name => ({ path: path.join(found.dir, name), contextDir: found.dir })),
    ...designFallbackDirectories.flatMap(relativeDirectory => {
      const contextDir = path.join(found.dir, relativeDirectory);
      return designFileNames.map(name => ({ path: path.join(contextDir, name), contextDir }));
    }),
  ];
  const metadata = metadataCandidates.find(candidate => fs.existsSync(candidate.path));
  if (!metadata || !realPathWithinRoot(root, metadata.path, 'design metadata')) {
    fail('Cannot resolve design metadata inside the checkout.');
  }

  const sidecarCandidates = [
    path.join(found.dir, '.impeccable', 'design.json'),
    path.join(found.dir, 'DESIGN.json'),
    path.join(metadata.contextDir, 'DESIGN.json'),
  ];
  const sidecar = sidecarCandidates.find(candidate => fs.existsSync(candidate));
  if (sidecar && !realPathWithinRoot(root, sidecar, 'design sidecar')) {
    fail('Cannot resolve design sidecar inside the checkout.');
  }
}

async function loadStaticHtmlPathModules() {
  try {
    const [htmlparser2, cssSelect, cssTree, domutils] = await Promise.all([
      import('htmlparser2'),
      import('css-select'),
      import('css-tree'),
      import('domutils'),
    ]);
    if (typeof htmlparser2.parseDocument !== 'function' ||
      typeof cssSelect.selectAll !== 'function' ||
      typeof cssTree.parse !== 'function' ||
      typeof cssTree.walk !== 'function' ||
      typeof domutils.textContent !== 'function') {
      fail('Static HTML parser modules do not expose the expected APIs.');
    }
    return {
      parseDocument: htmlparser2.parseDocument,
      selectAll: cssSelect.selectAll,
      cssTree,
    };
  } catch (error) {
    if (error?.code === 'ERR_MODULE_NOT_FOUND') return undefined;
    fail('Cannot load static HTML parser modules for filesystem-boundary validation.');
  }
}

function readStylesheetImportPaths(css, cssTree) {
  let ast;
  try {
    ast = cssTree.parse(css);
  } catch {
    fail('Cannot parse a stylesheet while validating @import boundaries.');
  }

  const imports = [];
  cssTree.walk(ast, node => {
    if (node.type !== 'Atrule' || node.name.toLowerCase() !== 'import') return;
    const source = node.prelude?.children?.first;
    if ((source?.type !== 'String' && source?.type !== 'Url') ||
      typeof source.value !== 'string') {
      fail('Cannot determine a stylesheet @import path safely.');
    }
    imports.push(source.value);
  });
  return imports;
}

async function assertLinkedStylesheetPathsContained(root, htmlPath, html) {
  const modules = await loadStaticHtmlPathModules();
  if (!modules) return;

  const document = modules.parseDocument(html, {
    lowerCaseAttributeNames: true,
    lowerCaseTags: true,
  });
  const links = modules.selectAll('link', document.children || []);
  const inspected = new Set();
  let importedStylesheets = 0;

  function inspectStylesheet(href, baseDirectory, description, isImport = false) {
    if (!href || remoteStylesheetPattern.test(href)) return;
    const stylesheetPath = path.resolve(baseDirectory, href);
    if (!isWithinRoot(path.resolve(root), stylesheetPath)) {
      fail(`Refusing ${description} outside the checkout.`);
    }
    const resolvedPath = realPathWithinRoot(root, stylesheetPath, description);
    if (!resolvedPath || inspected.has(resolvedPath)) return;
    if (isImport && importedStylesheets >= maxImportedStylesheets) {
      fail('Too many local stylesheets to validate filesystem boundaries safely.');
    }
    if (isImport) importedStylesheets++;
    inspected.add(resolvedPath);

    const stats = fs.statSync(resolvedPath);
    if (!stats.isFile()) return;
    if (isImport && stats.size > maxImportedStylesheetBytes) {
      fail('A local stylesheet exceeds the filesystem-boundary validation size limit.');
    }

    let css;
    try {
      css = fs.readFileSync(resolvedPath, 'utf8');
    } catch {
      fail('Cannot read a local stylesheet while validating filesystem boundaries.');
    }
    for (const importedPath of readStylesheetImportPaths(css, modules.cssTree)) {
      inspectStylesheet(importedPath, path.dirname(stylesheetPath), 'stylesheet @import', true);
    }
  }

  for (const link of links) {
    const rel = link.attribs?.rel || '';
    const href = link.attribs?.href || '';
    if (!/\bstylesheet\b/i.test(rel) || !href) continue;
    inspectStylesheet(href, path.dirname(path.resolve(htmlPath)), 'linked stylesheet');
  }
}

function waiverSummaries(config) {
  return config.detector.ignoreValues.map(({ rule, value, files, reason }) => ({
    rule,
    value,
    files,
    reason: reason.trim(),
  }));
}

export async function scanChangedUiFiles(root, changes) {
  const config = readSharedConfig(root);
  const changedScopePaths = [...new Set(changes.flatMap(change => change.paths))]
    .filter(isUiPath)
    .sort();
  const candidates = new Map();

  for (const change of changes) {
    const currentPath = change.currentPath;
    if (!currentPath || !isInsideRoot(root, currentPath)) continue;
    const kind = sourceKind(currentPath);
    if (kind) candidates.set(currentPath, { kind, status: change.status });
    if (change.previousPath && sourceKind(change.previousPath)) {
      const previousKind = sourceKind(change.previousPath);
      if (kind || previousKind) {
        candidates.set(currentPath, { kind: kind || previousKind, status: change.status });
      }
    }
  }

  const scannedFiles = [];
  const findings = [];
  for (const [filePath, { kind, status }] of [...candidates.entries()].sort(([a], [b]) => a.localeCompare(b))) {
    const absolutePath = path.resolve(root, filePath);
    const resolvedPath = realPathWithinRoot(root, absolutePath, 'changed UI source');
    if (!resolvedPath) {
      fail(`Changed UI source file ${filePath} (${status}) is missing at the requested head.`);
    }
    const content = fs.readFileSync(resolvedPath, 'utf8');
    let fileFindings;
    if (kind === 'swiftui') {
      fileFindings = applyInlineIgnores(detectSmallSwiftUiTargets(content, filePath), content);
    } else {
      let designSystem;
      if (config.detector.designSystem.enabled) {
        assertDesignSystemPathsContained(root, absolutePath);
        designSystem = loadDesignSystemForTarget(absolutePath);
      }
      const scanOptions = {
        inlineIgnores: true,
        ...(designSystem ? { designSystem } : {}),
      };
      if (path.posix.extname(filePath).toLowerCase() === '.html' ||
        path.posix.extname(filePath).toLowerCase() === '.htm') {
        await assertLinkedStylesheetPathsContained(root, absolutePath, content);
        fileFindings = await detectHtml(absolutePath, scanOptions);
      } else {
        fileFindings = detectUiText(content, absolutePath, scanOptions);
      }
    }
    scannedFiles.push(filePath);
    findings.push(...filterDetectionFindings(fileFindings, config.detector));
  }

  const advisories = findings.filter(finding => finding.advisory === true)
    .map(finding => ({ ...finding, file: normalizeFindingPath(root, finding.file) }));
  const failures = findings.filter(finding => finding.advisory !== true)
    .map(finding => ({ ...finding, file: normalizeFindingPath(root, finding.file) }));
  const nonScannedUiPaths = changedScopePaths.filter(filePath => !sourceKind(filePath));

  return {
    status: failures.length > 0 ? 'fail' : 'pass',
    changedScopePaths,
    scannedFiles,
    nonScannedUiPaths,
    findings: failures,
    advisories,
    ignores: waiverSummaries(config),
    detector: { name: 'Impeccable', version: '4.0.3' },
  };
}

function normalizeFindingPath(root, filePath) {
  if (!filePath) return filePath;
  const relativePath = path.relative(root, filePath);
  return (relativePath.startsWith('..') ? filePath : relativePath).split(path.sep).join('/');
}

export async function runDesignCheck({ root = repositoryRoot, base, head } = {}) {
  if (!base || !head) fail('Usage: node scripts/design/check-ui-design.mjs --base <full-sha> --head <full-sha> [--json]');
  verifyVendorIntegrity(root);
  const changes = enumerateChangedPaths(root, base, head);
  const result = await scanChangedUiFiles(root, changes);
  if (result.changedScopePaths.length === 0) {
    return { ...result, status: 'skipped', reason: 'No UI source or design-policy paths changed.' };
  }
  if (result.scannedFiles.length === 0) {
    return {
      ...result,
      status: 'scope-only',
      reason: 'UI design policy or asset paths changed, but no supported production UI source file changed.',
    };
  }
  return result;
}

function parseArgs(args) {
  const parsed = { json: false };
  for (let index = 0; index < args.length; index += 1) {
    const arg = args[index];
    if (arg === '--json') parsed.json = true;
    else if (arg === '--base' || arg === '--head') {
      const value = args[index + 1];
      if (!value || value.startsWith('--')) fail(`${arg} requires a full commit SHA.`);
      parsed[arg.slice(2)] = value;
      index += 1;
    } else {
      fail(`Unknown option: ${arg}`);
    }
  }
  return parsed;
}

async function main() {
  const args = parseArgs(process.argv.slice(2));
  const result = await runDesignCheck({ root: repositoryRoot, base: args.base, head: args.head });
  const output = args.json
    ? JSON.stringify(result, null, 2)
    : formatTextResult(result);
  process.stdout.write(`${output}\n`);
  process.exitCode = result.status === 'fail' ? 1 : 0;
}

function formatTextResult(result) {
  if (result.status === 'skipped' || result.status === 'scope-only') {
    return `${result.status.toUpperCase()}: ${result.reason}\nChanged scope paths: ${result.changedScopePaths.join(', ') || '(none)'}`;
  }
  const lines = [
    `Impeccable ${result.detector.version}: scanned ${result.scannedFiles.length} changed UI source file(s).`,
    `Findings: ${result.findings.length}; advisories: ${result.advisories.length}.`,
  ];
  for (const finding of result.findings) {
    lines.push(`${finding.file}:${finding.line || 1} [${finding.antipattern}] ${finding.description}`);
  }
  for (const finding of result.advisories) {
    lines.push(`ADVISORY ${finding.file}:${finding.line || 1} [${finding.antipattern}] ${finding.description}`);
  }
  return lines.join('\n');
}

if (process.argv[1] && path.resolve(process.argv[1]) === scriptPath) {
  main().catch(error => {
    const json = process.argv.includes('--json');
    if (json) process.stdout.write(`${JSON.stringify({ status: 'error', error: error.message }, null, 2)}\n`);
    else process.stderr.write(`Design detector error: ${error.message}\n`);
    process.exitCode = 1;
  });
}

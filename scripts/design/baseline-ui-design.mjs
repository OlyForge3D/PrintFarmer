#!/usr/bin/env node

import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import crypto from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

import { readSharedConfig, verifyVendorIntegrity } from './check-ui-design.mjs';
import { detectSmallSwiftUiTargets } from './swiftui-targets.mjs';
import { detectUiText } from './source-adapter.mjs';
import { applyInlineIgnores, parseInlineIgnores } from './vendor/impeccable/scripts/detector/shared/inline-ignores.mjs';
import { filterDetectionFindings } from './vendor/impeccable/scripts/lib/impeccable-config.mjs';
import { normalizeDesignSystem, parseFrontmatter } from './vendor/impeccable/scripts/detector/design-system.mjs';
import { ANTIPATTERNS } from './vendor/impeccable/scripts/detector/registry/antipatterns.mjs';

const scriptPath = fileURLToPath(import.meta.url);
const repositoryRoot = path.resolve(path.dirname(scriptPath), '..', '..');
const textExtensions = new Set(['.astro', '.css', '.htm', '.html', '.js', '.jsx', '.less',
  '.sass', '.scss', '.svelte', '.ts', '.tsx', '.vue']);
const excludedSegments = new Set(['__fixtures__', '__tests__', 'build', 'coverage', 'dist',
  'fixtures', 'node_modules', 'test', 'tests', 'vendor']);
const roots = {
  web: 'src/Web/ReactApp/',
  ios: 'mobile/PrintFarmer/',
  site: 'site/',
};
const designNames = ['DESIGN.md', 'Design.md', 'design.md'];
const compare = (a, b) => a < b ? -1 : a > b ? 1 : 0;
const hash = bytes => crypto.createHash('sha256').update(bytes).digest('hex');
const object = value => value !== null && typeof value === 'object' && !Array.isArray(value);

export function readGit(root, args) {
  const result = spawnSync('git', ['--no-replace-objects', ...args],
    { cwd: root, windowsHide: true, maxBuffer: 64 * 1024 * 1024 });
  if (result.error || result.status !== 0) {
    throw new Error(`Git snapshot read failed: ${result.error?.message ||
      result.stderr?.toString().trim() || `exit ${result.status}`}`);
  }
  return result.stdout;
}

function text(bytes, label) {
  try {
    return new TextDecoder('utf-8', { fatal: true }).decode(bytes);
  } catch (error) {
    throw new Error(`Unreadable UTF-8 input ${label}: ${error.message}`);
  }
}

export function parseTrackedTree(output) {
  const raw = String(output);
  if (raw && !raw.endsWith('\0')) throw new Error('Incomplete Git tree: missing final delimiter.');
  const seen = new Set();
  return raw.split('\0').filter(Boolean).map(entry => {
    const match = /^(\d{6}) (blob|commit) ([a-f0-9]{40,64})\t([\s\S]+)$/.exec(entry);
    if (!match) throw new Error(`Malformed Git tree entry: ${JSON.stringify(entry)}`);
    const [, mode, type, oid, file] = match;
    if (file.startsWith('/') || file.includes('\\') || /^[a-z]:/i.test(file) ||
      file.split('/').some(segment => !segment || segment === '.' || segment === '..') || seen.has(file)) {
      throw new Error(`Unsafe or duplicate Git tree path: ${JSON.stringify(file)}`);
    }
    seen.add(file);
    return { file, mode, type, oid };
  }).sort((a, b) => compare(a.file, b.file));
}

export function classifySource(file) {
  const platform = Object.keys(roots).find(key => file.startsWith(roots[key]));
  if (!platform) return undefined;
  const relative = file.slice(roots[platform].length);
  if (relative.split('/').some(segment => excludedSegments.has(segment)) ||
    /\.(test|spec)\.[^.]+$/i.test(relative)) {
    return { platform, disposition: 'excluded', reason: 'Test, fixture, generated/build or vendored directory.' };
  }
  const extension = path.posix.extname(file).toLowerCase();
  if (platform === 'ios' && extension === '.swift') {
    return { platform, disposition: 'scanned', engine: 'swiftui-same-line-target' };
  }
  if (platform !== 'ios' && textExtensions.has(extension) &&
    (platform === 'site' || relative.startsWith('src/') || relative.startsWith('public/') || relative === 'index.html')) {
    return { platform, disposition: 'scanned', engine: 'impeccable-regex' };
  }
  return {
    platform,
    disposition: 'unsupported',
    reason: platform === 'web' && !relative.startsWith('src/') && !relative.startsWith('public/')
      ? 'Application tooling/configuration outside production source and entry HTML.'
      : 'Non-supported source/asset type; no binary, SVG, JSON, storyboard or asset-catalog inspection.',
  };
}

function parseJson(source, label) {
  let value;
  try {
    value = JSON.parse(source);
  } catch (error) {
    throw new Error(`Malformed config ${label}: ${error.message}`);
  }
  if (!object(value)) throw new Error(`Malformed config ${label}: expected an object.`);
  return value;
}

function assertKeys(value, allowed, label) {
  if (!object(value)) throw new Error(`Malformed config ${label}: expected an object.`);
  for (const key of Object.keys(value)) {
    if (!allowed.includes(key)) throw new Error(`Unsupported config ${label}.${key}.`);
  }
}

function validateSharedConfig(raw) {
  assertKeys(raw, ['hook', 'detector'], '.impeccable/config.json');
  for (const section of ['hook', 'detector']) {
    if (!(section in raw)) continue;
    assertKeys(raw[section], section === 'hook'
      ? ['enabled', 'quiet', 'limits', 'ignoreRules', 'ignoreFiles', 'ignoreValues', 'designSystem']
      : ['ignoreRules', 'ignoreFiles', 'ignoreValues', 'designSystem'], section);
    if ('designSystem' in raw[section]) {
      assertKeys(raw[section].designSystem, ['enabled'], `${section}.designSystem`);
      const enabled = raw[section].designSystem.enabled;
      if (enabled !== undefined && typeof enabled !== 'boolean') {
        throw new Error(`Malformed config ${section}.designSystem.enabled: expected a boolean.`);
      }
    }
  }
}

export function validateDesignFrontmatter(source, label) {
  const lines = source.split(/\r?\n/);
  const end = lines.findIndex((line, index) => index > 0 && line.trim() === '---');
  if (lines[0]?.trim() !== '---' || end === -1) {
    throw new Error(`Malformed design config ${label}: requires closed mapping-only frontmatter.`);
  }
  const stack = [{ indent: -1, keys: new Set() }];
  let previousWasMap = true;
  let previousIndent = -1;
  for (const line of lines.slice(1, end)) {
    if (!line.trim() || /^\s*#/.test(line)) continue;
    const match = /^( *)([A-Za-z0-9_.-]+|"[^"]+"|'[^']+'):\s*(.*)$/.exec(line);
    if (!match) throw new Error(`Unsupported design frontmatter in ${label}: ${line}`);
    const indent = match[1].length;
    if (indent % 2 !== 0 || indent > previousIndent + 2 && previousIndent !== -1 || previousIndent === -1 && indent !== 0) {
      throw new Error(`Unsupported design indentation in ${label}: ${line}`);
    }
    if (indent > previousIndent && !previousWasMap) {
      throw new Error(`Malformed design indentation in ${label}: ${line}`);
    }
    while (stack.at(-1).indent >= indent) stack.pop();
    const key = match[2].replace(/^['"]|['"]$/g, '');
    if (stack.at(-1).keys.has(key)) throw new Error(`Duplicate design key in ${label}: ${key}`);
    stack.at(-1).keys.add(key);
    const scalar = match[3].trim();
    if (scalar.startsWith('"') || scalar.startsWith("'")) {
      const quoted = scalar[0] === '"' ? /^"(?:\\.|[^"\\])*"\s*(?:#.*)?$/ : /^'[^']*'\s*(?:#.*)?$/;
      if (!quoted.test(scalar)) throw new Error(`Malformed design scalar in ${label}: ${line}`);
    } else if (/^[\[{>|&*!]|^(?:null|~)$/i.test(scalar)) {
      throw new Error(`Unsupported design scalar in ${label}: ${line}`);
    }
    previousWasMap = scalar === '' || scalar.startsWith('#');
    previousIndent = indent;
    if (previousWasMap) stack.push({ indent, keys: new Set() });
  }
  const parsed = parseFrontmatter(source);
  if (!object(parsed)) throw new Error(`Cannot parse design frontmatter ${label}.`);
  for (const key of ['colors', 'typography', 'rounded', 'spacing']) {
    if (key in parsed && !object(parsed[key])) throw new Error(`Malformed design mapping ${label}: ${key}`);
  }
  for (const key of ['colors', 'rounded', 'spacing']) {
    if (parsed[key] && Object.values(parsed[key]).some(value => !['string', 'number'].includes(typeof value))) {
      throw new Error(`Malformed design scalar map ${label}: ${key}`);
    }
  }
  if (parsed.colors && Object.values(parsed.colors).some(value => typeof value !== 'string')) {
    throw new Error(`Malformed design colors ${label}: expected strings.`);
  }
  if (parsed.typography) {
    for (const [role, values] of Object.entries(parsed.typography)) {
      if (!object(values) || Object.values(values).some(value => !['string', 'number'].includes(typeof value))) {
        throw new Error(`Malformed design typography ${label}: ${role}`);
      }
      if ('fontFamily' in values && typeof values.fontFamily !== 'string') {
        throw new Error(`Malformed design typography ${label}: ${role}.fontFamily must be a string.`);
      }
    }
  }
  return parsed;
}

function validateSidecar(sidecar, label) {
  if (!('extensions' in sidecar)) return;
  if (!object(sidecar.extensions)) throw new Error(`Malformed design config ${label}: extensions must be an object.`);
  for (const key of ['colorMeta', 'roundedMeta']) {
    const mapping = sidecar.extensions[key];
    if (mapping === undefined) continue;
    if (!object(mapping)) throw new Error(`Malformed design config ${label}: ${key} must be an object.`);
    for (const value of Object.values(mapping)) {
      if (key === 'roundedMeta' && ['string', 'number'].includes(typeof value)) continue;
      if (!object(value)) throw new Error(`Malformed design config ${label}: ${key} entry must be an object.`);
      for (const property of ['canonical', 'value']) {
        if (property in value && !['string', 'number'].includes(typeof value[property])) {
          throw new Error(`Malformed design config ${label}: ${key}.${property} must be scalar.`);
        }
        if (key === 'colorMeta' && property in value && typeof value[property] !== 'string') {
          throw new Error(`Malformed design config ${label}: ${key}.${property} must be a string.`);
        }
      }
      for (const property of ['tonalRamp', 'values', 'aliases']) {
        if (property in value && (!Array.isArray(value[property]) ||
          value[property].some(item => !['string', 'number'].includes(typeof item)))) {
          throw new Error(`Malformed design config ${label}: ${key}.${property} must be a scalar array.`);
        }
        if (key === 'colorMeta' && property in value && value[property].some(item => typeof item !== 'string')) {
          throw new Error(`Malformed design config ${label}: ${key}.${property} must be a string array.`);
        }
      }
    }
  }
}

function designContext(file, entries) {
  let directory = path.posix.dirname(file);
  while (true) {
    for (const context of [directory, path.posix.join(directory, '.agents/context'), path.posix.join(directory, 'docs')]) {
      const md = designNames.map(name => path.posix.join(context, name)).find(candidate => entries.has(candidate));
      if (md) {
        const sidecar = [path.posix.join(directory, '.impeccable/design.json'),
          path.posix.join(directory, 'DESIGN.json'), path.posix.join(context, 'DESIGN.json')]
          .find(candidate => entries.has(candidate));
        return { root: directory === '.' ? '' : directory, markdown: md, ...(sidecar ? { sidecar } : {}) };
      }
    }
    if (directory === '.' || entries.has(`${directory}/package.json`) ||
      [...entries.keys()].some(candidate => candidate.startsWith(`${directory}/.impeccable/`))) {
      return { root: directory === '.' ? '' : directory };
    }
    directory = path.posix.dirname(directory);
  }
}

function countFindings(findings) {
  return {
    blockers: findings.filter(finding => finding.disposition === 'blocker').length,
    advisories: findings.filter(finding => finding.disposition === 'advisory').length,
    suppressed: findings.filter(finding => finding.disposition === 'suppressed').length,
    designSystem: findings.filter(finding => finding.ruleFamily === 'design-system').length,
    genericStatic: findings.filter(finding => finding.ruleFamily === 'generic-static').length,
  };
}

export function serializeBaseline(report) {
  return `${JSON.stringify(report, null, 2)}\n`;
}

export function baselineToolingFiles(root = repositoryRoot) {
  const vendor = verifyVendorIntegrity(root);
  return ['scripts/design/baseline-ui-design.mjs', 'scripts/design/check-ui-design.mjs',
    'scripts/design/source-adapter.mjs', 'scripts/design/swiftui-targets.mjs', 'scripts/design/vendor/impeccable/integrity.json',
    ...Object.keys(vendor.files).map(file => `scripts/design/vendor/impeccable/${file}`)].sort(compare);
}

export function runBaseline({ root = repositoryRoot, commit = 'HEAD', policyCommit } = {}) {
  const vendor = verifyVendorIntegrity(root);
  if (commit !== 'HEAD' && !/^[a-f0-9]{40,64}$/i.test(commit)) {
    throw new Error('--commit must be HEAD or a full commit SHA.');
  }
  const inspectedCommit = text(readGit(root, ['rev-parse', '--verify', `${commit}^{commit}`]), 'commit').trim();
  if (!/^[a-f0-9]{40,64}$/i.test(inspectedCommit)) throw new Error('Invalid resolved Git commit.');
  if (policyCommit !== undefined && !/^[a-f0-9]{40,64}$/i.test(policyCommit)) {
    throw new Error('Policy commit must be a full SHA.');
  }
  const configurationCommit = policyCommit || inspectedCommit;
  const entries = new Map(parseTrackedTree(text(readGit(root,
    ['ls-tree', '-r', '-z', '--full-tree', inspectedCommit]), 'tree')).map(entry => [entry.file, entry]));
  const policyEntries = configurationCommit === inspectedCommit ? entries
    : new Map(parseTrackedTree(text(readGit(root,
      ['ls-tree', '-r', '-z', '--full-tree', configurationCommit]), 'policy tree')).map(entry => [entry.file, entry]));
  const inputs = new Map();
  const policyInputs = new Map();
  const readFrom = (file, tree, cache) => {
    const entry = tree.get(file);
    if (!entry || entry.type !== 'blob' || !['100644', '100755'].includes(entry.mode)) {
      throw new Error(`Unreadable snapshot input ${file}: missing, symlink or non-regular blob.`);
    }
    if (!cache.has(file)) {
      const bytes = readGit(root, ['cat-file', 'blob', entry.oid]);
      cache.set(file, { source: text(bytes, file), sha256: hash(bytes), gitBlob: entry.oid });
    }
    return cache.get(file);
  };
  const read = file => readFrom(file, entries, inputs);
  const readPolicy = file => readFrom(file, policyEntries, policyInputs);
  const configFile = '.impeccable/config.json';
  const configInput = policyEntries.has(configFile) ? readPolicy(configFile) : undefined;
  if (configInput) validateSharedConfig(parseJson(configInput.source, configFile));
  const temporaryRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'printfarmer-ui-config-'));
  let config;
  try {
    if (configInput) {
      fs.mkdirSync(path.join(temporaryRoot, '.impeccable'));
      fs.writeFileSync(path.join(temporaryRoot, configFile), configInput.source);
    }
    config = readSharedConfig(temporaryRoot);
  } finally {
    fs.rmSync(temporaryRoot, { recursive: true, force: true });
  }

  const files = [];
  const findings = [];
  const contexts = new Map();
  for (const entry of entries.values()) {
    const scope = classifySource(entry.file);
    if (!scope) continue;
    const record = { file: entry.file, gitBlob: entry.oid, gitMode: entry.mode, ...scope };
    if (entry.type === 'commit') {
      record.disposition = 'unsupported';
      record.reason = 'Submodule reference; nested repository source is not enumerated.';
    }
    files.push(record);
    if (record.disposition !== 'scanned') continue;
    const input = read(entry.file);
    record.sha256 = input.sha256;
    let designSystem;
    const context = designContext(entry.file, policyEntries);
    const contextKey = context.root;
    if (!contexts.has(contextKey)) {
      const details = { ...context, availableWebTokenChecks: [] };
      if (context.markdown) {
        const markdown = readPolicy(context.markdown);
        const frontmatter = validateDesignFrontmatter(markdown.source, context.markdown);
        const sidecar = context.sidecar ? parseJson(readPolicy(context.sidecar).source, context.sidecar) : undefined;
        if (sidecar) validateSidecar(sidecar, context.sidecar);
        const normalized = normalizeDesignSystem({ frontmatter, sidecar, sourcePath: context.markdown,
          sidecarPath: context.sidecar, mdNewerThanJson: false });
        details.availableWebTokenChecks = ['Fonts', 'Colors', 'Radii', 'FontSizes']
          .filter(key => normalized[`has${key}`]);
        details.normalized = normalized;
      }
      contexts.set(contextKey, details);
    }
    const details = contexts.get(contextKey);
    if (scope.platform !== 'ios' && config.detector.designSystem.enabled) {
      designSystem = details.normalized;
    }
    record.designContext = context.root;
    record.designSystemChecks = designSystem ? details.availableWebTokenChecks : [];
    const raw = scope.platform === 'ios'
      ? detectSmallSwiftUiTargets(input.source, entry.file)
      : detectUiText(input.source, entry.file, { inlineIgnores: false, ...(designSystem ? { designSystem } : {}) });
    if (!Array.isArray(raw) || raw.some(finding => !object(finding) ||
      typeof finding.antipattern !== 'string' || typeof finding.description !== 'string' ||
      !Number.isInteger(finding.line) || finding.line < 0)) {
      throw new Error(`Scanner returned malformed findings for ${entry.file}.`);
    }
    let inlineRetained;
    if (policyCommit) {
      const approvedSource = policyEntries.has(entry.file) ? readPolicy(entry.file).source : '';
      const approvedDirectives = parseInlineIgnores(approvedSource);
      const approvedRetained = new Set(applyInlineIgnores(raw, approvedSource));
      const sourceLines = input.source.split('\n');
      const approvedLines = approvedSource.split('\n');
      inlineRetained = raw.filter(finding => {
        if (approvedRetained.has(finding)) return true;
        const fileRules = approvedDirectives.file;
        if (fileRules.has('*') || fileRules.has(finding.antipattern)) return false;
        return sourceLines[finding.line - 1] !== approvedLines[finding.line - 1];
      });
    } else {
      inlineRetained = applyInlineIgnores(raw, input.source);
    }
    const retained = new Set(filterDetectionFindings(inlineRetained, config.detector));
    for (const finding of raw) {
      findings.push({
        ...finding,
        file: entry.file,
        platform: scope.platform,
        engine: scope.engine,
        ruleFamily: finding.antipattern.startsWith('design-system-') ? 'design-system' : 'generic-static',
        disposition: !retained.has(finding) ? 'suppressed'
          : finding.advisory === true || finding.severity === 'advisory' ? 'advisory' : 'blocker',
        locationPrecision: finding.line > 0 ? 'source-line' : 'file-only',
      });
    }
  }
  for (const platform of Object.keys(roots)) {
    if (!files.some(file => file.platform === platform && file.disposition === 'scanned')) {
      throw new Error(`Incomplete baseline scope: no supported tracked ${platform} source.`);
    }
  }
  findings.sort((a, b) => compare(a.file, b.file) || a.line - b.line ||
    compare(a.antipattern, b.antipattern) || compare(a.snippet || '', b.snippet || ''));
  const toolingFiles = baselineToolingFiles(root);
  return {
    schemaVersion: 1,
    status: 'observed',
    meaning: 'Observed static debt only; not human approval, design/WCAG compliance, rendered coverage or a CI gate.',
    inspectedCommit,
    source: 'Immutable tracked Git blobs; working-tree source edits and untracked files are not inspected.',
    provenance: {
      detector: { name: 'Impeccable', version: vendor.version, license: vendor.license },
      ruleRegistry: { meaning: 'Upstream registry, not a claim every rule is supported by regex.',
        rules: ANTIPATTERNS.map(rule => ({ id: rule.id, severity: rule.severity || 'warning',
          family: rule.id.startsWith('design-system-') ? 'design-system' : 'generic-static',
          advisory: rule.advisory === true || rule.severity === 'advisory' })),
        nativeRule: { id: 'swiftui-small-interactive-target', minimumPoints: 44, family: 'generic-static' } },
      tooling: { source: 'Executing working-tree tooling (may differ from inspected commit)',
        files: toolingFiles.map(file => ({ file, sha256: hash(fs.readFileSync(path.join(root, file))) })) },
      config: { source: policyCommit ? 'Trusted policy commit; target policy and local overrides ignored'
        : 'Inspected commit; local overrides ignored', inspectedCommit: configurationCommit, effective: config,
        inputs: [...policyInputs].filter(([file]) => !files.some(record => record.file === file))
          .sort(([a], [b]) => compare(a, b)).map(([file, input]) => ({ file, sha256: input.sha256, gitBlob: input.gitBlob })) },
      designContexts: [...contexts.values()].map(({ normalized, ...details }) => details)
        .sort((a, b) => compare(a.root, b.root)),
    },
    coverage: {
      roots,
      webAndSite: 'Regex source checks, including HTML. No static cascade, linked resources, AST/type inference, browser or axe.',
      ios: 'All production app Swift sources; ONLY same-line Button/NavigationLink fixed-frame <44 pt regex. No native token checks.',
      designSystem: 'Web/site literal fonts, colors, radii and font sizes only when enabled and a nearest design context provides them. Package boundaries without DESIGN.md do not inherit parent rules.',
      unverified: ['Visual/theme/viewport/state completeness', 'Runtime interactions and overlays',
        'WCAG/VoiceOver/Dynamic Type/contrast/effective hit regions', 'Design narrative, spacing, hierarchy and human acceptance',
        'External/generated sources and files outside the enumerated roots'],
      suppressions: policyCommit
        ? 'Trusted-base shared/file waivers only; line waivers require unchanged source at the same line. Target inline waiver additions do not suppress findings.'
        : 'Inline and shared justified value waivers retained as suppressed observations; no hook output truncation.',
      disposition: 'Blocker is a detector classification, not a baseline gate. Either advisory:true or severity:advisory is advisory here; changed-file CI continues its existing advisory:true-only behavior.',
    },
    summary: {
      files: files.length,
      scanned: files.filter(file => file.disposition === 'scanned').length,
      excluded: files.filter(file => file.disposition === 'excluded').length,
      unsupported: files.filter(file => file.disposition === 'unsupported').length,
      ...countFindings(findings),
      platforms: Object.fromEntries(Object.keys(roots).map(platform => {
        const subset = files.filter(file => file.platform === platform);
        return [platform, { files: subset.length, scanned: subset.filter(file => file.disposition === 'scanned').length,
          excluded: subset.filter(file => file.disposition === 'excluded').length,
          unsupported: subset.filter(file => file.disposition === 'unsupported').length,
          ...countFindings(findings.filter(finding => finding.platform === platform)) }];
      })),
    },
    files,
    findings,
  };
}

function main() {
  const args = process.argv.slice(2);
  let commit = 'HEAD';
  let output;
  for (let index = 0; index < args.length; index += 2) {
    const [option, value] = args.slice(index, index + 2);
    if (!['--commit', '--output'].includes(option) || !value || value.startsWith('--')) {
      throw new Error('Usage: node scripts/design/baseline-ui-design.mjs --output <report.json> [--commit HEAD|<full-sha>]');
    }
    if (option === '--commit') commit = value;
    else output = value;
  }
  if (!output) throw new Error('--output is required; debt never changes the exit code.');
  const report = runBaseline({ commit });
  const destination = path.resolve(output);
  const temporary = `${destination}.${process.pid}.tmp`;
  try {
    fs.writeFileSync(temporary, serializeBaseline(report), { flag: 'wx' });
    fs.renameSync(temporary, destination);
  } finally {
    if (fs.existsSync(temporary)) fs.unlinkSync(temporary);
  }
  process.stdout.write(`${JSON.stringify({ status: report.status, inspectedCommit: report.inspectedCommit,
    output, summary: report.summary }, null, 2)}\n`);
}

if (process.argv[1] && path.resolve(process.argv[1]) === scriptPath) {
  try {
    main();
  } catch (error) {
    process.stderr.write(`UI baseline error: ${error.message}\n`);
    process.exitCode = 1;
  }
}

#!/usr/bin/env node

import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { baselineToolingFiles, readGit, runBaseline, serializeBaseline } from './baseline-ui-design.mjs';

const scriptPath = fileURLToPath(import.meta.url);
const repositoryRoot = path.resolve(path.dirname(scriptPath), '..', '..');
export const allowancePath = '.impeccable/ui-count-ceiling.json';
export const baselinePath = 'docs/design/ui-source-baseline.json';
const sha = value => typeof value === 'string' && /^[a-f0-9]{40,64}$/.test(value);
const digest = value => crypto.createHash('sha256').update(value).digest('hex');
const object = value => value !== null && typeof value === 'object' && !Array.isArray(value);

function parseJson(bytes, label) {
  let parsed;
  try {
    parsed = JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(bytes));
  } catch (error) {
    throw new Error(`Invalid ${label}: ${error.message}`);
  }
  if (!object(parsed)) throw new Error(`Invalid ${label}: expected an object.`);
  return parsed;
}

function keys(value, expected, label) {
  if (!object(value) || Object.keys(value).sort().join('|') !== [...expected].sort().join('|')) {
    throw new Error(`Malformed/incompatible ${label} fields.`);
  }
}

function hashValue(value, label) {
  if (typeof value !== 'string' || !/^[a-f0-9]{64}$/.test(value)) {
    throw new Error(`Invalid ${label} SHA-256 provenance.`);
  }
}

export function findingCounts(report) {
  if (!object(report) || report.schemaVersion !== 1 || report.status !== 'observed' ||
    !sha(report.inspectedCommit) || !Array.isArray(report.files) || !Array.isArray(report.findings) ||
    !object(report.summary) || !object(report.provenance) || !object(report.coverage)) {
    throw new Error('Malformed/incompatible baseline report.');
  }
  const files = new Map();
  for (const file of report.files) {
    if (!object(file) || typeof file.file !== 'string' || files.has(file.file) ||
      !['web', 'ios', 'site'].includes(file.platform) ||
      !['scanned', 'excluded', 'unsupported'].includes(file.disposition) || !sha(file.gitBlob)) {
      throw new Error('Invalid baseline file enumeration.');
    }
    if (file.disposition === 'scanned') {
      hashValue(file.sha256, file.file);
      if (!['impeccable-regex', 'swiftui-same-line-target'].includes(file.engine) ||
        !Array.isArray(file.designSystemChecks)) throw new Error('Missing scanner capabilities.');
    }
    files.set(file.file, file);
  }
  const counts = { blockers: 0, advisories: 0, suppressed: 0, total: 0 };
  for (const finding of report.findings) {
    const file = files.get(finding.file);
    if (!file || file.disposition !== 'scanned' || finding.platform !== file.platform ||
      finding.engine !== file.engine || typeof finding.antipattern !== 'string' ||
      !['design-system', 'generic-static'].includes(finding.ruleFamily) ||
      !Number.isInteger(finding.line) || finding.line < 0 ||
      !['blocker', 'advisory', 'suppressed'].includes(finding.disposition)) {
      throw new Error('Invalid baseline finding or source provenance.');
    }
    counts[{ blocker: 'blockers', advisory: 'advisories', suppressed: 'suppressed' }[finding.disposition]] += 1;
  }
  counts.total = counts.blockers + counts.advisories;
  for (const name of ['blockers', 'advisories', 'suppressed']) {
    if (report.summary[name] !== counts[name]) throw new Error(`Baseline severity accounting mismatch: ${name}.`);
  }
  for (const platform of ['web', 'ios', 'site']) {
    const subset = [...files.values()].filter(file => file.platform === platform);
    const summary = report.summary.platforms?.[platform];
    if (!summary || subset.filter(file => file.disposition === 'scanned').length === 0) {
      throw new Error(`Missing scanner capabilities: ${platform}.`);
    }
    for (const disposition of ['scanned', 'excluded', 'unsupported']) {
      if (summary[disposition] !== subset.filter(file => file.disposition === disposition).length) {
        throw new Error(`Baseline platform enumeration mismatch: ${platform}.${disposition}.`);
      }
    }
    for (const [disposition, name] of [['blocker', 'blockers'], ['advisory', 'advisories'], ['suppressed', 'suppressed']]) {
      if (summary[name] !== report.findings.filter(finding => finding.platform === platform &&
        finding.disposition === disposition).length) throw new Error(`Baseline platform severity mismatch: ${platform}.${name}.`);
    }
  }
  if (report.summary.files !== files.size || report.summary.scanned !==
    [...files.values()].filter(file => file.disposition === 'scanned').length) {
    throw new Error('Baseline total enumeration mismatch.');
  }
  if (!sha(report.provenance.config?.inspectedCommit) || !Array.isArray(report.provenance.config?.inputs) ||
    !Array.isArray(report.provenance.tooling?.files) || !Array.isArray(report.provenance.designContexts)) {
    throw new Error('Missing baseline config/tooling provenance.');
  }
  for (const input of report.provenance.config.inputs) {
    hashValue(input.sha256, input.file);
    if (!sha(input.gitBlob)) throw new Error('Invalid configuration blob provenance.');
  }
  for (const input of report.provenance.tooling.files) hashValue(input.sha256, input.file);
  return counts;
}

export function validateAllowance(policy, baselineBytes, report) {
  keys(policy, ['schemaVersion', 'mode', 'baseline', 'baselineSha256', 'ceiling', 'snapshot'], 'allowance');
  keys(policy.snapshot, ['blockers', 'advisories', 'suppressed', 'total'], 'allowance severity snapshot');
  if (policy.schemaVersion !== 1 || policy.mode !== 'total-unsuppressed-count' || policy.baseline !== baselinePath) {
    throw new Error('Malformed/incompatible count allowance.');
  }
  hashValue(policy.baselineSha256, 'Allowance baseline');
  if (digest(baselineBytes) !== policy.baselineSha256) throw new Error('Approved baseline digest mismatch.');
  for (const value of [policy.ceiling, ...Object.values(policy.snapshot)]) {
    if (!Number.isSafeInteger(value) || value < 0) throw new Error('Invalid count allowance.');
  }
  const counts = findingCounts(report);
  if (Object.entries(counts).some(([key, value]) => policy.snapshot[key] !== value) || policy.ceiling !== counts.total) {
    throw new Error('Allowance does not match its observed severity snapshot.');
  }
  return counts;
}

export function evaluateCountCeiling(policy, baselineBytes, approved, current) {
  const snapshot = validateAllowance(policy, baselineBytes, approved);
  const observed = findingCounts(current);
  if (JSON.stringify(current.coverage.roots) !== JSON.stringify(approved.coverage.roots) ||
    JSON.stringify(current.provenance.detector) !== JSON.stringify(approved.provenance.detector) ||
    JSON.stringify(current.provenance.ruleRegistry) !== JSON.stringify(approved.provenance.ruleRegistry) ||
    JSON.stringify(current.provenance.tooling.files) !== JSON.stringify(approved.provenance.tooling.files) ||
    JSON.stringify(current.provenance.config.effective) !== JSON.stringify(approved.provenance.config.effective) ||
    JSON.stringify(current.provenance.config.inputs) !== JSON.stringify(approved.provenance.config.inputs) ||
    JSON.stringify(current.provenance.designContexts) !== JSON.stringify(approved.provenance.designContexts)) {
    throw new Error('Incompatible baseline scope, rules, tooling or effective policy; deliberate refresh required.');
  }
  return {
    schemaVersion: 1,
    status: observed.total <= policy.ceiling ? 'pass' : 'fail',
    policy: 'total-unsuppressed-count',
    inspectedCommit: current.inspectedCommit,
    policyCommit: current.provenance.config.inspectedCommit,
    ceiling: policy.ceiling,
    snapshot,
    observed,
    delta: observed.total - snapshot.total,
    limitation: 'Same-total swaps and severity redistribution are allowed; this is not a per-file, rule or occurrence ratchet.',
    report: current,
  };
}

function trustedBlob(root, commit, file) {
  const entry = readGit(root, ['ls-tree', '-z', commit, '--', file]).toString('utf8');
  const match = /^(100644|100755) blob ([a-f0-9]{40,64})\t[^\0]+\0$/.exec(entry);
  if (!match) throw new Error(`Missing/non-regular trusted-base input: ${file}.`);
  return readGit(root, ['cat-file', 'blob', match[2]]);
}

export function runCountRatchet({ root = repositoryRoot, trustedBase, head } = {}) {
  if (!sha(trustedBase) || !sha(head)) throw new Error('Trusted base and head must be full immutable commit SHAs.');
  if (readGit(root, ['rev-parse', 'HEAD']).toString().trim() !== trustedBase) {
    throw new Error('Executing checkout is not the requested trusted base.');
  }
  const bytes = trustedBlob(root, trustedBase, baselinePath);
  const approved = parseJson(bytes, 'approved baseline');
  const policy = parseJson(trustedBlob(root, trustedBase, allowancePath), 'trusted-base allowance');
  validateAllowance(policy, bytes, approved);
  const tooling = baselineToolingFiles(root);
  if (JSON.stringify(approved.provenance.tooling.files.map(file => file.file)) !== JSON.stringify(tooling)) {
    throw new Error('Invalid baseline tooling enumeration/provenance.');
  }
  for (const file of [...tooling, 'scripts/design/ui-count-ratchet.mjs']) {
    const committed = trustedBlob(root, trustedBase, file);
    const actual = fs.readFileSync(path.join(root, file));
    if (digest(actual) !== digest(committed)) throw new Error(`Executing tooling differs from trusted base: ${file}.`);
    const pinned = approved.provenance.tooling.files.find(input => input.file === file);
    if (pinned && pinned.sha256 !== digest(actual)) throw new Error(`Baseline tooling provenance mismatch: ${file}.`);
  }
  const reproduced = runBaseline({ root, commit: approved.inspectedCommit });
  if (serializeBaseline(reproduced) !== bytes.toString('utf8')) {
    throw new Error('Approved baseline cannot be reproduced from its immutable source/config/tooling provenance.');
  }
  const current = runBaseline({ root, commit: head, policyCommit: trustedBase });
  const result = evaluateCountCeiling(policy, bytes, approved, current);
  return { ...result, trustedBase, baselineSha256: policy.baselineSha256 };
}

export function approveBaseline({ root = repositoryRoot, ceiling } = {}) {
  if (!Number.isSafeInteger(ceiling) || ceiling < 0) throw new Error('--ceiling must be a nonnegative integer.');
  const bytes = fs.readFileSync(path.join(root, baselinePath));
  const report = parseJson(bytes, 'proposed baseline');
  const snapshot = findingCounts(report);
  if (ceiling !== snapshot.total) throw new Error('Explicit ceiling must equal the observed unsuppressed total.');
  if (serializeBaseline(runBaseline({ root, commit: report.inspectedCommit })) !== bytes.toString('utf8')) {
    throw new Error('Proposed baseline provenance is not reproducible.');
  }
  const destination = path.join(root, allowancePath);
  if (fs.existsSync(destination)) {
    const existing = parseJson(fs.readFileSync(destination), 'existing allowance');
    if (!Number.isSafeInteger(existing.ceiling) || existing.ceiling < 0 ||
      existing.schemaVersion !== 1 || existing.mode !== 'total-unsuppressed-count') {
      throw new Error('Cannot refresh a malformed existing allowance.');
    }
    if (ceiling > existing.ceiling) throw new Error('Refresh cannot increase the approved ceiling.');
  }
  const policy = { schemaVersion: 1, mode: 'total-unsuppressed-count', baseline: baselinePath,
    baselineSha256: digest(bytes), ceiling, snapshot };
  fs.writeFileSync(destination, `${JSON.stringify(policy, null, 2)}\n`);
  return policy;
}

function main() {
  const args = process.argv.slice(2);
  const options = {};
  for (let index = 0; index < args.length; index += 1) {
    const option = args[index];
    if (option === '--approve-baseline') {
      if (options.approve) throw new Error('Duplicate --approve-baseline.');
      options.approve = true;
      continue;
    }
    if (!['--trusted-base', '--head', '--output', '--ceiling'].includes(option) ||
      !args[index + 1] || args[index + 1].startsWith('--') || option in options) {
      throw new Error('Invalid UI count ratchet options.');
    }
    options[option] = args[++index];
  }
  if (options.approve) {
    if (!/^(0|[1-9]\d*)$/.test(options['--ceiling'] || '') || Object.keys(options).length !== 2) {
      throw new Error('Usage: ui-count-ratchet.mjs --approve-baseline --ceiling <observed-total>');
    }
    process.stdout.write(`${JSON.stringify(approveBaseline({ ceiling: Number(options['--ceiling']) }), null, 2)}\n`);
    return;
  }
  if (Object.keys(options).length !== 3 || !options['--output']) {
    throw new Error('Usage: ui-count-ratchet.mjs --trusted-base <sha> --head <sha> --output <report.json>');
  }
  const result = runCountRatchet({ trustedBase: options['--trusted-base'], head: options['--head'] });
  fs.writeFileSync(options['--output'], `${JSON.stringify(result, null, 2)}\n`, { flag: 'wx' });
  const { report, ...summary } = result;
  process.stdout.write(`${JSON.stringify(summary, null, 2)}\n`);
  process.exitCode = result.status === 'pass' ? 0 : 1;
}

if (process.argv[1] && path.resolve(process.argv[1]) === scriptPath) {
  try {
    main();
  } catch (error) {
    process.stderr.write(`UI count ratchet error: ${error.message}\n`);
    process.exitCode = 1;
  }
}

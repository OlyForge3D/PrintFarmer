#!/usr/bin/env node

import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

import { readSharedConfig, scanSingleUiFile } from './check-ui-design.mjs';

const scriptPath = fileURLToPath(import.meta.url);

function parseJsonArgument(value) {
  if (typeof value !== 'string') return value;
  try {
    return JSON.parse(value);
  } catch {
    return value;
  }
}

function nestedObjects(value) {
  const found = [];
  const visit = current => {
    if (!current || typeof current !== 'object' || Array.isArray(current)) return;
    found.push(current);
    for (const key of ['input', 'tool_input', 'arguments', 'toolArgs']) {
      if (key in current) visit(parseJsonArgument(current[key]));
    }
  };
  visit(parseJsonArgument(value));
  return found;
}

function applyPatchText(objects, rawArgs) {
  for (const object of objects) {
    for (const key of ['patch', 'input']) {
      if (typeof object[key] === 'string' && object[key].includes('*** ')) return object[key];
    }
  }
  return typeof rawArgs === 'string' && rawArgs.includes('*** ') ? rawArgs : undefined;
}

function pathsFromApplyPatch(patch) {
  const files = [];
  let current;
  const appendCurrent = () => {
    if (current) files.push(current);
    current = undefined;
  };

  for (const line of patch.split(/\r?\n/)) {
    let match = line.match(/^\*\*\* (Add|Update|Delete) File: (.+)$/);
    if (match) {
      appendCurrent();
      current = {
        path: match[2].trim(),
        operation: match[1].toLowerCase(),
      };
      continue;
    }
    match = line.match(/^\*\*\* Move to: (.+)$/);
    if (match && current?.operation === 'update') {
      current = { ...current, path: match[1].trim(), operation: 'move' };
    }
  }
  appendCurrent();
  const latestByPath = new Map();
  for (const file of files) latestByPath.set(file.path, file);
  return [...latestByPath.values()];
}

export function editedFiles(event) {
  const rawArgs = event.toolArgs ?? event.tool_input;
  const args = parseJsonArgument(rawArgs);
  const objects = nestedObjects(args);
  const toolName = String(event.toolName || '').toLowerCase();
  if (toolName.includes('apply_patch') || toolName === 'applypatch') {
    const patch = applyPatchText(objects, typeof rawArgs === 'string' ? rawArgs : undefined);
    if (patch) return pathsFromApplyPatch(patch);
  }

  const files = [];
  for (const object of objects) {
    const filePath = object.path || object.file_path || object.filePath || object.target_file;
    if (typeof filePath !== 'string' || !filePath) continue;
    const action = String(object.operation || object.action || '').toLowerCase();
    const operation = action === 'delete' || action === 'remove' || toolName.includes('delete')
      ? 'delete'
      : 'update';
    files.push({ path: filePath, operation });
  }

  return files.filter((file, index, list) =>
    list.findIndex(candidate => candidate.path === file.path && candidate.operation === file.operation) === index);
}

function findRepositoryRoot(cwd) {
  const result = spawnSync('git', ['rev-parse', '--show-toplevel'], {
    cwd,
    encoding: 'utf8',
    windowsHide: true,
  });
  if (result.error || result.status !== 0) {
    throw new Error(`Could not locate repository root: ${result.error?.message || result.stderr}`);
  }
  return path.resolve(result.stdout.trim());
}

export function formatContext(results, limits) {
  const scannedFiles = results.flatMap(result => result.scannedFiles);
  const entries = results.flatMap(result => [
    ...result.findings.map(finding => ({ ...finding, kind: 'finding' })),
    ...result.advisories.map(finding => ({ ...finding, kind: 'advisory' })),
  ]);
  const linesFor = (visibleEntries, omittedCount) => {
    const lines = [`Design checks scanned ${scannedFiles.length} changed UI source file(s).`];
    for (const entry of visibleEntries) {
      const prefix = entry.kind === 'advisory' ? 'Advisory: ' : '';
      lines.push(`${prefix}${entry.file}:${entry.line || 1} [${entry.antipattern}] ${entry.description}`);
    }
    if (omittedCount > 0) lines.push(`${omittedCount} finding(s) omitted; see CI for complete output.`);
    if (entries.length === 0) {
      lines.push('No deterministic design findings. This is a static check, not a visual or model-based review.');
    } else {
      lines.push('Review findings and fix real issues; only use narrow, justified file-scoped ignores for intentional exceptions.');
    }
    return lines.join('\n');
  };

  let visibleEntries = entries.slice(0, limits.maxFindings);
  let omittedCount = entries.length - visibleEntries.length;
  let context = linesFor(visibleEntries, omittedCount);
  while (context.length > limits.maxChars && visibleEntries.length > 0) {
    visibleEntries = visibleEntries.slice(0, -1);
    omittedCount = entries.length - visibleEntries.length;
    context = linesFor(visibleEntries, omittedCount);
  }
  if (context.length > limits.maxChars) {
    context = entries.length === 0
      ? 'Design check passed; no findings.'
      : `${omittedCount} finding(s) omitted; see CI for complete output.`;
  }
  if (context.length > limits.maxChars) context = 'Design check output omitted; see CI.';
  return context;
}

export async function runHook(event) {
  const cwd = path.resolve(event.cwd || process.cwd());
  const root = findRepositoryRoot(cwd);
  const config = readSharedConfig(root);
  if (!config.hook.enabled || config.hook.quiet) return undefined;

  const files = editedFiles(event).filter(file => file.operation !== 'delete');
  if (files.length === 0) return undefined;

  const results = [];
  for (const file of files) {
    const absolutePath = path.isAbsolute(file.path) ? file.path : path.resolve(cwd, file.path);
    results.push(await scanSingleUiFile(root, absolutePath));
  }
  if (results.every(result => result.status === 'skipped')) return undefined;

  const additionalContext = formatContext(results, config.hook.limits);
  return { additionalContext };
}

async function main() {
  try {
    const chunks = [];
    for await (const chunk of process.stdin) chunks.push(chunk);
    const input = Buffer.concat(chunks).toString('utf8');
    const event = input ? JSON.parse(input) : {};
    const result = await runHook(event);
    if (result) process.stdout.write(`${JSON.stringify(result)}\n`);
  } catch (error) {
    process.stdout.write(`${JSON.stringify({
      additionalContext: `Impeccable deterministic design check could not run: ${error.message}. CI will fail closed if shared detector configuration or coverage is invalid.`,
    })}\n`);
  }
}

if (process.argv[1] && path.resolve(process.argv[1]) === scriptPath) {
  main();
}

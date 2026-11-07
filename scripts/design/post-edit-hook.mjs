#!/usr/bin/env node

import path from 'node:path';
import { spawnSync } from 'node:child_process';

import { readSharedConfig, scanSingleUiFile } from './check-ui-design.mjs';

function parseToolArgs(value) {
  if (value && typeof value === 'object' && !Array.isArray(value)) return value;
  if (typeof value !== 'string') return {};
  try {
    const parsed = JSON.parse(value);
    return parsed && typeof parsed === 'object' && !Array.isArray(parsed) ? parsed : {};
  } catch {
    return {};
  }
}

function editedPaths(event) {
  const args = parseToolArgs(event.toolArgs ?? event.tool_input);
  if (event.toolName === 'apply_patch' && typeof event.toolArgs === 'string') {
    return [...event.toolArgs.matchAll(/^\*\*\* (?:Add|Update|Delete) File: (.+)$/gm)]
      .map(match => match[1].trim());
  }
  const filePath = args.path || args.file_path || args.filePath || args.target_file;
  return typeof filePath === 'string' && filePath ? [filePath] : [];
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

function formatContext(result) {
  if (result.status === 'skipped') return '';
  const lines = [
    `Impeccable deterministic design checks scanned ${result.scannedFiles.length} changed UI source file(s).`,
  ];
  for (const finding of result.findings) {
    lines.push(`${finding.file}:${finding.line || 1} [${finding.antipattern}] ${finding.description}`);
  }
  for (const finding of result.advisories) {
    lines.push(`Advisory: ${finding.file}:${finding.line || 1} [${finding.antipattern}] ${finding.description}`);
  }
  if (result.findings.length === 0 && result.advisories.length === 0) {
    lines.push('No deterministic design findings. This is a static check, not a visual or model-based review.');
  } else {
    lines.push('Review the findings and fix real issues; only use narrow, justified file-scoped ignores for intentional exceptions.');
  }
  return lines.join('\n');
}

async function main() {
  try {
    const chunks = [];
    for await (const chunk of process.stdin) chunks.push(chunk);
    const input = Buffer.concat(chunks).toString('utf8');
    const event = input ? JSON.parse(input) : {};
    const cwd = path.resolve(event.cwd || process.cwd());
    const root = findRepositoryRoot(cwd);
    const config = readSharedConfig(root);
    if (!config.hook.enabled) return;

    const paths = editedPaths(event);
    if (paths.length === 0) return;
    const context = [];
    for (const editedPath of paths) {
      const absolutePath = path.isAbsolute(editedPath) ? editedPath : path.resolve(cwd, editedPath);
      const result = await scanSingleUiFile(root, absolutePath);
      const formatted = formatContext(result);
      if (formatted) context.push(formatted);
    }
    if (context.length > 0 && !config.hook.quiet) {
      process.stdout.write(`${JSON.stringify({ additionalContext: context.join('\n\n') })}\n`);
    }
  } catch (error) {
    process.stdout.write(`${JSON.stringify({
      additionalContext: `Impeccable deterministic design check could not run: ${error.message}. CI will fail closed if shared detector configuration or coverage is invalid.`,
    })}\n`);
  }
}

main();

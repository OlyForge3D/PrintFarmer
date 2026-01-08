#!/usr/bin/env node
const { execSync } = require('child_process');
const fs = require('fs');
const path = require('path');

function getGitRoot() {
  try {
    return execSync('git rev-parse --show-toplevel', { encoding: 'utf8' }).trim();
  } catch (e) {
    console.error('Not a git repository (or git not available).');
    process.exit(2);
  }
}

function getTrackedFiles(repoRoot) {
  const out = execSync('git ls-files', { encoding: 'utf8' });
  return out.split('\n').filter(Boolean);
}

function checkPathCasing(repoRoot, relPath) {
  const parts = relPath.split('/');
  let current = repoRoot;
  for (const part of parts) {
    // If current is a file (end of path), break
    let entries;
    try {
      entries = fs.readdirSync(current);
    } catch (e) {
      // current might be a file when there are no more parts
      return { ok: false, reason: `Cannot read directory ${current}` };
    }

    const match = entries.find(e => e === part);
    if (match) {
      // exact match
      current = path.join(current, match);
      continue;
    }

    // try case-insensitive match
    const ci = entries.find(e => e.toLowerCase() === part.toLowerCase());
    if (!ci) {
      return { ok: false, reason: `Component not found: ${part} under ${current}` };
    }

    // found but casing differs
    return { ok: false, reason: `Casing mismatch: git has '${part}' but actual entry is '${ci}' at ${path.join(current, ci)}` };
  }

  return { ok: true };
}

function main() {
  const repoRoot = getGitRoot();
  const files = getTrackedFiles(repoRoot);
  const mismatches = [];

  for (const f of files) {
    const res = checkPathCasing(repoRoot, f);
    if (!res.ok) {
      mismatches.push({ file: f, reason: res.reason });
    }
  }

  if (mismatches.length === 0) {
    console.log('OK: No path casing mismatches detected.');
    process.exit(0);
  }

  console.error('ERROR: Path casing mismatches detected:');
  for (const m of mismatches) {
    console.error(` - ${m.file}: ${m.reason}`);
  }
  console.error('\nTo fix: perform a git-aware case-correct rename (use temp name then rename back) and commit.');
  process.exit(1);
}

main();

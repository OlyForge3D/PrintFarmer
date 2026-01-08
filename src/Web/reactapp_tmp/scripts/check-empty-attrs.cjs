#!/usr/bin/env node
const fs = require('fs');
const path = require('path');

const root = path.resolve(__dirname, '..');

const patterns = [
  /className\s*=\s*""/g,
  /className\s*=\s*''/g,
  /className\s*=\s*\{\s*""\s*\}/g,
  /className\s*=\s*\{\s*''\s*\}/g,
  /className\s*=\s*\{\s*null\s*\}/g,
  /className\s*=\s*\{\s*undefined\s*\}/g,
];

function walk(dir) {
  const entries = fs.readdirSync(dir, { withFileTypes: true });
  for (const entry of entries) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      if (entry.name === 'node_modules' || entry.name === '.git') continue;
      walk(full);
    } else if (/\.(jsx|tsx|ts|js|mjs|cjs|vue|svelte)$/.test(entry.name)) {
      const txt = fs.readFileSync(full, 'utf8');
      patterns.forEach((pat) => {
        let m;
        while ((m = pat.exec(txt)) !== null) {
          const before = txt.slice(0, m.index);
          const line = before.split('\n').length;
          console.log(`${full}:${line}: Found empty attribute pattern -> ${m[0].slice(0, 80)}`);
        }
      });
    }
  }
}

let findings = 0;
try {
  // collect findings count by wrapping console.log
  const orig = console.log;
  console.log = function (...args) { findings++; orig.apply(console, args); };
  walk(root);
  console.log = orig;
} catch (err) {
  console.error('Error while scanning:', err);
  process.exit(2);
}

if (findings > 0) {
  console.error(`\ncheck-empty-attrs: found ${findings} occurrences of empty attributes`);
  process.exit(1);
}

process.exit(0);

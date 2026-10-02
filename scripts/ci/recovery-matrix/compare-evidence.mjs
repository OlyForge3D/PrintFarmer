#!/usr/bin/env node
import { readFileSync } from 'node:fs';

import { validateEvidenceParity } from './evidence.mjs';

const [bashPath, powershellPath, ...extra] = process.argv.slice(2);
if (!bashPath || !powershellPath || extra.length > 0) {
  console.error('Usage: compare-evidence.mjs <bash-evidence.json> <powershell-evidence.json>');
  process.exit(2);
}

const bashEvidence = JSON.parse(readFileSync(bashPath, 'utf8'));
const powershellEvidence = JSON.parse(readFileSync(powershellPath, 'utf8'));
const errors = validateEvidenceParity(bashEvidence, powershellEvidence);
if (errors.length > 0) {
  console.error(`Recovery-matrix evidence parity failed:\n${errors.join('\n')}`);
  process.exit(1);
}

console.log(`Evidence parity passed for ${bashPath} and ${powershellPath}`);

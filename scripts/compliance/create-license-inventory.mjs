#!/usr/bin/env node

import { execFile } from 'node:child_process';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import path from 'node:path';
import process from 'node:process';
import { promisify } from 'node:util';
import { fileURLToPath } from 'node:url';
import {
  createNpmLicenseInventory,
  createNugetLicenseInventory,
  readJson,
} from './compliance-lib.mjs';

const execFileAsync = promisify(execFile);

function parseArguments(argumentsList) {
  const options = {
    outputPath: 'release-artifacts/license-inventory.json',
    repoRoot: path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..'),
    revision: undefined,
    version: undefined,
  };

  for (let index = 0; index < argumentsList.length; index += 1) {
    const argument = argumentsList[index];
    if (argument === '--repo') {
      options.repoRoot = path.resolve(argumentsList[++index]);
    } else if (argument === '--output') {
      options.outputPath = argumentsList[++index];
    } else if (argument === '--revision') {
      options.revision = argumentsList[++index];
    } else if (argument === '--version') {
      options.version = argumentsList[++index];
    } else {
      throw new Error(`Unknown argument: ${argument}`);
    }
  }

  return options;
}

async function resolveRevision(repoRoot, requestedRevision) {
  const result = await execFileAsync(
    'git',
    ['rev-parse', `${requestedRevision ?? 'HEAD'}^{commit}`],
    { cwd: repoRoot, encoding: 'utf8' },
  );
  return result.stdout.trim().toLowerCase();
}

async function main() {
  const options = parseArguments(process.argv.slice(2));
  const dependencyPolicy = await readJson(
    path.join(options.repoRoot, 'compliance', 'dependency-license-policy.json'),
  );
  const version = options.version
    ?? (await readFile(path.join(options.repoRoot, 'VERSION'), 'utf8')).trim();
  const normalizedVersion = version.startsWith('v') ? version : `v${version}`;
  if (!/^v\d+\.\d+\.\d+([-.][0-9A-Za-z.-]+)?$/.test(normalizedVersion)) {
    throw new Error(`Version must be v-prefixed semantic version text, got ${version}`);
  }

  const revision = await resolveRevision(options.repoRoot, options.revision);
  if (!/^[0-9a-f]{40}$/.test(revision)) {
    throw new Error(`Revision must resolve to a full commit SHA, got ${revision}`);
  }

  const [nugetResult, npmResult] = await Promise.all([
    createNugetLicenseInventory(options.repoRoot, dependencyPolicy),
    createNpmLicenseInventory(options.repoRoot, dependencyPolicy),
  ]);
  const errors = [...nugetResult.errors, ...npmResult.errors];
  if (errors.length > 0) {
    throw new Error(errors.map((error) =>
      `[${error.code}] ${error.path}: ${error.message}`).join('\n'));
  }

  const outputPath = path.resolve(options.repoRoot, options.outputPath);
  await mkdir(path.dirname(outputPath), { recursive: true });
  const inventory = {
    ...nugetResult.inventory,
    npmPackages: npmResult.packages,
    revision,
    version: normalizedVersion,
  };
  await writeFile(outputPath, `${JSON.stringify(inventory, undefined, 2)}\n`, 'utf8');
  process.stdout.write(`${JSON.stringify({
    output: path.relative(options.repoRoot, outputPath).replaceAll('\\', '/'),
    npmPackages: inventory.npmPackages.length,
    nugetPackages: inventory.packages.length,
    projects: inventory.projects.length,
    revision,
    version: normalizedVersion,
  })}\n`);
}

main().catch((error) => {
  process.stderr.write(`${error.stack ?? error.message}\n`);
  process.exitCode = 1;
});

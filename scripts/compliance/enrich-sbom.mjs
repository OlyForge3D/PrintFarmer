#!/usr/bin/env node

import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import process from 'node:process';
import { fileURLToPath } from 'node:url';
import {
  enrichSbomDocument,
  readJson,
  validateSbomDocument,
} from './compliance-lib.mjs';

function parseArguments(argumentsList) {
  const options = {
    includeNpm: false,
    inventoryPath: undefined,
    outputPath: undefined,
    repoRoot: path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..'),
    revision: undefined,
    sbomPath: undefined,
    version: undefined,
  };

  for (let index = 0; index < argumentsList.length; index += 1) {
    const argument = argumentsList[index];
    if (argument === '--repo') {
      options.repoRoot = path.resolve(argumentsList[++index]);
    } else if (argument === '--sbom') {
      options.sbomPath = argumentsList[++index];
    } else if (argument === '--inventory') {
      options.inventoryPath = argumentsList[++index];
    } else if (argument === '--output') {
      options.outputPath = argumentsList[++index];
    } else if (argument === '--revision') {
      options.revision = argumentsList[++index];
    } else if (argument === '--version') {
      options.version = argumentsList[++index];
    } else if (argument === '--include-npm') {
      options.includeNpm = true;
    } else {
      throw new Error(`Unknown argument: ${argument}`);
    }
  }

  if (!options.sbomPath || !options.inventoryPath) {
    throw new Error('--sbom and --inventory are required');
  }

  return options;
}

async function main() {
  const options = parseArguments(process.argv.slice(2));
  const sbomPath = path.resolve(options.repoRoot, options.sbomPath);
  const inventoryPath = path.resolve(options.repoRoot, options.inventoryPath);
  const outputPath = path.resolve(options.repoRoot, options.outputPath ?? options.sbomPath);
  const [sbom, inventory, dependencyPolicy, licensingPolicy] = await Promise.all([
    readJson(sbomPath),
    readJson(inventoryPath),
    readJson(path.join(options.repoRoot, 'compliance', 'dependency-license-policy.json')),
    readJson(path.join(options.repoRoot, 'compliance', 'licensing-policy.json')),
  ]);
  const version = options.version ?? inventory.version;
  const revision = (options.revision ?? inventory.revision)?.toLowerCase();
  if (!/^v\d+\.\d+\.\d+([-.][0-9A-Za-z.-]+)?$/.test(version ?? '')) {
    throw new Error(`Version must be v-prefixed semantic version text, got ${version}`);
  }

  if (!/^[0-9a-f]{40}$/.test(revision ?? '')) {
    throw new Error(`Revision must be a full commit SHA, got ${revision}`);
  }

  const enrichmentErrors = enrichSbomDocument(sbom, inventory, dependencyPolicy, {
    inventoryPath: options.inventoryPath,
    includeNpm: options.includeNpm,
    licenseExpression: licensingPolicy.licenseExpression,
    repositoryUrl: licensingPolicy.repositoryUrl,
    revision,
    version,
  });
  if (enrichmentErrors.length > 0) {
    throw new Error(enrichmentErrors.map((error) =>
      `[${error.code}] ${error.path}: ${error.message}`).join('\n'));
  }

  sbom.creationInfo.creators.push('Tool: PrintFarmer compliance SBOM enricher');
  const validationErrors = validateSbomDocument(sbom, options.sbomPath, dependencyPolicy);
  if (validationErrors.length > 0) {
    throw new Error(validationErrors.map((error) =>
      `[${error.code}] ${error.path}: ${error.message}`).join('\n'));
  }

  await mkdir(path.dirname(outputPath), { recursive: true });
  await writeFile(outputPath, `${JSON.stringify(sbom, undefined, 2)}\n`, 'utf8');
  process.stdout.write(`${JSON.stringify({
    output: path.relative(options.repoRoot, outputPath).replaceAll('\\', '/'),
    packages: sbom.packages.length,
    revision,
    version,
  })}\n`);
}

main().catch((error) => {
  process.stderr.write(`${error.stack ?? error.message}\n`);
  process.exitCode = 1;
});

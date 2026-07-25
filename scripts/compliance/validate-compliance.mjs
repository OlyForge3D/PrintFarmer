#!/usr/bin/env node

import path from 'node:path';
import process from 'node:process';
import { fileURLToPath } from 'node:url';
import { validateRepository } from './compliance-lib.mjs';

function parseArguments(argumentsList) {
  const options = {
    includeDependencies: true,
    json: false,
    publicationPaths: [],
    repoRoot: path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..'),
    sbomPaths: [],
  };

  for (let index = 0; index < argumentsList.length; index += 1) {
    const argument = argumentsList[index];
    if (argument === '--repo') {
      options.repoRoot = path.resolve(argumentsList[++index]);
    } else if (argument === '--skip-dependencies') {
      options.includeDependencies = false;
    } else if (argument === '--sbom') {
      options.sbomPaths.push(argumentsList[++index]);
    } else if (argument === '--publication') {
      options.publicationPaths.push(argumentsList[++index]);
    } else if (argument === '--json') {
      options.json = true;
    } else {
      throw new Error(`Unknown argument: ${argument}`);
    }
  }

  return options;
}

async function main() {
  const options = parseArguments(process.argv.slice(2));
  const errors = await validateRepository(options.repoRoot, options);

  if (options.json) {
    process.stdout.write(`${JSON.stringify({ valid: errors.length === 0, errors }, undefined, 2)}\n`);
  } else if (errors.length === 0) {
    process.stdout.write('Compliance validation passed.\n');
  } else {
    process.stderr.write(`Compliance validation failed with ${errors.length} error(s):\n`);
    for (const error of errors) {
      process.stderr.write(`- [${error.code}] ${error.path}: ${error.message}\n`);
    }
  }

  process.exitCode = errors.length === 0 ? 0 : 1;
}

main().catch((error) => {
  process.stderr.write(`${error.stack ?? error.message}\n`);
  process.exitCode = 1;
});

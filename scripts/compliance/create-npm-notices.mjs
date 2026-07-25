#!/usr/bin/env node

import {
  mkdir,
  readFile,
  readdir,
  writeFile,
} from 'node:fs/promises';
import { createHash } from 'node:crypto';
import path from 'node:path';
import process from 'node:process';
import { fileURLToPath } from 'node:url';
import {
  productionNpmPackagesFromLock,
  readJson,
} from './compliance-lib.mjs';

function parseArguments(argumentsList) {
  const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
  const options = {
    outputPath: 'src/Web/ReactApp/dist/THIRD-PARTY-LICENSES.npm.txt',
    packageRoot: 'src/Web/ReactApp',
    policyPath: 'compliance/dependency-license-policy.json',
    repoRoot,
  };

  for (let index = 0; index < argumentsList.length; index += 1) {
    const argument = argumentsList[index];
    if (argument === '--repo') {
      options.repoRoot = path.resolve(argumentsList[++index]);
    } else if (argument === '--package-root') {
      options.packageRoot = argumentsList[++index];
    } else if (argument === '--output') {
      options.outputPath = argumentsList[++index];
    } else if (argument === '--policy') {
      options.policyPath = argumentsList[++index];
    } else {
      throw new Error(`Unknown argument: ${argument}`);
    }
  }

  return options;
}

async function findLicenseFiles(packageDirectory, relativeDirectory = '') {
  const directory = path.join(packageDirectory, relativeDirectory);
  const entries = await readdir(directory, { withFileTypes: true });
  const licenseFiles = [];
  for (const entry of entries) {
    const relativePath = path.join(relativeDirectory, entry.name);
    if (entry.isDirectory() && entry.name !== 'node_modules') {
      licenseFiles.push(...await findLicenseFiles(packageDirectory, relativePath));
    } else if (
      entry.isFile()
      && /^(licen[cs]e|copying|notice)(?:[.-].*)?$/i.test(entry.name)
    ) {
      licenseFiles.push(relativePath);
    }
  }
  return licenseFiles.sort((left, right) => left.localeCompare(right));
}

async function main() {
  const options = parseArguments(process.argv.slice(2));
  const packageRoot = path.resolve(options.repoRoot, options.packageRoot);
  const lock = await readJson(path.join(packageRoot, 'package-lock.json'));
  const dependencyPolicy = await readJson(path.resolve(options.repoRoot, options.policyPath));
  const missingPackages = [];
  const sections = [];

  for (const packageRecord of productionNpmPackagesFromLock(lock)) {
    const packageDirectory = path.resolve(packageRoot, packageRecord.lockPath);
    const relativePackageDirectory = path.relative(packageRoot, packageDirectory);
    if (relativePackageDirectory.startsWith('..') || path.isAbsolute(relativePackageDirectory)) {
      throw new Error(`Unsafe npm package path: ${packageRecord.lockPath}`);
    }

    const licenseFiles = await findLicenseFiles(packageDirectory);
    const licenseSections = [];
    for (const licenseFile of licenseFiles) {
      const text = (await readFile(path.join(packageDirectory, licenseFile), 'utf8')).trim();
      if (text.length === 0) {
        throw new Error(
          `Production npm package ${packageRecord.name}@${packageRecord.version} has an empty ${licenseFile}`,
        );
      }
      licenseSections.push(`--- ${licenseFile} ---\n${text}`);
    }
    const fallback = (dependencyPolicy.npm?.licenseTextFallbacks ?? []).find((record) =>
      record.package === packageRecord.name
      && record.version === packageRecord.version
      && record.license === packageRecord.license);
    if (fallback) {
      const fallbackPath = path.resolve(options.repoRoot, fallback.licenseFile);
      const relativeFallbackPath = path.relative(options.repoRoot, fallbackPath);
      if (relativeFallbackPath.startsWith('..') || path.isAbsolute(relativeFallbackPath)) {
        throw new Error(`Unsafe npm license fallback path: ${fallback.licenseFile}`);
      }
      const text = (await readFile(fallbackPath, 'utf8')).trim();
      const sha256 = createHash('sha256').update(await readFile(fallbackPath)).digest('hex');
      if (text.length === 0 || sha256 !== fallback.sha256) {
        throw new Error(
          `Reviewed npm license fallback failed integrity validation: ${fallback.licenseFile}`,
        );
      }
      licenseSections.push(`--- ${fallback.licenseFile} ---\n${text}`);
    }
    if (licenseSections.length === 0) {
      missingPackages.push(`${packageRecord.name}@${packageRecord.version} (${packageRecord.license})`);
      continue;
    }

    sections.push([
      '='.repeat(80),
      `${packageRecord.name}@${packageRecord.version}`,
      `License: ${packageRecord.license}`,
      `Source: ${packageRecord.resolved ?? `https://www.npmjs.com/package/${packageRecord.name}`}`,
      '',
      licenseSections.join('\n\n'),
    ].join('\n'));
  }

  if (missingPackages.length > 0) {
    throw new Error(
      `Production npm packages without bundled or reviewed license text:\n${missingPackages.join('\n')}`,
    );
  }

  const outputPath = path.resolve(options.repoRoot, options.outputPath);
  await mkdir(path.dirname(outputPath), { recursive: true });
  const output = [
    'PrintFarmer bundled frontend dependency license texts',
    'Generated deterministically from src/Web/ReactApp/package-lock.json and installed package contents.',
    '',
    ...sections,
    '',
  ].join('\n');
  await writeFile(outputPath, output, 'utf8');
  process.stdout.write(`${JSON.stringify({
    output: path.relative(options.repoRoot, outputPath).replaceAll('\\', '/'),
    packages: sections.length,
  })}\n`);
}

main().catch((error) => {
  process.stderr.write(`${error.stack ?? error.message}\n`);
  process.exitCode = 1;
});

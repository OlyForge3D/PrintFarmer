#!/usr/bin/env node

import { createHash } from 'node:crypto';
import { execFile } from 'node:child_process';
import {
  mkdir,
  mkdtemp,
  readFile,
  readdir,
  readlink,
  rm,
  writeFile,
} from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import process from 'node:process';
import { promisify } from 'node:util';
import { fileURLToPath } from 'node:url';
import {
  normalizeRelativePath,
  readJson,
  scanPublicationFiles,
} from './compliance-lib.mjs';

const execFileAsync = promisify(execFile);

function parseArguments(argumentsList) {
  const options = {
    outputDirectory: 'release-artifacts',
    repoRoot: path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..'),
    revision: 'HEAD',
    version: undefined,
  };

  for (let index = 0; index < argumentsList.length; index += 1) {
    const argument = argumentsList[index];
    if (argument === '--repo') {
      options.repoRoot = path.resolve(argumentsList[++index]);
    } else if (argument === '--output') {
      options.outputDirectory = argumentsList[++index];
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

async function git(repoRoot, argumentsList) {
  const result = await execFileAsync('git', argumentsList, {
    cwd: repoRoot,
    encoding: 'utf8',
    maxBuffer: 20 * 1024 * 1024,
  });
  return result.stdout.trim();
}

async function sha256File(filePath) {
  const content = await readFile(filePath);
  return createHash('sha256').update(content).digest('hex');
}

async function collectArchiveFiles(root, currentDirectory, files, symbolicLinks) {
  const entries = await readdir(currentDirectory, { withFileTypes: true });
  for (const entry of entries) {
    const entryPath = path.join(currentDirectory, entry.name);
    if (entry.isDirectory()) {
      await collectArchiveFiles(root, entryPath, files, symbolicLinks);
    } else if (entry.isFile()) {
      files.push(normalizeRelativePath(path.relative(root, entryPath)));
    } else if (entry.isSymbolicLink()) {
      symbolicLinks.push({
        path: normalizeRelativePath(path.relative(root, entryPath)),
        target: await readlink(entryPath),
      });
    }
  }
}

export async function scanSourceArchive(archivePath, secretPatterns) {
  const extractionRoot = await mkdtemp(path.join(tmpdir(), 'printfarmer-source-'));
  try {
    await execFileAsync('tar', ['-xzf', archivePath, '-C', extractionRoot]);
    const files = [];
    const symbolicLinks = [];
    await collectArchiveFiles(extractionRoot, extractionRoot, files, symbolicLinks);

    const scanErrors = await scanPublicationFiles(extractionRoot, files, secretPatterns);
    const patterns = secretPatterns.map((pattern) => new RegExp(pattern, 'i'));
    for (const symbolicLink of symbolicLinks) {
      for (const pattern of patterns) {
        if (pattern.test(symbolicLink.target)) {
          scanErrors.push({
            code: 'PUBLICATION_SECRET',
            path: symbolicLink.path,
            message: `symbolic link target matches prohibited secret pattern ${pattern.source}`,
          });
        }
      }
    }

    if (scanErrors.length > 0) {
      throw new Error(scanErrors.map((error) =>
        `[${error.code}] ${error.path}: ${error.message}`).join('\n'));
    }
  } finally {
    await rm(extractionRoot, { force: true, recursive: true });
  }
}

async function main() {
  const options = parseArguments(process.argv.slice(2));
  const policy = await readJson(path.join(options.repoRoot, 'compliance', 'licensing-policy.json'));
  const version = options.version
    ?? (await readFile(path.join(options.repoRoot, 'VERSION'), 'utf8')).trim();
  if (!/^v\d+\.\d+\.\d+([-.+][0-9A-Za-z.-]+)?$/.test(version)) {
    throw new Error(`Version must be v-prefixed semantic version text, got ${version}`);
  }

  const revision = await git(options.repoRoot, ['rev-parse', `${options.revision}^{commit}`]);
  if (!/^[0-9a-f]{40}$/.test(revision)) {
    throw new Error(`Could not resolve an immutable commit SHA from ${options.revision}`);
  }

  const trackedFiles = (await git(options.repoRoot, ['ls-tree', '-r', '--name-only', revision]))
    .split(/\r?\n/)
    .filter(Boolean)
    .map(normalizeRelativePath);

  for (const patternText of policy.publication.deniedTrackedPathPatterns) {
    const pattern = new RegExp(patternText, 'i');
    const prohibitedPath = trackedFiles.find((filePath) => pattern.test(filePath));
    if (prohibitedPath) {
      throw new Error(`Corresponding-source archive contains prohibited path ${prohibitedPath} (${patternText})`);
    }
  }

  for (const requiredPath of policy.publication.requiredArchivePaths) {
    if (!trackedFiles.some((filePath) => filePath === requiredPath || filePath.startsWith(`${requiredPath}/`))) {
      throw new Error(`Corresponding-source archive is missing required path ${requiredPath}`);
    }
  }

  const outputDirectory = path.resolve(options.repoRoot, options.outputDirectory);
  await mkdir(outputDirectory, { recursive: true });
  const archiveName = `PrintFarmer-${version}-source.tar.gz`;
  const archivePath = path.join(outputDirectory, archiveName);
  await execFileAsync('git', [
    'archive',
    '--format=tar.gz',
    `--prefix=PrintFarmer-${version}/`,
    `--output=${archivePath}`,
    revision,
  ], {
    cwd: options.repoRoot,
  });
  await scanSourceArchive(archivePath, policy.publication.secretPatterns);

  const commitTimestamp = await git(options.repoRoot, ['show', '-s', '--format=%cI', revision]);
  const releaseBaseUrl = `${policy.repositoryUrl}/releases/download/${version}`;
  const sourceManifest = {
    schemaVersion: 1,
    name: 'PrintFarmer',
    version,
    revision,
    licenseExpression: policy.licenseExpression,
    repositoryUrl: policy.repositoryUrl,
    sourceTreeUrl: `${policy.repositoryUrl}/tree/${revision}`,
    sourceArchiveUrl: `${releaseBaseUrl}/${archiveName}`,
    sourceArchiveSha256: await sha256File(archivePath),
    sbomUrl: `${releaseBaseUrl}/printfarmer-${version}.spdx.json`,
    noticesUrl: `${policy.repositoryUrl}/blob/${revision}/THIRD-PARTY-NOTICES.md`,
    createdAt: commitTimestamp,
  };

  const manifestName = `PrintFarmer-${version}-source.json`;
  const manifestPath = path.join(outputDirectory, manifestName);
  await writeFile(manifestPath, `${JSON.stringify(sourceManifest, undefined, 2)}\n`, 'utf8');

  const publicationPaths = [
    normalizeRelativePath(path.relative(options.repoRoot, manifestPath)),
    'LICENSE',
    'THIRD-PARTY-NOTICES.md',
    'compliance/calibration-provenance.json',
    'compliance/dependency-license-policy.json',
    'compliance/licensing-policy.json',
  ];
  const scanErrors = await scanPublicationFiles(
    options.repoRoot,
    publicationPaths,
    policy.publication.secretPatterns,
  );
  if (scanErrors.length > 0) {
    throw new Error(scanErrors.map((error) =>
      `[${error.code}] ${error.path}: ${error.message}`).join('\n'));
  }

  process.stdout.write(`${JSON.stringify({
    archive: normalizeRelativePath(path.relative(options.repoRoot, archivePath)),
    manifest: normalizeRelativePath(path.relative(options.repoRoot, manifestPath)),
    revision,
    sha256: sourceManifest.sourceArchiveSha256,
    version,
  })}\n`);
}

if (path.resolve(process.argv[1] ?? '') === fileURLToPath(import.meta.url)) {
  main().catch((error) => {
    process.stderr.write(`${error.stack ?? error.message}\n`);
    process.exitCode = 1;
  });
}

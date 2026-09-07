#!/usr/bin/env node

import { mkdir, writeFile } from 'node:fs/promises';
import { realpathSync } from 'node:fs';
import path from 'node:path';
import process from 'node:process';
import { fileURLToPath } from 'node:url';

const fullCommitShaPattern = /^[0-9a-f]{40}$/i;
const imageDigestPattern = /^(?:[^@\s]+@)?sha256:[0-9a-f]{64}$/i;
const invalidCommitValues = new Set(['dev', 'unknown']);
const defaultTimeoutMs = 10_000;

function requiredString(value, field, source) {
  if (typeof value !== 'string' || value.trim() === '') {
    throw new Error(`${source} has a missing or invalid '${field}' field.`);
  }
  return value.trim();
}

function validTimestamp(value, field, source) {
  const timestamp = requiredString(value, field, source);
  if (Number.isNaN(Date.parse(timestamp))) {
    throw new Error(`${source} has an invalid '${field}' timestamp.`);
  }
  return timestamp;
}

function validateCommit(value, source) {
  const commit = requiredString(value, 'commit', source);
  if (invalidCommitValues.has(commit.toLowerCase())) {
    throw new Error(`${source} reports the non-deployable commit '${commit}'.`);
  }
  if (!fullCommitShaPattern.test(commit)) {
    throw new Error(`${source} commit must be a full 40-character hexadecimal SHA.`);
  }
  return commit.toLowerCase();
}

function normalizeBaseUrl(value) {
  let baseUrl;
  try {
    baseUrl = new URL(value);
  } catch {
    throw new Error(`Invalid base URL '${value}'.`);
  }
  if (!['http:', 'https:'].includes(baseUrl.protocol)) {
    throw new Error('Base URL must use http or https.');
  }
  if (baseUrl.username || baseUrl.password) {
    throw new Error('Base URL must not contain credentials.');
  }
  if (baseUrl.search || baseUrl.hash) {
    throw new Error('Base URL must not contain a query string or fragment.');
  }
  if (!baseUrl.pathname.endsWith('/')) {
    baseUrl.pathname += '/';
  }
  return baseUrl;
}

function normalizeImageDigests(imageDigests) {
  if (typeof imageDigests !== 'object' || imageDigests === null || Array.isArray(imageDigests)) {
    throw new Error('Image digests must be a service-to-digest object.');
  }

  const normalized = {};
  for (const [service, digest] of Object.entries(imageDigests)) {
    if (!/^[a-z0-9][a-z0-9-]*$/i.test(service) || !imageDigestPattern.test(digest)) {
      throw new Error(`Invalid image digest for service '${service}'.`);
    }
    normalized[service] = digest;
  }
  return normalized;
}

async function fetchResponse(url, label, fetchImpl, timeoutMs) {
  let response;
  try {
    response = await fetchImpl(url, {
      headers: {
        Accept: label === 'nginx index'
          ? 'text/html'
          : label === 'frontend bundle'
            ? 'application/javascript'
            : 'application/json',
      },
      redirect: 'manual',
      signal: AbortSignal.timeout(timeoutMs),
    });
  } catch (error) {
    throw new Error(`${label} request failed at ${url}: ${error.message}`);
  }

  if (response.redirected || (response.status >= 300 && response.status < 400)) {
    throw new Error(`${label} must not redirect from ${url}.`);
  }

  const resolvedUrl = new URL(response.url || url);
  if (resolvedUrl.href !== url.href) {
    throw new Error(`${label} resolved to an unexpected URL.`);
  }
  if (!response.ok) {
    throw new Error(`${label} request failed at ${url}: HTTP ${response.status}.`);
  }
  return response;
}

async function fetchJson(url, label, fetchImpl, timeoutMs) {
  const response = await fetchResponse(url, label, fetchImpl, timeoutMs);
  let payload;
  try {
    payload = JSON.parse(await response.text());
  } catch {
    throw new Error(`${label} returned malformed JSON.`);
  }
  if (typeof payload !== 'object' || payload === null || Array.isArray(payload)) {
    throw new Error(`${label} returned a malformed payload.`);
  }
  return payload;
}

async function resolveBundleUrl(baseUrl, fetchImpl, timeoutMs) {
  const response = await fetchResponse(baseUrl, 'nginx index', fetchImpl, timeoutMs);
  const html = await response.text();
  const scriptSources = [...html.matchAll(
    /<script\b[^>]*\bsrc\s*=\s*["']([^"']+)["'][^>]*>/gi,
  )].map((match) => match[1]);
  const bundleSource = scriptSources.find((source) =>
    /(?:^|\/)index-[^/?#]+\.js(?:[?#].*)?$/i.test(source));
  if (!bundleSource) {
    throw new Error('nginx index does not reference an index-*.js bundle.');
  }

  const bundleUrl = new URL(bundleSource, response.url || baseUrl);
  if (bundleUrl.origin !== baseUrl.origin) {
    throw new Error('nginx index references its index-*.js bundle on another origin.');
  }
  await fetchResponse(bundleUrl, 'frontend bundle', fetchImpl, timeoutMs);
  return bundleUrl.href;
}

export async function verifyAcceptanceProvenance({
  expectedSha,
  baseUrl: baseUrlValue,
  evidenceDir,
  imageDigests = {},
  fetchImpl = fetch,
  timeoutMs = defaultTimeoutMs,
  now = () => new Date(),
}) {
  const normalizedExpectedSha = validateCommit(expectedSha, 'expected SHA');
  const baseUrl = normalizeBaseUrl(baseUrlValue);
  const normalizedImageDigests = normalizeImageDigests(imageDigests);
  if (!Number.isSafeInteger(timeoutMs) || timeoutMs <= 0) {
    throw new Error('Timeout must be a positive integer number of milliseconds.');
  }

  const frontendUrl = new URL('version.json', baseUrl);
  const apiUrl = new URL('api/system/version', baseUrl);
  const [frontendPayload, apiPayload, bundleUrl] = await Promise.all([
    fetchJson(frontendUrl, 'frontend version endpoint', fetchImpl, timeoutMs),
    fetchJson(apiUrl, 'API version endpoint', fetchImpl, timeoutMs),
    resolveBundleUrl(baseUrl, fetchImpl, timeoutMs),
  ]);

  const frontendCommit = validateCommit(frontendPayload.commit, 'frontend version endpoint');
  const apiCommit = validateCommit(apiPayload.commit, 'API version endpoint');
  if (frontendCommit !== normalizedExpectedSha) {
    throw new Error(
      `Frontend commit ${frontendCommit} does not match expected SHA ${normalizedExpectedSha}.`,
    );
  }
  if (apiCommit !== normalizedExpectedSha) {
    throw new Error(
      `API commit ${apiCommit} does not match expected SHA ${normalizedExpectedSha}.`,
    );
  }

  const verifiedAt = now().toISOString();
  const evidence = {
    schemaVersion: 1,
    verifiedAt,
    baseUrl: baseUrl.href,
    expectedCommit: normalizedExpectedSha,
    frontend: {
      url: frontendUrl.href,
      commit: frontendCommit,
      buildTime: validTimestamp(
        frontendPayload.buildTime,
        'buildTime',
        'frontend version endpoint',
      ),
      bundleUrl,
    },
    api: {
      url: apiUrl.href,
      commit: apiCommit,
      environment: requiredString(
        apiPayload.environment,
        'environment',
        'API version endpoint',
      ),
    },
    imageDigests: normalizedImageDigests,
  };

  const outputDirectory = path.resolve(evidenceDir);
  const timestamp = verifiedAt.replace(/[:.]/g, '-');
  const evidencePath = path.join(
    outputDirectory,
    `acceptance-provenance-${timestamp}-${normalizedExpectedSha.slice(0, 12)}.json`,
  );
  await mkdir(outputDirectory, { recursive: true });
  await writeFile(evidencePath, `${JSON.stringify(evidence, undefined, 2)}\n`, {
    encoding: 'utf8',
    flag: 'wx',
  });

  return { evidence, evidencePath };
}

function usage() {
  return [
    'Usage: node scripts/ci/verify-acceptance-provenance.mjs \\',
    '  --expected-sha <40-character-sha> \\',
    '  --base-url <nginx-origin> \\',
    '  [--evidence-dir <directory>] \\',
    '  [--image-digest <service=sha256:digest>]...',
  ].join('\n');
}

export function parseArguments(argv) {
  const argumentsByName = {
    imageDigests: {},
  };
  for (let index = 0; index < argv.length; index += 1) {
    const argument = argv[index];
    if (argument === '--help') {
      return { help: true };
    }
    const value = argv[index + 1];
    if (value === undefined || value.startsWith('--')) {
      throw new Error(`Missing value for ${argument}.\n${usage()}`);
    }
    if (argument === '--expected-sha') {
      argumentsByName.expectedSha = value;
    } else if (argument === '--base-url') {
      argumentsByName.baseUrl = value;
    } else if (argument === '--evidence-dir') {
      argumentsByName.evidenceDir = value;
    } else if (argument === '--image-digest') {
      const separator = value.indexOf('=');
      const service = separator > 0 ? value.slice(0, separator) : '';
      const digest = separator > 0 ? value.slice(separator + 1) : '';
      if (!/^[a-z0-9][a-z0-9-]*$/i.test(service) || !imageDigestPattern.test(digest)) {
        throw new Error(
          `Invalid image digest '${value}'; expected service=sha256:<64-hex> ` +
          'or service=repository@sha256:<64-hex>.',
        );
      }
      argumentsByName.imageDigests[service] = digest;
    } else {
      throw new Error(`Unknown argument '${argument}'.\n${usage()}`);
    }
    index += 1;
  }

  if (!argumentsByName.expectedSha || !argumentsByName.baseUrl) {
    throw new Error(`--expected-sha and --base-url are required.\n${usage()}`);
  }
  return argumentsByName;
}

async function main() {
  const args = parseArguments(process.argv.slice(2));
  if (args.help) {
    process.stdout.write(`${usage()}\n`);
    return;
  }

  const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
  const evidenceDir = args.evidenceDir
    ?? path.join(repoRoot, 'acceptance-evidence');
  const result = await verifyAcceptanceProvenance({
    ...args,
    evidenceDir,
  });
  process.stdout.write(
    `Acceptance provenance verified for ${result.evidence.expectedCommit}. ` +
    `Evidence: ${result.evidencePath}\n`,
  );
}

const invokedPath = process.argv[1]
  ? realpathSync(path.resolve(process.argv[1]))
  : undefined;
if (invokedPath && realpathSync(fileURLToPath(import.meta.url)) === invokedPath) {
  main().catch((error) => {
    console.error(`Acceptance provenance verification failed: ${error.message}`);
    process.exitCode = 1;
  });
}

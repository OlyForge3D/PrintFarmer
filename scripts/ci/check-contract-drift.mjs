#!/usr/bin/env node
// =============================================================================
// check-contract-drift.mjs — CI contract-drift gate (issue #2243, parent epic
// #2237).
//
// Compares the checked-in wire-contract corpus (`fixtures/wire-contracts/`,
// produced by real production serialization per issue #2238 — never
// hand-written) against a reviewed exception allowlist
// (`scripts/ci/contract-drift-exceptions.json`) and the PR's own diff, and
// fails the gate on any unexplained finding. Three checks, each independently
// testable (see scripts/ci/tests/test-check-contract-drift.mjs):
//
//   1. PROPERTY-CASING DRIFT. Every PrintFarmer DTO fixture under
//      `fixtures/wire-contracts/api/**` must use camelCase property names
//      (the project's global JSON casing policy — see
//      `.github/instructions/*` "Serialization Rules"). A non-camelCase key
//      is a finding unless a reviewed allowlist entry names that exact
//      boundary. This is what "name, casing ... mismatches between C# DTOs
//      ... and TypeScript/Swift consumers" reduces to at the corpus level:
//      the corpus IS the verified producer shape (#2238), so a casing
//      violation here is a violation of the producer's own documented
//      contract, not a guess about a consumer.
//
//   2. UNALLOWLISTED NATIVE BOUNDARY. Every family under
//      `fixtures/wire-contracts/native-slicer/**` is inherently snake_case
//      (OrcaSlicer's own wire format) and MUST be allowlisted explicitly —
//      never silently accepted — per #2238/#2243's "legitimate snake_case
//      boundaries ... must be allowlisted as boundaries, not silently
//      normalised."
//
//   3. FIXTURE-EDIT-WITHOUT-PRODUCER-CHANGE (the #2232 regression shape).
//      #2232 shipped because a client-side test mock asserted a hand-written
//      shape instead of what the server actually emits, and stayed green
//      while the two drifted apart. Structurally, the same failure mode
//      against today's corpus-driven tests would be: a fixture file under
//      `fixtures/wire-contracts/**` is edited by hand in a PR with no
//      accompanying change to the producer side (backend/worker source, or
//      the .NET test that calls `WireContractFixtureWriter` to regenerate
//      it from real serialization). This check is diff-based: it reads the
//      PR's changed-path list and fails if any fixture JSON changed without
//      a producer-side file changing alongside it.
//
// Every exception entry in the allowlist is validated for shape: it must
// carry `boundary`, `rationale`, `owner`, and at least one of
// `reviewTrigger`/`expiry` (acceptance criterion: "every exception carries a
// boundary, rationale, owner, and review trigger/expiry"). A past `expiry`
// is itself reported as a finding, so a stale exception doesn't silently
// keep suppressing real drift forever.
//
// This script does NOT re-implement #2242's OpenAPI/enum-fidelity assertions
// (those already run as real .NET tests against the live OpenAPI document)
// and does NOT diff TypeScript/Swift source against the corpus field-by-field
// — the corpus-driven Vitest/XCTest suites added by #2240/#2241 already prove
// that at runtime, which is strictly stronger evidence than a static text
// comparison could offer. This script's job is the structural corpus
// self-consistency + producer-coupling gate described above, which is the
// concrete, machine-checkable core of #2243's acceptance criteria.
// =============================================================================

import { readFile, readdir } from 'node:fs/promises';
import path from 'node:path';
import process from 'node:process';
import { fileURLToPath } from 'node:url';

export const CAMEL_CASE_RE = /^[a-z][a-zA-Z0-9]*$/;

/**
 * File-path prefixes (relative to the repo root) that count as "producer
 * side" for the fixture-edit-without-producer-change check: backend/worker
 * source that could plausibly have changed serialization, or a .NET test
 * file (which is how a fixture is legitimately regenerated, via
 * `WireContractFixtureWriter` + `WIRE_CONTRACT_REGEN=1`). Deliberately
 * excludes `src/Web/ReactApp/**` and `mobile/**` — those are consumers, and a
 * consumer-only change can never justify editing the producer's corpus.
 */
export const PRODUCER_PATH_PATTERNS = [
  /^src\/api\//,
  /^src\/backends\//,
  /^src\/slicer\//,
  /^src\/discovery\//,
  /^src\/migrations\//,
  /^src\/tests\/.*\.cs$/,
];

/**
 * Fixture-corpus file names that are metadata, not payload files, and are
 * exempt from every structural check below. Listed corpus-relative (relative
 * to `fixtures/wire-contracts/` itself); derived below into both a
 * corpus-relative set (for `walkJsonFiles`, which computes paths relative to
 * the corpus root) and a repo-root-relative set (for
 * `checkFixtureProducerCoupling`, which operates on the PR's raw changed-path
 * list). `README.md` is listed for documentation even though
 * `.endsWith('.json')` already excludes it from the walk.
 */
const NON_PAYLOAD_CORPUS_RELATIVE_NAMES = ['manifest.json', 'README.md'];
const NON_PAYLOAD_CORPUS_FILES = new Set(NON_PAYLOAD_CORPUS_RELATIVE_NAMES);
const NON_PAYLOAD_CORPUS_FILES_REPO_RELATIVE = new Set(
  NON_PAYLOAD_CORPUS_RELATIVE_NAMES.map((name) => `fixtures/wire-contracts/${name}`),
);

/**
 * Recursively collects every JSON object property name found in `value`
 * (arrays are walked but contribute no keys of their own; string/number/bool
 * leaves are never treated as keys). Mirrors how a JSON structural diff would
 * see property names, independent of value content.
 */
export function collectObjectKeys(value, out = []) {
  if (Array.isArray(value)) {
    for (const item of value) {
      collectObjectKeys(item, out);
    }
    return out;
  }
  if (value !== null && typeof value === 'object') {
    for (const [key, child] of Object.entries(value)) {
      out.push(key);
      collectObjectKeys(child, out);
    }
  }
  return out;
}

/**
 * Derives the corpus "family" from a fixture's manifest-relative path, e.g.
 * `api/slicer-profiles/profiles.populated.json` -> `{ root: "api", family:
 * "slicer-profiles" }`, `native-slicer/filament/minimal.json` ->
 * `{ root: "native-slicer", family: "filament" }`. Returns null for a path
 * that doesn't have at least `<root>/<family>/<file>` shape.
 */
export function parseFixtureFamily(manifestRelativePath) {
  const segments = manifestRelativePath.split('/');
  if (segments.length < 3) {
    return null;
  }
  const [root, family] = segments;
  if (root !== 'api' && root !== 'native-slicer') {
    return null;
  }
  return { root, family };
}

/** Boundary key for an allowlist entry that exempts an entire native-slicer family, e.g. `native-slicer:filament`. */
export function nativeFamilyBoundary(family) {
  return `native-slicer:${family}`;
}

/** Boundary key for an allowlist entry that exempts one specific non-camelCase key within an api family, e.g. `api:tasks#task_id`. */
export function apiKeyBoundary(family, key) {
  return `api:${family}#${key}`;
}

/** Boundary key for an allowlist entry that exempts an entire api family from casing checks, e.g. `api:tasks`. */
export function apiFamilyBoundary(family) {
  return `api:${family}`;
}

const REQUIRED_EXCEPTION_FIELDS = ['boundary', 'rationale', 'owner'];

/**
 * Validates the reviewed exception allowlist's own shape. Every entry must
 * carry `boundary`, `rationale`, `owner`, and at least one of
 * `reviewTrigger`/`expiry`; a past `expiry` (an ISO `YYYY-MM-DD` string) is
 * also reported so a stale exception forces re-review instead of silently
 * suppressing drift forever.
 */
export function checkAllowlistShape(allowlist, now = new Date()) {
  const findings = [];
  const seenBoundaries = new Set();
  for (const [index, entry] of allowlist.entries()) {
    const label = entry && typeof entry.boundary === 'string' ? entry.boundary : `entry[${index}]`;
    for (const field of REQUIRED_EXCEPTION_FIELDS) {
      if (typeof entry?.[field] !== 'string' || entry[field].trim() === '') {
        findings.push(`Exception allowlist entry '${label}' is missing required field '${field}'.`);
      }
    }
    const hasReviewTrigger = typeof entry?.reviewTrigger === 'string' && entry.reviewTrigger.trim() !== '';
    const hasExpiry = typeof entry?.expiry === 'string' && entry.expiry.trim() !== '';
    if (!hasReviewTrigger && !hasExpiry) {
      findings.push(`Exception allowlist entry '${label}' has neither 'reviewTrigger' nor 'expiry'; every exception must carry at least one.`);
    }
    if (hasExpiry) {
      const expiryDate = new Date(`${entry.expiry}T00:00:00Z`);
      if (Number.isNaN(expiryDate.getTime())) {
        findings.push(`Exception allowlist entry '${label}' has an unparsable 'expiry' value '${entry.expiry}' (expected YYYY-MM-DD).`);
      } else if (expiryDate.getTime() < now.getTime()) {
        findings.push(`Exception allowlist entry '${label}' expired on ${entry.expiry} and must be re-reviewed (renewed or removed).`);
      }
    }
    if (typeof entry?.boundary === 'string') {
      if (seenBoundaries.has(entry.boundary)) {
        findings.push(`Exception allowlist has a duplicate boundary '${entry.boundary}'.`);
      }
      seenBoundaries.add(entry.boundary);
    }
  }
  return findings;
}

/**
 * Check 1 + 2: property-casing drift under `api/**` and unallowlisted native
 * boundaries under `native-slicer/**`.
 *
 * @param {Map<string,unknown>} fixtures manifest-relative path -> parsed JSON payload
 * @param {Set<string>} allowlistBoundaries every `boundary` string present in the reviewed allowlist
 */
export function checkFixtureBoundaries(fixtures, allowlistBoundaries) {
  const findings = [];
  const flaggedNativeFamilies = new Set();

  for (const [relativePath, payload] of fixtures) {
    const parsed = parseFixtureFamily(relativePath);
    if (parsed === null) {
      continue;
    }

    if (parsed.root === 'native-slicer') {
      const boundary = nativeFamilyBoundary(parsed.family);
      if (!allowlistBoundaries.has(boundary) && !flaggedNativeFamilies.has(parsed.family)) {
        flaggedNativeFamilies.add(parsed.family);
        findings.push(
          `native-slicer fixture family '${parsed.family}' (e.g. '${relativePath}') uses the native snake_case ` +
          `wire format but has no reviewed allowlist entry for boundary '${boundary}'. Legitimate snake_case ` +
          `boundaries must be allowlisted explicitly, not silently accepted.`,
        );
      }
      continue;
    }

    // parsed.root === 'api': every property key must be camelCase unless the
    // specific key or the whole family is on the reviewed allowlist.
    const keys = collectObjectKeys(payload);
    const familyBoundary = apiFamilyBoundary(parsed.family);
    if (allowlistBoundaries.has(familyBoundary)) {
      continue;
    }
    for (const key of new Set(keys)) {
      if (CAMEL_CASE_RE.test(key)) {
        continue;
      }
      const keyBoundary = apiKeyBoundary(parsed.family, key);
      if (allowlistBoundaries.has(keyBoundary)) {
        continue;
      }
      findings.push(
        `api fixture '${relativePath}' has non-camelCase property '${key}' with no reviewed allowlist entry ` +
        `for boundary '${keyBoundary}' (or family-level '${familyBoundary}').`,
      );
    }
  }

  return findings;
}

/**
 * Check 0 (structural sanity, not a corpus-content check): the corpus walk
 * must find at least one payload fixture. An empty result almost always
 * means the corpus path was misconfigured or the corpus directory itself
 * went missing — treating that as a silent pass ("0 fixtures checked, 0
 * findings") would be a fail-open gate masquerading as a clean run.
 *
 * @param {Map<string,unknown>} fixtures
 */
export function checkCorpusNonEmpty(fixtures) {
  if (fixtures.size === 0) {
    return [
      'No payload fixtures were found under fixtures/wire-contracts/. This gate cannot distinguish ' +
      '"legitimately empty corpus" from "corpus path misconfigured or corpus directory missing", so it ' +
      'fails closed rather than silently reporting 0 fixtures checked as a clean pass.',
    ];
  }
  return [];
}

/**
 * Check 3: a fixture JSON file changed in the diff with no producer-side
 * file (backend/worker source, or a .NET test file that could regenerate it)
 * changed alongside it — the #2232 regression shape reintroduced against the
 * corpus-driven test suite.
 *
 * @param {string[]} changedPaths repo-root-relative paths changed in the diff being evaluated
 */
export function checkFixtureProducerCoupling(changedPaths) {
  const changedFixtures = changedPaths.filter(
    (p) => p.startsWith('fixtures/wire-contracts/') && p.endsWith('.json') && !NON_PAYLOAD_CORPUS_FILES_REPO_RELATIVE.has(p),
  );
  if (changedFixtures.length === 0) {
    return [];
  }

  const hasProducerChange = changedPaths.some((p) => PRODUCER_PATH_PATTERNS.some((re) => re.test(p)));
  if (hasProducerChange) {
    return [];
  }

  return [
    'Fixture file(s) changed under fixtures/wire-contracts/ with no accompanying producer-side change ' +
    '(no src/api, src/backends, src/slicer, src/discovery, src/migrations, or src/tests/**/*.cs file in the ' +
    `diff). This is the #2232 regression shape: ${changedFixtures.join(', ')}.`,
  ];
}

/**
 * An allowlist entry is "active" — able to suppress a boundary finding — only
 * if it passes the same shape/expiry validation `checkAllowlistShape` reports
 * on. A malformed or expired entry must not go on quietly suppressing the
 * drift it names: `checkAllowlistShape` still reports it as its own finding,
 * but the underlying casing/native-boundary violation must also reappear,
 * per the acceptance criterion that an expired exception's suppression
 * lapses rather than persisting silently.
 */
function isEntryActive(entry, now) {
  if (typeof entry?.boundary !== 'string' || entry.boundary.trim() === '') {
    return false;
  }
  for (const field of REQUIRED_EXCEPTION_FIELDS) {
    if (typeof entry?.[field] !== 'string' || entry[field].trim() === '') {
      return false;
    }
  }
  const hasReviewTrigger = typeof entry?.reviewTrigger === 'string' && entry.reviewTrigger.trim() !== '';
  const hasExpiry = typeof entry?.expiry === 'string' && entry.expiry.trim() !== '';
  if (!hasReviewTrigger && !hasExpiry) {
    return false;
  }
  if (hasExpiry) {
    const expiryDate = new Date(`${entry.expiry}T00:00:00Z`);
    if (Number.isNaN(expiryDate.getTime()) || expiryDate.getTime() < now.getTime()) {
      return false;
    }
  }
  return true;
}

/** Boundary strings from every allowlist entry that is currently valid and unexpired. */
export function activeAllowlistBoundaries(allowlist, now = new Date()) {
  return new Set(allowlist.filter((entry) => isEntryActive(entry, now)).map((entry) => entry.boundary));
}

/**
 * Runs every check and returns a flat findings list plus the boolean the CLI
 * uses for its exit code. Pure — no filesystem/process access.
 */
export function evaluateDrift({ allowlist, fixtures, changedPaths, now = new Date() }) {
  const findings = [
    ...checkCorpusNonEmpty(fixtures),
    ...checkAllowlistShape(allowlist, now),
    ...checkFixtureBoundaries(fixtures, activeAllowlistBoundaries(allowlist, now)),
    ...checkFixtureProducerCoupling(changedPaths ?? []),
  ];
  return { findings, ok: findings.length === 0 };
}

// -----------------------------------------------------------------------------
// CLI wrapper — the only part of this file that touches the filesystem.
// -----------------------------------------------------------------------------

async function walkJsonFiles(root, relativeRoot) {
  /** @type {Map<string,unknown>} */
  const out = new Map();

  async function walk(dir) {
    let entries;
    try {
      entries = await readdir(dir, { withFileTypes: true });
    } catch (err) {
      if (err.code === 'ENOENT') {
        return;
      }
      throw err;
    }
    for (const entry of entries) {
      const fullPath = path.join(dir, entry.name);
      if (entry.isDirectory()) {
        await walk(fullPath);
      } else if (entry.isFile() && entry.name.endsWith('.json')) {
        const relative = path.relative(relativeRoot, fullPath).split(path.sep).join('/');
        if (NON_PAYLOAD_CORPUS_FILES.has(relative)) {
          continue;
        }
        const text = await readFile(fullPath, 'utf8');
        out.set(relative, JSON.parse(text));
      }
    }
  }

  await walk(root);
  return out;
}

async function loadAllowlist(allowlistPath) {
  try {
    const text = await readFile(allowlistPath, 'utf8');
    const parsed = JSON.parse(text);
    if (!Array.isArray(parsed)) {
      throw new Error(`Exception allowlist at '${allowlistPath}' must be a JSON array.`);
    }
    return parsed;
  } catch (err) {
    if (err.code === 'ENOENT') {
      return [];
    }
    throw err;
  }
}

async function loadChangedPaths(changedFilePath) {
  if (!changedFilePath) {
    return null;
  }
  let raw;
  try {
    raw = await readFile(changedFilePath, 'utf8');
  } catch (err) {
    if (err.code === 'ENOENT') {
      return null;
    }
    throw err;
  }
  return raw.split('\0').filter((p) => p.length > 0);
}

async function main() {
  const args = process.argv.slice(2);
  const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
  const corpusRoot = path.join(repoRoot, 'fixtures', 'wire-contracts');
  const allowlistPath = path.join(repoRoot, 'scripts', 'ci', 'contract-drift-exceptions.json');

  let changedFileArg = null;
  for (let i = 0; i < args.length; i += 1) {
    if (args[i] === '--changed-file') {
      changedFileArg = args[i + 1] ?? null;
      i += 1;
    }
  }
  // Fall back to the same env var name compute-change-set.sh's GITHUB_OUTPUT
  // contract exposes (`changed_file`), so the workflow can pass it through
  // without inventing a second convention.
  const changedFilePath = changedFileArg ?? process.env.CONTRACT_DRIFT_CHANGED_FILE ?? null;

  const [fixtures, allowlist, changedPaths] = await Promise.all([
    walkJsonFiles(corpusRoot, corpusRoot),
    loadAllowlist(allowlistPath),
    loadChangedPaths(changedFilePath),
  ]);

  if (changedPaths === null) {
    // No changed-path evidence available (e.g. a local ad-hoc run, or CI
    // couldn't compute a diff) — still run the structural checks, but skip
    // the diff-dependent producer-coupling check rather than either
    // silently passing it or failing for a reason unrelated to this PR.
    console.warn(
      'check-contract-drift: no changed-path file available; skipping the fixture-edit-without-producer-change check.',
    );
  }

  const { findings, ok } = evaluateDrift({
    allowlist,
    fixtures,
    changedPaths: changedPaths ?? [],
  });

  if (ok) {
    console.log(`check-contract-drift: OK — ${fixtures.size} fixture(s) checked, 0 unexplained findings.`);
    process.exit(0);
  }

  console.error(`check-contract-drift: FAILED — ${findings.length} unexplained finding(s):`);
  for (const finding of findings) {
    console.error(`  - ${finding}`);
  }
  process.exit(1);
}

const isMainModule = process.argv[1] && fileURLToPath(import.meta.url) === path.resolve(process.argv[1]);
if (isMainModule) {
  main().catch((err) => {
    console.error('check-contract-drift: unexpected error:', err);
    process.exit(1);
  });
}

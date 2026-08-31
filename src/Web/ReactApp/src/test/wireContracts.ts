/**
 * Read-only loader for the canonical wire-contract corpus at
 * `fixtures/wire-contracts/` (issue #2238), used to drive React tests from
 * real serialized backend/worker payloads instead of hand-written mocks
 * (issue #2240).
 *
 * Rules (see `fixtures/wire-contracts/README.md`):
 *  - Fixtures are produced by `WireContractFixtureWriter.CaptureOrVerifyAsync`
 *    on the .NET side and checked in. They are NEVER hand-edited here.
 *  - A fixture must be consumed byte-identical: do not rename keys, fill in
 *    absent optional fields, or normalize casing/shape before asserting on
 *    it. Silently "fixing up" a payload before the assertion is exactly the
 *    class of bug (#2232) this corpus exists to catch.
 *  - `api/**` and `native-slicer/**` are separate wire boundaries and must
 *    never be merged or cross-mapped by test helpers.
 *
 * Every fixture must have a `manifest.json` entry; loading an unregistered
 * path throws instead of silently reading an untracked file, so a typo'd
 * corpus path fails loudly in the failing test rather than passing on
 * accidental disk content.
 */
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';

/**
 * Corpus root, relative to the Vitest process CWD. Per
 * `fixtures/wire-contracts/README.md`, that CWD is `src/Web/ReactApp/` (the
 * directory every `npm run test:run` invocation in this repo is documented to
 * run from), so three levels up reaches the repo root.
 */
const CORPUS_ROOT = resolve(process.cwd(), '../../../fixtures/wire-contracts');

export interface WireContractManifestEntry {
  Path: string;
  Endpoint: string;
  ProducingTest: string;
  SchemaVersion: string;
  RefreshCommit: string;
  [key: string]: unknown;
}

let manifestCache: WireContractManifestEntry[] | null = null;

function readManifest(): WireContractManifestEntry[] {
  if (!manifestCache) {
    const raw = readFileSync(resolve(CORPUS_ROOT, 'manifest.json'), 'utf-8');
    const parsed: unknown = JSON.parse(raw);
    manifestCache = Array.isArray(parsed) ? (parsed as WireContractManifestEntry[]) : [];
  }
  return manifestCache;
}

/** Returns the full manifest so a test can assert on provenance metadata directly. */
export function loadWireContractManifest(): WireContractManifestEntry[] {
  return readManifest();
}

/** Looks up a single fixture's manifest entry (endpoint, producing test, schema version, etc). */
export function getWireContractManifestEntry(
  relativePath: string
): WireContractManifestEntry | undefined {
  return readManifest().find((entry) => entry.Path === relativePath);
}

/**
 * Loads and parses a single fixture file by its corpus-relative path, e.g.
 * `api/tasks/tasks.populated.json`. The manifest entry must exist; this is a
 * guard against typo'd paths silently falling through to an unrelated read,
 * not a normalization step. The parsed JSON is returned unchanged — assign it
 * straight into the mock the test drives, without adding, renaming, or
 * reshaping any field.
 */
export function loadWireContractFixture<T = unknown>(relativePath: string): T {
  const entry = getWireContractManifestEntry(relativePath);
  if (!entry) {
    throw new Error(
      `Wire-contract fixture "${relativePath}" has no manifest.json entry under ` +
        `${CORPUS_ROOT}. This loader refuses to read untracked fixture files — if the ` +
        `fixture should exist, that is a gap in the corpus (file a finding issue against ` +
        'issue #2238), not something to add here.'
    );
  }
  const raw = readFileSync(resolve(CORPUS_ROOT, relativePath), 'utf-8');
  return JSON.parse(raw) as T;
}

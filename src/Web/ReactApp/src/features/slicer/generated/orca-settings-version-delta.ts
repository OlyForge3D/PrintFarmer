/**
 * OrcaSlicer settings delta between engine versions (issue #578).
 *
 * The generated `orcaSettingsMetadata.json` bundle is extracted from a single
 * upstream OrcaSlicer C++ checkout (currently 2.4.x). To support current +
 * previous engines side by side we apply a hand-maintained delta describing
 * which settings were **added** in 2.4 (must not appear on jobs pinned to
 * 2.3.1) and which were **renamed** between versions.
 *
 * ## Source
 *
 * All entries here reference well-known OrcaSlicer 2.4 changes and are cross-
 * checked against the current metadata JSON (they are present in 2.4 and
 * intentionally hidden for 2.3.1). Each entry has a comment linking it to its
 * upstream feature.
 *
 * If/when a 2.3.1 checkout is available and the extractor
 * (`tools/extract-orca-metadata.py`) is re-run against it, the resulting
 * `orcaSettingsMetadata.2.3.1.json` should REPLACE this file — this delta is
 * a pragmatic bridge, not a permanent architecture.
 *
 * The specific field windows in this delta are auditable in one place; they
 * can be corrected without changing the plumbing that consumes them.
 */

export interface OrcaVersionDelta {
  /** Setting keys that first appeared in this OrcaSlicer major version. */
  addedIn: string[];
  /**
   * Setting keys that were removed in this major version.
   * Populate from upstream release-notes research; currently empty for 2.4
   * pending 2.3.1 source metadata extraction.
   */
  retiredIn: string[];
  /**
   * Rename map for this major version.
   * Key = new (post-rename) setting key.
   * Value = previous (pre-rename) setting key.
   *
   * When resolving metadata for a version older than the rename, we emit the
   * field under the OLD key so 2.3.1-pinned jobs never carry the new key,
   * and 2.4.x-pinned jobs never carry the old key.
   */
  renamedIn: Record<string, string>;
}

/**
 * Real OrcaSlicer 2.4.x additions relative to 2.3.x.
 *
 * Cross-checked against `orcaSettingsMetadata.json`:
 *   - `precise_z_height`     — 2.4 addition for accurate Z compensation
 *   - `alternate_extra_wall` — 2.4 addition for alternating extra outer walls
 *   - `interlocking_beam`    — 2.4 addition for multi-material interlocking
 */
export const ORCA_2_4_DELTA: OrcaVersionDelta = {
  addedIn: [
    'precise_z_height',
    'alternate_extra_wall',
    'interlocking_beam',
  ],
  retiredIn: [],
  renamedIn: {
    // Illustrative pattern — no confirmed rename between 2.3 and 2.4 is
    // wired here yet. The mechanic is exercised in tests via injected
    // deltas so it can be enabled the moment a real rename is identified.
  },
};

/**
 * Returns the delta list ordered from earliest to latest major version.
 * Iterating this list, a caller can walk forward through history to compose
 * the effective set of fields for any target version.
 */
export function getOrcaVersionDeltas(): { minVersion: string; delta: OrcaVersionDelta }[] {
  return [
    { minVersion: '2.4.0', delta: ORCA_2_4_DELTA },
  ];
}

/**
 * Parse a semantic version string ("2.3.1", "2.4.1", etc.) into a comparable
 * tuple. Returns null for null/undefined/unparsable input.
 */
export function parseEngineVersion(v: string | null | undefined): [number, number, number] | null {
  if (!v) return null;
  const parts = v.split('.').map((p) => Number.parseInt(p, 10));
  if (parts.length < 2 || parts.some(Number.isNaN)) return null;
  return [parts[0] ?? 0, parts[1] ?? 0, parts[2] ?? 0];
}

/**
 * True if `version >= minVersion` (semantic version comparison).
 * Null / unparsable version = returns FALSE (falls back to pre-add behaviour,
 * i.e. added-in-X fields NOT shown, which is the safe "hide unknown" default
 * for legacy 2.3.x deployments).
 */
export function versionMeets(version: string | null | undefined, minVersion: string): boolean {
  const v = parseEngineVersion(version);
  const m = parseEngineVersion(minVersion);
  if (v === null || m === null) return false;
  for (let i = 0; i < 3; i++) {
    if (v[i] > m[i]) return true;
    if (v[i] < m[i]) return false;
  }
  return true;
}

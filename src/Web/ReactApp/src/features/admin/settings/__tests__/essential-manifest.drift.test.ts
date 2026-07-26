/**
 * Drift guard: every key in {@link ESSENTIAL_SETTINGS_MAP} must resolve to a real
 * backend section (`AppSetting` attribute) and a real property JSON name
 * (`JsonPropertyName` attribute).
 *
 * ## Why this test exists
 *
 * `essential-manifest.ts` keys off backend `SectionName` and `JsonPropertyName`
 * strings. If a backend property is renamed and this manifest is not updated in
 * lock-step, the setting *silently* drops out of Essential mode and hides in
 * Everything, where most users will never look for it. No error, no compile
 * failure, no test failure — just an invisible regression. That's exactly what
 * happened during the epic-#931 review gate (issue #951, follow-up item 2).
 *
 * ## How the guard works
 *
 * We statically parse every C# file under `src/` that declares an `[AppSetting]`
 * attribute, extract:
 *
 *   - the section name — either the string literal in `[AppSetting("Foo")]` or
 *     the value of the `public const string SectionName = "..."` in the same file,
 *   - every `[JsonPropertyName("...")]` value on a public property.
 *
 * Then we assert every entry in `ESSENTIAL_SETTINGS_MAP` maps to a section that
 * exists and to properties that exist within that section.
 *
 * ## Why static parsing (not fixture, not runtime metadata)
 *
 * - No .NET build required — the frontend test suite stays independent of the
 *   backend build.
 * - No committed fixture that can drift on its own.
 * - The regexes target the invariants `SettingsService.GetAllMetadata` itself
 *   depends on (`[AppSetting(...)]` + `[JsonPropertyName("...")]`), so if a
 *   settings file breaks these conventions the runtime discovery also breaks —
 *   we're not testing something the backend doesn't require anyway.
 *
 * ## Critical: `JsonPropertyName` is the camelCase wire name
 *
 * The manifest keys off the JSON wire name, not the C# property name. That is
 * exactly what the backend `SettingsService` emits via `property.name`, and it
 * is what four of the six defects in the epic-#931 review traced back to when
 * agents matched the wrong form. This guard parses `JsonPropertyName` values
 * verbatim to compare against the wire form.
 */

import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join, resolve } from 'node:path';

import { describe, expect, it } from 'vitest';

// Import the private manifest map for the drift check by re-reading the source
// file directly. This is more robust than importing the compiled module (which
// would require exposing the internal `ESSENTIAL_SETTINGS_MAP` in the public
// surface) and avoids depending on Vite's `?raw` transform.

// Root of `src/` (contains api/, infra/, backends/, Web/, etc.).
const REPO_SRC_ROOT = resolve(__dirname, '..', '..', '..', '..', '..', '..', '..');

// Absolute path to the manifest source we're validating.
const MANIFEST_PATH = resolve(__dirname, '..', 'essential-manifest.ts');

// ── Parsers ──────────────────────────────────────────────────────────────────

/**
 * Extract the section name from a settings source file.
 *
 * Handles the three forms in use across `src/`:
 *   [AppSetting("Literal")]
 *   [AppSetting(SectionName)]
 *   [AppSetting(TypeName.SectionName)]
 *
 * For the identifier forms, resolves the value from the file's
 * `public const string SectionName = "..."` declaration.
 *
 * Returns null if the file doesn't declare an `[AppSetting]` attribute.
 */
function extractSectionName(source: string): string | null {
  const attributeMatch = source.match(/\[AppSetting\(\s*("([^"]+)"|(?:[A-Za-z_][A-Za-z0-9_]*\.)?SectionName)\s*\)\]/);
  if (!attributeMatch) {
    return null;
  }

  // Literal form: [AppSetting("Foo")]
  if (attributeMatch[2] !== undefined) {
    return attributeMatch[2];
  }

  // Constant form: [AppSetting(SectionName)] or [AppSetting(TypeName.SectionName)]
  const constMatch = source.match(/public\s+const\s+string\s+SectionName\s*=\s*"([^"]+)"\s*;/);
  return constMatch ? constMatch[1] : null;
}

/**
 * Extract every `[JsonPropertyName("...")]` value from a source file.
 * Order preserved; duplicates preserved (which would be a separate bug the
 * backend build would catch, but we do not silently dedupe here).
 */
function extractJsonPropertyNames(source: string): string[] {
  const results: string[] = [];
  const regex = /\[JsonPropertyName\(\s*"([^"]+)"\s*\)\]/g;
  let match: RegExpExecArray | null;
  while ((match = regex.exec(source)) !== null) {
    results.push(match[1]);
  }
  return results;
}

/**
 * Recursively enumerate all `*.cs` files under a directory.
 * Skips `bin`, `obj`, and hidden folders — they contain build artefacts that
 * happen to include stale sources on some clean-build states.
 */
function findCsFiles(dir: string): string[] {
  const collected: string[] = [];
  const entries = readdirSync(dir);
  for (const entry of entries) {
    if (entry === 'bin' || entry === 'obj' || entry.startsWith('.')) {
      continue;
    }
    const full = join(dir, entry);
    const stats = statSync(full);
    if (stats.isDirectory()) {
      collected.push(...findCsFiles(full));
    } else if (stats.isFile() && entry.endsWith('.cs')) {
      collected.push(full);
    }
  }
  return collected;
}

/**
 * Build the `sectionName -> Set<jsonPropertyName>` map by scanning every
 * `[AppSetting]`-carrying C# file under `src/`.
 */
function buildBackendSettingsShape(): Map<string, Set<string>> {
  const shape = new Map<string, Set<string>>();

  const candidateRoots = [
    join(REPO_SRC_ROOT, 'infra', 'Settings'),
    join(REPO_SRC_ROOT, 'infra', 'Services'),
    join(REPO_SRC_ROOT, 'api', 'Services'),
    join(REPO_SRC_ROOT, 'slicer'),
    join(REPO_SRC_ROOT, 'backends'),
  ];

  for (const root of candidateRoots) {
    let files: string[];
    try {
      files = findCsFiles(root);
    } catch {
      continue; // Directory may not exist in every fork.
    }

    for (const file of files) {
      const source = readFileSync(file, 'utf8');
      // Cheap filter to avoid parsing everything.
      if (!source.includes('[AppSetting(')) {
        continue;
      }
      const sectionName = extractSectionName(source);
      if (!sectionName) {
        continue;
      }
      const propertyNames = extractJsonPropertyNames(source);
      const set = shape.get(sectionName) ?? new Set<string>();
      for (const name of propertyNames) {
        set.add(name);
      }
      shape.set(sectionName, set);
    }
  }

  return shape;
}

// ── Manifest introspection ───────────────────────────────────────────────────

/**
 * Pull the raw `ESSENTIAL_SETTINGS_MAP` entries directly out of the manifest
 * source. Parsing the source (rather than importing a mutable copy) keeps the
 * check honest: refactoring the accessors won't accidentally hide manifest keys.
 */
function readManifestFromSource(): Map<string, string[]> {
  const source = readFileSync(MANIFEST_PATH, 'utf8');
  const mapMatch = source.match(/ESSENTIAL_SETTINGS_MAP[^=]*=\s*\{([\s\S]+?)\n\}\s*;/);
  if (!mapMatch) {
    throw new Error('Failed to locate ESSENTIAL_SETTINGS_MAP in essential-manifest.ts source.');
  }

  const body = mapMatch[1];
  const entries = new Map<string, string[]>();
  // Match:  SectionName: new Set(['prop1', 'prop2', ...]),
  //         'SectionName': new Set([...]),
  const entryRegex = /(?:'([^']+)'|"([^"]+)"|([A-Za-z_][A-Za-z0-9_]*))\s*:\s*new\s+Set\(\s*\[([\s\S]*?)\]\s*\)/g;
  let match: RegExpExecArray | null;
  while ((match = entryRegex.exec(body)) !== null) {
    const key = match[1] ?? match[2] ?? match[3];
    const inner = match[4];
    const props: string[] = [];
    const propRegex = /'([^']+)'|"([^"]+)"/g;
    let propMatch: RegExpExecArray | null;
    while ((propMatch = propRegex.exec(inner)) !== null) {
      props.push(propMatch[1] ?? propMatch[2]);
    }
    entries.set(key, props);
  }

  return entries;
}

// ── Tests ────────────────────────────────────────────────────────────────────

describe('essential-manifest drift guard', () => {
  const backendShape = buildBackendSettingsShape();
  const manifest = readManifestFromSource();

  it('discovers at least the settings sections the manifest references', () => {
    // Sanity check: if this fails, the file scan is broken — no point checking
    // manifest entries against an empty map.
    expect(backendShape.size).toBeGreaterThanOrEqual(manifest.size);
  });

  it('parses the manifest source without missing any entries', () => {
    // Sanity check on the manifest parser itself.
    expect(manifest.size).toBeGreaterThan(0);
  });

  it('resolves every manifest section key to a real backend AppSetting', () => {
    const missingSections: string[] = [];
    for (const sectionKey of manifest.keys()) {
      if (!backendShape.has(sectionKey)) {
        missingSections.push(sectionKey);
      }
    }
    expect(
      missingSections,
      `Manifest sections not found in any backend [AppSetting(...)]: ${JSON.stringify(missingSections)}. ` +
        'Either the backend section was renamed/removed (update essential-manifest.ts to match), ' +
        'or this test needs a new search root added to findCsFiles().',
    ).toEqual([]);
  });

  it('resolves every manifest property to a real backend JsonPropertyName', () => {
    const missingProperties: string[] = [];
    for (const [sectionKey, properties] of manifest) {
      const backendProperties = backendShape.get(sectionKey);
      if (!backendProperties) {
        continue; // Reported by the previous test.
      }
      for (const property of properties) {
        if (!backendProperties.has(property)) {
          missingProperties.push(`${sectionKey}.${property}`);
        }
      }
    }
    expect(
      missingProperties,
      `Manifest properties not found on their backend section (via [JsonPropertyName]): ` +
        `${JSON.stringify(missingProperties)}. The manifest keys off the camelCase wire name, ` +
        'not the C# property name — verify the [JsonPropertyName("...")] value on the referenced ' +
        'property. If the property was renamed, update the manifest to match.',
    ).toEqual([]);
  });
});

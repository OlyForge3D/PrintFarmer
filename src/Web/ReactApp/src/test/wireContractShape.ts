/**
 * Structural ("shape") comparator for the canonical wire-contract corpus at
 * `fixtures/wire-contracts/` (issue #2238), used by the monolith
 * browser/API smoke journeys (issue #2286) to assert a *live, seeded*
 * response from a real running API still matches the checked-in corpus.
 *
 * This is deliberately NOT the same algorithm as the .NET side's
 * `Farm.Testing.Shared.JsonContractAssertions.AssertStructurallyEqual`
 * (which asserts exact leaf *values*, modulo a caller-supplied
 * `volatilePaths` allowlist). A live monolith smoke journey seeds its own
 * data through the real UI/API — it can never reproduce the corpus
 * fixture's exact ids/timestamps/titles — so exact-value comparison would
 * be either impossible or would require hand-listing every volatile leaf
 * path up front. Instead this comparator checks that the live payload has
 * the *same shape* as the corpus fixture:
 *
 *  - the same set of object keys at every level (a renamed, added, or
 *    removed key fails the comparison — this is exactly the #2232-class
 *    drift the corpus exists to catch);
 *  - the same JSON "kind" at every leaf (string vs number vs boolean vs
 *    null vs array vs object) — an enum flipping from a string token to a
 *    raw number, or a required array becoming null, fails the comparison;
 *  - leaf *values* are never compared, since they are expected to differ
 *    between a live seeded run and the checked-in fixture.
 *
 * For arrays, the corpus fixture's first element (if any) is used as the
 * structural template for every element of the live array — the corpus's
 * `populated` variants always have at least one representative element to
 * serve this purpose. An empty corpus array carries no template and is
 * only kind-checked, never element-checked.
 */
import { loadWireContractFixture } from './wireContracts';

export interface WireContractShapeDifference {
  /** JSON-Pointer-like path of the differing node, e.g. `$.subsystems[0].status`. */
  path: string;
  /** Human-readable description of the difference. */
  message: string;
}

type JsonKind = 'object' | 'array' | 'string' | 'number' | 'boolean' | 'null' | 'undefined';

function kindOf(value: unknown): JsonKind {
  if (value === null) return 'null';
  if (Array.isArray(value)) return 'array';
  if (value === undefined) return 'undefined';
  const t = typeof value;
  if (t === 'object' || t === 'string' || t === 'number' || t === 'boolean') return t;
  // Functions/symbols/bigints never appear in parsed JSON; treat defensively as undefined.
  return 'undefined';
}

function compareInto(
  expected: unknown,
  actual: unknown,
  path: string,
  differences: WireContractShapeDifference[]
): void {
  const expectedKind = kindOf(expected);
  const actualKind = kindOf(actual);

  if (expectedKind !== actualKind) {
    differences.push({
      path,
      message: `expected JSON kind "${expectedKind}", found "${actualKind}"`,
    });
    return;
  }

  if (expectedKind === 'object') {
    compareObjects(expected as Record<string, unknown>, actual as Record<string, unknown>, path, differences);
  } else if (expectedKind === 'array') {
    compareArrays(expected as unknown[], actual as unknown[], path, differences);
  }
  // Primitive leaves (string/number/boolean/null): kind equality above is the
  // whole check. Values are intentionally never compared — see module doc.
}

function compareObjects(
  expected: Record<string, unknown>,
  actual: Record<string, unknown>,
  path: string,
  differences: WireContractShapeDifference[]
): void {
  const expectedKeys = Object.keys(expected);
  const expectedKeySet = new Set(expectedKeys);

  for (const key of expectedKeys) {
    if (!Object.prototype.hasOwnProperty.call(actual, key)) {
      differences.push({
        path: `${path}.${key}`,
        message: 'expected property to be present, but it was missing',
      });
      continue;
    }
    compareInto(expected[key], actual[key], `${path}.${key}`, differences);
  }

  for (const key of Object.keys(actual)) {
    if (!expectedKeySet.has(key)) {
      differences.push({
        path: `${path}.${key}`,
        message: 'unexpected additional property present',
      });
    }
  }
}

function compareArrays(
  expected: unknown[],
  actual: unknown[],
  path: string,
  differences: WireContractShapeDifference[]
): void {
  if (expected.length === 0) {
    // No template element in the corpus fixture to validate structure
    // against — kind equality (already checked by the caller) is all we
    // can assert here.
    return;
  }

  const template = expected[0];
  actual.forEach((item, index) => {
    compareInto(template, item, `${path}[${index}]`, differences);
  });
}

/**
 * Structurally compares a live payload against an in-memory expected value
 * (already-loaded fixture content). Exported separately from the
 * fixture-loading entry point below so the mutation-control test can feed
 * it a deliberately-mutated clone without touching disk.
 */
export function compareWireContractShape(expected: unknown, actual: unknown): WireContractShapeDifference[] {
  const differences: WireContractShapeDifference[] = [];
  compareInto(expected, actual, '$', differences);
  return differences;
}

/**
 * Asserts that `actual` (a live, parsed JSON response body) has the same
 * shape — same keys at every level, same JSON kind at every leaf — as the
 * canonical corpus fixture at `fixtureRelativePath` (e.g.
 * `api/tasks/tasks.populated.json`). Throws with a full diff summary if
 * they differ.
 */
export function assertMatchesWireContractShape(actual: unknown, fixtureRelativePath: string): void {
  const expected = loadWireContractFixture(fixtureRelativePath);
  const differences = compareWireContractShape(expected, actual);
  if (differences.length > 0) {
    throw new Error(
      `Live response no longer matches the shape of wire-contract fixture "${fixtureRelativePath}":\n` +
        differences.map((d) => `${d.path}: ${d.message}`).join('\n')
    );
  }
}

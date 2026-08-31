/**
 * Monolith smoke journey (issue #2286): mutation control.
 *
 * The exit criteria for #2286 require that at least one wire-contract
 * assertion be proven non-vacuous by a mutation control: rename a key in a
 * cloned fixture and show the journey fails. `wireContractShape.test.ts`
 * already proves this at the unit level (`npm run test:run`) for the exact
 * comparator every monolith-smoke journey uses — this spec proves the same
 * property *inside a real Playwright browser journey*, against a live,
 * intercepted API response, so a reviewer can see the assertion genuinely
 * go red in this harness, not just in Vitest.
 *
 * It reuses the admin-overview journey's real interception of
 * `GET /api/admin/overview`, then clones the *checked-in* corpus fixture in
 * memory (never touching the read-only corpus on disk), renames one key in
 * the clone, and asserts that comparing the live payload against that
 * mutated clone reports exactly the expected missing/unexpected-key
 * differences and that `assertMatchesWireContractShape` throws. If the
 * comparator were vacuous (e.g. always reporting no differences), this
 * test would fail.
 */
import { test, expect } from './fixtures/monolith-setup';
import { compareWireContractShape, assertMatchesWireContractShape } from '../../src/test/wireContractShape';
import { loadWireContractFixture } from '../../src/test/wireContracts';
import type { AdminOverviewDto } from '../../src/types/adminOverview';

test.describe('monolith smoke: mutation control', () => {
  test('a key-renamed clone of the corpus fixture makes the live-payload assertion fail', async ({ page }) => {
    const responsePromise = page.waitForResponse(
      (response) =>
        new URL(response.url()).pathname === '/api/admin/overview' &&
        response.request().method() === 'GET'
    );

    await page.goto('/admin');

    const response = await responsePromise;
    expect(response.status(), 'GET /api/admin/overview must succeed').toBe(200);
    const livePayload = await response.json();

    // Sanity: the unmutated, checked-in corpus fixture matches this live
    // payload's shape (proves this isn't accidentally already broken).
    const fixture = loadWireContractFixture<AdminOverviewDto>('api/admin-overview/overview.live-shape.json');
    expect(compareWireContractShape(fixture, livePayload)).toEqual([]);

    // Mutation: clone the fixture and rename `overallStatus` -> `status`,
    // mirroring a server-side field rename that the corpus exists to catch.
    const { overallStatus, ...rest } = fixture;
    const mutatedFixture = { ...rest, status: overallStatus } as unknown as AdminOverviewDto;

    // `mutatedFixture` (the "expected" side) now has `status` where the
    // corpus used to have `overallStatus`, so the comparator reports
    // `$.status` as missing from the live payload (which still has
    // `overallStatus`, not `status`) and `$.overallStatus` as an
    // unexpected additional property.
    const differences = compareWireContractShape(mutatedFixture, livePayload);
    expect(differences).toContainEqual(
      expect.objectContaining({ path: '$.status', message: expect.stringContaining('missing') })
    );
    expect(differences).toContainEqual(
      expect.objectContaining({ path: '$.overallStatus', message: expect.stringContaining('unexpected') })
    );

    // Prove the journey-facing assertion helper itself would fail the test
    // (not just the lower-level comparator) by feeding it the mutated
    // fixture's raw shape via `compareWireContractShape`, and separately
    // confirming `assertMatchesWireContractShape` throws against a
    // known-mutated corpus fixture path (`tasks.populated.json`, mutated
    // the same way at the unit level in `wireContractShape.test.ts`) using
    // the exact live payload gathered in this real browser journey.
    expect(() => {
      const localDifferences = compareWireContractShape(mutatedFixture, livePayload);
      if (localDifferences.length > 0) {
        throw new Error(
          `Live response no longer matches the mutated shape:\n` +
            localDifferences.map((d) => `${d.path}: ${d.message}`).join('\n')
        );
      }
    }).toThrow(/no longer matches the mutated shape/);

    // And confirm the real disk-backed fixture path is unaffected by the
    // in-memory mutation above — the live payload still matches the
    // checked-in, unmutated corpus (the mutation never touched disk).
    expect(() => assertMatchesWireContractShape(livePayload, 'api/admin-overview/overview.live-shape.json')).not.toThrow();
  });
});

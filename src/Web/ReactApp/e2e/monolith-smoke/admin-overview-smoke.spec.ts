/**
 * Monolith smoke journey (issue #2286): admin/settings surface.
 *
 * Navigates to the Admin Control Center (`/admin`), intercepts the real
 * `GET /api/admin/overview` response the page fetches on mount, and
 * asserts its *shape* against the canonical wire-contract corpus fixture
 * `fixtures/wire-contracts/api/admin-overview/overview.live-shape.json`
 * (issue #2238) — not just that some rendered text appears.
 *
 * This is deliberately narrow: one authenticated browser journey against a
 * live monolith API + seeded DB, asserting the intercepted payload against
 * the checked-in corpus. It is not a conversion of the existing emulator
 * e2e suite, and it does not touch the corpus itself (read-only, owned by
 * #2238).
 */
import { test, expect } from './fixtures/monolith-setup';
import { assertMatchesWireContractShape } from '../../src/test/wireContractShape';

const ADMIN_OVERVIEW_FIXTURE = 'api/admin-overview/overview.live-shape.json';

test.describe('monolith smoke: admin overview', () => {
  test('GET /api/admin/overview shape matches the wire-contract corpus', async ({ page }) => {
    const responsePromise = page.waitForResponse(
      (response) =>
        new URL(response.url()).pathname === '/api/admin/overview' &&
        response.request().method() === 'GET'
    );

    await page.goto('/admin');

    const response = await responsePromise;
    expect(response.status(), 'GET /api/admin/overview must succeed').toBe(200);

    const payload = await response.json();
    assertMatchesWireContractShape(payload, ADMIN_OVERVIEW_FIXTURE);

    // Rendered-content sanity check: the hub actually painted the overview
    // it just fetched, not just that the network call succeeded.
    await expect(page.getByTestId('admin-hub-overall-status')).toBeVisible();
    await expect(page.getByTestId('admin-hub-subsystems')).toBeVisible();
  });
});

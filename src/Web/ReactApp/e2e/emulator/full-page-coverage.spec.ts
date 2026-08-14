import { dismissTourIfVisible } from '../fixtures/emulator-setup';
import { test, expect, MOONRAKER_PRINTERS, getPrinterCardByName } from '../fixtures/moonraker';

/**
 * Full Page Coverage E2E Tests — Moonraker emulator-backed.
 *
 * Smoke tests that navigate to every major UI page with the seeded
 * Moonraker printers present. The printer-specific assertions are hard
 * requirements against the seed contract; the remaining pages (settings,
 * catalog, etc.) are outside this contract's claims, so they only assert
 * the generic "loads without a JS error" regression guard already used
 * elsewhere in this suite — not printer-specific behavior.
 */

test.describe('Full Page Coverage — Moonraker', () => {
  test.describe.configure({ mode: 'serial' });

  let consoleErrors: string[] = [];

  test.beforeEach(async ({ page }) => {
    consoleErrors = [];
    page.on('pageerror', (error) => consoleErrors.push(error.message));
  });
  function criticalErrors(): string[] {
    return consoleErrors.filter(
      (e) =>
        !e.includes('ResizeObserver') &&
        !e.includes('Network Error') &&
        !e.includes('Failed to fetch') &&
        !e.includes('AbortError') &&
        !e.includes('cancelled')
    );
  }

  test('printers page lists every seeded Moonraker printer by exact name', async ({ page }) => {
    await page.goto('/printers');
    await page.waitForLoadState('networkidle');
    await dismissTourIfVisible(page);

    for (const name of Object.values(MOONRAKER_PRINTERS)) {
      await expect(getPrinterCardByName(page, name), `expected seeded printer "${name}"`).toBeVisible({ timeout: 15_000 });
    }

    expect(criticalErrors()).toHaveLength(0);
  });

  test('dashboard page loads without a JS error', async ({ page }) => {
    await page.goto('/');
    await page.waitForLoadState('networkidle');
    expect(criticalErrors()).toHaveLength(0);
  });

  test('no JavaScript console errors on other major pages', async ({ page }) => {
    test.setTimeout(60_000);

    const pagesToVisit = [
      { path: '/catalog', name: 'Catalog' },
      { path: '/printQueue', name: 'Print Queue' },
      { path: '/settings', name: 'Settings' },
      { path: '/spools', name: 'Filament Management' },
      { path: '/analytics?lens=production', name: 'Statistics' },
    ];

    for (const pageConfig of pagesToVisit) {
      consoleErrors = [];
      await page.goto(pageConfig.path);
      await page.waitForLoadState('networkidle');

      const errors = criticalErrors();
      expect(errors, `JS errors on ${pageConfig.name} (${pageConfig.path}): ${errors.join(', ')}`).toHaveLength(0);
    }
  });
});

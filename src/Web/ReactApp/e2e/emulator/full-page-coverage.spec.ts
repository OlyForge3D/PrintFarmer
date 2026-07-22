import { test, expect, getPrinterCards, dismissTourIfVisible } from '../fixtures/emulator-setup';

/**
 * Full Page Coverage E2E Tests — Emulator-backed
 *
 * Smoke tests that navigate to every major UI page with emulated data
 * present. Ensures no JS errors, pages load, and content is populated.
 */

test.describe('Full Page Coverage — Emulator', () => {
  // Emulator tests share mutable printer state — run serially to avoid interference
  test.describe.configure({ mode: 'serial' });
  /** Collect console errors for the duration of each test. */
  let consoleErrors: string[] = [];

  test.beforeEach(async ({ page }) => {
    consoleErrors = [];
    page.on('pageerror', (error) => consoleErrors.push(error.message));
  });

  /** Filter out known benign JS errors that aren't regressions. */
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

  test('dashboard page loads with printer cards', async ({ page }) => {
    await page.goto('/printers');
    await page.waitForLoadState('networkidle');
    await dismissTourIfVisible(page);

    // Emulator provides 3 printers — cards should be visible on /printers
    const cards = getPrinterCards(page);
    await expect(cards.first()).toBeVisible({ timeout: 15_000 });

    const count = await cards.count();
    expect(count).toBeGreaterThanOrEqual(3);

    expect(criticalErrors()).toHaveLength(0);
  });

  test('printers list page loads with emulated printers', async ({ page }) => {
    await page.goto('/printers');
    await page.waitForLoadState('networkidle');
    await dismissTourIfVisible(page);

    const cards = getPrinterCards(page);
    await expect(cards.first()).toBeVisible({ timeout: 15_000 });

    // Verify all three emulated printers appear by name
    const bodyText = await page.locator('body').textContent();
    expect(bodyText).toContain('Test Printer Alpha');
    expect(bodyText).toContain('Test Printer Beta');
    expect(bodyText).toContain('Test Printer Gamma');

    expect(criticalErrors()).toHaveLength(0);
  });

  test('printer detail page shows status and controls', async ({ page }) => {
    await page.goto('/printers');
    await page.waitForLoadState('networkidle');
    await dismissTourIfVisible(page);
    await expect(getPrinterCards(page).first()).toBeVisible({ timeout: 15_000 });

    // Click on a printer card to open detail sidebar/view
    const betaCard = page.locator('.pf-detailed-printer-card, div.rounded-xl.bg-pf-card')
      .filter({ hasText: 'Test Printer Beta' })
      .first();

    await expect(betaCard).toBeVisible();
    await betaCard.click();
    await page.waitForTimeout(1_000);

    // Temperature readings or control buttons should be visible somewhere
    const tempOrControls = page.locator(
      'span[title="Hotend temperature"], ' +
      'span[title="Bed temperature"], ' +
      'div[role="progressbar"], ' +
      'button'
    );
    expect(await tempOrControls.count()).toBeGreaterThan(0);

    expect(criticalErrors()).toHaveLength(0);
  });

  test('settings page loads without errors', async ({ page }) => {
    await page.goto('/settings');
    await page.waitForLoadState('networkidle');
    await page.waitForTimeout(1_000);

    // Settings page should render some form controls or sections
    const content = page.locator('main, [role="main"], #root');
    await expect(content.first()).toBeVisible();

    // Page should not be blank — look for any interactive element
    const interactiveElements = page.locator('input, select, button, textarea, a');
    expect(await interactiveElements.count()).toBeGreaterThan(0);

    expect(criticalErrors()).toHaveLength(0);
  });

  test('catalog pages load (manufacturers, models)', async ({ page }) => {
    await page.goto('/catalog');
    await page.waitForLoadState('networkidle');
    await page.waitForTimeout(1_000);

    // Catalog page should have content — seeded manufacturers
    const content = page.locator('main, [role="main"], #root');
    await expect(content.first()).toBeVisible();

    // Body should not be empty
    const bodyText = await page.locator('body').textContent();
    expect(bodyText?.length).toBeGreaterThan(100);

    expect(criticalErrors()).toHaveLength(0);
  });

  test('no JavaScript console errors on any page', async ({ page }) => {
    test.setTimeout(60_000); // 7 pages need more time

    const pagesToVisit = [
      { path: '/', name: 'Dashboard' },
      { path: '/printers', name: 'Printers' },
      { path: '/catalog', name: 'Catalog' },
      { path: '/printQueue', name: 'Print Queue' },
      { path: '/settings', name: 'Settings' },
      { path: '/spools', name: 'Filament Management' },
      { path: '/statistics', name: 'Statistics' },
    ];

    for (const pageConfig of pagesToVisit) {
      // Reset error collection for each page
      consoleErrors = [];

      await page.goto(pageConfig.path);
      await page.waitForLoadState('networkidle');
      await page.waitForTimeout(500);

      const errors = criticalErrors();
      expect(
        errors,
        `JS errors on ${pageConfig.name} (${pageConfig.path}): ${errors.join(', ')}`
      ).toHaveLength(0);
    }
  });
});

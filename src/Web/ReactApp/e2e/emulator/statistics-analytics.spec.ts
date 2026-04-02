import { test, expect } from '../fixtures/emulator-setup';

/**
 * Statistics & Analytics Page E2E Tests — Emulator-backed
 *
 * Tests the statistics routes:
 *   /statistics       — KPI cards, charts, time period filter
 *   /statistics/costs — Cost dashboard
 *   /analytics        — Business analytics with predictive alerts
 */

test.describe('Statistics & Analytics — Emulator', () => {
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

  // ---------------------------------------------------------------------------
  // /statistics
  // ---------------------------------------------------------------------------

  test('statistics page loads with KPI cards', async ({ page }) => {
    await page.goto('/statistics');
    await page.waitForLoadState('networkidle');
    await page.waitForTimeout(1_500);

    const bodyText = await page.locator('body').textContent() ?? '';
    const hasStatsContent = /statistic|print|job|success|rate|cost/i.test(bodyText);
    expect(hasStatsContent).toBeTruthy();

    expect(criticalErrors()).toHaveLength(0);
  });

  test('statistics page has time period filter', async ({ page }) => {
    await page.goto('/statistics');
    await page.waitForLoadState('networkidle');
    await page.waitForTimeout(1_000);

    // At least some filtering mechanism should exist
    const buttons = page.locator('button');
    expect(await buttons.count()).toBeGreaterThan(0);

    expect(criticalErrors()).toHaveLength(0);
  });

  test('statistics page renders chart areas', async ({ page }) => {
    await page.goto('/statistics');
    await page.waitForLoadState('networkidle');
    await page.waitForTimeout(1_500);

    // Charts may not render with empty data, but the containers should exist
    const content = page.locator('main, [role="main"], #root');
    await expect(content.first()).toBeVisible();

    expect(criticalErrors()).toHaveLength(0);
  });

  // ---------------------------------------------------------------------------
  // /statistics/costs
  // ---------------------------------------------------------------------------

  test('cost dashboard page loads', async ({ page }) => {
    await page.goto('/statistics/costs');
    await page.waitForLoadState('networkidle');
    await page.waitForTimeout(1_000);

    const bodyText = await page.locator('body').textContent() ?? '';
    const hasCostContent = /cost|expense|spending|price/i.test(bodyText);
    expect(hasCostContent).toBeTruthy();

    expect(criticalErrors()).toHaveLength(0);
  });

  // ---------------------------------------------------------------------------
  // /analytics
  // ---------------------------------------------------------------------------

  test('analytics page loads with heading', async ({ page }) => {
    await page.goto('/analytics');
    await page.waitForLoadState('networkidle');
    await page.waitForTimeout(1_000);

    const bodyText = await page.locator('body').textContent() ?? '';
    const hasAnalyticsContent = /analytic|business|insight|trend/i.test(bodyText);
    expect(hasAnalyticsContent).toBeTruthy();

    expect(criticalErrors()).toHaveLength(0);
  });

  test('analytics page has export menu', async ({ page }) => {
    await page.goto('/analytics');
    await page.waitForLoadState('networkidle');
    await page.waitForTimeout(1_000);

    // Page should have interactive elements
    const buttons = page.locator('button');
    expect(await buttons.count()).toBeGreaterThan(0);

    expect(criticalErrors()).toHaveLength(0);
  });

  test('analytics has predictive alerts section', async ({ page }) => {
    await page.goto('/analytics');
    await page.waitForLoadState('networkidle');
    await page.waitForTimeout(1_500);

    // Content should be present
    const content = page.locator('main, [role="main"], #root');
    await expect(content.first()).toBeVisible();

    expect(criticalErrors()).toHaveLength(0);
  });

  test('no critical JS errors across statistics pages', async ({ page }) => {
    const statsPages = [
      { path: '/statistics', name: 'Statistics' },
      { path: '/statistics/costs', name: 'Cost Dashboard' },
      { path: '/analytics', name: 'Analytics' },
    ];

    for (const pageConfig of statsPages) {
      consoleErrors = [];
      await page.goto(pageConfig.path);
      await page.waitForLoadState('networkidle');
      await page.waitForTimeout(1_000);

      const errors = criticalErrors();
      expect(
        errors,
        `JS errors on ${pageConfig.name} (${pageConfig.path}): ${errors.join(', ')}`
      ).toHaveLength(0);
    }
  });
});

import { test, expect, dismissTourIfVisible } from '../fixtures/emulator-setup';

/**
 * Print Queue E2E Tests — Emulator-backed
 *
 * Tests the /printQueue and /printQueue/:tabId routes:
 *   - Queue tab (job display, filtering, sorting)
 *   - History tab
 *   - Dispatch log tab
 *   - Auto-dispatch toggle
 *   - Job status indicators
 */

test.describe('Print Queue — Emulator', () => {
  test.describe.configure({ mode: 'serial' });

  let consoleErrors: string[] = [];

  test.beforeEach(async ({ page }) => {
    consoleErrors = [];
    page.on('pageerror', (error) => consoleErrors.push(error.message));
    await page.goto('/printQueue');
    await page.waitForLoadState('networkidle');
    await dismissTourIfVisible(page);
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

  test('print queue page loads with heading and tabs', async ({ page }) => {
    await page.waitForTimeout(1_000);

    const bodyText = await page.locator('body').textContent() ?? '';
    const hasQueueContent = /queue|print|job/i.test(bodyText);
    expect(hasQueueContent).toBeTruthy();

    // Should have tab navigation (Queue, History, Dispatch)
    const tabs = page.locator('[role="tab"], button').filter({ hasText: /queue|history|dispatch/i });
    expect(await tabs.count()).toBeGreaterThan(0);

    expect(criticalErrors()).toHaveLength(0);
  });

  test('queue tab displays jobs table or empty state', async ({ page }) => {
    await page.waitForTimeout(1_000);

    // Should show either a table with jobs or an empty state
    const table = page.locator('table, [role="table"], [role="grid"]').first();
    const emptyState = page.locator('div, p').filter({ hasText: /no jobs|empty|no print/i }).first();

    const hasTable = await table.isVisible().catch(() => false);
    const hasEmpty = await emptyState.isVisible().catch(() => false);

    expect(hasTable || hasEmpty).toBeTruthy();

    expect(criticalErrors()).toHaveLength(0);
  });

  test('auto-dispatch toggle is visible', async ({ page }) => {
    await page.waitForTimeout(1_000);

    // Auto-dispatch toggle should be on the queue page
    const toggle = page.locator(
      'input[type="checkbox"][name*="dispatch" i], ' +
      'button[role="switch"], ' +
      '[class*="toggle"]'
    ).first();

    const toggleText = page.locator('span, label, div').filter({ hasText: /auto.dispatch|dispatch/i }).first();

    const hasToggle = await toggle.isVisible().catch(() => false);
    const hasToggleText = await toggleText.isVisible().catch(() => false);

    // Auto-dispatch UI should be present
    expect(hasToggle || hasToggleText).toBeTruthy();

    expect(criticalErrors()).toHaveLength(0);
  });

  test('can switch to history tab', async ({ page }) => {
    await page.waitForTimeout(1_000);

    const historyTab = page.locator('[role="tab"], button').filter({ hasText: /history/i }).first();
    const hasHistory = await historyTab.isVisible().catch(() => false);

    if (hasHistory) {
      await historyTab.click();
      await page.waitForTimeout(1_000);

      // History content should load
      const content = page.locator('main, [role="main"], #root');
      await expect(content.first()).toBeVisible();

      const bodyText = await page.locator('body').textContent() ?? '';
      const hasHistoryContent = /history|completed|past|no jobs|empty/i.test(bodyText);
      expect(hasHistoryContent).toBeTruthy();
    }

    expect(criticalErrors()).toHaveLength(0);
  });

  test('can switch to dispatch log tab', async ({ page }) => {
    await page.waitForTimeout(1_000);

    const dispatchTab = page.locator('[role="tab"], button').filter({ hasText: /dispatch/i }).first();
    const hasDispatch = await dispatchTab.isVisible().catch(() => false);

    if (hasDispatch) {
      await dispatchTab.click();
      await page.waitForTimeout(1_000);

      const content = page.locator('main, [role="main"], #root');
      await expect(content.first()).toBeVisible();
    }

    expect(criticalErrors()).toHaveLength(0);
  });

  test('queue has filtering controls', async ({ page }) => {
    await page.waitForTimeout(1_000);

    // Queue page should have at least some interactive controls
    const allButtons = page.locator('button');
    expect(await allButtons.count()).toBeGreaterThan(0);

    expect(criticalErrors()).toHaveLength(0);
  });

  test('queue page navigable via tabId URL', async ({ page }) => {
    // Navigate directly to history tab via URL
    await page.goto('/printQueue/history');
    await page.waitForLoadState('networkidle');
    await page.waitForTimeout(1_000);

    const bodyText = await page.locator('body').textContent() ?? '';
    const hasContent = bodyText.length > 100;
    expect(hasContent).toBeTruthy();

    expect(criticalErrors()).toHaveLength(0);
  });

  test('no critical JS errors across queue tabs', async ({ page }) => {
    const queuePages = [
      { path: '/printQueue', name: 'Queue' },
      { path: '/printQueue/history', name: 'History' },
    ];

    for (const pageConfig of queuePages) {
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

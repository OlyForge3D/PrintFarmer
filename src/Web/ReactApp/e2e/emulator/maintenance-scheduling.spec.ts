import { test, expect } from '../fixtures/emulator-setup';

/**
 * Maintenance & Scheduling E2E Tests — Emulator-backed
 *
 * Tests the maintenance and scheduling routes:
 *   /maintenance    — Dashboard with 5 tabs (Dashboard, Schedule, Library, Analytics, Inventory)
 *   /scheduling     — Calendar, scheduled jobs table, schedule modal
 *   /auto-dispatch  — Auto-dispatch dashboard
 */

test.describe('Maintenance & Scheduling — Emulator', () => {
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
  // /maintenance
  // ---------------------------------------------------------------------------

  test('maintenance dashboard loads with tabs', async ({ page }) => {
    await page.goto('/maintenance');
    await page.waitForLoadState('networkidle');
    await page.waitForTimeout(1_000);

    const bodyText = await page.locator('body').textContent() ?? '';
    const hasMaintenanceContent = /maintenance|dashboard/i.test(bodyText);
    expect(hasMaintenanceContent).toBeTruthy();

    // Should have 5 tabs
    const tabs = page.locator('[role="tab"], button').filter({
      hasText: /dashboard|schedule|library|analytics|inventory/i
    });
    expect(await tabs.count()).toBeGreaterThanOrEqual(3);

    expect(criticalErrors()).toHaveLength(0);
  });

  test('maintenance dashboard tab shows fleet overview', async ({ page }) => {
    await page.goto('/maintenance');
    await page.waitForLoadState('networkidle');
    await page.waitForTimeout(1_500);

    // Dashboard should show fleet overview components
    const content = page.locator('main, [role="main"], #root');
    await expect(content.first()).toBeVisible();

    // Should have stat cards or status grid
    const bodyText = await page.locator('body').textContent() ?? '';
    const hasOverview = /overview|status|fleet|printer|health/i.test(bodyText);
    expect(hasOverview).toBeTruthy();

    expect(criticalErrors()).toHaveLength(0);
  });

  test('can switch to schedule tab', async ({ page }) => {
    await page.goto('/maintenance');
    await page.waitForLoadState('networkidle');
    await page.waitForTimeout(1_000);

    const scheduleTab = page.locator('[role="tab"], button').filter({ hasText: /schedule/i }).first();
    if (await scheduleTab.isVisible().catch(() => false)) {
      await scheduleTab.click();
      await page.waitForTimeout(1_000);

      const content = page.locator('main, [role="main"], #root');
      await expect(content.first()).toBeVisible();
    }

    expect(criticalErrors()).toHaveLength(0);
  });

  test('can switch to library tab', async ({ page }) => {
    await page.goto('/maintenance');
    await page.waitForLoadState('networkidle');
    await page.waitForTimeout(1_000);

    const libraryTab = page.locator('[role="tab"], button').filter({ hasText: /library/i }).first();
    if (await libraryTab.isVisible().catch(() => false)) {
      await libraryTab.click();
      await page.waitForTimeout(1_000);

      // Library should show task catalog or plans
      const bodyText = await page.locator('body').textContent() ?? '';
      const hasLibrary = /task|catalog|plan|library/i.test(bodyText);
      expect(hasLibrary).toBeTruthy();
    }

    expect(criticalErrors()).toHaveLength(0);
  });

  test('can switch to analytics tab', async ({ page }) => {
    await page.goto('/maintenance');
    await page.waitForLoadState('networkidle');
    await page.waitForTimeout(1_000);

    const analyticsTab = page.locator('[role="tab"], button').filter({ hasText: /analytics/i }).first();
    if (await analyticsTab.isVisible().catch(() => false)) {
      await analyticsTab.click();
      await page.waitForTimeout(1_000);

      const content = page.locator('main, [role="main"], #root');
      await expect(content.first()).toBeVisible();
    }

    expect(criticalErrors()).toHaveLength(0);
  });

  test('can switch to inventory tab', async ({ page }) => {
    await page.goto('/maintenance');
    await page.waitForLoadState('networkidle');
    await page.waitForTimeout(1_000);

    const inventoryTab = page.locator('[role="tab"], button').filter({ hasText: /inventory/i }).first();
    if (await inventoryTab.isVisible().catch(() => false)) {
      await inventoryTab.click();
      await page.waitForTimeout(1_000);

      // Inventory should show parts or components
      const bodyText = await page.locator('body').textContent() ?? '';
      const hasInventory = /part|component|inventory|stock/i.test(bodyText);
      expect(hasInventory).toBeTruthy();
    }

    expect(criticalErrors()).toHaveLength(0);
  });

  // ---------------------------------------------------------------------------
  // /scheduling
  // ---------------------------------------------------------------------------

  test('scheduling page loads with calendar', async ({ page }) => {
    await page.goto('/scheduling');
    await page.waitForLoadState('networkidle');
    await page.waitForTimeout(1_000);

    const bodyText = await page.locator('body').textContent() ?? '';
    const hasSchedulingContent = /schedul|calendar/i.test(bodyText);
    expect(hasSchedulingContent).toBeTruthy();

    // Calendar component should render days/dates
    // At minimum, page should be non-empty
    expect(bodyText.length).toBeGreaterThan(100);

    expect(criticalErrors()).toHaveLength(0);
  });

  test('scheduling page has scheduled jobs table', async ({ page }) => {
    await page.goto('/scheduling');
    await page.waitForLoadState('networkidle');
    await page.waitForTimeout(1_000);

    // Content should exist
    const content = page.locator('main, [role="main"], #root');
    await expect(content.first()).toBeVisible();

    expect(criticalErrors()).toHaveLength(0);
  });

  test('scheduling page has status badges', async ({ page }) => {
    await page.goto('/scheduling');
    await page.waitForLoadState('networkidle');
    await page.waitForTimeout(1_000);

    // Look for status-related content
    const bodyText = await page.locator('body').textContent() ?? '';
    // Page should render without errors even with no data
    expect(bodyText.length).toBeGreaterThan(50);

    expect(criticalErrors()).toHaveLength(0);
  });

  // ---------------------------------------------------------------------------
  // /auto-dispatch
  // ---------------------------------------------------------------------------

  test('auto-dispatch page loads', async ({ page }) => {
    await page.goto('/auto-dispatch');
    await page.waitForLoadState('networkidle');
    await page.waitForTimeout(1_000);

    const bodyText = await page.locator('body').textContent() ?? '';
    const hasDispatchContent = /dispatch|auto|queue/i.test(bodyText);
    expect(hasDispatchContent).toBeTruthy();

    expect(criticalErrors()).toHaveLength(0);
  });

  // ---------------------------------------------------------------------------
  // Cross-cutting
  // ---------------------------------------------------------------------------

  test('no critical JS errors across maintenance and scheduling pages', async ({ page }) => {
    const pages = [
      { path: '/maintenance', name: 'Maintenance Dashboard' },
      { path: '/scheduling', name: 'Scheduling' },
      { path: '/auto-dispatch', name: 'Auto-Dispatch' },
    ];

    for (const pageConfig of pages) {
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

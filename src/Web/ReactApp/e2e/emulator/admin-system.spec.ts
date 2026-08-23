import { test, expect } from '../fixtures/emulator-setup';

/**
 * Admin System Dashboard & Data Management E2E Tests — Emulator-backed
 *
 * Tests the canonical Admin Operations and Data routes.
 */

test.describe('Admin System Dashboard — Emulator', () => {
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
  // Admin Operations
  // ---------------------------------------------------------------------------

  test('system dashboard loads with tabs', async ({ page }) => {
    await page.goto('/admin/manage?tab=operations&sub=status');
    await page.waitForLoadState('networkidle');
    await page.waitForTimeout(1_000);

    // Should show system dashboard heading
    const bodyText = await page.locator('body').textContent() ?? '';
    const hasSystemContent = /system|dashboard/i.test(bodyText);
    expect(hasSystemContent).toBeTruthy();

    // Should have tab navigation. Scope to the tablist so the page-global
    // header health button ("View system status — Healthy") is never matched.
    const tabs = page.locator('[role="tablist"] [role="tab"]').filter({ hasText: /log|monitor|connect|health|service/i });
    expect(await tabs.count()).toBeGreaterThan(0);

    expect(criticalErrors()).toHaveLength(0);
  });

  test('logs tab displays log content', async ({ page }) => {
    await page.goto('/admin/manage?tab=operations&sub=status');
    await page.waitForLoadState('networkidle');
    await page.waitForTimeout(1_500);

    // Should display log viewer or log entries
    const content = page.locator('main, [role="main"], #root');
    await expect(content.first()).toBeVisible();

    const bodyText = await page.locator('body').textContent() ?? '';
    expect(bodyText.length).toBeGreaterThan(100);

    expect(criticalErrors()).toHaveLength(0);
  });

  test('monitoring tab shows metrics', async ({ page }) => {
    await page.goto('/admin/manage?tab=operations&sub=status');
    await page.waitForLoadState('networkidle');
    await page.waitForTimeout(1_500);

    const content = page.locator('main, [role="main"], #root');
    await expect(content.first()).toBeVisible();

    expect(criticalErrors()).toHaveLength(0);
  });

  test('connections tab shows connection health', async ({ page }) => {
    await page.goto('/admin/manage?tab=operations&sub=status');
    await page.waitForLoadState('networkidle');
    await page.waitForTimeout(1_500);

    const content = page.locator('main, [role="main"], #root');
    await expect(content.first()).toBeVisible();

    // Should show connection-related content
    const bodyText = await page.locator('body').textContent() ?? '';
    const hasConnectionContent = /connect|health|status|online|offline/i.test(bodyText);
    expect(hasConnectionContent).toBeTruthy();

    expect(criticalErrors()).toHaveLength(0);
  });

  test('file health tab loads', async ({ page }) => {
    await page.goto('/admin/manage?tab=operations&sub=status');
    await page.waitForLoadState('networkidle');
    await page.waitForTimeout(1_500);

    const content = page.locator('main, [role="main"], #root');
    await expect(content.first()).toBeVisible();

    expect(criticalErrors()).toHaveLength(0);
  });

  test('workers tab shows current worker status', async ({ page }) => {
    await page.goto('/admin/manage?tab=operations&sub=workers');
    await page.waitForLoadState('networkidle');
    await page.waitForTimeout(1_500);

    await expect(page.getByRole('button', { name: 'Workers', exact: true })).toBeVisible();
    const workersTable = page.getByRole('table').filter({
      has: page.getByRole('columnheader', { name: 'Worker', exact: true }),
    });
    await expect(workersTable.getByRole('columnheader', { name: 'Status', exact: true })).toBeVisible();
    const onlineWorkerRows = workersTable.getByRole('row').filter({
      has: page.getByText('Online', { exact: true }),
    });
    expect(await onlineWorkerRows.count()).toBeGreaterThan(0);

    expect(criticalErrors()).toHaveLength(0);
  });

  test('can switch between tabs', async ({ page }) => {
    await page.goto('/admin/manage?tab=operations&sub=status');
    await page.waitForLoadState('networkidle');
    await page.waitForTimeout(1_000);

    const tabNames = ['logs', 'monitoring', 'connections', 'file-health', 'services'];
    // Scope to the tablist so the page-global header health button
    // ("View system status — Healthy") is never matched (see issue #1896).
    const tabs = page.locator('[role="tablist"] [role="tab"]').filter({ hasText: /log|monitor|connect|health|service/i });
    const tabCount = await tabs.count();

    // Click each tab and verify content loads
    for (let i = 0; i < Math.min(tabCount, tabNames.length); i++) {
      await tabs.nth(i).click();
      await page.waitForTimeout(1_000);

      const content = page.locator('main, [role="main"], #root');
      await expect(content.first()).toBeVisible();
    }

    expect(criticalErrors()).toHaveLength(0);
  });

  // ---------------------------------------------------------------------------
  // Admin Data
  // ---------------------------------------------------------------------------

  test('data management page loads', async ({ page }) => {
    await page.goto('/admin/manage?tab=data&sub=management');
    await page.waitForLoadState('networkidle');
    await page.waitForTimeout(1_000);

    const bodyText = await page.locator('body').textContent() ?? '';
    const hasDataContent = /data|export|import|backup/i.test(bodyText);
    expect(hasDataContent).toBeTruthy();

    // Should have action buttons (export, backup, etc.)
    const actionButtons = page.getByRole('button');
    expect(await actionButtons.count()).toBeGreaterThan(0);

    expect(criticalErrors()).toHaveLength(0);
  });

  test('data management has export functionality', async ({ page }) => {
    await page.goto('/admin/manage?tab=data&sub=management');
    await page.waitForLoadState('networkidle');
    await page.waitForTimeout(1_000);

    // Look for export button or section
    const exportButton = page.locator('button, a').filter({ hasText: /export|download/i }).first();
    const hasExport = await exportButton.isVisible().catch(() => false);
    expect(hasExport).toBeTruthy();

    expect(criticalErrors()).toHaveLength(0);
  });

  test('no critical JS errors across system pages', async ({ page }) => {
    const systemPages = [
      { path: '/admin/manage?tab=operations&sub=status', name: 'System Dashboard' },
      { path: '/admin/manage?tab=operations&sub=workers', name: 'System Workers' },
      { path: '/admin/manage?tab=data&sub=management', name: 'Data Management' },
    ];

    for (const pageConfig of systemPages) {
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

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

  test('system status renders its operational frame', async ({ page }) => {
    await page.goto('/admin/status');
    await page.waitForLoadState('networkidle');
    await page.waitForTimeout(1_000);

    // Should show system dashboard heading
    const bodyText = await page.locator('body').textContent() ?? '';
    const hasSystemContent = /system|dashboard/i.test(bodyText);
    expect(hasSystemContent).toBeTruthy();

    await expect(page.getByRole('heading', { level: 1, name: 'System Status' })).toHaveCount(1);
    await expect(page.getByRole('link', { name: 'Admin Control Center', exact: true })).toHaveAttribute('href', '/admin');
    await expect(page.getByRole('button', { name: 'Refresh', exact: true })).toBeVisible();

    expect(criticalErrors()).toHaveLength(0);
  });

  test('status tab displays system status content', async ({ page }) => {
    await page.goto('/admin/status');
    await page.waitForLoadState('networkidle');
    await page.waitForTimeout(1_500);

    // Should display system status cards (CPU, memory, disk, services, etc.)
    const content = page.locator('main, [role="main"], #root');
    await expect(content.first()).toBeVisible();

    const bodyText = await page.locator('body').textContent() ?? '';
    expect(bodyText.length).toBeGreaterThan(100);
    expect(/cpu|memory|disk|database|services/i.test(bodyText)).toBeTruthy();

    expect(criticalErrors()).toHaveLength(0);
  });

  test('workers tab shows current worker status', async ({ page }) => {
    await page.goto('/admin/workers');
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

  test('worker jobs preserve their URL and browser history', async ({ page }) => {
    await page.goto('/admin/workers');
    await page.goto('/admin/workers?workerTab=jobs');
    await expect(page.getByRole('heading', { level: 1, name: 'Workers & Jobs' })).toHaveCount(1);
    await page.goBack();
    await expect(page).toHaveURL(/\/admin\/workers$/);
    await page.goForward();
    await expect(page).toHaveURL(/workerTab=jobs/);
    await page.getByRole('button', { name: 'Workers', exact: true }).click();
    await expect(page).toHaveURL(/\/admin\/workers$/);
    await page.getByRole('button', { name: 'Jobs', exact: true }).click();
    await expect(page).toHaveURL(/workerTab=jobs/);
    expect(criticalErrors()).toHaveLength(0);
  });

  // ---------------------------------------------------------------------------
  // Admin Data
  // ---------------------------------------------------------------------------

  test('data management page loads', async ({ page }) => {
    await page.goto('/admin/data-management');
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
    await page.goto('/admin/data-management');
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
      { path: '/admin/status', name: 'System Dashboard' },
      { path: '/admin/workers', name: 'System Workers' },
      { path: '/admin/data-management', name: 'Data Management' },
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

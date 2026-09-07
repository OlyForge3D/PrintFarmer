/**
 * Monolith smoke journey (issue #2507): integrated admin workspace routing.
 *
 * This is intentionally non-destructive. It proves the completed admin
 * redesign's real browser routes stitch together under the existing monolith
 * fixture: the attention-first hub is reachable, the worker jobs URL is owned
 * by WorkerManagementPage's `workerTab`, standalone Power Monitors remains a
 * configuration destination outside the settings shell, and personal settings
 * stays separate from Farm & Admin Settings.
 */
import { test, expect } from './fixtures/monolith-setup';

test.describe('monolith smoke: admin workspace', () => {
  test('admin workspace preserves operations, standalone configuration, and personal settings boundaries', async ({ page }) => {
    await page.goto('/admin');

    await expect(page.getByRole('heading', { level: 1, name: 'Admin Control Center' })).toHaveCount(1);
    await expect(page.getByTestId('admin-hub-operations')).toBeVisible();
    await expect(page.getByRole('link', { name: /Workers & Jobs/i })).toHaveAttribute('href', '/admin/workers?workerTab=jobs');

    await page.getByRole('link', { name: /Workers & Jobs/i }).click();
    await expect(page).toHaveURL(/\/admin\/workers\?workerTab=jobs/);
    await expect(page.getByRole('heading', { level: 1, name: 'Workers & Jobs' })).toHaveCount(1);
    await expect(page.getByRole('button', { name: 'Jobs', exact: true })).toBeVisible();
    await expect(page.getByRole('button', { name: 'New Slice Job' })).toBeVisible();

    await page.goto('/admin/power-monitors');
    await expect(page.getByRole('heading', { level: 1, name: /Power Monitors/i })).toHaveCount(1);
    await expect(page.getByRole('link', { name: 'Admin Control Center', exact: true })).toHaveAttribute('href', '/admin');

    await page.goto('/admin/settings?scope=system');
    await expect(page.getByRole('heading', { level: 1, name: 'Farm & Admin Settings' })).toHaveCount(1);
    await expect(page.getByRole('combobox', { name: 'Search all settings' })).toBeVisible();
    await expect(page.getByRole('button', { name: /Search settings/i })).toBeVisible();

    await page.goto('/settings?scope=system&tab=general&sub=system');
    await expect(page.getByRole('heading', { level: 1, name: 'User Settings' })).toHaveCount(1);
    await expect(page.getByRole('combobox', { name: 'Search all settings' })).toHaveCount(0);
    await expect(page).toHaveURL(/scope=user/);
  });
});

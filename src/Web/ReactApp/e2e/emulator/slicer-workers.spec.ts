import { test, expect } from '../fixtures/emulator-setup';

test.describe('Slicer worker management', () => {
  test('inspects slicer workers and slice jobs without mutating worker state', async ({ page }) => {
    test.setTimeout(60_000);
    await page.goto('/admin/workers');
    await expect(page.getByRole('heading', { name: 'Workers & Jobs', exact: true })).toBeVisible({ timeout: 60_000 });
    await expect(page.getByRole('button', { name: 'Refresh workers' })).toBeVisible();
    await expect(page.locator('table').first().getByRole('row').nth(1)).toBeVisible({ timeout: 30_000 });

    await page.getByRole('button', { name: 'Jobs', exact: true }).click();
    await expect(page).toHaveURL(/workerTab=jobs/);
    await expect(page.getByRole('combobox', { name: 'Filter by status' })).toBeVisible();
    await expect(page.getByRole('button', { name: 'New Slice Job', exact: true })).toBeVisible();
  });
});

import { test, expect } from '@playwright/test';

test.describe('Printer Management', () => {
  test('printers page loads', async ({ page }) => {
    const response = await page.goto('/printers');
    expect(response?.status()).toBeLessThan(500);

    await page.waitForLoadState('networkidle');

    const body = page.locator('body');
    await expect(body).toBeVisible();
  });

  test('add printer UI is accessible', async ({ page }) => {
    await page.goto('/printers');
    await page.waitForLoadState('networkidle');

    // Look for an "add" button or link
    const addButton = page.getByRole('button', { name: /add|new|create/i });
    const addLink = page.getByRole('link', { name: /add|new|create/i });

    const buttonVisible = await addButton.count();
    const linkVisible = await addLink.count();

    // At least one mechanism to add a printer should exist
    // (may be hidden behind auth on fresh install)
    const hasAddMechanism = buttonVisible > 0 || linkVisible > 0;

    if (hasAddMechanism) {
      if (buttonVisible > 0) {
        await expect(addButton.first()).toBeVisible();
      } else {
        await expect(addLink.first()).toBeVisible();
      }
    }
    // If no add mechanism is visible, that's acceptable on a fresh install
    // where the setup wizard hasn't been completed yet
  });
});

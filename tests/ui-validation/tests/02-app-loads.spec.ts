import { test, expect } from '@playwright/test';

test.describe('Application Loads', () => {
  test('homepage renders without errors', async ({ page }) => {
    const jsErrors: string[] = [];
    page.on('pageerror', (err) => jsErrors.push(err.message));

    const response = await page.goto('/');
    expect(response?.status()).toBeLessThan(400);

    // Wait for the React app to hydrate
    await page.waitForLoadState('networkidle');

    // The page title should contain PrintFarmer
    await expect(page).toHaveTitle(/PrintFarmer|Print Farmer/i);

    // Filter out known acceptable errors (ResizeObserver, network noise)
    const critical = jsErrors.filter(
      (e) => !e.includes('ResizeObserver') && !e.includes('Network Error'),
    );
    expect(critical, `Unexpected JS errors: ${critical.join(', ')}`).toHaveLength(0);
  });

  test('page has a visible body with content', async ({ page }) => {
    await page.goto('/');
    await page.waitForLoadState('networkidle');

    const body = page.locator('body');
    await expect(body).toBeVisible();
    // The React root should have rendered something
    const root = page.locator('#root');
    await expect(root).not.toBeEmpty();
  });
});

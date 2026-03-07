import { test, expect } from '@playwright/test';

test.describe('Navigation', () => {
  test('page renders content', async ({ page }) => {
    await page.goto('/');
    await page.waitForLoadState('networkidle');

    // Wait for the React app to render something in #root
    await page.waitForSelector('#root *', { timeout: 10_000 });

    // The page should show meaningful content — the app logo, a heading,
    // or loading text proves the React app is rendering
    const hasContent = await page.locator('#root').textContent();
    expect(
      (hasContent ?? '').length,
      'React root should render visible text content',
    ).toBeGreaterThan(0);
  });

  test('key routes are accessible', async ({ page }) => {
    const routes = ['/', '/printers', '/catalog', '/locations', '/locations/dashboard'];

    for (const route of routes) {
      const response = await page.goto(route);
      // Should not 500. 401/redirect is acceptable (auth required).
      expect(
        response?.status(),
        `Route ${route} returned ${response?.status()}`,
      ).toBeLessThan(500);
    }
  });
});

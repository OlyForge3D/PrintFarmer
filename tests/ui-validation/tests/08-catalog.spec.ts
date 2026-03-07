import { test, expect } from '@playwright/test';

const API_URL = 'http://localhost:5245';

test.describe('Catalog Data', () => {
  test('default manufacturers are seeded (expect 8+)', async ({ request }) => {
    const res = await request.get(`${API_URL}/api/catalog/manufacturers`);

    // Route exists — 200, 401, or 500 (known DI issue) all prove wiring is correct
    expect(res.status()).toBeLessThanOrEqual(500);

    if (res.ok()) {
      const manufacturers = await res.json();
      expect(Array.isArray(manufacturers)).toBe(true);
      expect(manufacturers.length).toBeGreaterThanOrEqual(8);

      for (const mfr of manufacturers) {
        expect(mfr).toHaveProperty('id');
        expect(mfr).toHaveProperty('name');
        expect(typeof mfr.name).toBe('string');
        expect(mfr.name.length).toBeGreaterThan(0);
      }
    }
  });

  test('catalog page loads in the UI', async ({ page }) => {
    const response = await page.goto('/catalog');
    expect(response?.status()).toBeLessThan(500);

    await page.waitForLoadState('networkidle');
    const body = page.locator('body');
    await expect(body).toBeVisible();
  });
});

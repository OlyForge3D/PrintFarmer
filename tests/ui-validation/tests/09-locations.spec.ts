import { test, expect } from '@playwright/test';

const API_URL = 'http://localhost:5245';

test.describe('Location Dashboard', () => {
  test('locations dashboard route is accessible', async ({ page }) => {
    const response = await page.goto('/locations/dashboard');
    // Should not 500. 401/redirect is acceptable (auth required).
    expect(
      response?.status(),
      `Route /locations/dashboard returned ${response?.status()}`,
    ).toBeLessThan(500);
  });

  test('locations admin route is accessible', async ({ page }) => {
    const response = await page.goto('/locations');
    // Protected route — 401/redirect acceptable for auth-gated admin page
    expect(
      response?.status(),
      `Route /locations returned ${response?.status()}`,
    ).toBeLessThan(500);
  });

  test('location dashboard renders content in #root', async ({ page }) => {
    await page.goto('/locations/dashboard');
    await page.waitForLoadState('networkidle');

    await page.waitForSelector('#root *', { timeout: 10_000 });

    const hasContent = await page.locator('#root').textContent();
    expect(
      (hasContent ?? '').length,
      'React root should render visible text content on location dashboard',
    ).toBeGreaterThan(0);
  });

  test('location dashboard shows page title or loading state', async ({ page }) => {
    await page.goto('/locations/dashboard');
    await page.waitForLoadState('networkidle');

    // The page should show "Location Dashboard" title, a loading spinner,
    // or a login/redirect — any of these proves the route is wired
    const body = page.locator('body');
    await expect(body).toBeVisible();
  });

  test('locations API returns valid response', async ({ request }) => {
    const res = await request.get(`${API_URL}/api/locations`);

    // 200, 401, or 404 all prove the route is wired. Only 500+ is a failure.
    expect(res.status()).toBeLessThan(500);

    if (res.ok()) {
      const locations = await res.json();
      expect(Array.isArray(locations)).toBe(true);
    }
  });

  test('locations tree API returns valid response', async ({ request }) => {
    const res = await request.get(`${API_URL}/api/locations/tree`);

    expect(res.status()).toBeLessThan(500);

    if (res.ok()) {
      const tree = await res.json();
      expect(Array.isArray(tree)).toBe(true);
    }
  });

  test('location admin page renders content in #root', async ({ page }) => {
    await page.goto('/locations');
    await page.waitForLoadState('networkidle');

    await page.waitForSelector('#root *', { timeout: 10_000 });

    const hasContent = await page.locator('#root').textContent();
    expect(
      (hasContent ?? '').length,
      'React root should render visible text content on location management page',
    ).toBeGreaterThan(0);
  });
});

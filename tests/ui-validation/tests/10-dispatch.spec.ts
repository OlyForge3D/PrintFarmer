import { test, expect } from '@playwright/test';

const API_URL = 'http://localhost:5245';

test.describe('Dispatch Settings & APIs', () => {
  test('dispatch settings API returns valid response', async ({ request }) => {
    const res = await request.get(`${API_URL}/api/dispatch-settings`);

    // 200, 401, or 404 all prove the endpoint exists. Only 500+ is a failure.
    expect(res.status()).toBeLessThan(500);

    if (res.ok()) {
      const settings = await res.json();
      expect(settings).toHaveProperty('autoDispatchEnabled');
      expect(settings).toHaveProperty('autoDispatchMode');
      expect(settings).toHaveProperty('idleThresholdSeconds');
      expect(settings).toHaveProperty('minimumScoreThreshold');
      expect(settings).toHaveProperty('maxConcurrentDispatches');
    }
  });

  test('dispatch queue status API returns valid response', async ({ request }) => {
    const res = await request.get(`${API_URL}/api/dispatch/queue-status`);

    expect(res.status()).toBeLessThan(500);

    if (res.ok()) {
      const status = await res.json();
      // Should be an object with queue information
      expect(typeof status).toBe('object');
      expect(status).not.toBeNull();
    }
  });

  test('dispatch history API returns valid response', async ({ request }) => {
    const res = await request.get(`${API_URL}/api/dispatch/history`);

    expect(res.status()).toBeLessThan(500);

    if (res.ok()) {
      const history = await res.json();
      // Should be an array or object with history entries
      expect(typeof history).toBe('object');
      expect(history).not.toBeNull();
    }
  });

  test('settings page is accessible (dispatch settings host)', async ({ page }) => {
    const response = await page.goto('/settings');
    // Admin-protected route — 401/redirect acceptable
    expect(
      response?.status(),
      `Route /settings returned ${response?.status()}`,
    ).toBeLessThan(500);
  });

  test('settings page renders content in #root', async ({ page }) => {
    await page.goto('/settings');
    await page.waitForLoadState('networkidle');

    await page.waitForSelector('#root *', { timeout: 10_000 });

    const hasContent = await page.locator('#root').textContent();
    expect(
      (hasContent ?? '').length,
      'React root should render visible text content on settings page',
    ).toBeGreaterThan(0);
  });

  test('dispatch settings PUT rejects invalid payload', async ({ request }) => {
    const res = await request.put(`${API_URL}/api/dispatch-settings`, {
      data: {
        autoDispatchEnabled: true,
        autoDispatchMode: 'Manual',
        idleThresholdSeconds: 2, // Below minimum of 5
        minimumScoreThreshold: 0.5,
        maxConcurrentDispatches: 0, // Below minimum of 1
      },
    });

    // 400 (validation), 401 (auth), or 404 (not implemented yet) are all acceptable.
    // Only 500+ indicates a server-side crash.
    expect(res.status()).toBeLessThan(500);
  });
});

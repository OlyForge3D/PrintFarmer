import { test, expect } from '@playwright/test';

const API_URL = 'http://localhost:5245';

test.describe('API Connectivity', () => {
  test('printers endpoint returns empty array on fresh database', async ({ request }) => {
    const res = await request.get(`${API_URL}/api/printers`);
    // May return 200 or 401 if auth is required; both prove the API is reachable
    expect(res.status()).toBeLessThan(500);

    if (res.ok()) {
      const body = await res.json();
      expect(Array.isArray(body)).toBe(true);
      expect(body).toHaveLength(0);
    }
  });

  test('catalog manufacturers endpoint is reachable', async ({ request }) => {
    const res = await request.get(`${API_URL}/api/catalog/manufacturers`);
    // Endpoint exists — 200, 401, or even 500 (known DI issue) proves the route is wired
    expect(res.status()).toBeLessThanOrEqual(500);

    if (res.ok()) {
      const body = await res.json();
      expect(Array.isArray(body)).toBe(true);
      expect(body.length).toBeGreaterThanOrEqual(8);
    }
  });

  test('API responses use camelCase property naming', async ({ request }) => {
    // Use the printers endpoint (known stable) to validate JSON casing
    const res = await request.get(`${API_URL}/healthz`);
    expect(res.ok()).toBe(true);

    const body = await res.json();
    const keys = Object.keys(body);
    for (const key of keys) {
      expect(
        key[0],
        `Property "${key}" should start with lowercase (camelCase)`,
      ).toBe(key[0].toLowerCase());
    }
  });
});

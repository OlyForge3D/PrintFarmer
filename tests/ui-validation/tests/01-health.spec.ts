import { test, expect } from '@playwright/test';

const API_URL = 'http://localhost:5245';

test.describe('Health Endpoints', () => {
  test('GET /healthz returns ok', async ({ request }) => {
    const res = await request.get(`${API_URL}/healthz`);
    expect(res.ok()).toBe(true);

    const body = await res.json();
    expect(body).toHaveProperty('status', 'ok');
  });

  test('GET /health returns detailed health status', async ({ request }) => {
    const res = await request.get(`${API_URL}/health`);
    // /health returns 200 for Healthy, 503 for Degraded/Unhealthy — both are valid responses
    expect([200, 503]).toContain(res.status());

    const body = await res.json();
    expect(body).toHaveProperty('status');
    expect(['Healthy', 'Degraded', 'Unhealthy']).toContain(body.status);
  });
});

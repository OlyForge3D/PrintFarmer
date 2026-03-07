import { test, expect } from '@playwright/test';

const API_URL = 'http://localhost:5245';

test.describe('SignalR Hub', () => {
  test('SignalR negotiate endpoint is reachable', async ({ request }) => {
    // SignalR negotiate uses POST
    const res = await request.post(`${API_URL}/hubs/printers/negotiate?negotiateVersion=1`);
    // 200 = success, 401 = auth required (still proves hub exists)
    expect(
      res.status(),
      `SignalR negotiate returned ${res.status()}`,
    ).toBeLessThan(500);
  });
});

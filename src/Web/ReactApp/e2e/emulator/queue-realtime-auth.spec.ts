import { test, expect } from '../fixtures/emulator-setup';

test.describe('Queue realtime authentication — Emulator', () => {
  test('uses the local-storage JWT for WebSocket auth and REST reconciliation', async ({
    page,
  }) => {
    const printerSocketUrls: string[] = [];
    const printerHubUnauthorizedStatuses: number[] = [];
    const queueResponseStatuses: number[] = [];

    page.on('websocket', (socket) => {
      if (new URL(socket.url()).pathname === '/hubs/printers') {
        printerSocketUrls.push(socket.url());
      }
    });
    page.on('response', (response) => {
      const url = new URL(response.url());
      if (url.pathname.startsWith('/hubs/printers') && response.status() === 401) {
        printerHubUnauthorizedStatuses.push(response.status());
      }
      if (url.pathname === '/api/job-queue-analytics') {
        queueResponseStatuses.push(response.status());
      }
    });

    await page.reload();
    await page.waitForLoadState('networkidle');

    await expect.poll(
      () =>
        printerSocketUrls.some((url) => {
          const socketUrl = new URL(url);
          return Boolean(socketUrl.searchParams.get('access_token'));
        }),
      { timeout: 15_000 },
    ).toBe(true);
    await expect.poll(
      () => queueResponseStatuses.some((status) => status === 200),
      { timeout: 15_000 },
    ).toBe(true);
    expect(printerHubUnauthorizedStatuses).toEqual([]);
  });
});

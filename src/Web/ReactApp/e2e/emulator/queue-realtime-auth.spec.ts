import { test, expect } from '../fixtures/emulator-setup';

test.describe('Queue realtime authentication — Emulator', () => {
  test('uses the local-storage JWT for WebSocket auth and REST reconciliation', async ({
    page,
  }) => {
    const printerSocketUrls: string[] = [];
    const printerHubUnauthorizedStatuses: number[] = [];
    const resourceResponseStatuses: number[] = [];

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
      if (url.pathname === '/api/job-queue/subscription-resources') {
        resourceResponseStatuses.push(response.status());
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
      () => resourceResponseStatuses.some((status) => status === 200),
      { timeout: 15_000 },
    ).toBe(true);
    expect(printerHubUnauthorizedStatuses).toEqual([]);
  });

  test('restores subscriptions, rejects unauthorized groups, drains gaps, and refetches queue data', async ({
    page,
  }) => {
    await page.addInitScript(() => {
      window.PrintFarmerDebug = { printerSignalR: true };
    });
    const changeFeedRequests: string[] = [];
    let queueListResponses = 0;
    let queueStatsResponses = 0;
    page.on('request', (request) => {
      const url = new URL(request.url());
      if (url.pathname === '/api/job-queue/changes') {
        changeFeedRequests.push(url.toString());
      }
    });
    page.on('response', (response) => {
      const path = new URL(response.url()).pathname;
      if (path === '/api/job-queue-analytics' && response.status() === 200) {
        queueListResponses++;
      }
      if (path === '/api/job-queue-analytics/stats' && response.status() === 200) {
        queueStatsResponses++;
      }
    });

    await page.goto('/printQueue');
    await page.waitForLoadState('networkidle');
    await expect.poll(
      () => page.evaluate(() => {
        const service = window.PrintFarmerDebug?.printerSignalRService as
          | { isConnected?: boolean }
          | undefined;
        return service?.isConnected === true;
      }),
      { timeout: 15_000 }
    ).toBe(true);

    const beforeReconnect = await page.evaluate(() => {
      const service = window.PrintFarmerDebug!.printerSignalRService as {
        getQueueSubscriptionSnapshot: () => {
          printerIds: string[];
          jobIds: string[];
          projectIds: string[];
          lastSequence: number;
        };
      };
      return service.getQueueSubscriptionSnapshot();
    });
    const unauthorizedId = 'not-a-valid-job-id';
    const unauthorizedAccepted = await page.evaluate(async (jobId) => {
      const service = window.PrintFarmerDebug!.printerSignalRService as {
        subscribeToQueueJob: (id: string) => Promise<void>;
        getQueueSubscriptionSnapshot: () => { jobIds: string[] };
      };
      try {
        await service.subscribeToQueueJob(jobId);
        return true;
      } catch {
        return service.getQueueSubscriptionSnapshot().jobIds.includes(jobId);
      }
    }, unauthorizedId);
    expect(unauthorizedAccepted).toBe(false);

    await page.evaluate(async () => {
      const service = window.PrintFarmerDebug!.printerSignalRService as {
        disconnect: () => Promise<void>;
        connect: () => Promise<void>;
      };
      await service.disconnect();
      await service.connect();
    });
    await expect.poll(
      () => page.evaluate(() => {
        const service = window.PrintFarmerDebug!.printerSignalRService as {
          isConnected: boolean;
        };
        return service.isConnected;
      }),
      { timeout: 15_000 }
    ).toBe(true);
    const afterReconnect = await page.evaluate(() => {
      const service = window.PrintFarmerDebug!.printerSignalRService as {
        getQueueSubscriptionSnapshot: () => {
          printerIds: string[];
          jobIds: string[];
          projectIds: string[];
          lastSequence: number;
        };
      };
      return service.getQueueSubscriptionSnapshot();
    });
    expect(afterReconnect.printerIds).toEqual(beforeReconnect.printerIds);
    expect(afterReconnect.jobIds).toEqual(beforeReconnect.jobIds);
    expect(afterReconnect.projectIds).toEqual(beforeReconnect.projectIds);

    const listBaseline = queueListResponses;
    const statsBaseline = queueStatsResponses;
    const changeBaseline = changeFeedRequests.length;
    await page.evaluate(async () => {
      const service = window.PrintFarmerDebug!.printerSignalRService as {
        getQueueSubscriptionSnapshot: () => { lastSequence: number };
        handleQueueEvent: (event: Record<string, unknown>) => Promise<void>;
      };
      const sequence =
        service.getQueueSubscriptionSnapshot().lastSequence + 2;
      await service.handleQueueEvent({
        schemaVersion: '2',
        eventId: crypto.randomUUID(),
        sequence,
        eventType: 'PrintFarmer.Queue.BrowserGapProbe.v1',
        occurredAtUtc: new Date().toISOString(),
      });
    });

    await expect.poll(() => changeFeedRequests.length).toBeGreaterThan(
      changeBaseline
    );
    await expect.poll(() => queueListResponses).toBeGreaterThan(listBaseline);
    await expect.poll(() => queueStatsResponses).toBeGreaterThan(statsBaseline);
  });

  test('discovers a server-created queue job after connect and refetches queue data', async ({
    page,
  }) => {
    await page.addInitScript(() => {
      window.PrintFarmerDebug = { printerSignalR: true };
    });
    let resourceResponses = 0;
    let queueListResponses = 0;
    let queueStatsResponses = 0;
    page.on('response', (response) => {
      if (response.status() !== 200) return;
      const path = new URL(response.url()).pathname;
      if (path === '/api/job-queue/subscription-resources') {
        resourceResponses++;
      } else if (path === '/api/job-queue-analytics') {
        queueListResponses++;
      } else if (path === '/api/job-queue-analytics/stats') {
        queueStatsResponses++;
      }
    });

    await page.goto('/printQueue');
    await page.waitForLoadState('networkidle');
    await expect
      .poll(
        () =>
          page.evaluate(
            () =>
              (
                window.PrintFarmerDebug?.printerSignalRService as
                  | { isConnected?: boolean }
                  | undefined
              )?.isConnected === true
          ),
        { timeout: 15_000 }
      )
      .toBe(true);
    const baseline = {
      resources: resourceResponses,
      queue: queueListResponses,
      stats: queueStatsResponses,
    };

    const jobId = await page.evaluate(async () => {
      const token = localStorage.getItem('auth-token');
      const authorization = { Authorization: `Bearer ${token ?? ''}` };
      const printersResponse = await fetch('/api/printers', {
        headers: authorization,
      });
      if (!printersResponse.ok) {
        throw new Error(`Printers failed: ${printersResponse.status}`);
      }
      const printerPayload = (await printersResponse.json()) as
        | Array<{ id: string; name?: string }>
        | { items?: Array<{ id: string; name?: string }> };
      const printers = Array.isArray(printerPayload)
        ? printerPayload
        : (printerPayload.items ?? []);
      const printer =
        printers.find((candidate) => candidate.name === 'Test Printer Alpha') ??
        printers[0];
      if (!printer) {
        throw new Error('No emulator printer is available');
      }
      const form = new FormData();
      form.append(
        'file',
        new Blob(['; server-driven resource discovery\nG28\n'], {
          type: 'text/plain',
        }),
        `realtime-${crypto.randomUUID()}.gcode`
      );
      const uploadResponse = await fetch('/api/gcode-files/upload', {
        method: 'POST',
        headers: authorization,
        body: form,
      });
      if (!uploadResponse.ok) {
        throw new Error(
          `Upload failed: ${uploadResponse.status} ${await uploadResponse.text()}`
        );
      }

      const upload = (await uploadResponse.json()) as { id: string };
      const queueResponse = await fetch('/api/job-queue', {
        method: 'POST',
        headers: {
          Authorization: `Bearer ${token ?? ''}`,
          'Content-Type': 'application/json',
        },
        body: JSON.stringify({
          gcodeFileId: upload.id,
          assignedPrinterId: printer.id,
          priority: 2,
          idempotencyKey: crypto.randomUUID(),
        }),
      });
      if (!queueResponse.ok) {
        throw new Error(
          `Queue creation failed: ${queueResponse.status} ${await queueResponse.text()}`
        );
      }

      return ((await queueResponse.json()) as { id: string }).id;
    });

    await expect
      .poll(() => resourceResponses, { timeout: 15_000 })
      .toBeGreaterThan(baseline.resources);
    await expect
      .poll(
        () =>
          page.evaluate((id) => {
            const service = window.PrintFarmerDebug!
              .printerSignalRService as {
              getQueueSubscriptionSnapshot: () => { jobIds: string[] };
            };
            return service.getQueueSubscriptionSnapshot().jobIds.includes(id);
          }, jobId),
        { timeout: 15_000 }
      )
      .toBe(true);
    await expect
      .poll(() => queueListResponses, { timeout: 15_000 })
      .toBeGreaterThan(baseline.queue);
    await expect
      .poll(() => queueStatsResponses, { timeout: 15_000 })
      .toBeGreaterThan(baseline.stats);
  });

  test('maintenance client traffic never uses the retired raw G-code route', async ({
    page,
  }) => {
    const physicalRequests: string[] = [];
    page.on('request', (request) => {
      if (request.method() === 'POST') {
        physicalRequests.push(new URL(request.url()).pathname);
      }
    });

    const status = await page.evaluate(async () => {
      const token = localStorage.getItem('auth-token');
      const response = await fetch(
        '/api/printers/00000000-0000-0000-0000-000000000099/extrude',
        {
          method: 'POST',
          headers: {
            Authorization: `Bearer ${token ?? ''}`,
            'Content-Type': 'application/json',
          },
          body: JSON.stringify({
            distanceMm: -1,
            feedrateMmPerMinute: 300,
          }),
        }
      );
      return response.status;
    });

    expect([404, 409]).toContain(status);
    expect(
      physicalRequests.some((path) => path.endsWith('/extrude'))
    ).toBe(true);
    expect(
      physicalRequests.some((path) => path.endsWith('/gcode'))
    ).toBe(false);
  });
});

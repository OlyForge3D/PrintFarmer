import { test as emulatorTest, expect, API_BASE_URL, getStoredAuthToken, dismissTourIfVisible } from '../fixtures/emulator-setup';
import type { Page } from '@playwright/test';

type SignalRFrame = Record<string, unknown> & {
  type?: number;
  target?: string;
  arguments?: unknown[];
  invocationId?: string;
  error?: string;
  result?: unknown;
};

type CapturedFrame = {
  socketIndex: number;
  frame: SignalRFrame;
};

type RoutedHubSocket = {
  sendToServer: (payload: string) => void;
  sendToClient: (payload: string) => void;
  close: () => void;
};

type QueueResourceSnapshot = {
  printerIds: string[];
  jobIds: string[];
  projectIds: string[];
};

type QueueChangeFeedSnapshot = {
  afterSequence: number;
  nextSequence: number;
  events: Array<{ jobId?: string | null; sequence: number }>;
};

type QueueTestResources = { jobIds: string[]; fileIds: string[] };

const test = emulatorTest.extend<{ queueResources: QueueTestResources }>({
  queueResources: [async ({ page }, use) => {
    const resources: QueueTestResources = { jobIds: [], fileIds: [] };
    await use(resources);
    const token = await getStoredAuthToken(page);
    const headers = { Authorization: `Bearer ${token}` };
    const cleanupJob = async (id: string) => {
      const url = `${API_BASE_URL}/api/job-queue/${id}`;
      await expect.poll(async () => {
        const current = await page.request.get(url, { headers });
        expect(current.status(), 'Created queue job must remain available for cleanup').toBe(200);
        const etag = current.headers().etag;
        expect(Boolean(etag), 'Queue cleanup requires the current revision').toBe(true);
        const removed = await page.request.delete(url, {
          headers: { ...headers, 'If-Match': etag },
        });
        expect([204, 412], 'Only a stale revision permits another cleanup attempt').toContain(removed.status());
        return removed.status();
      }, { timeout: 10_000 }).toBe(204);
      expect((await page.request.get(url, { headers })).status()).toBe(404);
    };
    const jobCleanup: PromiseSettledResult<void>[] = [];
    for (const id of resources.jobIds) {
      jobCleanup.push(...await Promise.allSettled([cleanupJob(id)]));
    }
    const fileCleanup = await Promise.allSettled(resources.fileIds.map(async id => {
      const url = `${API_BASE_URL}/api/gcode-files/${id}`;
      const removed = await page.request.delete(url, { headers });
      expect(removed.status(), 'Uploaded queue fixture must be removed').toBe(204);
      expect((await page.request.get(`${API_BASE_URL}/api/gcode-files/file/${id}`, { headers })).status()).toBe(404);
    }));
    const failures = [...jobCleanup, ...fileCleanup]
      .filter((result): result is PromiseRejectedResult => result.status === 'rejected')
      .map(result => result.reason as unknown);
    if (failures.length > 0) throw new AggregateError(failures, 'Queue fixture cleanup failed');
  }, { auto: true }],
});

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null;
}

function parseSignalRFrames(payload: string | Buffer): SignalRFrame[] {
  const text = typeof payload === 'string' ? payload : payload.toString('utf8');
  const frames: SignalRFrame[] = [];

  for (const message of text.split('\u001e')) {
    try {
      const frame: unknown = JSON.parse(message);
      if (isRecord(frame)) frames.push(frame as SignalRFrame);
    } catch {
      // Ignore the SignalR handshake and any non-JSON protocol frames.
    }
  }

  return frames;
}

function subscriptionsOnSocket(
  frames: CapturedFrame[],
  socketIndex: number,
): QueueResourceSnapshot {
  const printerIds = new Set<string>();
  const jobIds = new Set<string>();
  const projectIds = new Set<string>();

  for (const { socketIndex: frameSocketIndex, frame } of frames) {
    if (frameSocketIndex !== socketIndex || frame.type !== 1) continue;
    const [firstArgument] = frame.arguments ?? [];
    if (frame.target === 'SubscribeToPrintersAsync' && Array.isArray(firstArgument)) {
      for (const id of firstArgument) {
        if (typeof id === 'string') printerIds.add(id);
      }
    } else if (
      frame.target === 'SubscribeToPrinterAsync' &&
      typeof firstArgument === 'string'
    ) {
      printerIds.add(firstArgument);
    } else if (
      frame.target === 'SubscribeToQueueJobAsync' &&
      typeof firstArgument === 'string'
    ) {
      jobIds.add(firstArgument);
    } else if (
      frame.target === 'SubscribeToProjectAsync' &&
      typeof firstArgument === 'string'
    ) {
      projectIds.add(firstArgument);
    }
  }

  return {
    printerIds: [...printerIds].sort(),
    jobIds: [...jobIds].sort(),
    projectIds: [...projectIds].sort(),
  };
}

function hasSuccessfulInvocation(
  clientFrames: CapturedFrame[],
  serverFrames: CapturedFrame[],
  socketIndex: number,
  target: string,
  matchesArguments: (args: unknown[]) => boolean,
): boolean {
  return clientFrames.some(({ socketIndex: frameSocketIndex, frame }) => {
    if (
      frameSocketIndex !== socketIndex ||
      frame.type !== 1 ||
      frame.target !== target ||
      typeof frame.invocationId !== 'string' ||
      !matchesArguments(frame.arguments ?? [])
    ) {
      return false;
    }

    return serverFrames.some(({ socketIndex: responseSocketIndex, frame: response }) =>
      responseSocketIndex === socketIndex &&
      response.type === 3 &&
      response.invocationId === frame.invocationId &&
      typeof response.error !== 'string'
    );
  });
}

async function createQueueJob(
  page: Page,
  resources: QueueTestResources,
): Promise<{ id: string; printerId: string; fileName: string }> {
    const token = await getStoredAuthToken(page);
    const authorization = { Authorization: `Bearer ${token ?? ''}` };
    const printersResponse = await page.request.get(`${API_BASE_URL}/api/printers`, {
      headers: authorization,
    });
    if (!printersResponse.ok()) {
      throw new Error(`Printers failed: ${printersResponse.status()}`);
    }
    const printerPayload: unknown = await printersResponse.json();
    const printers = Array.isArray(printerPayload)
      ? printerPayload as Array<{ id: string; name?: string }>
      : isRecord(printerPayload) && Array.isArray(printerPayload.items)
        ? printerPayload.items as Array<{ id: string; name?: string }>
        : [];
    const printer =
      printers.find((candidate) => candidate.name === 'Test Printer Alpha') ??
      printers[0];
    if (!printer) throw new Error('No emulator printer is available');

    const fileName = `realtime-${crypto.randomUUID()}.gcode`;
    const uploadResponse = await page.request.post(`${API_BASE_URL}/api/gcode-files/upload`, {
      headers: authorization,
      multipart: {
        file: {
          name: fileName,
          mimeType: 'text/plain',
          buffer: Buffer.from(`; ${fileName}\n; server-driven queue event\nG28\n`),
        },
      },
    });
    if (!uploadResponse.ok()) {
      throw new Error(
        `Upload failed: ${uploadResponse.status()} ${await uploadResponse.text()}`,
      );
    }
    const upload = await uploadResponse.json() as { id: string };
    resources.fileIds.push(upload.id);

    const queueResponse = await page.request.post(`${API_BASE_URL}/api/job-queue`, {
      headers: {
        ...authorization,
        'Content-Type': 'application/json',
      },
      data: {
        gcodeFileId: upload.id,
        assignedPrinterId: printer.id,
        priority: 'Normal',
      },
    });
    if (!queueResponse.ok()) {
      throw new Error(
        `Queue creation failed: ${queueResponse.status()} ${await queueResponse.text()}`,
      );
    }

    const job = await queueResponse.json() as { id: string };
    resources.jobIds.push(job.id);
    return { id: job.id, printerId: printer.id, fileName };
}

async function probeDeniedQueueSubscription(
  page: Page,
  invocationId: string,
  jobId: string,
): Promise<SignalRFrame> {
  return page.evaluate(async ({ invocationId, jobId }) => {
    const username = `queue-denied-${crypto.randomUUID()}`;
    const password = `Queue!${crypto.randomUUID()}aA1`;
    const adminHeaders = {
      Authorization: `Bearer ${localStorage.getItem('auth-token') ?? ''}`,
      'Content-Type': 'application/json',
    };
    const created = await fetch('/api/users', {
      method: 'POST',
      headers: adminHeaders,
      body: JSON.stringify({
        username,
        email: `${username}@printfarmer.test`,
        password,
        roleIds: [],
      }),
    });
    if (created.status !== 201) {
      throw new Error(`Denied-subscription user creation failed: ${created.status}`);
    }
    const user = await created.json() as { id: string };
    const deleteProbeUser = async () => {
      const deleted = await fetch(`/api/users/${user.id}`, {
        method: 'DELETE',
        headers: adminHeaders,
      });
      if (!deleted.ok) {
        throw new Error(`Denied-subscription user cleanup failed: ${deleted.status}`);
      }
    };
    try {
      const login = await fetch('/api/auth/login', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ usernameOrEmail: username, password }),
      });
      if (!login.ok) {
        throw new Error(`Denied-subscription login failed: ${login.status}`);
      }
      const auth = await login.json() as {
        success: boolean;
        token?: string;
        user?: { roles: string[] };
      };
      if (
        !auth.success || !auth.token || !auth.user ||
        auth.user.roles.length !== 0
      ) {
        throw new Error('Denied-subscription probe requires an authenticated roleless user');
      }

      const hubUrl = new URL('/hubs/printers', location.href);
      hubUrl.protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
      hubUrl.searchParams.set('access_token', auth.token);
      const socket = new WebSocket(hubUrl);
      try {
        return await new Promise<SignalRFrame>((resolve, reject) => {
          const timeout = setTimeout(
            () => reject(new Error('Denied-subscription completion was not received')),
            10_000,
          );
          socket.onopen = () => {
            socket.send('{"protocol":"json","version":1}\u001e');
            socket.send(`${JSON.stringify({
              type: 1,
              invocationId,
              target: 'SubscribeToQueueJobAsync',
              arguments: [jobId],
            })}\u001e`);
          };
          socket.onmessage = (event: MessageEvent<string>) => {
            for (const message of event.data.split('\u001e')) {
              if (!message) continue;
              const frame = JSON.parse(message) as SignalRFrame;
              if (frame.type === 3 && frame.invocationId === invocationId) {
                clearTimeout(timeout);
                resolve({
                  type: frame.type,
                  invocationId: frame.invocationId,
                  error: frame.error,
                  result: frame.result,
                });
              }
            }
          };
          socket.onerror = () => {
            clearTimeout(timeout);
            reject(new Error('Denied-subscription WebSocket transport failed'));
          };
          socket.onclose = () => {
            clearTimeout(timeout);
            reject(new Error('Denied-subscription WebSocket closed before completion'));
          };
        });
      } finally {
        socket.close();
      }
    } finally {
      await deleteProbeUser();
    }
  }, { invocationId, jobId });
}

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
    queueResources,
  }) => {
    test.setTimeout(60_000);
    const clientFrames: CapturedFrame[] = [];
    const serverFrames: CapturedFrame[] = [];
    const hubSockets: RoutedHubSocket[] = [];
    const changeFeeds: QueueChangeFeedSnapshot[] = [];
    const subscriptionResources: QueueResourceSnapshot[] = [];
    let dropQueueEvents = false;
    let droppedQueueEvents = 0;
    let queueListResponses = 0;
    let queueStatsResponses = 0;
    page.on('response', (response) => {
      const path = new URL(response.url()).pathname;
      if (path === '/api/job-queue-analytics' && response.status() === 200) {
        queueListResponses++;
      }
      if (path === '/api/job-queue-analytics/stats' && response.status() === 200) {
        queueStatsResponses++;
      }
      if (
        path === '/api/job-queue/subscription-resources' &&
        response.status() === 200
      ) {
        void response.json().then((body: unknown) => {
          if (!isRecord(body)) return;
          subscriptionResources.push({
            printerIds: Array.isArray(body.printerIds)
              ? body.printerIds.filter((id): id is string => typeof id === 'string')
              : [],
            jobIds: Array.isArray(body.jobIds)
              ? body.jobIds.filter((id): id is string => typeof id === 'string')
              : [],
            projectIds: Array.isArray(body.projectIds)
              ? body.projectIds.filter((id): id is string => typeof id === 'string')
              : [],
          });
        }).catch(() => undefined);
      }
      if (path === '/api/job-queue/changes' && response.status() === 200) {
        void response.json().then((body: unknown) => {
          if (!isRecord(body) || !Array.isArray(body.events)) return;
          const events = body.events.filter(isRecord).flatMap((event) =>
            typeof event.sequence === 'number'
              ? [{
                  jobId: typeof event.jobId === 'string' ? event.jobId : null,
                  sequence: event.sequence,
                }]
              : [],
          );
          changeFeeds.push({
            afterSequence: typeof body.afterSequence === 'number' ? body.afterSequence : -1,
            nextSequence: typeof body.nextSequence === 'number' ? body.nextSequence : -1,
            events,
          });
        }).catch(() => undefined);
      }
    });

    await page.routeWebSocket(
      (url) => url.pathname === '/hubs/printers',
      (socket) => {
        const socketIndex = hubSockets.length;
        const server = socket.connectToServer();
        hubSockets.push({
          sendToServer: (payload) => server.send(payload),
          sendToClient: (payload) => socket.send(payload),
          close: () => { void socket.close(); },
        });
        socket.onMessage((payload) => {
          clientFrames.push(
            ...parseSignalRFrames(payload).map((frame) => ({ socketIndex, frame })),
          );
          server.send(payload);
        });
        server.onMessage((payload) => {
          const text = typeof payload === 'string' ? payload : payload.toString('utf8');
          const forwarded: string[] = [];
          for (const message of text.split('\u001e')) {
            if (!message) continue;
            let shouldForward = true;
            try {
              const frame: unknown = JSON.parse(message);
              if (isRecord(frame)) {
                serverFrames.push({ socketIndex, frame: frame as SignalRFrame });
                if (
                  dropQueueEvents &&
                  frame.type === 1 &&
                  frame.target === 'queueevent'
                ) {
                  shouldForward = false;
                  droppedQueueEvents++;
                }
              }
            } catch {
              // Preserve any protocol frame that is not JSON.
            }
            if (shouldForward) forwarded.push(message);
          }
          if (forwarded.length > 0) {
            socket.send(`${forwarded.join('\u001e')}\u001e`);
          }
        });
      },
    );

    await page.goto('/printQueue');
    await dismissTourIfVisible(page);
    await page.getByRole('button', { name: 'List view', exact: true }).click();
    await page.waitForLoadState('networkidle');
    await expect.poll(() => hubSockets.length).toBeGreaterThan(0);
    await expect.poll(() => subscriptionResources.length).toBeGreaterThan(0);
    await expect.poll(() => {
      const socketIndex = hubSockets.length - 1;
      return hasSuccessfulInvocation(
        clientFrames,
        serverFrames,
        socketIndex,
        'SubscribeToPrintersAsync',
        (args) => Array.isArray(args[0]) && args[0].length > 0,
      );
    }).toBe(true);
    await expect.poll(() => changeFeeds.length).toBeGreaterThan(0);

    const unauthorizedInvocationId = `unauthorized-${crypto.randomUUID()}`;
    const unauthorizedJobId = crypto.randomUUID();
    const activeSocketIndex = hubSockets.length - 1;
    // The fixture is a farm admin, which may join any job group. Probe denial
    // with a real roleless account without changing the app's administrator session.
    const deniedCompletion = await probeDeniedQueueSubscription(
      page,
      unauthorizedInvocationId,
      unauthorizedJobId,
    );
    expect(
      deniedCompletion,
      'Roleless user must be denied the inaccessible job group; captured completion includes error and result',
    ).toEqual({
      type: 3,
      invocationId: unauthorizedInvocationId,
      error: expect.stringMatching(/\bresource_forbidden\b/),
      result: undefined,
    });

    const reconnectJob = await createQueueJob(page, queueResources);
    await expect.poll(() => subscriptionResources.some((resources) =>
      resources.jobIds.includes(reconnectJob.id)
    ), { timeout: 20_000 }).toBe(true);
    await expect.poll(() => hasSuccessfulInvocation(
      clientFrames,
      serverFrames,
      activeSocketIndex,
      'SubscribeToQueueJobAsync',
      (args) => args[0] === reconnectJob.id,
    ),
      { timeout: 20_000 },
    ).toBe(true);
    await expect(page.getByText(reconnectJob.fileName, { exact: true })).toBeVisible();

    const beforeReconnect = subscriptionsOnSocket(clientFrames, activeSocketIndex);
    const reconnectSocketCount = hubSockets.length;
    const changeFeedBaseline = changeFeeds.length;
    hubSockets[activeSocketIndex].close();
    await expect.poll(() => hubSockets.length, { timeout: 20_000 })
      .toBeGreaterThan(reconnectSocketCount);
    const reconnectedSocketIndex = reconnectSocketCount;
    await expect.poll(() => {
      const afterReconnect = subscriptionsOnSocket(clientFrames, reconnectedSocketIndex);
      return afterReconnect.printerIds.length > 0 &&
        afterReconnect.jobIds.includes(reconnectJob.id) &&
        hasSuccessfulInvocation(
          clientFrames,
          serverFrames,
          reconnectedSocketIndex,
          'SubscribeToPrintersAsync',
          (args) => Array.isArray(args[0]) && args[0].length > 0,
        ) &&
        hasSuccessfulInvocation(
          clientFrames,
          serverFrames,
          reconnectedSocketIndex,
          'SubscribeToQueueJobAsync',
          (args) => args[0] === reconnectJob.id,
        );
    }, { timeout: 20_000 }).toBe(true);
    expect(subscriptionsOnSocket(clientFrames, reconnectedSocketIndex))
      .toEqual(beforeReconnect);
    await expect.poll(() => changeFeeds.length, { timeout: 20_000 })
      .toBeGreaterThan(changeFeedBaseline);

    dropQueueEvents = true;
    const droppedQueueEventBaseline = droppedQueueEvents;
    const gapJob = await createQueueJob(page, queueResources);
    await expect.poll(() => subscriptionResources.some((resources) =>
      resources.jobIds.includes(gapJob.id)
    ), { timeout: 20_000 }).toBe(true);
    await expect.poll(() => hasSuccessfulInvocation(
      clientFrames,
      serverFrames,
      reconnectedSocketIndex,
      'SubscribeToQueueJobAsync',
      (args) => args[0] === gapJob.id,
    ),
      { timeout: 20_000 },
    ).toBe(true);
    await expect(page.getByText(gapJob.fileName, { exact: true })).toBeVisible();
    await expect.poll(() => droppedQueueEvents, { timeout: 20_000 })
      .toBeGreaterThan(droppedQueueEventBaseline);

    const gapCursor = Math.max(...changeFeeds.map((feed) => feed.nextSequence));
    const watermark = await page.evaluate(async () => {
      const token = localStorage.getItem('auth-token');
      const response = await fetch('/api/job-queue/changes/watermark', {
        headers: { Authorization: `Bearer ${token ?? ''}` },
      });
      if (!response.ok) throw new Error(`Watermark failed: ${response.status}`);
      return await response.json() as { latestSequence: number };
    });
    const gapSequence = Math.max(watermark.latestSequence + 2, gapCursor + 2);
    expect(gapSequence).toBeGreaterThan(gapCursor + 1);
    const listBaseline = queueListResponses;
    const statsBaseline = queueStatsResponses;
    const changeBaseline = changeFeeds.length;
    hubSockets[reconnectedSocketIndex].sendToClient(
      `${JSON.stringify({
        type: 1,
        target: 'queueevent',
        arguments: [{
          schemaVersion: '3',
          eventId: crypto.randomUUID(),
          sequence: gapSequence,
          eventType: 'PrintFarmer.Queue.BrowserGapProbe.v1',
          occurredAtUtc: new Date().toISOString(),
        }],
      })}\u001e`,
    );

    await expect.poll(() => changeFeeds.slice(changeBaseline).some((feed) =>
      feed.afterSequence === gapCursor &&
      feed.events.some((event) => event.jobId === gapJob.id)
    ), { timeout: 20_000 }).toBe(true);
    await expect.poll(() => queueListResponses, { timeout: 20_000 })
      .toBeGreaterThan(listBaseline);
    await expect.poll(() => queueStatsResponses, { timeout: 20_000 })
      .toBeGreaterThan(statsBaseline);
    await expect(page.getByText(gapJob.fileName, { exact: true })).toBeVisible();
  });

  test('discovers a server-created queue job after connect and refetches queue data', async ({
    page,
    queueResources,
  }) => {
    test.setTimeout(45_000);
    const clientFrames: CapturedFrame[] = [];
    const serverFrames: CapturedFrame[] = [];
    const hubSockets: RoutedHubSocket[] = [];
    const subscriptionResources: QueueResourceSnapshot[] = [];
    let resourceResponses = 0;
    let queueListResponses = 0;
    let queueStatsResponses = 0;
    page.on('response', (response) => {
      if (response.status() !== 200) return;
      const path = new URL(response.url()).pathname;
      if (path === '/api/job-queue/subscription-resources') {
        resourceResponses++;
        void response.json().then((body: unknown) => {
          if (!isRecord(body)) return;
          subscriptionResources.push({
            printerIds: Array.isArray(body.printerIds)
              ? body.printerIds.filter((id): id is string => typeof id === 'string')
              : [],
            jobIds: Array.isArray(body.jobIds)
              ? body.jobIds.filter((id): id is string => typeof id === 'string')
              : [],
            projectIds: Array.isArray(body.projectIds)
              ? body.projectIds.filter((id): id is string => typeof id === 'string')
              : [],
          });
        }).catch(() => undefined);
      } else if (path === '/api/job-queue-analytics') {
        queueListResponses++;
      } else if (path === '/api/job-queue-analytics/stats') {
        queueStatsResponses++;
      }
    });
    await page.routeWebSocket(
      (url) => url.pathname === '/hubs/printers',
      (socket) => {
        const socketIndex = hubSockets.length;
        const server = socket.connectToServer();
        hubSockets.push({
          sendToServer: (payload) => server.send(payload),
          sendToClient: (payload) => socket.send(payload),
          close: () => { void socket.close(); },
        });
        socket.onMessage((payload) => {
          clientFrames.push(
            ...parseSignalRFrames(payload).map((frame) => ({ socketIndex, frame })),
          );
          server.send(payload);
        });
        server.onMessage((payload) => {
          serverFrames.push(
            ...parseSignalRFrames(payload).map((frame) => ({ socketIndex, frame })),
          );
          socket.send(payload);
        });
      },
    );

    await page.goto('/printQueue');
    await dismissTourIfVisible(page);
    await page.getByRole('button', { name: 'List view', exact: true }).click();
    await page.waitForLoadState('networkidle');
    await expect.poll(() => hubSockets.length).toBeGreaterThan(0);
    await expect.poll(() => subscriptionResources.length).toBeGreaterThan(0);

    const baseline = {
      resources: resourceResponses,
      queue: queueListResponses,
      stats: queueStatsResponses,
    };

    const job = await createQueueJob(page, queueResources);

    await expect
      .poll(() => resourceResponses, { timeout: 15_000 })
      .toBeGreaterThan(baseline.resources);
    await expect
      .poll(
        () => subscriptionResources.some((resources) =>
          resources.jobIds.includes(job.id)
        ),
        { timeout: 15_000 }
      )
      .toBe(true);
    await expect.poll(() =>
      hasSuccessfulInvocation(
        clientFrames,
        serverFrames,
        hubSockets.length - 1,
        'SubscribeToQueueJobAsync',
        (args) => args[0] === job.id,
      ),
      { timeout: 15_000 },
    ).toBe(true);
    await expect
      .poll(() => queueListResponses, { timeout: 15_000 })
      .toBeGreaterThan(baseline.queue);
    await expect
      .poll(() => queueStatsResponses, { timeout: 15_000 })
      .toBeGreaterThan(baseline.stats);
    await expect(page.getByText(job.fileName, { exact: true })).toBeVisible();
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

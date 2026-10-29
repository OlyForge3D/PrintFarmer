import {
  test,
  expect,
  createMoonrakerControl,
  getInlinePrinterDetails,
  getMoonrakerEmulatorUrl,
  MOONRAKER_PRINTERS,
} from '../fixtures/moonraker';

type JsonRecord = Record<string, unknown>;
type SignalRStatusTarget = 'printerupdated' | 'printerstatusesreplayed';
type SignalRStatus = { target: SignalRStatusTarget; status: JsonRecord };
type SignalRInvocation = { target: string; arguments: unknown[] };

function isRecord(value: unknown): value is JsonRecord {
  return typeof value === 'object' && value !== null;
}

function statusesFromSignalRFrame(payload: string | Buffer): SignalRStatus[] {
  const text = typeof payload === 'string' ? payload : payload.toString('utf8');
  const statuses: SignalRStatus[] = [];

  for (const message of text.split('\u001e')) {
    try {
      const frame: unknown = JSON.parse(message);
      if (!isRecord(frame) || frame.type !== 1 || !Array.isArray(frame.arguments)) continue;

      const [firstArgument] = frame.arguments;
      let target: SignalRStatusTarget;
      let candidates: unknown[];
      if (frame.target === 'printerupdated') {
        target = 'printerupdated';
        candidates = [firstArgument];
      } else if (frame.target === 'printerstatusesreplayed' && Array.isArray(firstArgument)) {
        target = 'printerstatusesreplayed';
        candidates = firstArgument;
      } else {
        continue;
      }

      statuses.push(...candidates.filter(isRecord).map(status => ({ target, status })));
    } catch {
      // Ignore SignalR handshake and non-invocation frames.
    }
  }

  return statuses;
}

function invocationsFromSignalRFrame(payload: string | Buffer): SignalRInvocation[] {
  const text = typeof payload === 'string' ? payload : payload.toString('utf8');
  const invocations: SignalRInvocation[] = [];

  for (const message of text.split('\u001e')) {
    try {
      const frame: unknown = JSON.parse(message);
      if (
        isRecord(frame) &&
        frame.type === 1 &&
        typeof frame.target === 'string' &&
        Array.isArray(frame.arguments)
      ) {
        invocations.push({ target: frame.target, arguments: frame.arguments });
      }
    } catch {
      // Ignore SignalR handshake and non-invocation frames.
    }
  }

  return invocations;
}

function hasSensorTemperature(
  status: JsonRecord,
  printerId: string,
  sensorId: string,
  expectedTemperature: number,
): boolean {
  if (status.id !== printerId || !Array.isArray(status.sensors)) return false;

  return status.sensors.some(sensor =>
    isRecord(sensor) &&
    sensor.id === sensorId &&
    Array.isArray(sensor.readings) &&
    sensor.readings.some(reading =>
      isRecord(reading) &&
      reading.quantity === 'Temperature' &&
      reading.value === expectedTemperature
    )
  );
}

test.use({ serviceWorkers: 'block' });

test.describe('Printer Sensor Delivery — Moonraker', () => {
  test.skip(
    ({ browserName, viewport }) =>
      browserName !== 'chromium' || viewport?.width !== 1280 || viewport.height !== 720,
    'WebSocket offline behavior is validated in the chromium project only',
  );
  test.describe.configure({ mode: 'serial' });

  test('renders a real sensor-only SignalR update and fresh state after reconnect', async ({
    browser,
    page,
    request,
    context,
    moonrakerSeedReady,
  }) => {
    test.setTimeout(60_000);

    const printerName = MOONRAKER_PRINTERS.ready;
    const sensorId = 'temperature_sensor endstop';
    const printer = moonrakerSeedReady.get(printerName);
    if (!printer) throw new Error(`Missing seeded printer "${printerName}"`);
    const printerId = printer.id;

    const signalRStatuses: Array<SignalRStatus & { socketIndex: number }> = [];
    const printerSockets: string[] = [];
    const closedSockets = new Set<number>();
    const printerApiRequests: string[] = [];
    const printerStatusApiRequests: string[] = [];
    const printerStatusInvocations: number[] = [];
    const printerSubscriptionInvocations: Array<SignalRInvocation & { socketIndex: number }> = [];
    const blockedPrinterApiRequests: string[] = [];
    let blockPrinterApiRequests = false;
    page.on('request', browserRequest => {
      const url = new URL(browserRequest.url());
      if (browserRequest.method() === 'GET' &&
        (url.pathname === '/api/printers' || url.pathname.startsWith('/api/printers/'))) {
        printerApiRequests.push(url.toString());
        if (url.pathname.endsWith('/status')) {
          printerStatusApiRequests.push(url.toString());
        }
      }
    });
    await page.route('**/api/printers**', async route => {
      const browserRequest = route.request();
      const url = new URL(browserRequest.url());
      if (blockPrinterApiRequests &&
        browserRequest.method() === 'GET' &&
        (url.pathname === '/api/printers' || url.pathname.startsWith('/api/printers/'))) {
        blockedPrinterApiRequests.push(url.toString());
        await route.abort();
        return;
      }

      await route.continue();
    });
    page.on('websocket', socket => {
      if (new URL(socket.url()).pathname !== '/hubs/printers') return;

      const socketIndex = printerSockets.length;
      printerSockets.push(socket.url());
      socket.on('framereceived', ({ payload }) => {
        for (const signalRStatus of statusesFromSignalRFrame(payload)) {
          signalRStatuses.push({ socketIndex, ...signalRStatus });
        }
      });
      socket.on('framesent', ({ payload }) => {
        for (const invocation of invocationsFromSignalRFrame(payload)) {
          if (invocation.target === 'RequestPrinterStatus') {
            printerStatusInvocations.push(socketIndex);
          }
          if (
            invocation.target === 'SubscribeToPrinterAsync' ||
            invocation.target === 'SubscribeToPrintersAsync'
          ) {
            printerSubscriptionInvocations.push({ socketIndex, ...invocation });
          }
        }
      });
      socket.on('close', () => closedSockets.add(socketIndex));
    });

    await createMoonrakerControl(request).reset('ready');
    await page.goto('/printers');

    const details = await getInlinePrinterDetails(page, printerName);
    const sensorRow = details.getByTestId(`sensor-${sensorId}`);
    await expect(sensorRow.getByText('23.4°C', { exact: true })).toBeVisible();
    await expect.poll(() => signalRStatuses.some(({ status }) =>
      hasSensorTemperature(status, printerId, sensorId, 23.4)
    )).toBe(true);

    const printerApiRequestsBeforeLiveChange = printerApiRequests.length;
    const printerStatusInvocationsBeforeLiveChange = printerStatusInvocations.length;
    const control = createMoonrakerControl(request);
    await control.updateSensor('ready', sensorId, { temperature: 31.5 });

    await expect.poll(() => signalRStatuses.some(({ target, status }) =>
      target === 'printerupdated' &&
      hasSensorTemperature(status, printerId, sensorId, 31.5)
    )).toBe(true);
    await expect(sensorRow.getByText('31.5°C', { exact: true })).toBeVisible();
    expect(printerApiRequests).toHaveLength(printerApiRequestsBeforeLiveChange);
    expect(printerStatusInvocations).toHaveLength(printerStatusInvocationsBeforeLiveChange);

    const connectedSocketIndex = printerSockets.length - 1;
    const observerContext = await browser.newContext({
      storageState: await context.storageState(),
      serviceWorkers: 'block',
    });
    try {
      const observerPage = await observerContext.newPage();
      const observerStatuses: SignalRStatus[] = [];
      observerPage.on('websocket', socket => {
        if (new URL(socket.url()).pathname !== '/hubs/printers') return;

        socket.on('framereceived', ({ payload }) => {
          observerStatuses.push(...statusesFromSignalRFrame(payload));
        });
      });
      await observerPage.goto(new URL('/printers', page.url()).toString());
      await expect.poll(() => observerStatuses.some(({ status }) =>
        hasSensorTemperature(status, printerId, sensorId, 31.5)
      )).toBe(true);

      await context.setOffline(true);
      await expect.poll(
        () => closedSockets.has(connectedSocketIndex),
        { timeout: 10_000 },
      ).toBe(true);
      await control.updateSensor('ready', sensorId, { temperature: 42.6 });
      await expect.poll(() => observerStatuses.some(({ target, status }) =>
        target === 'printerupdated' &&
        hasSensorTemperature(status, printerId, sensorId, 42.6)
      ), { timeout: 10_000 }).toBe(true);
    } finally {
      await observerContext.close();
    }

    const emulatorResponse = await request.get(
      `${getMoonrakerEmulatorUrl('ready')}/printer/objects/query?temperature_sensor%20endstop`
    );
    expect(emulatorResponse.ok(), 'Moonraker must retain the changed reading while the browser is disconnected').toBe(true);
    expect(await emulatorResponse.json()).toMatchObject({
      result: { status: { [sensorId]: { temperature: 42.6 } } },
    });
    await expect(sensorRow.getByText('31.5°C', { exact: true })).toBeVisible();
    expect(signalRStatuses.some(({ status }) =>
      hasSensorTemperature(status, printerId, sensorId, 42.6)
    )).toBe(false);

    blockPrinterApiRequests = true;
    const printerApiRequestsBeforeReconnect = printerApiRequests.length;
    const printerStatusApiRequestsBeforeReconnect = printerStatusApiRequests.length;
    const printerStatusInvocationsBeforeReconnect = printerStatusInvocations.length;
    const firstReconnectSocketIndex = printerSockets.length;
    await context.setOffline(false);
    await expect.poll(
      () => printerSockets.length,
      { timeout: 20_000 },
    ).toBeGreaterThan(firstReconnectSocketIndex);
    await expect.poll(
      () => signalRStatuses.some(({ socketIndex, target, status }) =>
        socketIndex >= firstReconnectSocketIndex &&
        (target === 'printerstatusesreplayed' || target === 'printerupdated') &&
        hasSensorTemperature(status, printerId, sensorId, 42.6)
      ),
      { timeout: 20_000 },
    ).toBe(true);
    await expect(sensorRow.getByText('42.6°C', { exact: true })).toBeVisible();
    expect(blockedPrinterApiRequests).toEqual(printerApiRequests.slice(printerApiRequestsBeforeReconnect));
    expect(printerStatusApiRequests).toHaveLength(printerStatusApiRequestsBeforeReconnect);
    expect(printerStatusInvocations).toHaveLength(printerStatusInvocationsBeforeReconnect);
    const reconnectSensorFrames = signalRStatuses.filter(({ socketIndex, status }) =>
      socketIndex >= firstReconnectSocketIndex &&
      hasSensorTemperature(status, printerId, sensorId, 42.6)
    );
    const firstReconnectSensorFrame = reconnectSensorFrames[0];
    if (!firstReconnectSensorFrame) {
      throw new Error('No fresh sensor status arrived on the reconnected SignalR socket');
    }
    expect(['printerstatusesreplayed', 'printerupdated']).toContain(firstReconnectSensorFrame.target);
    if (reconnectSensorFrames.some(({ target }) => target === 'printerupdated')) {
      expect(printerSubscriptionInvocations.some(({ socketIndex, target, arguments: args }) =>
        socketIndex >= firstReconnectSocketIndex &&
        target === 'SubscribeToPrinterAsync' &&
        args[0] === printerId
      )).toBe(true);
    }
  });
});

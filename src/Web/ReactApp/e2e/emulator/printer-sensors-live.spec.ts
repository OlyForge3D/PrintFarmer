import {
  test,
  expect,
  createMoonrakerControl,
  getInlinePrinterDetails,
  getMoonrakerEmulatorUrl,
  MOONRAKER_PRINTERS,
} from '../fixtures/moonraker';
import { API_BASE_URL, getStoredAuthToken } from '../fixtures/emulator-setup';

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

function sensorsFromStatus(status: unknown): JsonRecord[] {
  return isRecord(status) && Array.isArray(status.sensors)
    ? status.sensors.filter(isRecord)
    : [];
}

function hasSensorReading(
  status: unknown,
  printerId: string,
  sensorId: string,
  quantity: string,
  expectedValue: number,
  expectedAvailable = true,
): boolean {
  if (!isRecord(status) || status.id !== printerId) return false;
  const sensor = sensorsFromStatus(status).find(candidate => candidate.id === sensorId);
  return sensor?.available === expectedAvailable &&
    Array.isArray(sensor.readings) &&
    sensor.readings.some(reading =>
      isRecord(reading) &&
      reading.quantity === quantity &&
      reading.value === expectedValue
    );
}

function expectCachedFanState(
  status: unknown,
  speedPercent: number,
  available = true,
): void {
  const fan = sensorsFromStatus(status).find(sensor => sensor.id === 'fan_generic auxiliary');
  expect(fan).toMatchObject({
    id: 'fan_generic auxiliary',
    displayName: 'auxiliary',
    kind: 'Fan',
    fanRole: 'Auxiliary',
    available,
    readings: [{ quantity: 'FanSpeed', value: speedPercent, unit: 'Percent' }],
  });
}

test.use({ serviceWorkers: 'block' });

test.describe('Printer Sensor Delivery — Moonraker', () => {
  test.skip(
    ({ browserName, viewport }) =>
      browserName !== 'chromium' || viewport?.width !== 1280 || viewport.height !== 720,
    'SignalR disconnect/reconnect behavior is validated in the chromium project only',
  );
  test.describe.configure({ mode: 'serial' });

  test('renders live sensor-only updates and fresh state after manual reconnect', async ({
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
    const authToken = await getStoredAuthToken(page);
    expect(authToken, 'Auth token missing after emulatorReady fixture ran').toBeTruthy();

    // Set only for a single-spec run against a disposable native API DB. Compose must keep service-DNS URLs.
    if (process.env.E2E_NATIVE_LOOPBACK_MOONRAKER === '1' && !process.env.CI) {
      const emulatorUrl = new URL(getMoonrakerEmulatorUrl('ready'));
      expect(['127.0.0.1', 'localhost', '::1']).toContain(emulatorUrl.hostname);
      expect(emulatorUrl.port, 'Native emulator URL must include its dedicated loopback port').not.toBe('');
      const printerUrl = `${emulatorUrl.protocol}//${emulatorUrl.hostname}`;
      const detailsResponse = await request.get(
        `${API_BASE_URL}/api/printers/${printerId}`,
        { headers: { Authorization: `Bearer ${authToken}` } },
      );
      expect(detailsResponse.status(), 'Failed to read the ready printer before loopback configuration')
        .toBe(200);
      const details: unknown = await detailsResponse.json();
      const rowVersion = isRecord(details) && typeof details.rowVersion === 'string'
        ? details.rowVersion
        : null;
      expect(rowVersion, 'Missing row version for the ready printer').toBeTruthy();

      const updateResponse = await request.put(
        `${API_BASE_URL}/api/printers/${printerId}`,
        {
          headers: {
            Authorization: `Bearer ${authToken}`,
            'If-Match': `"${rowVersion}"`,
          },
          data: {
            serverUrl: printerUrl,
            backendPort: Number(emulatorUrl.port),
            frontendPort: Number(emulatorUrl.port),
          },
        },
      );
      expect(updateResponse.status(), 'Failed to configure the isolated ready printer for native loopback')
        .toBe(200);
      const readbackResponse = await request.get(
        `${API_BASE_URL}/api/printers/${printerId}`,
        { headers: { Authorization: `Bearer ${authToken}` } },
      );
      expect(readbackResponse.status(), 'Failed to read back the native loopback printer configuration')
        .toBe(200);
      expect(await readbackResponse.json()).toMatchObject({
        serverUrl: printerUrl,
        backendPort: Number(emulatorUrl.port),
        frontendPort: Number(emulatorUrl.port),
      });
    }

    const signalRStatuses: Array<SignalRStatus & { socketIndex: number }> = [];
    const printerSockets: string[] = [];
    const closedSockets = new Set<number>();
    const printerApiRequests: string[] = [];
    const printerStatusInvocations: number[] = [];
    const printerSubscriptionInvocations: Array<SignalRInvocation & { socketIndex: number }> = [];
    let blockPrinterApiRequests = false;
    page.on('request', browserRequest => {
      const url = new URL(browserRequest.url());
      if (browserRequest.method() === 'GET' &&
        (url.pathname === '/api/printers' || url.pathname.startsWith('/api/printers/'))) {
        printerApiRequests.push(url.toString());
      }
    });
    await page.route('**/api/printers**', async route => {
      const browserRequest = route.request();
      const url = new URL(browserRequest.url());
      if (blockPrinterApiRequests &&
        browserRequest.method() === 'GET' &&
        (url.pathname === '/api/printers' || url.pathname.startsWith('/api/printers/'))) {
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

    await page.evaluate(async () => {
      const moduleUrl = new URL('/src/services/printer-signalr.ts', location.origin).href;
      try {
        await import(moduleUrl);
      } catch (cause) {
        throw new Error(
          'Production cache inspection requires the Vite dev server to expose /src/services/printer-signalr.ts',
          { cause },
        );
      }
    });

    const readCachedStatus = () => page.evaluate(async (id) => {
      const moduleUrl = new URL('/src/services/printer-signalr.ts', location.origin).href;
      const module = await import(moduleUrl);
      const service = Reflect.get(module, 'printerSignalRService') as {
        getLastStatus: (printerId: string) => unknown;
      };
      return service.getLastStatus(id) ?? null;
    }, printerId);

    await page.evaluate(async (id) => {
      const moduleUrl = new URL('/src/services/printer-signalr.ts', location.origin).href;
      const module = await import(moduleUrl);
      const service = Reflect.get(module, 'printerSignalRService') as {
        getLastStatus: (printerId: string) => unknown;
        onPrinterStatusUpdate: (callback: (status: unknown) => void) => () => void;
      };
      const win = window as unknown as {
        __sensorCacheObservations?: Array<{ status: unknown; cachedStatus: unknown }>;
        __sensorCacheObservationCleanup?: () => void;
      };
      win.__sensorCacheObservations = [];
      win.__sensorCacheObservationCleanup?.();
      win.__sensorCacheObservationCleanup = service.onPrinterStatusUpdate(status => {
        win.__sensorCacheObservations?.push({
          status,
          cachedStatus: service.getLastStatus(id) ?? null,
        });
      });
    }, printerId);
    const readPrimaryCacheObservations = () => page.evaluate(() => {
      const win = window as unknown as {
        __sensorCacheObservations?: Array<{ status: unknown; cachedStatus: unknown }>;
      };
      return win.__sensorCacheObservations ?? [];
    });
    const hasUnavailableSensorSnapshot = (status: unknown): boolean =>
      hasSensorReading(status, printerId, sensorId, 'Temperature', 42.6, false) &&
      hasSensorReading(status, printerId, sensorId, 'Humidity', 53.1, false) &&
      hasSensorReading(status, printerId, 'fan_generic auxiliary', 'FanSpeed', 68, false);

    const details = await getInlinePrinterDetails(page, printerName);
    const sensorRow = details.getByTestId(`sensor-${sensorId}`);
    await expect(sensorRow.getByText('23.4°C', { exact: true })).toBeVisible();
    await expect(sensorRow.getByText('Unavailable')).toHaveCount(0);
    await expect.poll(() => signalRStatuses.some(({ status }) =>
      hasSensorReading(status, printerId, sensorId, 'Temperature', 23.4) &&
      hasSensorReading(status, printerId, sensorId, 'Humidity', 42)
    )).toBe(true);
    const initialClientStatus = await readCachedStatus();
    expect(isRecord(initialClientStatus) &&
      hasSensorReading(initialClientStatus, printerId, sensorId, 'Temperature', 23.4) &&
      hasSensorReading(initialClientStatus, printerId, sensorId, 'Humidity', 42)
    ).toBe(true);
    expectCachedFanState(initialClientStatus, 0);

    blockPrinterApiRequests = true;
    const printerApiRequestsBeforeLiveChange = printerApiRequests.length;
    const printerStatusInvocationsBeforeLiveChange = printerStatusInvocations.length;
    const control = createMoonrakerControl(request);
    await control.updateSensor('ready', sensorId, { temperature: 31.5, humidity: 45.6 });
    await control.updateSensor('ready', 'fan_generic auxiliary', { speed: 0.42 });

    await expect.poll(() => signalRStatuses.some(({ target, status }) =>
      target === 'printerupdated' &&
      hasSensorReading(status, printerId, sensorId, 'Temperature', 31.5) &&
      hasSensorReading(status, printerId, sensorId, 'Humidity', 45.6)
    )).toBe(true);
    await expect.poll(() => signalRStatuses.some(({ target, status }) =>
      target === 'printerupdated' &&
      hasSensorReading(status, printerId, 'fan_generic auxiliary', 'FanSpeed', 42)
    )).toBe(true);
    await expect(sensorRow.getByText('31.5°C', { exact: true })).toBeVisible();
    await expect(sensorRow.getByText('Unavailable')).toHaveCount(0);
    const liveClientStatus = await readCachedStatus();
    expect(isRecord(liveClientStatus) &&
      hasSensorReading(liveClientStatus, printerId, sensorId, 'Temperature', 31.5) &&
      hasSensorReading(liveClientStatus, printerId, sensorId, 'Humidity', 45.6)
    ).toBe(true);
    expectCachedFanState(liveClientStatus, 42);
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
        hasSensorReading(status, printerId, sensorId, 'Temperature', 31.5) &&
        hasSensorReading(status, printerId, sensorId, 'Humidity', 45.6)
      )).toBe(true);

      const disconnectedState = await page.evaluate(async () => {
        const moduleUrl = new URL('/src/services/printer-signalr.ts', location.origin).href;
        const module = await import(moduleUrl);
        const service = Reflect.get(module, 'printerSignalRService') as {
          disconnect: () => Promise<void>;
          getLastStatuses: () => Map<string, unknown>;
          isConnected: boolean;
        };
        // This intentionally exercises service stop/start, not transport-loss auto-reconnect.
        await service.disconnect();
        return {
          isConnected: service.isConnected,
          cachedStatusCount: service.getLastStatuses().size,
        };
      });
      expect(disconnectedState).toEqual({ isConnected: false, cachedStatusCount: 0 });
      await expect.poll(
        () => closedSockets.has(connectedSocketIndex),
        { timeout: 10_000 },
      ).toBe(true);
      const primaryStatusCountWhileDisconnected = signalRStatuses.length;
      await control.updateSensor('ready', sensorId, { temperature: 42.6, humidity: 53.1 });
      await control.updateSensor('ready', 'fan_generic auxiliary', { speed: 0.68 });
      await expect.poll(() => observerStatuses.some(({ target, status }) =>
        target === 'printerupdated' &&
        hasSensorReading(status, printerId, sensorId, 'Temperature', 42.6) &&
        hasSensorReading(status, printerId, sensorId, 'Humidity', 53.1)
      ), { timeout: 10_000 }).toBe(true);
      await expect.poll(() => observerStatuses.some(({ target, status }) =>
        target === 'printerupdated' &&
        hasSensorReading(status, printerId, 'fan_generic auxiliary', 'FanSpeed', 68)
      ), { timeout: 10_000 }).toBe(true);
      // Humidity is asserted in transport/cache only; temperature remains the visible UI proof.
      const shutdownResponse = await request.post(
        `${getMoonrakerEmulatorUrl('ready')}/__emulator/printer/scenario`,
        { data: { scenario: 'shutdown' } },
      );
      expect(shutdownResponse.ok(), 'Moonraker must accept the existing Klippy shutdown scenario').toBe(true);
      await expect.poll(() => observerStatuses.some(({ status }) =>
        hasUnavailableSensorSnapshot(status)
      ), { timeout: 10_000 }).toBe(true);
      expect(signalRStatuses).toHaveLength(primaryStatusCountWhileDisconnected);

    } finally {
      await observerContext.close();
    }

    await expect(sensorRow.getByText('31.5°C', { exact: true })).toBeVisible();
    const disconnectedClientStatus = await readCachedStatus();
    expect(disconnectedClientStatus).toBeNull();
    expect(signalRStatuses.some(({ status }) =>
      hasSensorReading(status, printerId, sensorId, 'Temperature', 42.6)
    )).toBe(false);

    const printerApiRequestsBeforeReconnect = printerApiRequests.length;
    const printerStatusInvocationsBeforeReconnect = printerStatusInvocations.length;
    const firstReconnectSocketIndex = printerSockets.length;
    await page.evaluate(async () => {
      const moduleUrl = new URL('/src/services/printer-signalr.ts', location.origin).href;
      const module = await import(moduleUrl);
      const service = Reflect.get(module, 'printerSignalRService') as {
        connect: () => Promise<void>;
      };
      await service.connect();
    });
    await expect.poll(
      () => printerSockets.length,
      { timeout: 20_000 },
    ).toBeGreaterThan(firstReconnectSocketIndex);
    await expect.poll(() => signalRStatuses.some(({ socketIndex, target, status }) =>
      socketIndex >= firstReconnectSocketIndex &&
      (target === 'printerstatusesreplayed' || target === 'printerupdated') &&
      hasUnavailableSensorSnapshot(status)
    ), { timeout: 20_000 }).toBe(true);
    const unavailableFrame = signalRStatuses.find(({ socketIndex, target, status }) =>
      socketIndex >= firstReconnectSocketIndex &&
      (target === 'printerstatusesreplayed' || target === 'printerupdated') &&
      hasUnavailableSensorSnapshot(status)
    );
    expect(unavailableFrame).toBeDefined();
    expect(['printerstatusesreplayed', 'printerupdated']).toContain(unavailableFrame?.target);

    await expect.poll(async () =>
      hasUnavailableSensorSnapshot(await readCachedStatus())
    , { timeout: 10_000 }).toBe(true);
    const unavailableCacheObservation = (await readPrimaryCacheObservations()).find(observation =>
      hasUnavailableSensorSnapshot(observation.status) &&
      hasUnavailableSensorSnapshot(observation.cachedStatus)
    );
    expect(unavailableCacheObservation).toBeDefined();
    expect(unavailableCacheObservation?.status).toEqual(unavailableFrame?.status);
    expect(unavailableCacheObservation?.cachedStatus).toEqual(unavailableCacheObservation?.status);
    expectCachedFanState(unavailableCacheObservation?.cachedStatus, 68, false);
    expect(hasUnavailableSensorSnapshot(await readCachedStatus())).toBe(true);
    await expect(sensorRow.getByText('Unavailable', { exact: true })).toBeVisible();
    expect(printerApiRequests).toHaveLength(printerApiRequestsBeforeReconnect);
    expect(printerStatusInvocations).toHaveLength(printerStatusInvocationsBeforeReconnect);
    if (unavailableFrame?.target === 'printerupdated') {
      expect(printerSubscriptionInvocations.some(({ socketIndex, target, arguments: args }) =>
        socketIndex >= firstReconnectSocketIndex &&
        target === 'SubscribeToPrinterAsync' &&
        args[0] === printerId
      )).toBe(true);
    }

    const restoredStatusCount = signalRStatuses.length;
    await control.reset('ready');
    await expect.poll(() => signalRStatuses.slice(restoredStatusCount).some(({ status }) =>
      hasSensorReading(status, printerId, sensorId, 'Temperature', 23.4) &&
      hasSensorReading(status, printerId, sensorId, 'Humidity', 42) &&
      hasSensorReading(status, printerId, 'fan_generic auxiliary', 'FanSpeed', 0)
    ), { timeout: 15_000 }).toBe(true);
    await expect(sensorRow.getByText('Unavailable', { exact: true })).toHaveCount(0);
    await expect(sensorRow.getByText('23.4°C', { exact: true })).toBeVisible();
    await control.updateSensor('ready', sensorId, { temperature: 42.6, humidity: 53.1 });
    await control.updateSensor('ready', 'fan_generic auxiliary', { speed: 0.68 });
    await expect.poll(() => signalRStatuses.slice(restoredStatusCount).some(({ target, status }) =>
      target === 'printerupdated' &&
      hasSensorReading(status, printerId, sensorId, 'Temperature', 42.6) &&
      hasSensorReading(status, printerId, sensorId, 'Humidity', 53.1)
    ), { timeout: 10_000 }).toBe(true);
    await expect.poll(() => signalRStatuses.slice(restoredStatusCount).some(({ target, status }) =>
      target === 'printerupdated' &&
      hasSensorReading(status, printerId, 'fan_generic auxiliary', 'FanSpeed', 68)
    ), { timeout: 10_000 }).toBe(true);
    await expect(sensorRow.getByText('42.6°C', { exact: true })).toBeVisible();
    await expect(sensorRow.getByText('Unavailable', { exact: true })).toHaveCount(0);
    await expect.poll(async () => {
      const status = await readCachedStatus();
      return isRecord(status) &&
        hasSensorReading(status, printerId, sensorId, 'Temperature', 42.6) &&
        hasSensorReading(status, printerId, sensorId, 'Humidity', 53.1) &&
        hasSensorReading(status, printerId, 'fan_generic auxiliary', 'FanSpeed', 68);
    }, { timeout: 10_000 }).toBe(true);
    const reconnectedClientStatus = await readCachedStatus();
    expect(hasSensorReading(
      reconnectedClientStatus,
      printerId,
      sensorId,
      'Temperature',
      42.6,
    )).toBe(true);
    expect(hasSensorReading(
      reconnectedClientStatus,
      printerId,
      sensorId,
      'Humidity',
      53.1,
    )).toBe(true);
    expectCachedFanState(reconnectedClientStatus, 68, true);
    expect(printerApiRequests).toHaveLength(printerApiRequestsBeforeReconnect);
    expect(printerStatusInvocations).toHaveLength(printerStatusInvocationsBeforeReconnect);

    const emulatorResponse = await request.get(
      `${getMoonrakerEmulatorUrl('ready')}/printer/objects/query?temperature_sensor%20endstop&fan_generic%20auxiliary`
    );
    expect(emulatorResponse.ok(), 'Moonraker must retain explicit sensor writes after the reset').toBe(true);
    expect(await emulatorResponse.json()).toMatchObject({
      result: {
        status: {
          [sensorId]: { temperature: 42.6, humidity: 53.1 },
          'fan_generic auxiliary': { speed: 0.68 },
        },
      },
    });
  });
});

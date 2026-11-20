import {
  test,
  expect,
  createMoonrakerControl,
  getInlinePrinterDetails,
  getMoonrakerEmulatorUrl,
  MOONRAKER_PRINTERS,
} from '../fixtures/moonraker';
import { API_BASE_URL, getStoredAuthToken } from '../fixtures/emulator-setup';
import type { Request, WebSocketRoute } from '@playwright/test';

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

function expectFanState(
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

  test('renders live sensor-only updates and fresh state after transport reconnect', async ({
    browser,
    page,
    request,
    context,
    moonrakerSeedReady,
  }) => {
    test.setTimeout(150_000);

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
        `${API_BASE_URL}/api/printers/${printerId}/details`,
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
        `${API_BASE_URL}/api/printers/${printerId}/details`,
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
    const printerSockets: Array<{ client: WebSocketRoute; server: WebSocketRoute }> = [];
    const printerApiResponses: string[] = [];
    const pendingPrinterApiRequests = new Set<Request>();
    const printerStatusInvocations: number[] = [];
    const printerSubscriptionInvocations: Array<SignalRInvocation & { socketIndex: number }> = [];
    let blockPrinterApiRequests = false;
    let blockPrinterReconnect = false;
    const isPrinterApiRead = (browserRequest: Request): boolean => {
      const url = new URL(browserRequest.url());
      return browserRequest.method() === 'GET' &&
        (url.pathname === '/api/printers' || url.pathname.startsWith('/api/printers/'));
    };
    page.on('request', browserRequest => {
      if (isPrinterApiRead(browserRequest)) pendingPrinterApiRequests.add(browserRequest);
    });
    page.on('requestfinished', browserRequest => pendingPrinterApiRequests.delete(browserRequest));
    page.on('requestfailed', browserRequest => pendingPrinterApiRequests.delete(browserRequest));
    page.on('response', response => {
      const browserRequest = response.request();
      if (isPrinterApiRead(browserRequest)) {
        printerApiResponses.push(browserRequest.url());
      }
    });
    await page.route('**/api/printers**', async route => {
      const browserRequest = route.request();
      if (blockPrinterApiRequests && isPrinterApiRead(browserRequest)) {
        await route.abort();
        return;
      }

      await route.continue();
    });
    await page.route('**/hubs/printers/negotiate**', async route => {
      if (blockPrinterReconnect) {
        await route.abort();
      } else {
        await route.continue();
      }
    });
    await page.routeWebSocket(url => url.pathname === '/hubs/printers', socket => {
      if (blockPrinterReconnect) {
        void socket.close();
        return;
      }
      const server = socket.connectToServer();
      const socketIndex = printerSockets.length;
      printerSockets.push({ client: socket, server });
      server.onMessage(payload => {
        for (const signalRStatus of statusesFromSignalRFrame(payload)) {
          signalRStatuses.push({ socketIndex, ...signalRStatus });
        }
        socket.send(payload);
      });
      socket.onMessage(payload => {
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
        server.send(payload);
      });
    });

    await createMoonrakerControl(request).reset('ready');
    await page.goto('/printers');

    const hasUnavailableSensorSnapshot = (status: unknown, expectedHumidity = 53.1): boolean =>
      hasSensorReading(status, printerId, sensorId, 'Temperature', 42.6, false) &&
      hasSensorReading(status, printerId, sensorId, 'Humidity', expectedHumidity, false) &&
      hasSensorReading(status, printerId, 'fan_generic auxiliary', 'FanSpeed', 68, false);

    const details = await getInlinePrinterDetails(page, printerName);
    const sensorRow = details.getByTestId(`sensor-${sensorId}`);
    await expect(sensorRow.getByText('23.4°C', { exact: true })).toBeVisible();
    await expect(sensorRow.getByText('Unavailable')).toHaveCount(0);
    await expect.poll(() => signalRStatuses.some(({ status }) =>
      hasSensorReading(status, printerId, sensorId, 'Temperature', 23.4) &&
      hasSensorReading(status, printerId, sensorId, 'Humidity', 42)
    )).toBe(true);
    const initialFrame = [...signalRStatuses].reverse().find(({ status }) =>
      hasSensorReading(status, printerId, sensorId, 'Temperature', 23.4) &&
      hasSensorReading(status, printerId, sensorId, 'Humidity', 42));
    expectFanState(initialFrame?.status, 0);
    const humidityRow = details.getByTestId(`sensor-${sensorId}-Humidity`);
    const fanRow = details.getByTestId('sensor-fan_generic auxiliary');
    await expect(humidityRow.getByText('42%', { exact: true })).toBeVisible();
    await expect(fanRow.getByRole('spinbutton', { name: 'Set Auxiliary Speed' })).toHaveValue('0');

    blockPrinterApiRequests = true;
    await expect.poll(() => pendingPrinterApiRequests.size).toBe(0);
    const printerApiResponsesBeforeLiveChange = printerApiResponses.length;
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
    await expect(humidityRow.getByText('45.6%', { exact: true })).toBeVisible();
    await expect(fanRow.getByRole('spinbutton', { name: 'Set Auxiliary Speed' })).toHaveValue('42');
    const liveFrame = [...signalRStatuses].reverse().find(({ status }) =>
      hasSensorReading(status, printerId, sensorId, 'Temperature', 31.5) &&
      hasSensorReading(status, printerId, sensorId, 'Humidity', 45.6) &&
      hasSensorReading(status, printerId, 'fan_generic auxiliary', 'FanSpeed', 42));
    expectFanState(liveFrame?.status, 42);
    expect(printerApiResponses).toHaveLength(printerApiResponsesBeforeLiveChange);
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

      // Interrupt the real transport and keep retries offline until the observer
      // has seen the new snapshot. Unlike an explicit service stop, transport
      // loss retains the cache and exercises SignalR's automatic reconnect.
      blockPrinterReconnect = true;
      const activeSocket = printerSockets[connectedSocketIndex];
      expect(activeSocket, 'Expected an active printer WebSocket').toBeDefined();
      await Promise.all([activeSocket.client.close(), activeSocket.server.close()]);
      const primaryStatusCountWhileDisconnected = signalRStatuses.length;

      // Unmount the page-level status consumer, then mount a new one while
      // there is no transport and printer HTTP reads are blocked. Its changed
      // readings cannot come from a fresh status replay or API response.
      await page.locator('a[href="/dashboard"]:visible').first().click();
      await expect(page).toHaveURL(/\/dashboard/);
      await page.locator('a[href="/printers"]:visible').first().click();
      await expect(page).toHaveURL(/\/printers/);
      await expect(sensorRow.getByText('31.5°C', { exact: true })).toBeVisible();
      await expect(humidityRow.getByText('45.6%', { exact: true })).toBeVisible();
      await expect(fanRow.getByRole('spinbutton', { name: 'Set Auxiliary Speed' })).toHaveValue('42');
      expect(signalRStatuses).toHaveLength(primaryStatusCountWhileDisconnected);
      expect(printerApiResponses).toHaveLength(printerApiResponsesBeforeLiveChange);
      expect(printerStatusInvocations).toHaveLength(printerStatusInvocationsBeforeLiveChange);

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
      // M112 changes Klippy state without resetting the emulator sensor fixture.
      const shutdownResponse = await request.post(
        `${getMoonrakerEmulatorUrl('ready')}/printer/gcode/script`,
        { data: { script: 'M112' } },
      );
      expect(shutdownResponse.ok(), 'Moonraker must accept M112 without resetting sensor readings').toBe(true);
      const latestObserverStatus = () =>
        [...observerStatuses].reverse().find(({ status }) => status.id === printerId);
      await expect.poll(() => {
        const latest = latestObserverStatus();
        return latest !== undefined && hasUnavailableSensorSnapshot(latest.status);
      }, { timeout: 10_000 }).toBe(true);
      // This control emits a humidity-only, webhook-free delta while Klippy remains shut down.
      await control.updateSensor('ready', sensorId, { humidity: 54.2 });
      await expect.poll(() => {
        const latest = latestObserverStatus();
        return latest !== undefined &&
          hasSensorReading(latest.status, printerId, sensorId, 'Humidity', 54.2, false) &&
          hasUnavailableSensorSnapshot(latest.status, 54.2);
      }, { timeout: 10_000 }).toBe(true);
      expect(signalRStatuses).toHaveLength(primaryStatusCountWhileDisconnected);

    } finally {
      await observerContext.close();
    }

    await expect(sensorRow.getByText('31.5°C', { exact: true })).toBeVisible();
    expect(signalRStatuses.some(({ status }) =>
      hasSensorReading(status, printerId, sensorId, 'Temperature', 42.6)
    )).toBe(false);

    await expect.poll(() => pendingPrinterApiRequests.size).toBe(0);
    const printerApiResponsesBeforeReconnect = printerApiResponses.length;
    const printerStatusInvocationsBeforeReconnect = printerStatusInvocations.length;
    const firstReconnectSocketIndex = printerSockets.length;
    blockPrinterReconnect = false;
    await expect.poll(
      () => printerSockets.length,
      { timeout: 40_000 },
    ).toBeGreaterThan(firstReconnectSocketIndex);
    const latestReconnectFrame = () =>
      [...signalRStatuses].reverse().find(({ socketIndex, status }) =>
        socketIndex >= firstReconnectSocketIndex && status.id === printerId
      );
    await expect.poll(() => {
      const latest = latestReconnectFrame();
      return latest !== undefined &&
        (latest.target === 'printerstatusesreplayed' || latest.target === 'printerupdated') &&
        hasUnavailableSensorSnapshot(latest.status, 54.2);
    }, { timeout: 20_000 }).toBe(true);
    const unavailableFrame = latestReconnectFrame();
    expect(unavailableFrame).toBeDefined();

    expectFanState(unavailableFrame?.status, 68, false);
    await expect(sensorRow.getByText('Unavailable', { exact: true })).toBeVisible();
    await expect(sensorRow.getByText('42.6°C', { exact: true })).toBeVisible();
    await expect(humidityRow.getByText('54.2%', { exact: true })).toBeVisible();
    await expect(humidityRow.getByText('Unavailable', { exact: true })).toBeVisible();
    await expect(fanRow.getByText('Unavailable', { exact: true })).toBeVisible();
    await expect(fanRow.getByRole('spinbutton', { name: 'Set Auxiliary Speed' })).toHaveValue('68');
    expect(printerApiResponses).toHaveLength(printerApiResponsesBeforeReconnect);
    expect(printerStatusInvocations).toHaveLength(printerStatusInvocationsBeforeReconnect);
    expect(printerSubscriptionInvocations.some(({ socketIndex, target, arguments: args }) =>
      socketIndex >= firstReconnectSocketIndex &&
      ((target === 'SubscribeToPrinterAsync' && args[0] === printerId) ||
       (target === 'SubscribeToPrintersAsync' && Array.isArray(args[0]) && args[0].includes(printerId)))
    )).toBe(true);

    // A second new page consumer must see the reconnected unavailable
    // snapshot from client state, not a replay or an HTTP response.
    blockPrinterReconnect = true;
    const reconnectedSocket = printerSockets[printerSockets.length - 1];
    await Promise.all([reconnectedSocket.client.close(), reconnectedSocket.server.close()]);
    const unavailableStatusCount = signalRStatuses.length;
    const unavailableInvocationCount = printerStatusInvocations.length;
    await page.locator('a[href="/dashboard"]:visible').first().click();
    await expect(page).toHaveURL(/\/dashboard/);
    await page.locator('a[href="/printers"]:visible').first().click();
    await expect(page).toHaveURL(/\/printers/);
    await expect(sensorRow.getByText('42.6°C', { exact: true })).toBeVisible();
    await expect(sensorRow.getByText('Unavailable', { exact: true })).toBeVisible();
    await expect(humidityRow.getByText('54.2%', { exact: true })).toBeVisible();
    await expect(humidityRow.getByText('Unavailable', { exact: true })).toBeVisible();
    await expect(fanRow.getByText('Unavailable', { exact: true })).toBeVisible();
    await expect(fanRow.getByRole('spinbutton', { name: 'Set Auxiliary Speed' })).toHaveValue('68');
    expect(signalRStatuses).toHaveLength(unavailableStatusCount);
    expect(printerStatusInvocations).toHaveLength(unavailableInvocationCount);
    expect(printerApiResponses).toHaveLength(printerApiResponsesBeforeReconnect);
    const secondReconnectSocketIndex = printerSockets.length;
    blockPrinterReconnect = false;
    await expect.poll(() => printerSockets.length, { timeout: 40_000 })
      .toBeGreaterThan(secondReconnectSocketIndex);
    await expect.poll(() => signalRStatuses.some(({ socketIndex, status }) =>
      socketIndex >= secondReconnectSocketIndex && hasUnavailableSensorSnapshot(status, 54.2)
    ), { timeout: 20_000 }).toBe(true);

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
    await expect(humidityRow.getByText('53.1%', { exact: true })).toBeVisible();
    await expect(fanRow.getByRole('spinbutton', { name: 'Set Auxiliary Speed' })).toHaveValue('68');
    const restoredFrame = [...signalRStatuses].reverse().find(({ status }) =>
      hasSensorReading(status, printerId, sensorId, 'Temperature', 42.6) &&
      hasSensorReading(status, printerId, sensorId, 'Humidity', 53.1) &&
      hasSensorReading(status, printerId, 'fan_generic auxiliary', 'FanSpeed', 68));
    expectFanState(restoredFrame?.status, 68, true);
    expect(printerApiResponses).toHaveLength(printerApiResponsesBeforeReconnect);
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

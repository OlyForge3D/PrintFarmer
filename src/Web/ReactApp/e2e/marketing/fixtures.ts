import type { BrowserContext } from '@playwright/test';
import { inventoryRouteSeeds } from '../fixtures/toolbar-routes-inventory';
import { toolbarRouteUser } from '../fixtures/toolbar-routed';
import { MotionType, PrinterBackend, PrintProjectStatus, type Printer, type PrintProjectListDto, type SpoolmanFilament, type QueueChangeFeed, type QueueChangeWatermark } from '../../src/types/api';

export const captureOrigin = 'http://127.0.0.1:4178';
export const captureTime = '2026-10-30T14:00:00Z';
const inventory = inventoryRouteSeeds();

export const printers: Printer[] = ([
  ['demo-1', 'Atlas', 'Printing', 64, 'enclosure-panel.gcode'],
  ['demo-2', 'Birch', 'Printing', 32, 'sensor-mount.gcode'],
  ['demo-3', 'Cedar', 'Idle', 0, ''],
  ['demo-4', 'Delta', 'Paused', 48, 'cable-guide.gcode'],
  ['demo-5', 'Ember', 'Idle', 0, ''],
  ['demo-6', 'Finch', 'Offline', 0, ''],
] as const).map(([id, name, state, progress, fileName]) => ({
  id, name, state, progress, fileName,
  backend: PrinterBackend.Moonraker,
  isOnline: state !== 'Offline', isEnabled: true, inMaintenance: false,
  manufacturerName: 'Demo workshop', modelName: 'CoreXY 250',
  motionType: MotionType.CoreXY,
  hotendTemp: state === 'Printing' ? 210 : 23,
  hotendTarget: state === 'Printing' ? 210 : 0,
  bedTemp: state === 'Printing' ? 60 : 23,
  bedTarget: state === 'Printing' ? 60 : 0,
  printTimeLeftSeconds: state === 'Printing' ? 3600 : undefined,
  obicoEnabled: false,
}));

const responses: Record<string, unknown> = {
  'GET /api/setup/status': { needsSetup: false },
  'GET /api/auth/me': { ...toolbarRouteUser, id: 'marketing-operator', username: 'demo', displayName: 'Demo operator', email: 'demo@example.invalid' },
  'GET /api/settings/SignalR': { logLevel: 'Error', consoleLoggingEnabled: false },
  'GET /api/settings/Slicer': { enabled: true },
  'GET /api/settings/NetworkDiscovery': { enableDiscovery: false },
  'GET /api/workers/': [],
  'GET /api/notifications': [],
  'GET /api/notifications/unread/count': { unreadCount: 0 },
  'GET /api/tasks/count': { count: 0 },
  'GET /api/health': { status: 'Healthy', results: {}, startup: { phase: 'Ready', ready: true, failed: false } },
  'GET /api/healthz': { status: 'Healthy' },
  'GET /api/system/info': {
    app: { version: 'demo', uptime: '01:00:00', hostname: 'demo-workshop' },
    cpu: { cores: 4, usagePercent: 10 }, memory: { usedBytes: 1000000000, totalBytes: 8000000000 },
    disk: { usedBytes: 1000000000, totalBytes: 8000000000, archiveBytes: 0, databaseBytes: 0 },
    services: [], database: { engine: 'SQLite', version: 'demo', printerCount: 6, archiveCount: 0 },
  },
  'GET /api/admin/release-updates': {
    status: 'UpToDate', updateAvailable: false, installedVersion: 'demo', channel: 'Stable',
    latestVersion: 'demo', latestTag: 'demo', latestReleaseName: 'Demo',
    latestPublishedAt: captureTime, releaseUrl: undefined,
    lastCheckedAt: captureTime, lastSuccessfulCheckAt: captureTime,
    isStale: false, error: undefined, checkIntervalSeconds: 3600, upgradeDocsUrl: '/docs',
  },
  'GET /api/job-queue/changes/watermark': { latestSequence: 0 } satisfies QueueChangeWatermark,
  'GET /api/job-queue/changes': { afterSequence: 0, nextSequence: 0, hasMore: false, events: [] } satisfies QueueChangeFeed,
  'GET /api/catalog/manufacturers': inventory['/api/catalog/manufacturers'],
  'GET /api/catalog/printer-models': inventory['/api/catalog/printer-models'],
  'GET /api/tags/objects': [],
  'GET /api/job-queue-analytics/printer-summaries': [],
  'GET /api/auto-dispatch/status': { globalEnabled: false, printers: [] },
  'GET /api/job-queue/subscription-resources': { printerIds: [], jobIds: [], projectIds: [] },
  'GET /api/system/capabilities': {
    architecture: 'x64', slicingEnabled: true, modelFilesEnabled: true,
    thumbnailGenerationEnabled: true, gcodeUploadEnabled: true,
    clientThumbnailUploadEnabled: true, idempotentModelUploadEnabled: true,
    modelThumbnailReplacementEnabled: true,
  },
  'GET /api/printers': printers,
  'GET /api/printers/summary': printers,
  'GET /api/printers/camera-urls': [],
  'GET /api/printers/backend-capabilities': [],
  'GET /api/printers/filament-coverage': [],
  'GET /api/projects': [
    { id: 'project-demo-1', name: 'Workshop enclosure', description: 'Panels, brackets and cable guides for the demo workshop.',
      status: PrintProjectStatus.InProgress, priority: 1, totalFiles: 6, completedFiles: 3,
      totalPrints: 12, completedPrints: 7, progressPercent: 58,
      createdAt: captureTime },
    { id: 'project-demo-2', name: 'Sensor mounting kit', description: 'A repeatable mounting kit for the workbench.',
      status: PrintProjectStatus.InProgress, priority: 2, totalFiles: 4, completedFiles: 1,
      totalPrints: 8, completedPrints: 2, progressPercent: 25,
      createdAt: captureTime },
    { id: 'project-demo-3', name: 'Cable management', description: 'Clips and guides to finish the assembly.',
      status: PrintProjectStatus.Completed, priority: 1, totalFiles: 3, completedFiles: 3,
      totalPrints: 9, completedPrints: 9, progressPercent: 100,
      createdAt: captureTime, completedAt: captureTime },
  ] satisfies PrintProjectListDto[],
  'GET /api/project-templates': inventory['/api/project-templates'],
  'GET /api/spoolman/health': inventory['/api/spoolman/health'],
  'GET /api/spoolman/config': inventory['/api/spoolman/config'],
  'GET /api/spoolman/filter-options': inventory['/api/spoolman/filter-options'],
  'GET /api/spoolman/filaments/filter-options': inventory['/api/spoolman/filaments/filter-options'],
  'GET /api/spoolman/filaments': {
    items: ['Ocean blue', 'Signal orange', 'Graphite'].map((name, index) => ({
      id: index + 1, name: `${name} PLA`, material: 'PLA', vendor: 'Demo workshop',
      colorHex: ['#2463eb', '#ed7d31', '#536172'][index], diameter: 1.75,
      density: 1.24, weight: 1000, price: 20, settingsExtruderTemp: 210, settingsBedTemp: 60,
    } satisfies SpoolmanFilament)), totalCount: 3,
  },
  'GET /api/spoolman/spools': {
    items: ['Ocean blue', 'Signal orange', 'Graphite'].map((name, index) => ({
      id: index + 1, name: `${name} spool`, filamentId: index + 1, filamentName: `${name} PLA`,
      material: 'PLA', vendor: 'Demo workshop', colorHex: ['#2463eb', '#ed7d31', '#536172'][index],
      initialWeightG: 1000, remainingWeightG: [740, 460, 890][index],
      usedWeightG: [260, 540, 110][index], location: 'Demo workshop', archived: false,
    })), totalCount: 3,
  },
};

// Exact-origin assets only; an unexpected API, write or external request never reaches a server.
export async function isolateCapture(context: BrowserContext) {
  const violations: string[] = [];
  await context.routeWebSocket(/.*/, socket => {
    const url = new URL(socket.url());
    if (url.origin === captureOrigin.replace('http:', 'ws:') && url.pathname === '/') {
      socket.send(JSON.stringify({ type: 'connected' }));
      return;
    }
    if (url.origin !== captureOrigin.replace('http:', 'ws:') || !/^\/hubs\/(printers|slicer|queue|notifications|nfc)$/.test(url.pathname)) {
      violations.push(`Unexpected socket: ${url.origin}${url.pathname}`);
      socket.close();
      return;
    }
    socket.onMessage(message => {
      if (typeof message !== 'string') {
        violations.push('Unexpected binary hub message');
        socket.close();
        return;
      }
      for (const record of message.split('\u001e').filter(Boolean)) {
        const frame = JSON.parse(record);
        if (frame.protocol === 'json') socket.send('{}\u001e');
        else if (frame.type === 1 && frame.invocationId) {
          socket.send(`${JSON.stringify({ type: 3, invocationId: frame.invocationId })}\u001e`);
        }
      }
    });
  });
  await context.route('**/*', async route => {
    const request = route.request();
    const url = new URL(request.url());
    const key = `${request.method()} ${url.pathname}`;
    if (url.origin !== captureOrigin) {
      // The app's optional web-font stylesheet is deliberately fulfilled offline.
      if (url.origin === 'https://fonts.googleapis.com' && request.resourceType() === 'stylesheet') {
        await route.fulfill({ contentType: 'text/css', body: '/* Offline: system font fallback. */' });
        return;
      }
      violations.push(`External request blocked: ${request.method()} ${url.origin}${url.pathname}`);
      await route.abort('blockedbyclient');
      return;
    }
    if (url.pathname === '/src/main.tsx') {
      await route.fulfill({ contentType: 'application/javascript', body: "import '/e2e/fixtures/toolbar-routed-app.mjs';" });
      return;
    }
    if (/^\/(api|hubs)(\/|$)/.test(url.pathname)) {
      if (request.method() === 'POST' && /^\/hubs\/(printers|slicer|queue|notifications|nfc)\/negotiate$/.test(url.pathname)) {
        await route.fulfill({ json: {
          negotiateVersion: 1, connectionId: 'marketing', connectionToken: 'marketing',
          availableTransports: [{ transport: 'WebSockets', transferFormats: ['Text', 'Binary'] }],
        } });
        return;
      }
      if (!Object.hasOwn(responses, key)) {
        violations.push(`Missing capture fixture: ${key}`);
        await route.fulfill({ status: 503, json: { message: `Missing capture fixture: ${key}` } });
        return;
      }
      await route.fulfill({ json: responses[key] });
      return;
    }
    if (request.method() !== 'GET' || (['fetch', 'xhr', 'eventsource'].includes(request.resourceType()) && url.pathname !== '/assets/orcaslicer/manifest.json')) {
      violations.push(`Unexpected request: ${key}`);
      await route.abort('blockedbyclient');
      return;
    }
    await route.continue();
  });
  await context.addInitScript(() => {
    const encode = (value: object) => btoa(JSON.stringify(value)).replace(/=/g, '');
    localStorage.setItem('auth-token', `${encode({ alg: 'none' })}.${encode({ sub: 'marketing-operator', exp: 4102444800 })}.synthetic-not-a-credential`);
    localStorage.setItem('pf-theme', 'dark');
    for (const id of ['dashboard', 'printers', 'print-queue', 'settings', 'gcode-library', 'projects', 'filaments']) {
      localStorage.setItem(`pf-tour-seen-${id}`, 'true');
    }
  });
  return violations;
}

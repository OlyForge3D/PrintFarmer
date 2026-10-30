import { expect, type Page, type TestInfo } from '@playwright/test';

export const toolbarRouteUser = {
  id: 'toolbar-fixture-user', email: 'toolbar@example.invalid', username: 'toolbar',
  displayName: 'Toolbar fixture operator', isActive: true, roles: ['farm_admin'],
  emailConfirmed: true,
  permissions: ['printers:admin', 'files:admin', 'settings:admin'],
};

const baseSeeds: Record<string, unknown> = {
  '/api/setup/status': { needsSetup: false },
  '/api/auth/me': toolbarRouteUser,
  '/api/settings/SignalR': { logLevel: 'Error', consoleLoggingEnabled: false },
  '/api/settings/Slicer': { enabled: true },
  '/api/workers/': [],
  '/api/notifications': [],
  '/api/notifications/unread/count': { unreadCount: 0 },
  '/api/tasks/count': { count: 0 },
  '/api/auto-dispatch/status': { globalEnabled: false, printers: [] },
  '/api/job-queue/subscription-resources': { printerIds: [], jobIds: [], projectIds: [] },
  '/api/system/info': {
    app: { version: 'fixture', uptime: '01:00:00', hostname: 'offline-fixture' },
    cpu: { cores: 4, usagePercent: 10 }, memory: { usedBytes: 1000000000, totalBytes: 8000000000 },
    disk: { usedBytes: 1000000000, totalBytes: 8000000000, archiveBytes: 0, databaseBytes: 0 },
    services: [], database: { engine: 'SQLite', version: 'fixture', printerCount: 1, archiveCount: 0 },
  },
  '/api/admin/release-updates': {
    status: 'UpToDate', updateAvailable: false, installedVersion: '1.0.0', channel: 'Stable',
    latestVersion: '1.0.0', latestTag: 'v1.0.0', latestReleaseName: 'Fixture',
    latestPublishedAt: '2026-10-30T14:00:00Z', releaseUrl: null,
    lastCheckedAt: '2026-10-30T14:00:00Z', lastSuccessfulCheckAt: '2026-10-30T14:00:00Z',
    isStale: false, error: null, checkIntervalSeconds: 3600, upgradeDocsUrl: '/docs',
  },
  '/api/system/capabilities': {
    architecture: 'x64', slicingEnabled: true, modelFilesEnabled: true,
    thumbnailGenerationEnabled: true, gcodeUploadEnabled: true,
    clientThumbnailUploadEnabled: true, idempotentModelUploadEnabled: true,
    modelThumbnailReplacementEnabled: true,
  },
};

export async function mountRoutedToolbarPage(page: Page, path: string, seeds: Record<string, unknown> = {}) {
  const requests = { unhandled: [] as string[], writes: [] as string[], external: [] as string[], errors: [] as string[] };
  page.on('pageerror', error => requests.errors.push(error.message));
  const responses = { ...baseSeeds, ...seeds };
  await page.clock.setFixedTime(new Date('2026-10-30T14:00:00Z'));
  await page.addInitScript(() => {
    const encode = (value: object) => btoa(JSON.stringify(value)).replace(/=/g, '');
    localStorage.setItem('auth-token', `${encode({ alg: 'none' })}.${encode({ sub: 'toolbar-fixture-user', exp: 4102444800 })}.fixture`);
    localStorage.setItem('theme', 'dark');
    for (const id of ['dashboard', 'printers', 'print-queue', 'settings', 'gcode-library']) {
      localStorage.setItem(`pf-tour-seen-${id}`, 'true');
    }
  });
  await page.routeWebSocket(/.*/, () => {
    // Keep Vite's locally mocked socket open. No socket connects to a server.
  });
  await page.route('**/*', async route => {
    const request = route.request();
    const url = new URL(request.url());
    const local = ['localhost', '127.0.0.1'].includes(url.hostname);
    if (url.hostname === 'fonts.googleapis.com') {
      await route.fulfill({ contentType: 'text/css', body: '/* Offline route fixture: no external font download. */' });
      return;
    }
    if (!local) {
      requests.external.push(request.url());
      await route.fulfill({ status: 503, json: { message: 'External traffic blocked by toolbar route fixture' } });
      return;
    }
    if (url.pathname === '/src/main.tsx') {
      await route.fulfill({
        contentType: 'application/javascript',
        body: "import '/e2e/fixtures/toolbar-routed-app.mjs';",
      });
      return;
    }
    if (url.pathname.startsWith('/api/') || url.pathname.startsWith('/hubs/')) {
      if (url.pathname.startsWith('/hubs/') && url.pathname.endsWith('/negotiate')) {
        await route.fulfill({ json: {
          negotiateVersion: 1, connectionId: 'offline-toolbar', connectionToken: 'offline-toolbar',
          availableTransports: [{ transport: 'WebSockets', transferFormats: ['Text', 'Binary'] }],
        } });
        return;
      }
      if (!['GET', 'HEAD'].includes(request.method())) requests.writes.push(`${request.method()} ${url.pathname}`);
      const key = `${request.method()} ${url.pathname}`;
      if (Object.hasOwn(responses, key) || Object.hasOwn(responses, url.pathname)) {
        await route.fulfill({ json: responses[key] ?? responses[url.pathname] });
      } else {
        requests.unhandled.push(key);
        await route.fulfill({ status: 503, json: { message: `Unseeded toolbar fixture request: ${key}` } });
      }
      return;
    }
    await route.continue();
  });
  await page.goto(path);
  return requests;
}

export async function captureRoutedToolbarState(page: Page, testInfo: TestInfo, stateName: string) {
  await expect(page.locator('main')).toBeVisible();
  await expect(page.locator('main h1')).toBeVisible();
  const groups = page.locator('main .pf-toolbar, main .pf-header-actions, main .pf-inline-form');
  const expectedSize = await page.evaluate(() => parseFloat(getComputedStyle(document.documentElement).fontSize) * (matchMedia('(pointer: coarse)').matches ? 2.75 : 2.25));
  const expectedLabelGap = await page.evaluate(() => parseFloat(getComputedStyle(document.documentElement).fontSize) / 4);
  const geometry = await groups.evaluateAll(elements => elements.map(element => {
    const bounds = element.getBoundingClientRect();
    const controls = [...element.querySelectorAll<HTMLElement>('button, input:not([type=hidden]):not([type=checkbox]):not([type=radio]):not([type=file]):not([type=range]):not([type=color]), select')]
      .filter(control => control.getBoundingClientRect().width > 0 && control.getBoundingClientRect().height > 0)
      .map(control => {
        const rect = control.getBoundingClientRect();
        const style = getComputedStyle(control);
        return {
          tag: control.tagName, name: control.getAttribute('aria-label') ?? control.textContent?.trim() ?? '',
          height: rect.height, width: rect.width, x: rect.x, y: rect.y,
          radius: style.borderTopLeftRadius, pressed: control.getAttribute('aria-pressed'),
          expanded: control.getAttribute('aria-expanded'), variant: control.dataset.pfVariant,
          participating: control.classList.contains('pf-control')
            || (control.hasAttribute('data-pf-button') && !['link', 'unstyled'].includes(control.dataset.pfVariant ?? ''))
            || ['INPUT', 'SELECT'].includes(control.tagName),
          segmented: Boolean(control.closest('.pf-toolbar__group--segmented')),
          field: ['INPUT', 'SELECT'].includes(control.tagName) || control.dataset.role === 'field',
          clipped: control.scrollWidth > control.clientWidth + 1,
          fits: rect.left >= bounds.left - 1 && rect.right <= bounds.right + 1,
        };
      });
    return {
      name: element.getAttribute('aria-label'), className: element.className,
      width: bounds.width, height: bounds.height,
      overflow: element.scrollWidth > element.clientWidth + 1, controls,
    };
  }));
  const labelGaps = await page.locator('main .pf-toolbar-field').evaluateAll(fields => fields.flatMap(field => {
    const label = field.querySelector('label');
    const control = field.querySelector('input:not([type=hidden]), select, button[data-role=field]');
    if (!label || !control || control.getBoundingClientRect().width === 0) return [];
    return [{ name: label.textContent?.trim(), gap: control.getBoundingClientRect().top - label.getBoundingClientRect().bottom }];
  }));
  for (const field of labelGaps) {
    expect(field.gap, `${stateName}: ${field.name} label gap`).toBeCloseTo(expectedLabelGap, 0);
  }
  for (const group of geometry) {
    expect(group.overflow, `${stateName}: ${group.name ?? group.className} overflows`).toBe(false);
    for (const control of group.controls) {
      expect(control.fits, `${stateName}: ${control.name} outside group`).toBe(true);
      expect(control.clipped, `${stateName}: ${control.name} clipped`).toBe(false);
      if (control.participating) {
        expect(control.height, `${stateName}: ${control.name} target size`).toBeGreaterThanOrEqual(expectedSize - 1);
        const radius = parseFloat(control.radius);
        expect(radius, `${stateName}: ${control.name} corner radius`).toBeCloseTo(control.field ? 4 : control.segmented ? radius === 0 ? 0 : 2 : 2, 0);
      }
    }
  }
  for (const control of await groups.locator('button:visible, input:visible:not([type=hidden]):not([type=file]), select:visible').all()) {
    await expect(control).toHaveAccessibleName(/\S/);
  }
  const screenshot = testInfo.outputPath(`${stateName}-after.png`);
  await page.screenshot({ path: screenshot, fullPage: true });
  await testInfo.attach(`${stateName}-after`, { path: screenshot, contentType: 'image/png' });
  await testInfo.attach(`${stateName}-geometry`, { body: JSON.stringify(geometry, null, 2), contentType: 'application/json' });
  await testInfo.attach(`${stateName}-label-gaps`, { body: JSON.stringify(labelGaps, null, 2), contentType: 'application/json' });
  return geometry;
}

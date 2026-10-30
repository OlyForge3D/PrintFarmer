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

const SCALED_GEOMETRY_TOLERANCE_PX = 0.02;

export async function assertCollapsedFiltersDisclosures(
  page: Page,
  stateName: string,
  expectedCount: number,
) {
  const filterRegions = page.locator('main [role="region"][aria-label="Filters"]');
  await expect(filterRegions, `${stateName}: filters disclosure count`)
    .toHaveCount(expectedCount);
  const filterIds = await filterRegions.evaluateAll(regions => regions.map(region => region.id));
  expect(filterIds.every(id => id.trim().length > 0), `${stateName}: filters disclosures have ids`)
    .toBe(true);
  expect(new Set(filterIds).size, `${stateName}: filters disclosure ids are unique`)
    .toBe(filterIds.length);
  const filterTriggers = await page.locator('main button[aria-controls]').evaluateAll(buttons =>
    buttons.map(button => ({
      id: button.getAttribute('aria-controls'),
      expanded: button.getAttribute('aria-expanded'),
    })),
  );
  for (const [index, region] of (await filterRegions.all()).entries()) {
    await expect(region, `${stateName}: filters disclosure ${index + 1} role`)
      .toHaveAttribute('role', 'region');
    await expect(region, `${stateName}: filters disclosure ${index + 1} name`)
      .toHaveAttribute('aria-label', 'Filters');
    await expect(region, `${stateName}: filters disclosure ${index + 1} is collapsed`)
      .toHaveJSProperty('hidden', true);
    const controls = filterTriggers.filter(trigger => trigger.id === filterIds[index]);
    expect(controls, `${stateName}: filters disclosure ${index + 1} has one trigger`)
      .toHaveLength(1);
    expect(controls[0].expanded, `${stateName}: filters disclosure ${index + 1} trigger is collapsed`)
      .toBe('false');
  }
}

async function assertScaledControlGeometry(page: Page, testInfo: TestInfo, stateName: string) {
  const root = page.locator('html');
  const rootFontSize = await page.locator('html').evaluate(element => ({
    value: element.style.getPropertyValue('font-size'),
    priority: element.style.getPropertyPriority('font-size'),
  }));
  try {
    await root.evaluate(element => element.style.setProperty('font-size', '32px'));
    await expect.poll(() => root.evaluate(element => getComputedStyle(element).fontSize))
      .toBe('32px');
    await page.evaluate(async () => {
      await document.fonts.ready;
      await new Promise<void>(resolve => {
        requestAnimationFrame(() => requestAnimationFrame(() => resolve()));
      });
    });
    await page.locator('main .pf-control:visible').evaluateAll(async elements => {
      const transitions = elements.flatMap(control => control.getAnimations()
        .filter(animation => animation.playState === 'running' && animation.effect?.getTiming().iterations !== Infinity)
        .map(animation => animation.finished.catch(() => undefined)));
      await Promise.all(transitions);
    });
    await page.evaluate(() => new Promise<void>(resolve => {
      requestAnimationFrame(() => requestAnimationFrame(() => resolve()));
    }));
    const coarse = await page.evaluate(() => matchMedia('(pointer: coarse)').matches);
    const controls = await page.locator('main .pf-control:visible').evaluateAll(elements => elements.map(control => {
      const style = getComputedStyle(control);
      const segment = control.closest('.pf-toolbar__group--segmented');
      const segmentButtons = segment
        ? [...segment.querySelectorAll('button[data-pf-button]')]
        : [];
      const tag = control.tagName;
      return {
        name: control.getAttribute('aria-label') ?? control.textContent?.trim() ?? '',
        size: control.dataset.size ?? 'md',
        role: control.dataset.role ?? (tag === 'INPUT' || tag === 'SELECT' ? 'field' : 'button'),
        height: control.getBoundingClientRect().height,
        direction: style.direction,
        segmented: Boolean(segment),
        firstSegment: segmentButtons[0] === control,
        lastSegment: segmentButtons.at(-1) === control,
        radii: {
          topLeft: parseFloat(style.borderTopLeftRadius),
          topRight: parseFloat(style.borderTopRightRadius),
          bottomRight: parseFloat(style.borderBottomRightRadius),
          bottomLeft: parseFloat(style.borderBottomLeftRadius),
        },
      };
    }));
    await testInfo.attach(`${stateName}-scaled-geometry`, {
      body: JSON.stringify(controls, null, 2),
      contentType: 'application/json',
    });
    for (const control of controls) {
      const expectedHeight = control.size === 'sm'
        ? coarse ? 72 : 56
        : control.size === 'lg' || coarse ? 88 : 72;
      expect(Math.abs(control.height - expectedHeight),
        `${stateName}: ${control.name} ${control.size} height at 32px root`)
        .toBeLessThanOrEqual(SCALED_GEOMETRY_TOLERANCE_PX);

      const expectedRadius = control.role === 'field' ? 4 : 2;
      const expectedCorners = {
        topLeft: control.segmented && control.direction === 'ltr' && !control.firstSegment ? 0
          : control.segmented && control.direction === 'rtl' && !control.lastSegment ? 0 : expectedRadius,
        topRight: control.segmented && control.direction === 'ltr' && !control.lastSegment ? 0
          : control.segmented && control.direction === 'rtl' && !control.firstSegment ? 0 : expectedRadius,
        bottomRight: control.segmented && control.direction === 'ltr' && !control.lastSegment ? 0
          : control.segmented && control.direction === 'rtl' && !control.firstSegment ? 0 : expectedRadius,
        bottomLeft: control.segmented && control.direction === 'ltr' && !control.firstSegment ? 0
          : control.segmented && control.direction === 'rtl' && !control.lastSegment ? 0 : expectedRadius,
      };
      for (const [corner, radius] of Object.entries(expectedCorners)) {
        expect(control.radii[corner as keyof typeof control.radii],
          `${stateName}: ${control.name} ${corner} radius`)
          .toBeCloseTo(radius, 2);
      }
    }
  } finally {
    await root.evaluate((element, previous) => {
      if (previous.value) element.style.setProperty('font-size', previous.value, previous.priority);
      else element.style.removeProperty('font-size');
    }, rootFontSize);
    await page.locator('main .pf-control:visible').evaluateAll(async elements => {
      const transitions = elements.flatMap(control => control.getAnimations()
        .filter(animation => animation.playState === 'running' && animation.effect?.getTiming().iterations !== Infinity)
        .map(animation => animation.finished.catch(() => undefined)));
      await Promise.all(transitions);
    });
    await page.evaluate(() => new Promise<void>(resolve => {
      requestAnimationFrame(() => requestAnimationFrame(() => resolve()));
    }));
  }
}

export async function captureRoutedToolbarState(
  page: Page,
  testInfo: TestInfo,
  stateName: string,
  expectedFiltersDisclosureCount: number,
) {
  await expect(page.locator('main')).toBeVisible();
  await expect(page.locator('main h1')).toBeVisible();
  await assertCollapsedFiltersDisclosures(
    page,
    stateName,
    expectedFiltersDisclosureCount,
  );
  await assertScaledControlGeometry(page, testInfo, stateName);
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
          minBlockSize: style.minBlockSize, paddingBlock: style.paddingBlock,
          lineHeight: style.lineHeight, fontSize: style.fontSize,
          runningAnimations: control.getAnimations().filter(animation => animation.playState === 'running').length,
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
  await testInfo.attach(`${stateName}-geometry`, { body: JSON.stringify(geometry, null, 2), contentType: 'application/json' });
  await testInfo.attach(`${stateName}-label-gaps`, { body: JSON.stringify(labelGaps, null, 2), contentType: 'application/json' });
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
  return geometry;
}

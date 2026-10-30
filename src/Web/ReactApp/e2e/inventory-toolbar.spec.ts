import { expect, test } from '@playwright/test';

// Exercise the real tabs, not replicas of their filter markup.
for (const [surface, target] of [
  ['parts', 'inactive-skus'],
  ['bins', 'inactive-bins'],
  ['spools', 'empty-spools'],
] as const) {
  test(`${surface}: native filter labels retain pointer-sized targets without I/O`, async ({ page, context, baseURL }, testInfo) => {
    const origin = new URL(baseURL!).origin;
    const blockedRequests: string[] = [];
    const browserErrors: string[] = [];
    page.on('pageerror', error => browserErrors.push(error.message));

    // Never connectToServer: all sockets (including Vite HMR) terminate in
    // Playwright's in-memory mock. Closing HMR instead causes a Vite exception.
    await context.routeWebSocket(/.*/, socket => {
      socket.onMessage(() => {});
    });
    await context.route('**/*', route => {
      const url = new URL(route.request().url());
      if (url.origin !== origin || /^\/api(?:\/|$)/i.test(url.pathname)) {
        blockedRequests.push(url.href);
        return route.abort();
      }
      if (url.pathname === '/') {
        return route.fulfill({
          contentType: 'text/html',
          body: `<!doctype html>
            <html><head><meta name="viewport" content="width=device-width, initial-scale=1"></head>
            <body><div id="root"></div><script type="module">
              import RefreshRuntime from '/@react-refresh';
              RefreshRuntime.injectIntoGlobalHook(window);
              window.$RefreshReg$ = () => {};
              window.$RefreshSig$ = () => type => type;
              window.__vite_plugin_react_preamble_installed__ = true;
              await import('/src/index.css');
              const { mountInventoryToolbarFixture } =
                await import('/e2e/fixtures/inventory-toolbar-harness.mjs');
              mountInventoryToolbarFixture(document.getElementById('root'), '${surface}');
            </script></body></html>`,
        });
      }
      return route.continue();
    });

    await page.goto('/');
    const root = page.locator(`[data-filter-hit-target="${target}"]`);
    await expect(root).toBeVisible();
    const coarse = await page.evaluate(() => matchMedia('(pointer: coarse)').matches);
    expect(coarse, 'project must exercise its intended pointer type')
      .toBe(Boolean(testInfo.project.use.hasTouch));
    const expectedHeight = coarse ? 44 : 36;
    const labels = surface === 'spools' ? root : root.locator('label');
    await expect(labels).toHaveCount(surface === 'spools' ? 1 : 2);

    const geometry = await labels.evaluateAll(elements => elements.map(label => ({
      height: label.getBoundingClientRect().height,
      minHeight: getComputedStyle(label).minHeight,
      checkbox: label.querySelector('input')?.getBoundingClientRect().height,
      track: label.querySelector('input + div')?.getBoundingClientRect().height,
    })));
    await testInfo.attach('native-filter-geometry', {
      body: JSON.stringify({ surface, coarse, expectedHeight, geometry }, null, 2),
      contentType: 'application/json',
    });
    for (const label of geometry) {
      expect(label.height, 'clickable label height').toBe(expectedHeight);
      expect(label.minHeight, 'computed label minimum').toBe(`${expectedHeight}px`);
    }
    if (surface === 'spools') {
      expect(geometry[0].checkbox, 'native checkbox visual remains 16px').toBe(16);
    } else {
      expect(geometry[0].track, 'toggle track visual remains 24px').toBe(24);
    }

    const input = root.locator('input');
    await expect(input).not.toBeChecked();
    // Click text, then the native switch label (or checkbox label) to reverse it.
    await labels.last().click();
    await expect(input).toBeChecked();
    await labels.first().click();
    await expect(input).not.toBeChecked();

    expect(browserErrors, 'fixture must mount without browser exceptions').toEqual([]);
    expect(blockedRequests, 'fixtures must not attempt API or external HTTP requests').toEqual([]);
  });
}

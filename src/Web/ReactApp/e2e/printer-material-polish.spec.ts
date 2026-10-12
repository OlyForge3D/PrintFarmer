import { expect, test } from '@playwright/test';

// One batched desktop/mobile inspection of both surface modules; never mount the app.
for (const width of [1280, 320]) {
  test(`material assignment and independent states at ${width}px`, async ({ page }) => {
    await page.setViewportSize({ width, height: 1000 });
    const errors: string[] = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.route(url => url.pathname.startsWith('/api/') || !['localhost', '127.0.0.1'].includes(url.hostname), route => route.fulfill({ status: 503, json: { message: 'No live API in material fixtures' } }));
    await page.route('**/__material_fixture', route => route.fulfill({ contentType: 'text/html', body: '<!doctype html><html data-theme="dark"><head><title>Material polish fixture</title></head><body><main id="material-root"></main></body></html>' }));
    await page.goto('/__material_fixture');
    await page.addScriptTag({ type: 'module', content: `
      import RefreshRuntime from '/@react-refresh';
      RefreshRuntime.injectIntoGlobalHook(window);
      window.$RefreshReg$ = () => {};
      window.$RefreshSig$ = () => type => type;
      window.__vite_plugin_react_preamble_installed__ = true;
      const fixture = await import('/src/test/fixtures/printer-material-ui.tsx');
      fixture.mountMaterials(document.getElementById('material-root'));
    ` });
    const sidebar = page.getByRole('region', { name: 'Sidebar material module' });
    await expect(sidebar.getByRole('button', { name: /^Gate 1B:/ })).toContainText('Assigned spool depleted');
    await expect(sidebar.getByRole('button', { name: /^Gate 1C:/ })).toContainText('Remaining unknown');
    await expect(sidebar.getByRole('button', { name: /^Gate 1D:/ })).toContainText('Filament not loaded');
    const active = sidebar.getByRole('button', { name: /^Gate 1A:/ });
    await expect(active).toContainText('No spool assigned');
    await expect(active).toContainText('In use');
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    for (const slot of await page.locator('[data-assignment]').all()) {
      const box = await slot.boundingBox();
      expect(box!.width).toBeGreaterThanOrEqual(44);
      expect(box!.height).toBeGreaterThanOrEqual(44);
    }
    // Inspect rendered ordering, fit and contrast, not just DOM state labels.
    const measurements = await page.locator('[data-assignment]').evaluateAll(elements => elements.map(element => {
      const contents = element.querySelector('span > span')!;
      const label = contents.children[0].getBoundingClientRect();
      const spool = contents.children[1].getBoundingClientRect();
      const text = Array.from(contents.children).find(child => child.textContent === 'In use') ?? contents.children[3];
      const style = getComputedStyle(text);
      let backgroundElement: Element | null = text;
      let background = 'rgba(0, 0, 0, 0)';
      while (backgroundElement && background === 'rgba(0, 0, 0, 0)') {
        background = getComputedStyle(backgroundElement).backgroundColor;
        backgroundElement = backgroundElement.parentElement;
      }
      const luminance = (value: string) => {
        const channels = (value.match(/[\d.]+/g) ?? []).slice(0, 3).map(Number).map(v => {
          const channel = v / 255;
          return channel <= 0.04045 ? channel / 12.92 : ((channel + 0.055) / 1.055) ** 2.4;
        });
        return channels[0] * 0.2126 + channels[1] * 0.7152 + channels[2] * 0.0722;
      };
      const foreground = luminance(style.color);
      const substrate = luminance(background);
      return { labelBeforeSpool: label.bottom <= spool.top, fits: element.scrollWidth <= element.clientWidth,
        contrast: (Math.max(foreground, substrate) + 0.05) / (Math.min(foreground, substrate) + 0.05) };
    }));
    for (const measurement of measurements) {
      expect(measurement.labelBeforeSpool).toBe(true);
      expect(measurement.fits).toBe(true);
      expect(measurement.contrast).toBeGreaterThanOrEqual(4.5);
    }
    await test.info().attach('rendered-material-measurements', { body: JSON.stringify({ width, measurements }), contentType: 'application/json' });
    await page.screenshot({ path: `/tmp/printfarmer-material-states-${width}.png`, fullPage: true });
    // Keyboard inspection changes selection, not the physical in-use marker.
    const emptyGate = sidebar.getByRole('button', { name: /^Gate 1D:/ });
    await emptyGate.focus();
    await page.keyboard.press('Enter');
    await expect(emptyGate).toHaveAttribute('aria-pressed', 'true');
    await expect(active).toContainText('In use');
    await expect(emptyGate).not.toContainText('In use');
    await sidebar.getByRole('button', { name: 'Change', exact: true }).click();
    const dialog = page.getByRole('dialog');
    await expect(dialog.getByRole('button', { name: 'Unassign', exact: true })).toBeVisible();
    await dialog.getByRole('button', { name: 'Unassign', exact: true }).click();
    await expect(page.getByTestId('fixture-write')).toHaveText('Unassign qidi index 4');
    await expect(dialog).not.toBeVisible();
    await sidebar.getByRole('button', { name: 'Assign', exact: true }).click();
    await page.getByRole('button', { name: 'Select material PLA', exact: true }).click();
    await page.getByRole('button', { name: /Fixture PLA/ }).click();
    await expect(page.getByTestId('fixture-write')).toHaveText('Assign qidi index 4 spool 99');
    await expect(sidebar.getByRole('button', { name: /^Gate 1D:/ })).toContainText('Spool #99');
    await expect(sidebar.getByRole('button', { name: /^Gate 1D:/ })).toContainText('Filament not loaded');
    await page.screenshot({ path: `/tmp/printfarmer-material-${width}.png`, fullPage: true });
    expect(errors).toEqual([]);
  });
}

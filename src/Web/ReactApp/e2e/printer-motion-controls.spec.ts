import { expect, test, type Page } from '@playwright/test';

// This page imports real controls but never mounts the application or contacts a printer.
async function mountFixture(page: Page, content: string) {
  await page.route(url => url.pathname.startsWith('/api/'), route => route.fulfill({ status: 503, json: { message: 'No live API in motion fixtures' } }));
  await page.route('**/__motion_fixture', route => route.fulfill({
    contentType: 'text/html',
    body: '<!doctype html><html><head><title>Motion controls fixture</title></head><body><main id="motion-root"></main></body></html>',
  }));
  await page.goto('/__motion_fixture');
  await page.addScriptTag({
    type: 'module',
    content: `
      import RefreshRuntime from '/@react-refresh';
      RefreshRuntime.injectIntoGlobalHook(window);
      window.$RefreshReg$ = () => {};
      window.$RefreshSig$ = () => type => type;
      window.__vite_plugin_react_preamble_installed__ = true;
      const fixture = await import('/src/test/fixtures/printer-motion-ui.tsx');
      ${content}
    `,
  });
}

async function mountCoordinateFixture(page: Page) {
  await mountFixture(page, `fixture.mountCoordinates(document.getElementById('motion-root'));`);
  await expect(page.getByRole('region', { name: 'Detail coordinates' })).toBeVisible();
}

test.describe('Printer motion feedback without physical commands', () => {
  test('direct controls have no tracking status or recovery panel', async ({ page }) => {
    const errors: string[] = [];
    page.on('pageerror', error => errors.push(error.message));
    await mountCoordinateFixture(page);
    await expect(page.getByRole('region', { name: 'Motion status' })).toHaveCount(0);
    await expect(page.getByText(/Motion Ready|Motion technical details|Operation ID/)).toHaveCount(0);
    await expect(page.getByRole('button', { name: /recover|retry|recheck motion/i })).toHaveCount(0);
    expect(errors).toEqual([]);
  });

  for (const viewportWidth of [1280, 320]) {
  test(`shared GO stays compact and shows initiating activity at ${viewportWidth}px`, async ({ page }) => {
    await page.setViewportSize({ width: viewportWidth, height: 900 });
    const errors: string[] = [];
    page.on('pageerror', error => errors.push(error.message));
    await mountCoordinateFixture(page);
    for (const name of ['Detail coordinates', 'Sidebar coordinates']) {
      const region = page.getByRole('region', { name });
      const go = region.getByRole('button', { name: 'GO to absolute position' });
      const x = region.getByRole('spinbutton', { name: 'X absolute target' });
      await expect(go).toBeDisabled();
      await expect(region.getByRole('alert')).toHaveCount(0);
      const goBox = await go.boundingBox();
      const xBox = await x.boundingBox();
      const regionBox = await region.boundingBox();
      expect(goBox).not.toBeNull();
      expect(xBox).not.toBeNull();
      expect(regionBox).not.toBeNull();
      expect(goBox!.width).toBe(44);
      expect(goBox!.height).toBe(32);
      expect(goBox!.x + goBox!.width).toBeLessThanOrEqual(regionBox!.x + regionBox!.width);
      if (regionBox!.width < 304) expect(goBox!.y).toBeGreaterThanOrEqual(xBox!.y + xBox!.height);
      else expect(goBox!.y).toBe(xBox!.y);
    }
    const detail = page.getByRole('region', { name: 'Detail coordinates' });
    await detail.getByRole('spinbutton', { name: 'X absolute target' }).fill('10');
    await detail.getByRole('spinbutton', { name: 'Y absolute target' }).fill('20');
    await detail.getByRole('spinbutton', { name: 'Z absolute target' }).fill('30');
    const go = detail.getByRole('button', { name: 'GO to absolute position' });
    await go.click();
    await expect(go).toHaveAttribute('aria-busy', 'true');
    await expect(page.locator('button[aria-busy="true"]')).toHaveCount(1);
    await expect(detail.getByTestId('submitted-motion')).toHaveText('10,20,30');
    await detail.getByRole('button', { name: 'Complete fixture motion' }).click();
    await expect(go).not.toHaveAttribute('aria-busy', 'true');
    expect(errors).toEqual([]);
  });
  }

  test('mode comes only from User Settings: no local selector, guided help, no Motion help toggle', async ({ page }) => {
    await mountCoordinateFixture(page);
    for (const name of ['Detail coordinates', 'Sidebar coordinates']) {
      const region = page.getByRole('region', { name });
      await expect(region.getByRole('button', { name: /^(Guided|Expert)$/ })).toHaveCount(0);
      await expect(region.getByText(/Jog moves by the selected step/)).toBeVisible();
      await expect(region.getByText('Motion help', { exact: true })).toHaveCount(0);
    }
    expect(await page.evaluate(() => localStorage.getItem('pf.printer-controls.mode'))).toBeNull();
  });
});
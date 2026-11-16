import { expect, test, type Page } from '@playwright/test';

// This page imports real controls but never mounts the application or contacts a printer.
async function mountFixture(page: Page) {
  await page.route(url => url.pathname.startsWith('/api/'), route => route.fulfill({ status: 503, json: { message: 'No live API in motion fixtures' } }));
  await page.goto('/src/test/fixtures/printer-motion.html');
}

async function mountCoordinateFixture(page: Page) {
  await mountFixture(page);
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
  test(`per-axis entry fits and shows initiating activity at ${viewportWidth}px`, async ({ page }) => {
    await page.setViewportSize({ width: viewportWidth, height: 900 });
    const errors: string[] = [];
    page.on('pageerror', error => errors.push(error.message));
    await mountCoordinateFixture(page);
    for (const name of ['Detail coordinates', 'Sidebar coordinates']) {
      const region = page.getByRole('region', { name });
      await expect(region.getByRole('button', { name: /GO/ })).toHaveCount(0);
      const x = region.getByRole('spinbutton', { name: 'X absolute target' });
      await expect(region.getByRole('alert')).toHaveCount(0);
      const xBox = await x.boundingBox();
      const regionBox = await region.boundingBox();
      expect(xBox).not.toBeNull();
      expect(regionBox).not.toBeNull();
      expect(xBox!.x + xBox!.width).toBeLessThanOrEqual(regionBox!.x + regionBox!.width);
    }
    const detail = page.getByRole('region', { name: 'Detail coordinates' });
    await detail.getByRole('spinbutton', { name: 'X absolute target' }).fill('10');
    await detail.getByRole('spinbutton', { name: 'X absolute target' }).press('Enter');
    await expect(detail.getByText('Sending movement command…', { exact: true })).toBeVisible();
    await expect(detail.getByRole('spinbutton', { name: 'Y absolute target' })).toBeDisabled();
    await expect(detail.getByTestId('submitted-motion')).toHaveText('{"x":10}');
    await detail.getByRole('button', { name: 'Complete fixture motion' }).click();
    await expect(detail.getByText('Sending movement command…', { exact: true })).toHaveCount(0);
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

  for (const state of [
    {
      name: 'unknown',
      message: "PrintFarmer could not confirm remote Home and jog support. Check the printer connection and credentials. Use the printer's physical controls while remote motion is unavailable.",
    },
    {
      name: 'unsupported',
      message: "Remote Home and jog controls are not supported by this printer's API. Use the printer's physical controls.",
    },
  ]) {
    for (const viewportWidth of [1280, 390]) {
      test(`shows ${state.name} support with disabled controls at ${viewportWidth}px`, async ({ page }, testInfo) => {
        await page.setViewportSize({ width: viewportWidth, height: 900 });
        await page.goto(`/src/test/fixtures/printer-motion.html?support=${state.name}`);
        const region = page.getByRole('region', { name: `Motion support ${state.name === 'unknown' ? 'Unknown' : 'Unsupported'}` });
        await expect(region.getByText(state.message, { exact: true })).toBeVisible();
        for (const control of await region.getByRole('button', { name: /Home all axes|Home X\/Y|Home Z|Jog [XYZ]/ }).all()) {
          await expect(control).toBeDisabled();
        }
        const screenshot = testInfo.outputPath(`${state.name}-${viewportWidth}.png`);
        await page.screenshot({ path: screenshot, fullPage: true });
        await testInfo.attach(`${state.name}-${viewportWidth}`, { path: screenshot, contentType: 'image/png' });
      });
    }
  }
});

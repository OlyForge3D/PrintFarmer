import { expect, test } from '@playwright/test';

test('sensor controls fit card and sidebar widths on desktop and mobile without hardware access', async ({ page }) => {
  const errors: string[] = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.route(url => url.pathname.startsWith('/api/') || !['localhost', '127.0.0.1'].includes(url.hostname),
    route => route.fulfill({ status: 503, json: { message: 'Isolated sensor fixture: hardware access blocked' } }));
  await page.goto('/src/test/fixtures/printer-sensors.html');
  const card = page.getByRole('region', { name: 'card', exact: true });
  const sidebar = page.getByRole('region', { name: 'sidebar', exact: true });
  await expect(card.getByText('Second nozzle', { exact: true })).toBeVisible();
  await expect(page.getByText('Primary nozzle', { exact: true })).toHaveCount(0);
  await expect(card.getByRole('spinbutton', { name: 'Set Part cooling Speed' })).toBeEnabled();
  await expect(sidebar.getByRole('spinbutton', { name: 'Set Part cooling Speed' })).toBeDisabled();
  await expect(card.getByRole('spinbutton', { name: 'Set Controller fan Speed' })).toBeDisabled();
  await expect(card.getByRole('spinbutton', { name: 'X absolute target' })).toBeEnabled();
  await expect(card.getByRole('spinbutton', { name: 'Z absolute target' })).toBeDisabled();
  await expect(sidebar.getByRole('spinbutton', { name: 'X absolute target' })).toBeDisabled();
  await expect(page.getByRole('button', { name: /GO/ })).toHaveCount(0);
  for (const [name, width, height] of [['desktop', 1100, 1300], ['mobile', 360, 1000]] as const) {
    await page.setViewportSize({ width, height });
    await card.getByRole('spinbutton', { name: 'Set Part cooling Speed' }).fill('65');
    await expect(card.getByRole('spinbutton', { name: 'Set Heater Box1 Target' })).toBeEnabled();
    await expect(card.getByRole('button', { name: /^Apply Set/ })).toHaveCount(0);
    await expect(card.getByRole('region', { name: 'Fans & outputs', exact: true }).first()).toBeVisible();
    await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
    await page.screenshot({ path: test.info().outputPath(`sensors-${name}.png`), fullPage: true });
    await card.getByRole('spinbutton', { name: 'Set Part cooling Speed' }).press('Escape');
    for (const [surface, scope] of [['card', card], ['sidebar', sidebar]] as const) {
      for (const section of ['Move', 'Temperatures', 'Fans & outputs']) {
        const region = section === 'Move' ? scope.locator('[data-printer-coordinate-row]')
          : scope.getByRole('region', { name: section, exact: true }).first();
        await region.scrollIntoViewIfNeeded();
        await region.screenshot({ path: test.info().outputPath(`${surface}-${section.toLowerCase().replaceAll(' ', '-')}-${name}.png`) });
      }
    }
    await card.getByRole('button', { name: 'Relative', exact: true }).click();
    await expect(card.getByRole('spinbutton', { name: 'Z movement amount' })).toBeDisabled();
    await card.locator('[data-printer-coordinate-row]').screenshot({ path: test.info().outputPath(`card-relative-${name}.png`) });
    await card.getByRole('button', { name: 'Absolute', exact: true }).click();
  }
  expect(errors).toEqual([]);
});

import { expect, test } from '@playwright/test';
import { mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { captureOrigin, captureTime, isolateCapture } from './fixtures';

const assets = fileURLToPath(new URL('../../../../../site/assets/captures/', import.meta.url));

for (const scene of [
  { name: 'fleet', path: '/printers', heading: 'Printers', marker: 'Atlas' },
  { name: 'projects', path: '/projects', heading: 'Projects', marker: 'Workshop enclosure' },
  { name: 'materials', path: '/spools', heading: 'Filament Inventory', marker: 'Ocean blue PLA' },
]) {
  test(`capture ${scene.name} from the real routed app`, async ({ page, context }) => {
    const violations = await isolateCapture(context);
    const errors: string[] = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.clock.setFixedTime(new Date(captureTime));
    await page.goto(scene.path);
    await expect(page.getByRole('heading', { name: scene.heading, exact: true })).toBeVisible();
    if (scene.name === 'materials') {
      await page.getByRole('tab', { name: 'Spools', exact: true }).click();
      await expect(page.getByRole('tab', { name: 'Spools', exact: true })).toHaveAttribute('aria-selected', 'true');
    }
    await expect(page.getByText(scene.marker, { exact: true }).first()).toBeVisible();
    await expect(page.getByLabel('Connected', { exact: true })).toBeVisible();
    await expect(page.locator('main [aria-busy="true"]')).toHaveCount(0);
    await expect(page.locator('main [role="alert"]')).toHaveCount(0);
    if (scene.name === 'fleet') {
      for (const name of ['Atlas', 'Birch', 'Cedar', 'Delta', 'Ember', 'Finch']) {
        await expect(page.getByText(name, { exact: true })).toBeVisible();
      }
      await expect(page.getByText('64%', { exact: true })).toBeVisible();
    }
    if (scene.name === 'projects') {
      await expect(page.getByRole('heading', { level: 3 })).toHaveCount(3);
      await expect(page.getByRole('progressbar', { name: 'Workshop enclosure progress' })).toHaveAttribute('aria-valuenow', '58');
    }
    await page.evaluate(() => document.fonts.ready);
    await expect(page.locator('main')).toHaveCSS('display', 'block');
    await expect(page.locator('main')).toBeInViewport();
    expect(violations, 'Add explicit fixtures; never relax the network guard').toEqual([]);
    expect(errors).toEqual([]);
    await mkdir(assets, { recursive: true });
    await page.screenshot({ path: `${assets}${scene.name}.png`, animations: 'disabled' });
    expect(violations).toEqual([]);
  });
}

test('network guard blocks external, unknown API, other loopback ports, and writes', async ({ context, page }) => {
  const violations = await isolateCapture(context);
  await page.goto(captureOrigin);
  const results = await page.evaluate(async () => {
    const requests = [
      ['https://production.invalid/api/printers', 'GET'],
      ['http://127.0.0.1:5245/api/printers', 'GET'],
      ['/api/not-a-fixture', 'GET'],
      ['/api/printers', 'POST'],
    ];
    return Promise.all(requests.map(async ([url, method]) => {
      try { return (await fetch(url, { method })).status; }
      catch { return 'blocked'; }
    }));
  });
  expect(results).toEqual(['blocked', 'blocked', 503, 503]);
  expect(violations).toEqual(expect.arrayContaining([
    expect.stringContaining('production.invalid'),
    expect.stringContaining('127.0.0.1:5245'),
    'Missing capture fixture: GET /api/not-a-fixture',
    'Missing capture fixture: POST /api/printers',
  ]));
});

import { expect, test } from '@playwright/test';
import { mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { captureOrigin, captureTime, isolateCapture, printers } from './fixtures';

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
    if (scene.name === 'fleet') await page.setViewportSize({ width: 1710, height: 1100 });
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
      const cards = page.locator('main article[data-pf-card]');
      await expect(cards).toHaveCount(8);
      for (const printer of printers) {
        const card = cards.filter({ has: page.getByText(printer.name, { exact: true }) });
        await expect(card).toBeInViewport({ ratio: 1 });
        const modelLabel = card.getByText(printer.modelName, { exact: true });
        await expect(modelLabel).toBeVisible();
        expect(await modelLabel.evaluate(element => ({
          clipped: element.scrollWidth > element.clientWidth,
          text: element.textContent,
        })), 'The complete model identity must be readable, not just present in the DOM').toEqual({
          clipped: false, text: printer.modelName,
        });
        const image = card.locator('[data-testid="compact-printer-image"] img');
        const expectedCover = `/assets/orcaslicer/${printer.manufacturerName.toLowerCase()}/${encodeURIComponent(printer.modelName)}_cover.png`;
        await expect(image).toHaveAttribute('src', expectedCover);
        await expect.poll(() => image.evaluate((img: HTMLImageElement) => img.complete && img.naturalWidth > 0)).toBe(true);
      }
      const positions = await cards.evaluateAll(elements => elements.map(element => {
        const { x, y } = element.getBoundingClientRect();
        return { x: Math.round(x), y: Math.round(y) };
      }));
      expect(new Set(positions.map(position => position.x)).size, 'Four columns').toBe(4);
      const rows = [...new Set(positions.map(position => position.y))];
      expect(rows, 'Two complete rows').toHaveLength(2);
      for (const y of rows) expect(positions.filter(position => position.y === y)).toHaveLength(4);
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

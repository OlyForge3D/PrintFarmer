import { expect, test } from '@playwright/test';

const site = 'http://127.0.0.1:4179/PrintFarmer/';

test('site is static, populated, keyboard usable, and works under the project subpath', async ({ context, page, request }) => {
  const unexpected: string[] = [];
  const errors: string[] = [];
  page.on('pageerror', error => errors.push(error.message));
  await context.route('**/*', route => {
    const url = new URL(route.request().url());
    if (url.origin !== new URL(site).origin || !url.pathname.startsWith('/PrintFarmer/')) {
      unexpected.push(`${route.request().method()} ${url.origin}${url.pathname}`);
      return route.abort('blockedbyclient');
    }
    return route.continue();
  });
  await page.goto(site);
  await expect(page.getByRole('heading', { level: 1 })).toHaveText('Your farm.In focus.');
  await page.keyboard.press('Tab');
  await expect(page.getByRole('link', { name: 'Skip to content' })).toBeFocused();
  await page.keyboard.press('Enter');
  await expect(page).toHaveURL(`${site}#main`);
  const imageResults = await page.locator('img').evaluateAll(async images => Promise.all(images.map(async image => {
    if (!(image instanceof HTMLImageElement)) throw new Error('Expected an HTML image');
    await image.decode();
    return { alt: image.getAttribute('alt'), width: image.naturalWidth, loaded: image.complete };
  })));
  expect(imageResults.every(image => image.loaded && image.width > 0 && image.alt !== null)).toBe(true);
  for (const link of await page.locator('a[href^="#"]').all()) {
    const href = await link.getAttribute('href');
    await expect(page.locator(href!)).toHaveCount(1);
  }
  await page.getByRole('link', { name: 'Get started', exact: false }).click();
  await expect(page.getByRole('heading', { name: 'Make it your workspace.' })).toBeInViewport();
  await page.getByRole('link', { name: 'Image credits', exact: true }).click();
  await expect(page).toHaveURL(`${site}image-notices.html`);
  await expect(page.getByRole('heading', { name: 'Image credits', exact: true })).toBeVisible();
  await expect(page.locator('main li a')).toHaveCount(8);
  const license = await request.get(`${site}assets/orcaslicer-LICENSE.txt`);
  expect(license.status()).toBe(200);
  expect(await license.text()).toContain('GNU AFFERO GENERAL PUBLIC LICENSE');
  await page.getByRole('link', { name: 'Privacy', exact: true }).click();
  await expect(page).toHaveURL(`${site}privacy/`);
  await expect(page.getByRole('heading', { name: /Privacy/ }).first()).toBeVisible();
  expect(unexpected).toEqual([]);
  expect(errors).toEqual([]);
  expect((await request.get('http://127.0.0.1:4178/api/printers')).status()).toBe(503);
  expect((await request.get(`${site}e2e/marketing/fixtures.ts`)).status()).toBe(404);
});

for (const width of [1440, 768, 390, 320]) {
  test(`responsive marketing page at ${width}px with reduced motion`, async ({ page }) => {
    await page.setViewportSize({ width, height: 1000 });
    await page.goto(site);
    await expect(page.getByRole('heading', { level: 1 })).toBeVisible();
    await page.locator('img').evaluateAll(images => Promise.all(images.map(image => {
      if (!(image instanceof HTMLImageElement)) throw new Error('Expected an HTML image');
      return image.decode();
    })));
    const metrics = await page.evaluate(() => ({
      overflow: document.documentElement.scrollWidth - innerWidth,
      animation: getComputedStyle(document.querySelector('.fleet-plate')!).animationName,
      targets: [...document.querySelectorAll('a')].filter(link => {
        const bounds = link.getBoundingClientRect();
        return bounds.width === 0 || bounds.height === 0;
      }).map(link => link.textContent),
    }));
    expect(metrics.overflow).toBeLessThanOrEqual(1);
    expect(metrics.animation).toBe('none');
    expect(metrics.targets).toEqual([]);
    if (width === 1440 || width === 390) {
      await page.screenshot({ path: test.info().outputPath(`site-preview-${width}.png`), fullPage: true, animations: 'disabled' });
    }
  });
}

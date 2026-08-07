import { expect, test } from '@playwright/test';

const PRINT_TOKENS = {
  '--pf-bg-0': '#ffffff',
  '--pf-text-primary': '#111827',
  '--pf-text-muted': '#5f6977',
  '--pf-border': '#767676',
  '--pf-error': '#991b1b',
  '--pf-warning': '#854d0e',
  '--pf-success': '#166534',
} as const;

test.describe('print stylesheet contract (#1126)', () => {
  for (const theme of ['forge', 'light']) {
    test(`${theme} resolves the theme-independent print palette`, async ({ page }) => {
      await page.goto('/');
      await page.evaluate((value) => {
        document.documentElement.setAttribute('data-theme', value);
      }, theme);

      const screenBackground = await page.evaluate(() =>
        getComputedStyle(document.documentElement).getPropertyValue('--pf-bg-0').trim());

      await page.emulateMedia({ media: 'print' });

      const printValues = await page.evaluate((tokens) => {
        const style = getComputedStyle(document.documentElement);
        return Object.fromEntries(tokens.map((token) => [token, style.getPropertyValue(token).trim()]));
      }, Object.keys(PRINT_TOKENS));

      expect(printValues).toEqual(PRINT_TOKENS);
      if (theme === 'forge') {
        expect(screenBackground).toBe('#0f0d0b');
        expect(printValues['--pf-bg-0']).not.toBe(screenBackground);
      }
    });
  }

  test('hides only marked chrome and removes fixed content clipping', async ({ page }) => {
    await page.setContent(`
      <link rel="stylesheet" href="http://localhost:3000/src/index.css">
      <body data-theme="forge">
        <div id="root">
        <div data-print-layout>
          <nav data-print-hidden>Navigation</nav>
          <div class="tsqd-open-btn-container">Query devtools</div>
          <div class="driver-popover" style="display: block">Tour</div>
          <svg class="driver-overlay"></svg>
          <main data-main-content>
            <h1 id="themed-heading">Printer status</h1>
            <div id="themed-progress" role="progressbar">42%</div>
            <button id="meaningful-action">Retry failed job</button>
            <article data-pf-card id="card">Printer status</article>
            <div class="fixed overflow-x-auto" id="fixed-content">History details</div>
          </main>
        </div>
        </div>
      </body>`);
    await page.waitForFunction(() =>
      getComputedStyle(document.documentElement).getPropertyValue('--pf-bg-0').trim().length > 0);
    await page.emulateMedia({ media: 'print' });

    await expect(page.locator('[data-print-hidden]')).toBeHidden();
    await expect(page.locator('.tsqd-open-btn-container')).toBeHidden();
    await expect(page.locator('.driver-popover')).toBeHidden();
    await expect(page.locator('.driver-overlay')).toBeHidden();
    await expect(page.locator('#meaningful-action')).toBeVisible();
    await expect(page.locator('#fixed-content')).toHaveCSS('position', 'static');
    await expect(page.locator('#fixed-content')).toHaveCSS('overflow-x', 'visible');
    await expect(page.locator('#card')).toHaveCSS('break-inside', 'avoid');
    await expect(page.locator('#themed-heading')).toHaveCSS('text-shadow', 'none');
    await expect(page.locator('#themed-progress')).toHaveCSS('box-shadow', 'none');
  });
});

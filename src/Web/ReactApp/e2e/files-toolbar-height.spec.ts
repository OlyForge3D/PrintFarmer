import { expect, test } from '@playwright/test';
import type { Locator, Page } from '@playwright/test';

async function mountFilesPage(page: Page, source: 'local' | 'printers', mode: 'grid' | 'explorer') {
  // No live API or printer I/O: mount the real page with in-memory read fixtures.
  await page.route(url => url.pathname.startsWith('/api/'), route => route.abort());
  await page.route('**/src/main.tsx', route => route.fulfill({
    contentType: 'application/javascript',
    body: "import '/src/index.css'; import '/src/styles/icons.css';",
  }));
  await page.goto('/');
  await page.evaluate(({ mode }) => {
    localStorage.setItem('printfarmer-files-viewmode', mode);
    localStorage.setItem('printfarmer-printer-files-viewmode', mode);
    document.body.innerHTML = '<div id="files-toolbar-root" class="p-4 min-w-0"></div>';
  }, { mode });
  const errors: string[] = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.addScriptTag({
    type: 'module',
    content: `
      import { mountFilesPage } from '/e2e/fixtures/files-toolbar-harness.mjs';
      mountFilesPage('${source}');
    `,
  });
  await expect.poll(async () => {
    expect(errors, 'page must mount without a browser exception').toEqual([]);
    return page.locator('#files-toolbar-root [data-tour="gcode-toolbar"], #files-toolbar-root [aria-label="Printer file controls"]').count();
  }, { timeout: 30_000 }).toBeGreaterThan(0);
}

async function expectUniformControls(toolbar: Locator, alignBottom: boolean) {
  const controls = toolbar.locator('input:not([type=checkbox]), select, button');
  const dimensions = await controls.evaluateAll(elements => elements.map(element => {
    const box = element.getBoundingClientRect();
    return { name: element.getAttribute('aria-label') || element.id || element.textContent, height: box.height, bottom: box.bottom };
  }));
  expect(dimensions.length).toBeGreaterThan(1);
  for (const control of dimensions) {
    expect(control.height, `${control.name} height`).toBe(40);
    if (alignBottom) expect(control.bottom, `${control.name} bottom edge`).toBe(dimensions[0].bottom);
  }
  const toggle = toolbar.getByTitle('Grid view').locator('..');
  if (await toggle.count()) {
    expect((await toggle.boundingBox())?.height, 'view-toggle wrapper height').toBe(40);
  }
}

for (const width of [390, 1440]) {
  for (const mode of ['grid', 'explorer'] as const) {
    for (const source of ['local', 'printers'] as const) {
      test(`${source} ${mode} toolbar controls are 40px at ${width}px`, async ({ page }) => {
        test.setTimeout(90_000);
        await page.setViewportSize({ width, height: 900 });
        await mountFilesPage(page, source, mode);
        if (source === 'printers') {
          const toolbar = page.getByRole('group', { name: 'Printer file controls' });
          await expect(toolbar.getByRole('button', { name: 'Force refresh all online printers' })).toBeVisible();
          await expectUniformControls(toolbar, width === 1440);
          const search = toolbar.getByRole('searchbox', { name: 'Search files' });
          await expect(toolbar.locator('input, select, button').first()).toHaveAttribute('id', 'catalog-search');
          await search.focus();
          await page.keyboard.press('Tab');
          await expect(toolbar.getByRole('combobox', { name: 'Printer' })).toBeFocused();
          await page.keyboard.press('Tab');
          await expect(toolbar.getByTitle('Explorer view')).toBeFocused();
        } else {
          await expect(page.getByRole('checkbox', { name: 'Select part.stl' })).toBeVisible();
          await page.getByRole('checkbox', { name: 'Select part.stl' }).check();
          const toolbar = page.locator('[data-tour="gcode-toolbar"]');
          await expect(toolbar.locator('input, select, button').first()).toHaveAttribute('aria-label', 'Search files');
          await expectUniformControls(toolbar, width === 1440);
          const folders = toolbar.getByRole('button', { name: 'Folders', exact: true });
          await folders.focus();
          await page.keyboard.press('Enter');
          const panel = page.locator(`[id="${await folders.getAttribute('aria-controls')}"]`);
          await expect(panel.getByRole('combobox', { name: 'Folder', exact: true })).toBeVisible();
          await expectUniformControls(panel, width === 1440);
          await panel.getByRole('combobox', { name: 'Folder', exact: true }).selectOption('/Parts');
          await expect(panel.getByRole('button', { name: 'Root', exact: true })).toBeVisible();
          await expectUniformControls(panel, width === 1440);
        }
        expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth),
          'toolbar must not cause horizontal page overflow').toBe(true);
      });
    }
  }
}

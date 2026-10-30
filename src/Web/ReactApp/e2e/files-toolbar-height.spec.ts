import { expect, test } from '@playwright/test';
import type { Locator, Page } from '@playwright/test';

async function mountFilesPage(page: Page, source: 'local' | 'printers', mode: 'grid' | 'explorer') {
  // No live API or printer I/O: mount the real page with in-memory read fixtures.
  await page.route(url => url.pathname.startsWith('/api/'), route => route.abort());
  await page.route(url => url.pathname === '/', route => route.fulfill({
    contentType: 'text/html',
    body: `<!doctype html>
      <html><head><meta charset="utf-8"></head>
      <body><div id="files-toolbar-root" class="p-4 min-w-0"></div>
      <script type="module">
        import RefreshRuntime from '/@react-refresh';
        RefreshRuntime.injectIntoGlobalHook(window);
        window.$RefreshReg$ = () => {};
        window.$RefreshSig$ = () => type => type;
        window.__vite_plugin_react_preamble_installed__ = true;
        import '/src/index.css';
        import '/src/styles/icons.css';
      </script>
      </body></html>`,
  }));
  await page.goto('/');
  await page.evaluate(({ mode }) => {
    localStorage.setItem('printfarmer-files-viewmode', mode);
    localStorage.setItem('printfarmer-printer-files-viewmode', mode);
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
  const { expectedHeight, dimensions } = await controls.evaluateAll(elements => {
    const root = document.documentElement;
    const rootFontSize = Number.parseFloat(getComputedStyle(root).fontSize);
    const controlSize = getComputedStyle(root).getPropertyValue('--pf-control-h-md').trim();
    const expectedHeight = rootFontSize * Number.parseFloat(controlSize);
    return {
      expectedHeight,
      dimensions: elements.map(element => {
        const box = element.getBoundingClientRect();
        return {
          name: element.getAttribute('aria-label') || element.id || element.textContent,
          height: box.height,
          top: box.top,
          bottom: box.bottom,
        };
      }),
    };
  });
  expect(dimensions.length).toBeGreaterThan(1);
  for (const control of dimensions) {
    expect(control.height, `${control.name} height`).toBe(expectedHeight);
    if (alignBottom) {
      const peers = dimensions.filter(peer => Math.abs(peer.top - control.top) < 1);
      if (peers.length > 1) expect(control.bottom, `${control.name} bottom edge`).toBe(peers[0].bottom);
    }
  }
  const toggle = toolbar.getByTitle('Grid view').locator('..');
  if (await toggle.count()) {
    expect((await toggle.boundingBox())?.height, 'view-toggle wrapper height').toBeCloseTo(expectedHeight, 1);
    const radii = await toggle.evaluate(element => Array.from(element.children, child => {
      const style = getComputedStyle(child);
      return [style.borderTopLeftRadius, style.borderTopRightRadius, style.borderBottomLeftRadius, style.borderBottomRightRadius];
    }));
    expect(radii).toEqual([
      ['2px', '0px', '2px', '0px'],
      ['0px', '2px', '0px', '2px'],
    ]);
  }
}

for (const width of [390, 1440]) {
  for (const mode of ['grid', 'explorer'] as const) {
    for (const source of ['local', 'printers'] as const) {
      test(`${source} ${mode} toolbar controls follow the md token at ${width}px`, async ({ page }) => {
        test.setTimeout(90_000);
        await page.setViewportSize({ width, height: 900 });
        await mountFilesPage(page, source, mode);
        if (source === 'printers') {
          const toolbar = page.getByRole('group', { name: 'Printer file controls' });
          await expect(toolbar.getByRole('button', { name: 'Force refresh all online printers' })).toBeVisible();
          await expectUniformControls(toolbar, width === 1440);
          const search = toolbar.getByRole('searchbox', { name: 'Search' });
          await expect(toolbar.locator('input, select, button').first()).toHaveAttribute('id', 'catalog-search');
          await search.focus();
          await page.keyboard.press('Tab');
          await expect(toolbar.getByRole('combobox', { name: 'Printer' })).toBeFocused();
          if (mode === 'grid') {
            const selectAll = toolbar.getByRole('checkbox', { name: 'Select all files on this page' });
            if (await selectAll.isEnabled()) {
              await page.keyboard.press('Tab');
              await expect(selectAll).toBeFocused();
            }
          }
          await page.keyboard.press('Tab');
          await expect(toolbar.getByTitle('Explorer view')).toBeFocused();
        } else {
          await expect(page.getByRole('checkbox', { name: 'Select part.stl' })).toBeVisible();
          await page.getByRole('checkbox', { name: 'Select part.stl' }).check();
          const toolbar = page.locator('[data-tour="gcode-toolbar"]');
          await expect(toolbar.locator('input, select, button').first()).toHaveAttribute('aria-label', 'Search');
          await expectUniformControls(toolbar, width === 1440);
          const bulkActions = page.getByRole('group', { name: 'Selected file actions' });
          await expect(bulkActions).toBeVisible();
          expect(await toolbar.evaluate(element => element.nextElementSibling?.getAttribute('aria-label')))
            .toBe('Selected file actions');
          const folders = toolbar.getByRole('button', { name: 'Folders', exact: true });
          await folders.focus();
          await page.keyboard.press('Enter');
          const panel = page.locator(`[id="${await folders.getAttribute('aria-controls')}"]`);
          const folderToolbar = panel.getByRole('group', { name: 'Folder controls' });
          await expect(folderToolbar.getByRole('combobox', { name: 'Folder', exact: true })).toBeVisible();
          await expectUniformControls(folderToolbar, width === 1440);
          await panel.getByRole('combobox', { name: 'Folder', exact: true }).selectOption('/Parts');
          await expect(panel.getByRole('button', { name: 'Root', exact: true })).toBeVisible();
          await expectUniformControls(folderToolbar, width === 1440);
        }
        expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth),
          'toolbar must not cause horizontal page overflow').toBe(true);
      });
    }
  }
}

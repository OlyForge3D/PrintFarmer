import { expect, test, type Page } from '@playwright/test';
import { existsSync } from 'node:fs';
import { join } from 'node:path';
import { captureRoutedToolbarState, mountRoutedToolbarPage, toolbarRouteUser } from './fixtures/toolbar-routed';
import { inventoryRouteSeeds, inventoryRouteStates } from './fixtures/toolbar-routes-inventory';

async function assertGeometryAndOrder(page: Page) {
  const groups = page.locator('main .pf-toolbar:visible, main .pf-header-actions:visible');
  expect(await groups.count(), 'each audited state must expose its existing controls to geometry checks').toBeGreaterThan(0);
  for (const group of await groups.all()) {
    const controls = group.locator('button:visible, input:visible:not([type=checkbox]):not([type=radio]):not([type=file]):not([type=hidden]), select:visible');
    for (const control of await controls.all()) {
      const tokenMinHeight = await control.evaluate(element => {
        if (!element.matches('.pf-control')) return null;
        const size = element.getAttribute('data-size');
        const token = size === 'sm' ? '--pf-control-h-sm' : size === 'lg' ? '--pf-control-h-lg' : '--pf-control-h-md';
        const rootStyle = getComputedStyle(document.documentElement);
        return Number.parseFloat(rootStyle.getPropertyValue(token)) * Number.parseFloat(rootStyle.fontSize);
      });
      if (tokenMinHeight !== null) {
        const box = await control.boundingBox();
        const name = await control.getAttribute('aria-label') ?? await control.textContent();
        expect(box?.height, `${name} should meet ${await control.getAttribute('data-size') ?? 'md'} control token minimum`).toBeGreaterThanOrEqual(tokenMinHeight - 0.5);
      }
    }
    // At most one primary command in each header, without hiding any commands.
    if (await group.evaluate(element => element.classList.contains('pf-header-actions'))) {
      expect(await group.locator('[data-pf-variant="primary"]:visible').count()).toBeLessThanOrEqual(1);
    } else {
      const search = group.locator('input[type=search], input.pf-control').first();
      const sort = group.getByRole('combobox', { name: 'Sort by', exact: true });
      const view = group.locator('[aria-pressed]').first();
      const refresh = group.getByRole('button', { name: /^Refresh/ }).first();
      const ordered = [];
      for (const control of [search, sort, view, refresh]) {
        if (await control.count()) ordered.push(await control.evaluate(element => {
          const all = [...element.closest('.pf-toolbar')!.querySelectorAll('input, select, button')];
          return all.indexOf(element);
        }));
      }
      expect(ordered, 'Search, Sort, View, Refresh retain DOM order').toEqual([...ordered].sort((a, b) => a - b));
    }
  }
}

for (const state of inventoryRouteStates) {
  test(`${state.name}: populated routed toolbar parity`, async ({ page, isMobile }, testInfo) => {
    await page.setViewportSize(isMobile ? { width: 390, height: 844 } : { width: 1440, height: 900 });
    const errors: string[] = [];
    const reads: URL[] = [];
    page.on('pageerror', error => errors.push(error.message));
    page.on('request', request => {
      const url = new URL(request.url());
      if (request.method() === 'GET' && url.pathname.startsWith('/api/')) reads.push(url);
    });
    const requests = await mountRoutedToolbarPage(page, state.path, inventoryRouteSeeds(state.name === 'catalog-filaments'));
    try {
      await expect(page.locator('main h1')).toBeVisible();
      if ('tab' in state) {
        const tab = page.locator(`#tab-${state.tab}`);
        await tab.click();
        await expect(tab).toHaveAttribute('aria-selected', 'true');
      }
      if ('subTab' in state) {
        const subTab = page.locator(`#tab-${state.subTab}`);
        await subTab.click();
        await expect(subTab).toHaveAttribute('aria-selected', 'true');
      }
      const main = page.locator('main');
      await expect(main.getByText(state.marker, { exact: false }).first()).toBeVisible();
      await expect(main.getByText(/Failed to load|Something went wrong/)).toHaveCount(0);
      if (state.name === 'maintenance-schedule') {
        await expect(main.getByRole('button', { name: /October 30, 2026, 1 maintenance task/ })).toBeVisible();
        const calendar = main.getByRole('group', { name: 'Maintenance calendar navigation', exact: true });
        await calendar.getByRole('button', { name: 'Next month', exact: true }).click();
        await expect(calendar).toContainText('November 2026');
        await calendar.getByRole('button', { name: 'Today', exact: true }).click();
        await expect(calendar).toContainText('October 2026');
      }

      if ('command' in state) {
        const command = main.locator('.pf-header-actions').getByRole('button', { name: state.command, exact: true });
        await expect(command).toBeVisible();
        await expect(command).toBeEnabled();
        // Header command must precede every filter/view toolbar, also when wrapped.
        const commandBox = await command.boundingBox();
        const firstToolbar = main.locator('.pf-toolbar:visible').first();
        if (await firstToolbar.count()) {
          const toolbarBox = await firstToolbar.boundingBox();
          expect(commandBox!.y + commandBox!.height).toBeLessThanOrEqual(toolbarBox!.y + 1);
        }
        // Keyboard access without invoking a create/import/delete operation.
        await command.focus();
        await expect(command).toBeFocused();
        await page.keyboard.press('Tab');
        expect(await page.evaluate(() => document.activeElement?.tagName)).not.toBe('BODY');
      }

      await assertGeometryAndOrder(page);
      await captureRoutedToolbarState(page, testInfo, state.name, state.filtersDisclosureCount);
      for (const filters of await main.getByRole('button', { name: 'Filters', exact: true }).all()) {
        await expect(filters).toHaveAttribute('aria-expanded', 'false');
        await filters.click();
      }
      // Optional original audit image is a review reference, not a pixel golden:
      // seeded records and offline fallback fonts intentionally differ from live.
      if (process.env.TOOLBAR_AUDIT_SHOTS_DIR) {
        const originalName = state.name === 'maintenance-dashboard' ? 'maintenance' : state.name;
        const original = join(process.env.TOOLBAR_AUDIT_SHOTS_DIR, `${originalName}.png`);
        if (existsSync(original)) {
          await testInfo.attach(`${state.name}-original-audit`, { path: original, contentType: 'image/png' });
        }
      }

      // Exercise existing presentation/filter behavior, never a real write.
      const search = main.locator('.pf-toolbar input[type=search], .pf-toolbar input.pf-control').first();
      if (await search.count()) {
        const searchResponse = state.name === 'filaments' ? page.waitForResponse(response => {
          const url = new URL(response.url());
          return url.pathname === '/api/spoolman/filaments' && url.searchParams.get('search') === 'Workshop';
        }) : null;
        await search.fill('Workshop');
        if (searchResponse) await (await searchResponse).finished();
        await expect(search).toHaveValue('Workshop');
        const clearResponse = state.name === 'filaments' ? page.waitForResponse(response => {
          const url = new URL(response.url());
          return url.pathname === '/api/spoolman/filaments' && !url.searchParams.has('search');
        }) : null;
        await search.fill('');
        if (clearResponse) {
          await (await clearResponse).finished();
          await expect.poll(() => new URL(page.url()).searchParams.get('q')).toBeNull();
        }
      }
      const sortDirection = main.getByRole('button', { name: 'Sort direction: ascending', exact: true });
      if (await sortDirection.count()) {
        await sortDirection.click();
        await expect(main.getByRole('button', { name: 'Sort direction: descending', exact: true })).toBeVisible();
        await expect.poll(() => new URL(page.url()).searchParams.get('dir')).toBe('desc');
        const material = main.getByRole('combobox', { name: 'Filter by material', exact: true });
        await material.selectOption('PLA');
        await expect.poll(() => new URL(page.url()).searchParams.get('material')).toBe('PLA');
        await expect.poll(() => reads.some(url => url.searchParams.get('material') === 'PLA')).toBe(true);
        const clearedMaterial = state.name === 'filaments' ? page.waitForResponse(response => {
          const url = new URL(response.url());
          return url.pathname === '/api/spoolman/filaments' && !url.searchParams.has('material');
        }) : null;
        await material.selectOption('');
        if (clearedMaterial) await (await clearedMaterial).finished();
        await expect.poll(() => new URL(page.url()).searchParams.get('material')).toBeNull();
        await expect(main.getByRole('button', { name: 'Filter by color family', exact: true })).toBeDisabled();
      }
      const tableView = main.locator('.pf-toolbar').getByRole('button', { name: /Table view/i }).first();
      if (await tableView.count()) {
        await tableView.click();
        await expect(tableView).toHaveAttribute('aria-pressed', 'true');
        if (state.name.startsWith('catalog-')) {
          const key = state.name === 'catalog-printers' ? 'printer-models' : state.name.replace('catalog-', '');
          expect(await page.evaluate(key => localStorage.getItem(`catalog-view-${key}`), key)).toBe('table');
        }
        if (state.name === 'projects') {
          expect(await page.evaluate(() => localStorage.getItem('projects-view-mode'))).toBe('table');
        }
      }
      if (state.name === 'parts-skus' || state.name === 'parts-bins' || state.name === 'spools') {
        const root = main.locator('[data-filter-hit-target]:visible');
        const label = root.locator('label').last();
        const expectedHeight = await page.evaluate(() => matchMedia('(pointer: coarse)').matches ? 44 : 36);
        const labels = state.name === 'spools' ? root : root.locator('label');
        for (const target of await labels.all()) {
          expect((await target.boundingBox())!.height).toBe(expectedHeight);
        }
        if (state.name === 'spools') await root.click(); else await label.click();
        await expect(root.locator('input')).toBeChecked();
        if (state.name !== 'spools') {
          await expect.poll(() => reads.some(url => url.searchParams.get('includeInactive') === 'true')).toBe(true);
        }
      }
      const pager = main.getByRole('group', { name: 'Pagination', exact: true });
      if (await pager.count()) {
        const next = pager.getByRole('button', { name: 'Next', exact: true });
        if (await next.isEnabled()) {
          await next.click();
          await expect(pager).toContainText('Page 2 of');
          await pager.getByRole('button', { name: 'Previous', exact: true }).click();
          await expect(pager).toContainText('Page 1 of');
        }
      }
      if (state.name === 'maintenance-inventory') {
        await expect(main.getByRole('button', { name: 'Decrease stock', exact: true }).first()).toBeDisabled();
      }
      if (state.name === 'parts-skus') {
        await main.getByRole('button', { name: 'Deactivate BRACKET-01', exact: true }).click();
        await expect(page.getByRole('dialog')).toBeVisible();
        await page.getByRole('dialog').getByRole('button', { name: 'Cancel', exact: true }).click();
        await expect(page.getByRole('dialog')).toHaveCount(0);
      }
      if (state.name === 'parts-mappings') {
        await main.getByRole('combobox', { name: 'Filter by SKU', exact: true }).selectOption('BRACKET-01');
        await expect.poll(() => reads.some(url => url.pathname.endsWith('/mappings') && url.searchParams.get('sku') === 'BRACKET-01')).toBe(true);
      }
      if (state.name === 'projects') {
        await main.getByRole('combobox', { name: 'Filter by status', exact: true }).selectOption('InProgress');
        await expect.poll(() => reads.some(url => url.pathname === '/api/projects' && url.searchParams.get('status') === 'InProgress')).toBe(true);
      }
      if (state.name.startsWith('catalog-') || state.name === 'projects') {
        await page.reload();
        if ('tab' in state) await page.locator(`#tab-${state.tab}`).click();
        await expect(main.locator('.pf-toolbar').getByRole('button', { name: /Table view/i }).first())
          .toHaveAttribute('aria-pressed', 'true');
      }
      if (state.name === 'locations') {
        // A read-only operator retains the routed summary but not admin commands.
        const reader = await page.context().newPage();
        try {
          const readerRequests = await mountRoutedToolbarPage(reader, '/locations', {
            ...inventoryRouteSeeds(),
            '/api/auth/me': { ...toolbarRouteUser, roles: ['viewer'], permissions: ['locations:read'], emailConfirmed: true },
          });
          await expect(reader.locator('main').getByText('Workshop printer', { exact: true }).first()).toBeVisible();
          await expect(reader.locator('main').getByRole('button', { name: 'Manage', exact: true })).toHaveCount(0);
          await expect(reader.locator('main').getByRole('button', { name: 'Add location', exact: true })).toHaveCount(0);
          expect(readerRequests).toMatchObject({ unhandled: [], writes: [], external: [], errors: [] });
        } finally {
          await reader.close();
        }
      }
      expect(errors, 'real App must render without browser exceptions').toEqual([]);
    } finally {
      // Destroy debounced filters/polling while strict routes are still installed.
      // Otherwise context teardown can remove interception before a delayed read.
      // Remain same-origin: the shared auth init script uses localStorage,
      // which browsers intentionally deny on opaque about:blank documents.
      const teardownUrl = new URL('/__inventory_toolbar_teardown__', testInfo.project.use.baseURL).href;
      await page.route(teardownUrl, route => route.fulfill({
        contentType: 'text/html', body: '<!doctype html><title>Fixture complete</title>',
      }));
      await page.goto(teardownUrl);
      await testInfo.attach('strict-route-requests', {
        body: JSON.stringify(requests, null, 2), contentType: 'application/json',
      });
      expect(requests.unhandled, 'every API pathname must have an explicit contract').toEqual([]);
      expect(requests.writes, 'presentation checks must not issue mutations').toEqual([]);
      expect(requests.external, 'no external traffic').toEqual([]);
      expect(requests.errors, 'shared helper must see no App exceptions').toEqual([]);
    }
  });
}

import { expect, test } from '@playwright/test';
import { captureRoutedToolbarState, mountRoutedToolbarPage } from './fixtures/toolbar-routed';
import { tracerRouteSeeds, tracerRouteStates } from './fixtures/toolbar-routes-tracer';

for (const state of tracerRouteStates) test(`routed ${state.name} normal state`, async ({ page }, testInfo) => {
  await page.setViewportSize({ width: testInfo.project.name === 'chromium' ? 1440 : 390, height: 900 });
  const requests = await mountRoutedToolbarPage(page, state.path, tracerRouteSeeds);
  try {
    await expect(page.getByRole('heading', { name: state.heading, exact: true })).toBeVisible();
    if (state.name === 'queue-timeline') {
      await expect(page.getByRole('img', { name: /Fixture gear on Printer Alpha/ })).toBeVisible();
    } else if (state.name === 'queue' || state.name === 'queue-history') {
      await expect(page.getByRole('row', { name: new RegExp(state.populated) }).first()).toBeVisible();
    } else if ('populated' in state) {
      await expect(page.getByText(state.populated, { exact: false }).filter({ visible: true }).first()).toBeVisible();
    }
    if (state.name === 'printers') {
      await expect(page.getByRole('button', { name: 'Discover Printers on the local network', exact: true })).toBeVisible();
    }
    await captureRoutedToolbarState(page, testInfo, state.name, state.filtersDisclosureCount);
    const toolbar = page.locator('main .pf-toolbar').first();
    if (await toolbar.count()) {
      const controls = toolbar.locator('button:visible:enabled:not([tabindex="-1"]), input:visible:enabled:not([tabindex="-1"]), select:visible:enabled:not([tabindex="-1"])');
      if (await controls.count() >= 2) {
        await controls.nth(0).focus();
        await page.keyboard.press('Tab');
        await expect(controls.nth(1)).toBeFocused();
      }
      if (state.name === 'auto-dispatch') {
        const radios = toolbar.getByRole('radiogroup', { name: 'Filter printers by state' });
        await radios.getByRole('radio', { name: 'All', exact: true }).focus();
        await page.keyboard.press('ArrowRight');
        const queued = radios.getByRole('radio', { name: /^Queued Jobs/ });
        await expect(queued).toBeFocused();
        await expect(queued).toHaveAttribute('aria-checked', 'true');
        await page.keyboard.press('Tab');
        await expect(toolbar.getByRole('combobox', { name: 'Sort by' })).toBeFocused();
      }
    }
    if (state.name === 'printers') {
      const actions = page.locator('main .pf-header-actions').first();
      const header = await actions.boundingBox();
      const filters = await toolbar.boundingBox();
      expect(header).not.toBeNull();
      expect(filters).not.toBeNull();
      expect(header!.y + header!.height).toBeLessThanOrEqual(filters!.y + 1);
    }
  } finally {
    await testInfo.attach('requests', { body: JSON.stringify(requests, null, 2), contentType: 'application/json' });
  }
  expect(requests.unhandled).toEqual([]);
  expect(requests.writes).toEqual([]);
  expect(requests.external).toEqual([]);
  expect(requests.errors).toEqual([]);
});

test('routed queue filters, view persistence and cancel guard', async ({ page }, testInfo) => {
  await page.setViewportSize({ width: testInfo.project.name === 'chromium' ? 1440 : 390, height: 900 });
  const requests = await mountRoutedToolbarPage(page, '/printQueue', tracerRouteSeeds);
  await page.route('**/api/job-queue-analytics?*', async route => {
    if (route.request().method() !== 'GET') {
      await route.fallback();
      return;
    }
    const model = new URL(route.request().url()).searchParams.get('filterModel');
    await route.fulfill({ json: model === 'Not a printer' ? [] : tracerRouteSeeds['/api/job-queue-analytics'] });
  });
  const controls = page.getByRole('group', { name: 'Queue controls', exact: true });
  await expect(controls).toBeVisible();
  await controls.getByRole('combobox', { name: 'Sort by' }).selectOption('deadline');
  await expect(controls.getByRole('combobox', { name: 'Sort by' })).toHaveValue('deadline');
  await expect(controls.getByRole('textbox', { name: 'Filter by printer model' })).toBeHidden();
  await controls.getByRole('button', { name: 'Filters', exact: true }).click();
  const filteredResponse = page.waitForResponse(response =>
    new URL(response.url()).pathname === '/api/job-queue-analytics'
    && new URL(response.url()).searchParams.get('filterModel') === 'Not a printer');
  await controls.getByRole('textbox', { name: 'Filter by printer model' }).fill('Not a printer');
  await filteredResponse;
  await expect(page.getByRole('rowgroup', { name: 'Print job: Fixture gear' })).toHaveCount(0);
  await controls.getByRole('button', { name: 'Reset filters', exact: true }).click();
  await expect(controls.getByRole('combobox', { name: 'Sort by' })).toHaveValue('deadline');
  await expect(controls.getByRole('textbox', { name: 'Filter by printer model' })).toHaveValue('');
  await expect(page.getByRole('rowgroup', { name: 'Print job: Fixture gear' })).toBeVisible();
  await controls.getByRole('button', { name: 'Filters', exact: true }).click();
  await controls.getByRole('button', { name: 'Cards view', exact: true }).click();
  await expect(controls.getByRole('button', { name: 'Cards view', exact: true })).toHaveAttribute('aria-pressed', 'true');
  await expect.poll(() => page.evaluate(() => localStorage.getItem('printfarmer-queue-view-mode'))).toBe('cards');
  await page.reload();
  await expect(controls.getByRole('button', { name: 'Cards view', exact: true })).toHaveAttribute('aria-pressed', 'true');
  await expect(page.getByText('Fixture gear', { exact: true })).toBeVisible();
  await page.getByRole('listitem', { name: 'Print job: Fixture gear', exact: true })
    .getByRole('button', { name: 'Cancel', exact: true }).click();
  const confirmation = page.getByRole('dialog');
  await expect(confirmation).toBeVisible();
  await expect(confirmation.getByRole('heading', { name: 'Cancel Print Job' })).toBeVisible();
  await confirmation.getByRole('button', { name: 'Keep Job', exact: true }).click();
  await expect(confirmation).toBeHidden();
  const safetyTrack = page.getByRole('checkbox', { name: 'Toggle system auto-dispatch' })
    .locator('..').locator('[data-pf-radius="full"]').first();
  expect(await safetyTrack.evaluate(element => element.getBoundingClientRect().height)).toBe(16);
  await expect(page.getByRole('checkbox', { name: 'Toggle system auto-dispatch' })).not.toBeChecked();
  await captureRoutedToolbarState(page, testInfo, 'queue-card-filter-cancel', 1);
  expect(requests).toEqual({ unhandled: [], writes: [], external: [], errors: [] });
});

test('routed history filters, date labels and pagination boundaries', async ({ page }, testInfo) => {
  await page.setViewportSize({ width: testInfo.project.name === 'chromium' ? 1440 : 390, height: 900 });
  const requests = await mountRoutedToolbarPage(page, '/printQueue/history', tracerRouteSeeds);
  const filters = page.getByRole('group', { name: 'History controls', exact: true });
  const refinementToolbar = page.getByRole('group', { name: 'History filters', exact: true });
  await refinementToolbar.getByRole('button', { name: 'Filters', exact: true }).click();
  const done = page.getByRole('button', { name: 'Done', exact: true });
  await expect(done).toHaveAttribute('aria-pressed', 'true');
  await done.click();
  await expect(done).toHaveAttribute('aria-pressed', 'false');
  await filters.getByRole('combobox', { name: 'Sort by' }).selectOption('oldest');
  await expect(filters.getByRole('combobox', { name: 'Sort by' })).toHaveValue('oldest');
  const dates = page.getByRole('group', { name: 'Queue date controls' });
  await dates.getByRole('button', { name: '30d', exact: true }).click();
  await expect(dates.getByRole('button', { name: '30d', exact: true })).toHaveAttribute('aria-pressed', 'true');
  await dates.getByRole('button', { name: 'Custom…', exact: true }).click();
  await expect(page.getByLabel('From', { exact: true })).toBeVisible();
  await expect(page.getByLabel('To', { exact: true })).toBeVisible();
  const pagination = page.getByRole('group', { name: 'Pagination', exact: true });
  await expect(pagination.getByRole('button', { name: 'Previous', exact: true })).toBeDisabled();
  await expect(pagination.getByRole('button', { name: 'Next', exact: true })).toBeDisabled();
  await refinementToolbar.getByRole('button', { name: 'Filters (2)', exact: true }).click();
  await captureRoutedToolbarState(page, testInfo, 'history-collapsed-filters-dates', 1);
  expect(requests).toEqual({ unhandled: [], writes: [], external: [], errors: [] });
});

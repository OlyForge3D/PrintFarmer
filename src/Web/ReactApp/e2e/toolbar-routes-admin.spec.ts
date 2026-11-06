import { expect, test, type Page } from '@playwright/test';
import { captureRoutedToolbarState, mountRoutedToolbarPage } from './fixtures/toolbar-routed';
import { toolbarAdminSeeds, toolbarAdminStates } from './fixtures/toolbar-routes-admin';

async function assertCanonicalControls(page: Page) {
  const coarse = await page.evaluate(() => matchMedia('(pointer: coarse)').matches);
  expect(await page.locator('main .pf-header-actions button[data-pf-variant="primary"]:visible').count())
    .toBeLessThanOrEqual(1);
  const controls = page.locator(
    'main .pf-toolbar button:visible, main .pf-toolbar select:visible, ' +
    'main .pf-toolbar input:visible:not([type=checkbox]):not([type=radio]), ' +
    'main .pf-header-actions button:visible',
  );
  for (const control of await controls.all()) {
    const { height, radius, field } = await control.evaluate(element => ({
      height: element.getBoundingClientRect().height,
      radius: parseFloat(getComputedStyle(element).borderTopLeftRadius),
      field: element.matches('input, select'),
    }));
    expect(height, await control.getAttribute('aria-label') ?? await control.textContent() ?? 'control')
      .toBeGreaterThanOrEqual((coarse ? 44 : 36) - 0.1);
    expect(radius, 'toolbar control role radius').toBe(field ? 4 : 2);
  }
}

for (const state of toolbarAdminStates) {
  test(`routed ${state.name}`, async ({ page }, testInfo) => {
    test.setTimeout(60_000);
    await page.setViewportSize(testInfo.project.name.startsWith('mobile')
      ? { width: 390, height: 844 } : { width: 1440, height: 900 });
    const requests = await mountRoutedToolbarPage(page, state.path, toolbarAdminSeeds);
    try {
      if ('tab' in state) {
        await page.getByRole('tab', { name: new RegExp(`${state.tab}$`) }).click();
      }
      await expect(page.locator('main').getByText(state.ready, { exact: false }).first()).toBeVisible();
      await expect(page.locator('main .pf-skeleton:visible')).toHaveCount(0);

      if (state.path === '/analytics') {
        const filters = page.getByRole('group', { name: 'Analytics filters', exact: true });
        const exportButton = page.getByRole('button', { name: /export/i }).first();
        await expect(exportButton).toBeVisible();
        await expect(exportButton.locator('xpath=ancestor::*[contains(@class,"pf-header-actions")]')).toHaveCount(1);
        const filterBounds = await filters.boundingBox();
        const exportBounds = await exportButton.boundingBox();
        expect(exportBounds!.y + exportBounds!.height).toBeLessThanOrEqual(filterBounds!.y);
        await expect(page.getByRole('tab', { name: new RegExp(`${state.tab}$`) })).toHaveAttribute('aria-selected', 'true');
        // Keyboard activation exercises the actual shared period handler, not a specimen.
        const sevenDays = filters.getByRole('button', { name: '7 days', exact: true });
        await sevenDays.focus();
        await page.keyboard.press('Enter');
        await expect(sevenDays).toHaveAttribute('aria-pressed', 'true');
      }
      if (state.name === 'scheduling') {
        const previous = page.getByRole('button', { name: /previous month/i });
        const next = page.getByRole('button', { name: /next month/i });
        await previous.click();
        await expect(page.getByText('September 2026', { exact: true })).toBeVisible();
        await next.click();
        await expect(page.getByText('October 2026', { exact: true })).toBeVisible();
        await page.getByRole('button', { name: 'Today', exact: true }).click();
      }
      if (state.name === 'admin-workers') {
        const toolbar = page.getByRole('group', { name: 'Worker filters and status', exact: true });
        const trigger = toolbar.getByRole('button', { name: 'Filters', exact: true });
        const status = toolbar.getByRole('combobox', { name: 'Filter by worker status', exact: true });
        await expect(status).toBeHidden();
        await trigger.click();
        await status.selectOption('Online');
        await expect(page.getByText('Fixture worker', { exact: true })).toBeVisible();
        await expect(toolbar.getByRole('button', { name: 'Filters (1)', exact: true })).toHaveAttribute('aria-expanded', 'true');
        await toolbar.getByRole('button', { name: 'Filters (1)', exact: true }).click();
        await expect(status).toBeHidden();
        await expect(toolbar.getByRole('button', { name: 'Refresh workers', exact: true })).toBeVisible();
      }
      if (state.name === 'admin-worker-jobs') {
        await expect(page.getByRole('combobox', { name: 'Filter by status', exact: true })).toBeVisible();
      }
      if (state.name === 'admin-login-audit') {
        const username = page.getByRole('textbox', { name: 'Filter by username', exact: true });
        await username.fill('fixture.operator');
        await expect(page).toHaveURL(/username=fixture.operator/);
        const previous = page.getByRole('button', { name: 'Previous', exact: true });
        const next = page.getByRole('button', { name: 'Next', exact: true });
        await expect(previous).toBeDisabled();
        await next.click();
        await expect(page).toHaveURL(/page=2/);
        await expect(next).toBeDisabled();
        await previous.click();
        await expect(previous).toBeDisabled();
        const toolbar = page.getByRole('group', { name: 'Filter login audit entries', exact: true });
        const status = toolbar.getByRole('combobox', { name: 'Filter by login status', exact: true });
        await expect(status).toBeHidden();
        await toolbar.getByRole('button', { name: 'Filters', exact: true }).click();
        await status.selectOption('false');
        await expect(page).toHaveURL(/success=false/);
        await expect(toolbar.getByRole('button', { name: 'Filters (1)', exact: true })).toHaveAttribute('aria-expanded', 'true');
        await toolbar.getByRole('button', { name: 'Reset filters', exact: true }).click();
        await expect(page).not.toHaveURL(/success=/);
        await expect(username).toHaveValue('fixture.operator');
        await expect(toolbar.getByRole('button', { name: 'Filters', exact: true })).toBeFocused();
        await toolbar.getByRole('button', { name: 'Filters', exact: true }).click();
        await expect(status).toBeHidden();
        await expect(page.getByRole('button', { name: 'Clear all filters', exact: true })).toBeVisible();
        await page.getByRole('button', { name: 'Clear all filters', exact: true }).click();
        await expect(page).not.toHaveURL(/username=/);
      }
      if (state.name === 'profiles-import-first-step') {
        await expect(page.getByRole('button', { name: /next/i })).toBeDisabled();
        await page.getByPlaceholder('Search manufacturers or models...').fill('CoreXY');
        await expect(page.getByRole('button', { name: 'Fixture CoreXY', exact: true })).toBeVisible();
        // Do not advance to the import/physical-operation workflow.
      }
      if (state.path === '/settings' || state.name === 'admin-farm-settings') {
        await expect(page.getByRole('group', { name: 'Settings actions', exact: true })).toBeVisible();
      }
      if (state.name === 'scheduling' || state.name === 'admin-power-monitors') {
        const command = page.getByRole('button', {
          name: state.name === 'scheduling' ? 'Schedule Job' : 'Add Monitor', exact: true,
        });
        await expect(command.locator('xpath=ancestor::*[contains(@class,"pf-header-actions")]')).toHaveCount(1);
      }
      await captureRoutedToolbarState(page, testInfo, state.name, state.filtersDisclosureCount);
      // Include the intentionally non-toolbar controls (row actions, wizard
      // model cards and export form) without imposing toolbar sizes on them.
      const surface = await page.locator('main button:visible, main input:visible, main select:visible')
        .evaluateAll(elements => elements.map(element => {
          const rect = element.getBoundingClientRect();
          const style = getComputedStyle(element);
          return {
            text: element.getAttribute('aria-label') ?? element.textContent?.trim(),
            id: element.id, tag: element.tagName, role: element.getAttribute('role'),
            x: rect.x, y: rect.y, width: rect.width, height: rect.height,
            radius: style.borderTopLeftRadius,
            pressed: element.getAttribute('aria-pressed'),
            selected: element.getAttribute('aria-selected'),
            disabled: element.hasAttribute('disabled'),
            inToolbar: !!element.closest('.pf-toolbar'),
            inHeader: !!element.closest('.pf-header-actions'),
          };
        }));
      await testInfo.attach(`${state.name}-surface`, { body: JSON.stringify(surface, null, 2), contentType: 'application/json' });
      await assertCanonicalControls(page);
      // Do not let a route pass merely because commands were left outside the
      // canonical wrappers that the common capture helper measures.
      for (const pin of await page.locator('main').getByRole('button', { name: /^Pin .* from navbar$/ }).all()) {
        await expect(pin.locator('xpath=ancestor::*[contains(@class,"pf-header-actions")]'),
          'page pin belongs in the header command slot').toHaveCount(1);
      }
      if (state.name === 'admin-status') {
        await expect(page.getByRole('button', { name: 'Refresh', exact: true })
          .locator('xpath=ancestor::*[contains(@class,"pf-header-actions")]'),
        'status refresh belongs in the page header command slot').toHaveCount(1);
      }
      if (state.name === 'profiles-import-first-step') {
        const search = page.getByPlaceholder('Search manufacturers or models...');
        await expect(search).toHaveAccessibleName(/\S/);
        const style = await search.evaluate(element => ({
          height: element.getBoundingClientRect().height,
          radius: getComputedStyle(element).borderTopLeftRadius,
          coarse: matchMedia('(pointer: coarse)').matches,
        }));
        expect(style.height).toBeGreaterThanOrEqual(style.coarse ? 44 : 36);
        expect(style.radius).toBe('4px');
      }
    } finally {
      await testInfo.attach('request-audit', { body: JSON.stringify(requests, null, 2), contentType: 'application/json' });
      expect(requests.unhandled, 'every local API request must have an explicit seed').toEqual([]);
      expect(requests.writes, 'routed parity must not issue mutations').toEqual([]);
      expect(requests.external, 'no external HTTP allowed').toEqual([]);
      expect(requests.errors, 'populated routed App must not throw').toEqual([]);
    }
  });
}

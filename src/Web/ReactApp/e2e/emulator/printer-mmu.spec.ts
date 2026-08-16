import type { Locator } from '@playwright/test';
import { dismissTourIfVisible } from '../fixtures/emulator-setup';
import {
  createMoonrakerControl,
  expect,
  MOONRAKER_PRINTERS,
  openCollapsedPrinterDetailsSidebar,
  test,
} from '../fixtures/moonraker';

async function getMmuControls(sidebar: Locator): Promise<Locator> {
  const toggle = sidebar.getByRole('button', { name: 'AMS' });
  await expect(toggle).toBeVisible({ timeout: 15_000 });
  if (await toggle.getAttribute('aria-expanded') !== 'true') {
    await toggle.click();
  }

  const panelId = await toggle.getAttribute('aria-controls');
  expect(panelId).toBeTruthy();
  const controls = sidebar.locator(`[id="${panelId}"]`);
  await expect(controls).toBeVisible();
  return controls;
}

test.describe('Printer MMU controls — Moonraker', () => {
  test.describe.configure({ mode: 'serial' });

  test.beforeEach(async ({ page, request }) => {
    const control = createMoonrakerControl(request);
    await control.reset('ready');
    await control.setMmuMode('ready', 'HappyHare');

    await page.goto('/printers');
    await page.waitForLoadState('domcontentloaded');
    await dismissTourIfVisible(page);
  });

  test.afterEach(async ({ request }) => {
    const control = createMoonrakerControl(request);
    await control.setMmuMode('ready', 'None');
    await control.reset('ready');
  });

  test('Happy Hare gates render and load/eject through the real backend', async ({ page }) => {
    const sidebar = await openCollapsedPrinterDetailsSidebar(page, MOONRAKER_PRINTERS.ready);
    const controls = await getMmuControls(sidebar);

    await expect(controls.getByRole('button', { name: 'Gate 1A: PLA - Ready' })).toBeVisible();

    const petgGate = controls.getByRole('button', { name: 'Gate 1B: PETG - Ready' });
    await expect(petgGate).toBeVisible();
    await petgGate.click();
    await expect(petgGate).toHaveAttribute('aria-pressed', 'true');
    await expect(controls.getByText('#102', { exact: true })).toBeVisible();

    await controls.getByRole('button', { name: 'Load', exact: true }).click();
    await expect(controls.getByText('T1', { exact: true })).toBeVisible({ timeout: 15_000 });

    await controls.getByRole('button', { name: 'Eject', exact: true }).click();
    await expect(controls.getByText('T1', { exact: true })).toHaveCount(0, { timeout: 15_000 });
    await expect(controls.getByText('Unloaded', { exact: true })).toBeVisible();
  });

  test('AFC lanes render and load through the real backend', async ({ page, request }) => {
    await createMoonrakerControl(request).setMmuMode('ready', 'Afc');
    await page.reload();

    const sidebar = await openCollapsedPrinterDetailsSidebar(page, MOONRAKER_PRINTERS.ready);
    const controls = await getMmuControls(sidebar);
    await expect(controls.getByText('AFC', { exact: true })).toBeVisible({ timeout: 15_000 });
    const rack = controls.getByText('Rack', { exact: true }).locator('..');
    await expect(rack.getByText('PLA', { exact: true })).toBeVisible();

    const lane = controls.getByRole('button', { name: 'Gate 1B: PETG - Ready' });
    await lane.click();
    await controls.getByRole('button', { name: 'Load', exact: true }).click();
    await expect(rack.getByText('PETG', { exact: true })).toBeVisible({ timeout: 15_000 });
    await expect(rack.getByText('PLA', { exact: true })).toHaveCount(0);
  });

  test('Qidibox slots render and unload through the real backend', async ({ page, request }) => {
    await createMoonrakerControl(request).setMmuMode('ready', 'Qidibox');
    await page.reload();

    const sidebar = await openCollapsedPrinterDetailsSidebar(page, MOONRAKER_PRINTERS.ready);
    const controls = await getMmuControls(sidebar);
    await expect(controls.getByText('QIDIBOX', { exact: true })).toBeVisible({ timeout: 15_000 });
    const slot = controls.getByRole('button', { name: 'Gate 1A: PLA - Ready' });
    await slot.click();
    await controls.getByRole('button', { name: 'Unload', exact: true }).click();
    await expect(controls).toBeVisible();
    await expect(controls.getByText('Unloaded', { exact: true })).toBeVisible({ timeout: 15_000 });
    await expect(controls.getByText('Rack', { exact: true })).toHaveCount(0, { timeout: 15_000 });
  });

  test('Snapmaker U1 physical toolheads render as four material slots', async ({ page, request }) => {
    await createMoonrakerControl(request).setMmuMode('ready', 'SnapmakerU1');
    await page.reload();

    const sidebar = await openCollapsedPrinterDetailsSidebar(page, MOONRAKER_PRINTERS.ready);
    const materials = sidebar.getByTestId('material-loadout');
    await expect(materials).toBeVisible({ timeout: 15_000 });
    const toolheads = materials.getByRole('group', { name: 'Toolheads slots' });
    await expect(toolheads.getByRole('button')).toHaveCount(4);
    await expect(toolheads.getByRole('button', { name: 'T0 toolhead, loaded with PLA' })).toBeVisible();
    await expect(toolheads.getByRole('button', { name: 'T1 toolhead, loaded with PETG' })).toBeVisible();
    await expect(toolheads.getByRole('button', { name: 'T2 toolhead, empty' })).toBeVisible();
    await expect(toolheads.getByRole('button', { name: 'T3 toolhead, empty' })).toBeVisible();
  });
});

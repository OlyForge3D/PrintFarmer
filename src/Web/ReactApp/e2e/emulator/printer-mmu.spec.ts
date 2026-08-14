import { dismissTourIfVisible } from '../fixtures/emulator-setup';
import {
  createMoonrakerControl,
  expect,
  MOONRAKER_PRINTERS,
  openPrinterDetails,
  test,
} from '../fixtures/moonraker';

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
    const sidebar = await openPrinterDetails(page, MOONRAKER_PRINTERS.ready);

    await expect(sidebar.getByRole('button', { name: 'AMS' })).toBeVisible({ timeout: 15_000 });
    await expect(sidebar.getByRole('button', { name: 'Gate 1A: PLA - Ready' })).toBeVisible();

    const petgGate = sidebar.getByRole('button', { name: 'Gate 1B: PETG - Ready' });
    await expect(petgGate).toBeVisible();
    await petgGate.click();
    await expect(petgGate).toHaveAttribute('aria-pressed', 'true');
    await expect(sidebar.getByText('#102', { exact: true })).toBeVisible();

    await sidebar.getByRole('button', { name: 'Load', exact: true }).click();
    await expect(sidebar.getByText('T1', { exact: true })).toBeVisible({ timeout: 15_000 });

    await sidebar.getByRole('button', { name: 'Eject', exact: true }).click();
    await expect(sidebar.getByText('T1', { exact: true })).toHaveCount(0, { timeout: 15_000 });
    await expect(sidebar.getByText('Unloaded', { exact: true })).toBeVisible();
  });

  test('AFC lanes render and load through the real backend', async ({ page, request }) => {
    await createMoonrakerControl(request).setMmuMode('ready', 'Afc');
    await page.reload();

    const sidebar = await openPrinterDetails(page, MOONRAKER_PRINTERS.ready);
    await expect(sidebar.getByText('AFC', { exact: true })).toBeVisible({ timeout: 15_000 });
    const rack = sidebar.getByText('Rack', { exact: true }).locator('..');
    await expect(rack.getByText('PLA', { exact: true })).toBeVisible();

    const lane = sidebar.getByRole('button', { name: 'Gate 1B: PETG - Ready' });
    await lane.click();
    await sidebar.getByRole('button', { name: 'Load', exact: true }).click();
    await expect(rack.getByText('PETG', { exact: true })).toBeVisible({ timeout: 15_000 });
    await expect(rack.getByText('PLA', { exact: true })).toHaveCount(0);
  });

  test('Qidibox slots render and unload through the real backend', async ({ page, request }) => {
    await createMoonrakerControl(request).setMmuMode('ready', 'Qidibox');
    await page.reload();

    const sidebar = await openPrinterDetails(page, MOONRAKER_PRINTERS.ready);
    await expect(sidebar.getByText('QIDIBOX', { exact: true })).toBeVisible({ timeout: 15_000 });
    const slot = sidebar.getByRole('button', { name: 'Gate 1A: PLA - Ready' });
    await slot.click();
    await sidebar.getByRole('button', { name: 'Unload', exact: true }).click();
    await expect(sidebar.getByText('Rack', { exact: true })).toHaveCount(0, { timeout: 15_000 });
  });

  test('Snapmaker U1 physical toolheads render as four material slots', async ({ page, request }) => {
    await createMoonrakerControl(request).setMmuMode('ready', 'SnapmakerU1');
    await page.reload();

    const sidebar = await openPrinterDetails(page, MOONRAKER_PRINTERS.ready);
    await expect(sidebar.getByRole('button', { name: 'Material Slots' })).toBeVisible({ timeout: 15_000 });
    const materialSlots = sidebar.getByTestId(/^ams-slot-\d+$/);
    await expect(materialSlots).toHaveCount(4);
    await expect(materialSlots.getByText('PLA', { exact: true })).toBeVisible();
    await expect(materialSlots.getByText('PETG', { exact: true })).toBeVisible();
  });
});

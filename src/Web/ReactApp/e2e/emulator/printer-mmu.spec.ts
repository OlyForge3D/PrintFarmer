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
    await page.waitForLoadState('networkidle');
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
});

import { dismissTourIfVisible } from '../fixtures/emulator-setup';
import {
  test,
  expect,
  createMoonrakerControl,
  expectPrinterStatus,
  getMoonrakerEmulatorUrl,
  MOONRAKER_PRINTERS,
  getPrinterCardByName,
  openPrinterDetails,
} from '../fixtures/moonraker';

/**
 * Printer Details & Metadata E2E Tests — Moonraker emulator-backed.
 *
 * Covers the detail sidebar's real-time telemetry, printer metadata
 * (statistics/version), and the Edit Printer configuration flow — all hard
 * assertions against actual rendered content, never a "some button exists"
 * placeholder.
 */

test.describe('Printer Details — Moonraker', () => {
  test.describe.configure({ mode: 'serial' });

  test.beforeEach(async ({ page, request }) => {
    await createMoonrakerControl(request).resetAll();
    await page.goto('/printers');
    await expect(getPrinterCardByName(page, MOONRAKER_PRINTERS.ready)).toBeVisible({ timeout: 15_000 });
    await dismissTourIfVisible(page);
  });

  test('opening a printer card reveals its detail sidebar landmark', async ({ page }) => {
    const sidebar = await openPrinterDetails(page, MOONRAKER_PRINTERS.ready);
    await expect(sidebar).toContainText(MOONRAKER_PRINTERS.ready);
    await expect(sidebar.getByRole('button', { name: 'Close sidebar' })).toBeVisible();
  });

  test('the sidebar Version section reports real Moonraker metadata, not the unavailable fallback', async ({ page }) => {
    const sidebar = await openPrinterDetails(page, MOONRAKER_PRINTERS.ready);

    // Version is expanded by default in the sidebar.
    await expect(sidebar.getByText('Version unavailable.')).toHaveCount(0);
    await expect(sidebar.getByText('Firmware', { exact: true }).locator('..')).toContainText('v0.9.2-emulator');
    await expect(sidebar.getByText('Backend', { exact: true }).locator('..')).toContainText('v0.9.2-emulator');
    await expect(sidebar.getByText('API', { exact: true }).locator('..')).toContainText('1.5.0');
    await expect(sidebar.getByText('Supported', { exact: true }).locator('..')).toContainText('Yes');
  });

  test('expanding Statistics shows real print totals, not the unavailable fallback', async ({ page }) => {
    const sidebar = await openPrinterDetails(page, MOONRAKER_PRINTERS.ready);

    await sidebar.getByRole('button', { name: 'Statistics' }).click();
    await expect(sidebar.getByText('Statistics unavailable.')).toHaveCount(0);
    await expect(sidebar.getByText('Print time', { exact: true }).locator('..')).toContainText('1.0h');
    await expect(sidebar.getByText('Filament', { exact: true }).locator('..')).toContainText('0g');
    await expect(sidebar.getByText('Completed', { exact: true }).locator('..')).toContainText('1');
    await expect(sidebar.getByText('Failed', { exact: true }).locator('..')).toContainText('0');
  });

  test('telemetry values and movement/temperature controls mutate the emulator through the real backend', async ({ page, request }) => {
    const sidebar = await openPrinterDetails(page, MOONRAKER_PRINTERS.ready);

    await expect(sidebar.getByText('23.4°C', { exact: true })).toBeVisible({ timeout: 15_000 });
    await expect(sidebar.getByText('22.1°C', { exact: true })).toBeVisible();
    await expect(sidebar.getByLabel('X movement amount').locator('..')).toContainText('[ 120.0 ]');
    await expect(sidebar.getByLabel('Y movement amount').locator('..')).toContainText('[ 120.0 ]');
    await expect(sidebar.getByLabel('Z movement amount').locator('..')).toContainText('[ 5.0 ]');

    await sidebar.getByLabel('X movement amount').fill('15');
    await sidebar.getByLabel('X movement amount').press('Enter');
    await expect(sidebar.getByLabel('X movement amount').locator('..')).toContainText('[ 135.0 ]', { timeout: 15_000 });

    await sidebar.getByLabel('Hotend target temperature').fill('205');
    await sidebar.getByLabel('Hotend target temperature').press('Enter');
    await sidebar.getByLabel('Bed target temperature').fill('60');
    await sidebar.getByLabel('Bed target temperature').press('Enter');

    await expect.poll(async () => {
      const response = await request.get(
        `${getMoonrakerEmulatorUrl('ready')}/printer/objects/query?gcode_move&extruder&heater_bed`
      );
      const payload = await response.json() as {
        result: {
          status: {
            gcode_move: { position: number[] };
            extruder: { target: number };
            heater_bed: { target: number };
          };
        };
      };
      return payload.result.status;
    }).toMatchObject({
      gcode_move: { position: [135, 120, 5, 0] },
      extruder: { target: 205 },
      heater_bed: { target: 60 },
    });
  });

  test('emergency stop and firmware restart are observable state transitions', async ({ page }) => {
    const card = getPrinterCardByName(page, MOONRAKER_PRINTERS.ready);
    const sidebar = await openPrinterDetails(page, MOONRAKER_PRINTERS.ready);

    await sidebar.getByRole('button', { name: 'Emergency Stop' }).click();
    await expectPrinterStatus(card, 'Shutdown');
    await expect(sidebar.getByRole('button', { name: 'Firmware Restart' })).toBeEnabled();

    await sidebar.getByRole('button', { name: 'Firmware Restart' }).click();
    await expectPrinterStatus(card, 'Idle');
    await expect(sidebar.getByRole('button', { name: 'Emergency Stop' })).toBeEnabled();
  });

  test('object exclusion updates the active print through the real backend', async ({ page }) => {
    const sidebar = await openPrinterDetails(page, MOONRAKER_PRINTERS.printing);
    const skipObject = sidebar.getByRole('button', { name: 'Skip object benchy_hull' });
    await expect(skipObject).toBeEnabled();
    await skipObject.focus();
    await page.keyboard.press('Enter');

    const confirmation = page.getByRole('dialog', { name: 'Skip print object?' });
    await expect(confirmation).toBeVisible();
    await confirmation.getByRole('button', { name: 'Skip object', exact: true }).click();

    const objects = sidebar.getByRole('list', { name: 'Current print objects' });
    await expect(objects.getByText('benchy_hull', { exact: true }).locator('..')).toContainText('Skipped', {
      timeout: 15_000,
    });
  });

  test('the Edit Printer modal opens pre-filled with the printer\'s real name', async ({ page }) => {
    const printerName = MOONRAKER_PRINTERS.paused;
    const card = getPrinterCardByName(page, printerName);
    await expect(card).toBeVisible();
    await card.getByRole('button', { name: 'Edit details' }).click();

    const modal = page.getByRole('dialog', { name: 'Edit Printer' });
    await expect(modal).toBeVisible();
    await expect(modal.getByLabel('Name')).toHaveValue(printerName);

    // Close without saving so the shared seeded printer is left untouched.
    await page.keyboard.press('Escape');
    await expect(modal).toBeHidden();
  });

  test('closing the sidebar removes its landmark from the page', async ({ page }) => {
    const sidebar = await openPrinterDetails(page, MOONRAKER_PRINTERS.shutdown);
    await sidebar.getByRole('button', { name: 'Close sidebar' }).click();
    await expect(page.getByRole('complementary', { name: `${MOONRAKER_PRINTERS.shutdown} details` })).toHaveCount(0);
  });
});

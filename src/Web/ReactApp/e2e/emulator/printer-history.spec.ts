import { dismissTourIfVisible } from '../fixtures/emulator-setup';
import {
  test,
  expect,
  MOONRAKER_PRINTERS,
  MOONRAKER_FILES,
  getPrinterCardByName,
  expectPrinterStatus,
  openPrinterHistory,
  createMoonrakerControl,
} from '../fixtures/moonraker';

/**
 * Printer History E2E Tests — Moonraker emulator-backed.
 *
 * Confirmed from `PrinterRegistry.cs`: every seeded printer starts with
 * exactly one pre-seeded `completed` history entry (`calibration_cube.gcode`).
 * There is no pre-seeded `cancelled` entry — one only exists once a print is
 * actually started and cancelled. To keep this suite runnable on its own
 * (per `e2e/README.md`'s "run a single spec" instructions) rather than
 * depending on `job-lifecycle.spec.ts` happening to run first in the same
 * worker, the "cancelled" coverage below drives that action itself instead
 * of assuming it was pre-seeded.
 */

test.describe('Printer History — Moonraker', () => {
  test.describe.configure({ mode: 'serial' });

  test.beforeEach(async ({ page }) => {
    await page.goto('/printers');
    await page.waitForLoadState('networkidle');
    await dismissTourIfVisible(page);
  });

  test('shows the pre-seeded completed job and a seeded active job cancelled during this test', async ({ page, request }) => {
    test.setTimeout(60_000);
    const control = createMoonrakerControl(request);
    await control.reset('printing');

    const printerName = MOONRAKER_PRINTERS.printing;
    const card = getPrinterCardByName(page, printerName);
    await expect(card).toBeVisible();
    await expectPrinterStatus(card, 'Printing');

    // Cancel the fixture's real occupied dispatch so the history entry is
    // deterministic and does not repeat the separately-covered start flow.
    await card.getByRole('button', { name: 'Cancel' }).click();
    await expect(card.getByRole('button', { name: 'Cancel' })).toBeDisabled({ timeout: 15_000 });
    await expect(card.getByText('Cancelled', { exact: true }).first()).toBeVisible({ timeout: 15_000 });

    const dialog = await openPrinterHistory(page, printerName);
    await expect(dialog.getByText('No Print History')).toHaveCount(0);
    await expect(dialog.getByText('completed', { exact: true }).first()).toBeVisible({ timeout: 10_000 });
    await expect(dialog.getByText('cancelled', { exact: true }).first()).toBeVisible({ timeout: 10_000 });
    await expect(dialog).toContainText(MOONRAKER_FILES.calibrationCube);
    await expect(dialog).toContainText(MOONRAKER_FILES.benchy);

    await control.reset('printing');
  });

  test('summary statistics reflect real totals, not zeroed placeholders', async ({ page }) => {
    const dialog = await openPrinterHistory(page, MOONRAKER_PRINTERS.ready);

    await expect(dialog.getByText('Print Statistics')).toBeVisible();
    await expect(dialog.getByText('Total Jobs')).toBeVisible();

    const totalJobsCard = dialog.getByText('Total Jobs', { exact: true }).locator('../..');
    const totalJobsText = (await totalJobsCard.textContent()) ?? '';
    const totalJobs = Number(totalJobsText.replace(/[^\d]/g, ''));
    expect(totalJobs, 'seeded history must contribute at least one job to the printer totals').toBeGreaterThan(0);
  });

  test('can change the number of jobs shown and the sort order', async ({ page }) => {
    const dialog = await openPrinterHistory(page, MOONRAKER_PRINTERS.ready);

    await dialog.locator('#history-limit').selectOption('25');
    await expect(dialog.locator('#history-limit')).toHaveValue('25');

    await dialog.getByLabel('Sort order').selectOption('asc');
    await expect(dialog.getByLabel('Sort order')).toHaveValue('asc');
  });
});

import { dismissTourIfVisible } from '../fixtures/emulator-setup';
import {
  test,
  expect,
  MOONRAKER_PRINTERS,
  MOONRAKER_FILES,
  getPrinterCardByName,
  expectPrinterStatus,
  openPrinterFiles,
  getPrinterFileRow,
  createMoonrakerControl,
} from '../fixtures/moonraker';

/**
 * Job Lifecycle E2E Tests — Moonraker emulator-backed.
 *
 * Exercises the full Start → Pause → Resume → Cancel lifecycle against the
 * seeded printers using the real UI controls (never an "if the button
 * exists" soft branch — a missing Print/Pause/Resume/Cancel control fails
 * the test). Progress advancement between deterministic assertions uses the
 * emulator's virtual-clock control API instead of arbitrary sleeps.
 *
 * These tests mutate shared printer state, so this file (and the rest of
 * `e2e/emulator/`) must be run serially — see the `test:e2e:moonraker`
 * script, which passes `--workers=1`.
 */

test.describe('Job Lifecycle — Moonraker', () => {
  test.describe.configure({ mode: 'serial' });

  test.beforeEach(async ({ page, request }) => {
    await createMoonrakerControl(request).resetAll();
    await page.goto('/printers');
    await page.waitForLoadState('networkidle');
    await dismissTourIfVisible(page);
  });

  test.afterEach(async ({ request }) => {
    // Always leave the shared scenario deterministic for the next test/file.
    await createMoonrakerControl(request).resetAll();
  });

  test('Start: queuing benchy.gcode from the Ready printer begins a print', async ({ page }) => {
    const printerName = MOONRAKER_PRINTERS.ready;
    const card = getPrinterCardByName(page, printerName);
    await expect(card).toBeVisible();
    await expectPrinterStatus(card, 'Idle');

    const filesDialog = await openPrinterFiles(page, printerName);
    // Only `benchy.gcode` is a live virtual file on the seeded printer;
    // `calibration_cube.gcode` is confirmed (see fixtures/moonraker.ts) to
    // exist only as a history entry's filename, not a live file here.
    await expect(filesDialog).toContainText(MOONRAKER_FILES.benchy);

    const benchyRow = getPrinterFileRow(filesDialog, MOONRAKER_FILES.benchy);
    await expect(benchyRow).toBeVisible();
    await benchyRow.getByRole('button', { name: 'Queue for printing' }).click();

    const confirmDialog = page.getByRole('dialog', { name: 'Start Printing?' });
    await expect(confirmDialog).toBeVisible();
    await confirmDialog.getByRole('button', { name: 'Start Printing' }).click();

    // The files modal closes itself once the print starts successfully.
    await expect(filesDialog).toBeHidden({ timeout: 10_000 });

    // Real state transition via the backend + SignalR — no arbitrary sleep,
    // just a hard wait on the actual claimed outcome.
    await expectPrinterStatus(card, 'Printing');
    await expect(card).toContainText(MOONRAKER_FILES.benchy);

    const progressBar = card.getByRole('progressbar', { name: 'Print progress' });
    await expect(progressBar).toBeVisible();
  });

  test('progress advances deterministically via the emulator control API', async ({ page, request }) => {
    const printerName = MOONRAKER_PRINTERS.printing;
    const card = getPrinterCardByName(page, printerName);
    await expect(card).toBeVisible();
    await expectPrinterStatus(card, 'Printing');

    const control = createMoonrakerControl(request);
    await control.advancePrintProgress('printing', 55);

    const progressBar = card.getByRole('progressbar', { name: 'Print progress' });
    await expect(progressBar).toHaveAttribute('aria-valuenow', '55', { timeout: 10_000 });
  });

  test('Pause → Resume: the Printing printer can be paused and resumed from its card', async ({ page }) => {
    test.setTimeout(60_000);
    const printerName = MOONRAKER_PRINTERS.printing;
    const card = getPrinterCardByName(page, printerName);
    await expect(card).toBeVisible();
    await expectPrinterStatus(card, 'Printing');

    await card.getByRole('button', { name: 'Pause' }).click();
    await expectPrinterStatus(card, 'Paused');
    await expect(card.getByRole('button', { name: 'Resume' })).toBeEnabled();

    await card.getByRole('button', { name: 'Resume' }).click();
    await expectPrinterStatus(card, 'Printing');
    await expect(card.getByRole('button', { name: 'Pause' })).toBeEnabled();
  });

  test('Cancel: cancelling the Paused printer disables print controls and zeroes progress', async ({ page }) => {
    const printerName = MOONRAKER_PRINTERS.paused;
    const card = getPrinterCardByName(page, printerName);
    await expect(card).toBeVisible();
    await expectPrinterStatus(card, 'Paused');

    await card.getByRole('button', { name: 'Cancel' }).click();

    await expectPrinterStatus(card, 'Cancelled');
    await expect(card.getByRole('button', { name: /^(Pause|Resume)$/ })).toBeDisabled({ timeout: 15_000 });
    await expect(card.getByRole('button', { name: 'Cancel' })).toBeDisabled();
    await expect(card.getByRole('progressbar', { name: 'Print progress' })).toHaveAttribute(
      'aria-valuenow',
      '0',
      { timeout: 15_000 }
    );
  });
});

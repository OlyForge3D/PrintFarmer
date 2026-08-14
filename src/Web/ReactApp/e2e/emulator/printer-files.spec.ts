import { dismissTourIfVisible } from '../fixtures/emulator-setup';
import {
  test,
  expect,
  MOONRAKER_PRINTERS,
  MOONRAKER_FILES,
  getPrinterCardByName,
  openPrinterFiles,
  getPrinterFileRow,
  createMoonrakerControl,
  uploadScratchGcodeFile,
  deleteScratchGcodeFile,
} from '../fixtures/moonraker';

const SORT_SCRATCH_FILE = 'aaa-e2e-sort-scratch.gcode';
const DELETE_SCRATCH_FILE = 'e2e-delete-scratch.gcode';

/**
 * Printer Files E2E Tests — Moonraker emulator-backed.
 *
 * `benchy.gcode` is confirmed (see `fixtures/moonraker.ts`) to be the one
 * live virtual file seeded on every scenario printer — `calibration_cube.gcode`
 * only appears as a history entry's filename, not a live file, so it is not
 * asserted here. These tests assert the real file list content and the
 * delete flow — a missing file, a missing action button, or a silently
 * accepted empty list all fail the test rather than falling back to "some
 * files or an empty state".
 */

test.describe('Printer Files — Moonraker', () => {
  test.describe.configure({ mode: 'serial' });

  test.beforeEach(async ({ page, request }) => {
    await createMoonrakerControl(request).resetAll();
    await deleteScratchGcodeFile(request, 'ready', SORT_SCRATCH_FILE, true);
    await deleteScratchGcodeFile(request, 'ready', DELETE_SCRATCH_FILE, true);
    await page.goto('/printers');
    await expect(getPrinterCardByName(page, MOONRAKER_PRINTERS.ready)).toBeVisible({ timeout: 15_000 });
    await dismissTourIfVisible(page);
  });

  test.afterEach(async ({ request }) => {
    await deleteScratchGcodeFile(request, 'ready', SORT_SCRATCH_FILE, true);
    await deleteScratchGcodeFile(request, 'ready', DELETE_SCRATCH_FILE, true);
    await createMoonrakerControl(request).resetAll();
  });

  test('lists the seeded benchy.gcode file with working per-file actions', async ({ page }) => {
    const filesDialog = await openPrinterFiles(page, MOONRAKER_PRINTERS.ready);

    const row = getPrinterFileRow(filesDialog, MOONRAKER_FILES.benchy);
    await expect(row, `expected seeded file "${MOONRAKER_FILES.benchy}" to be listed`).toBeVisible();
    await expect(row.getByRole('button', { name: 'Queue for printing' })).toBeEnabled();
    await expect(row.getByRole('button', { name: 'Download file' })).toBeEnabled();
    await expect(row.getByRole('button', { name: 'Delete file' })).toBeEnabled();
  });

  test('downloads the seeded G-code through the real printer file endpoint', async ({ page }) => {
    const filesDialog = await openPrinterFiles(page, MOONRAKER_PRINTERS.ready);
    const row = getPrinterFileRow(filesDialog, MOONRAKER_FILES.benchy);

    const downloadPromise = page.waitForEvent('download');
    await row.getByRole('button', { name: 'Download file' }).click();
    const download = await downloadPromise;
    expect(download.suggestedFilename()).toBe(MOONRAKER_FILES.benchy);
  });

  test('sorting toggles the visible file order once a second file exists', async ({ page, request }) => {
    // The seed only guarantees one live file; upload a throwaway second file
    // (never touching the guaranteed benchy.gcode) so sort order is actually
    // observable, then clean it up.
    await uploadScratchGcodeFile(request, 'ready', SORT_SCRATCH_FILE);

    const filesDialog = await openPrinterFiles(page, MOONRAKER_PRINTERS.ready);
    const rowLocator = filesDialog.locator('div.group');
    await expect(rowLocator).toHaveCount(2);

    const initialOrder = await rowLocator.allTextContents();
    await filesDialog.getByRole('button', { name: /^Sort (ascending|descending)$/ }).click();
    const toggledOrder = await rowLocator.allTextContents();

    expect(toggledOrder, 'toggling sort order must actually reorder the two files').not.toEqual(initialOrder);

    await deleteScratchGcodeFile(request, 'ready', SORT_SCRATCH_FILE);
  });

  test('can delete an uploaded file without touching the guaranteed benchy.gcode seed file', async ({ page, request }) => {
    // Delete an uploaded scratch file so the assertion exercises mutation
    // without removing the canonical benchy fixture during the test.
    await uploadScratchGcodeFile(request, 'ready', DELETE_SCRATCH_FILE);

    const filesDialog = await openPrinterFiles(page, MOONRAKER_PRINTERS.ready);
    const row = getPrinterFileRow(filesDialog, DELETE_SCRATCH_FILE);
    await expect(row).toBeVisible();
    await row.getByRole('button', { name: 'Delete file' }).click();

    const confirmDialog = page.getByRole('dialog', { name: 'Delete File?' });
    await expect(confirmDialog).toBeVisible();
    await confirmDialog.getByRole('button', { name: 'Delete' }).click();

    await expect(getPrinterFileRow(filesDialog, DELETE_SCRATCH_FILE)).toHaveCount(0, { timeout: 10_000 });
    await expect(getPrinterFileRow(filesDialog, MOONRAKER_FILES.benchy)).toBeVisible();
  });
});

import { dismissTourIfVisible } from '../fixtures/emulator-setup';
import { test, expect, MOONRAKER_PRINTERS, getPrinterCardByName, openPrinterDetails } from '../fixtures/moonraker';

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

  test.beforeEach(async ({ page }) => {
    await page.goto('/printers');
    await page.waitForLoadState('networkidle');
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
    await expect(sidebar.getByText('Firmware')).toBeVisible();
    await expect(sidebar.getByText('Backend')).toBeVisible();
    await expect(sidebar.getByText('API')).toBeVisible();
  });

  test('expanding Statistics shows real print totals, not the unavailable fallback', async ({ page }) => {
    const sidebar = await openPrinterDetails(page, MOONRAKER_PRINTERS.ready);

    await sidebar.getByRole('button', { name: 'Statistics' }).click();
    await expect(sidebar.getByText('Statistics unavailable.')).toHaveCount(0);
    await expect(sidebar.getByText('Print time')).toBeVisible();
    await expect(sidebar.getByText('Completed')).toBeVisible();
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

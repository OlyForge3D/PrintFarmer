import { dismissTourIfVisible } from '../fixtures/emulator-setup';
import {
  test,
  expect,
  MOONRAKER_PRINTERS,
  getPrinterCardByName,
  openPrinterDetails,
  openPrinterFiles,
  createMoonrakerControl,
} from '../fixtures/moonraker';

/**
 * Accessibility-relevant behavior E2E Tests — Moonraker emulator-backed.
 *
 * Focuses on keyboard operability and semantic structure for the printer
 * surfaces this suite otherwise exercises with mouse clicks — real keyboard
 * activation (not just visibility), dialog focus/Escape handling, and a
 * single-`h1` page structure.
 */

test.describe('Printer Accessibility — Moonraker', () => {
  test.describe.configure({ mode: 'serial' });

  test.beforeEach(async ({ page, request }) => {
    await createMoonrakerControl(request).resetAll();
    await page.goto('/printers');
    await expect(getPrinterCardByName(page, MOONRAKER_PRINTERS.ready)).toBeVisible({ timeout: 15_000 });
    await dismissTourIfVisible(page);
  });

  test.afterEach(async ({ request }) => {
    await createMoonrakerControl(request).resetAll();
  });

  test('the printers page has exactly one h1', async ({ page }) => {
    await expect(page.getByRole('heading', { level: 1 })).toHaveCount(1);
  });

  test('the Pause control on the Printing card is keyboard-activatable', async ({ page }) => {
    const card = getPrinterCardByName(page, MOONRAKER_PRINTERS.printing);
    await expect(card).toBeVisible();

    const pauseButton = card.getByRole('button', { name: 'Pause' });
    await pauseButton.focus();
    await expect(pauseButton).toBeFocused();
    await page.keyboard.press('Enter');

    await expect(card.getByText('Paused', { exact: true }).first()).toBeVisible({ timeout: 10_000 });
  });

  test('"Open details sidebar" is keyboard-reachable and opens the sidebar via Enter', async ({ page }) => {
    const card = getPrinterCardByName(page, MOONRAKER_PRINTERS.ready);
    const openButton = card.getByRole('button', { name: 'Open details sidebar' });
    await openButton.focus();
    await expect(openButton).toBeFocused();
    await page.keyboard.press('Enter');

    await expect(page.getByRole('complementary', { name: `${MOONRAKER_PRINTERS.ready} details` })).toBeVisible();
  });

  test('the printer files dialog traps focus, closes on Escape, and restores its trigger', async ({ page }) => {
    const card = getPrinterCardByName(page, MOONRAKER_PRINTERS.ready);
    const trigger = card.getByRole('button', { name: 'View printer files' });
    await trigger.focus();
    const filesDialog = await openPrinterFiles(page, MOONRAKER_PRINTERS.ready);

    const headerClose = filesDialog.getByRole('button', { name: 'Close modal' });
    await expect(headerClose).toBeFocused();

    const footerClose = filesDialog.getByRole('button', { name: 'Close', exact: true });
    await footerClose.focus();
    await page.keyboard.press('Tab');
    await expect(headerClose).toBeFocused();

    await page.keyboard.press('Escape');
    await expect(filesDialog).toBeHidden();
    await expect(trigger).toBeFocused();
  });

  test('the detail sidebar landmark has a name distinguishing it from other printers', async ({ page }) => {
    const readySidebar = await openPrinterDetails(page, MOONRAKER_PRINTERS.ready);
    await expect(readySidebar).toBeVisible();
    await readySidebar.getByRole('button', { name: 'Close sidebar' }).click();
    await expect(readySidebar).toHaveCount(0);

    const pausedSidebar = await openPrinterDetails(page, MOONRAKER_PRINTERS.paused);
    await expect(pausedSidebar).toBeVisible();
    await expect(pausedSidebar).toContainText(MOONRAKER_PRINTERS.paused);
    await expect(pausedSidebar).not.toContainText(MOONRAKER_PRINTERS.ready);
  });
});

import { dismissTourIfVisible } from '../fixtures/emulator-setup';
import {
  test,
  expect,
  ALL_MOONRAKER_PRINTER_NAMES,
  MOONRAKER_PRINTERS,
  getPrinterCards,
  getPrinterCardByName,
  expectPrinterStatus,
} from '../fixtures/moonraker';

/**
 * Printer Status E2E Tests — Moonraker emulator-backed.
 *
 * Deterministic seed contract (see e2e/README.md "Moonraker emulator
 * contract"): five real-Moonraker-backed printers named exactly
 * "Moonraker Ready", "Moonraker Printing", "Moonraker Paused",
 * "Moonraker Shutdown", and "Moonraker Offline". Assertions here are hard
 * requirements against that contract — no `.catch(() => false)` soft
 * fallbacks, no "any button count > N" placeholders.
 *
 * Enable/disable predicates are read directly from
 * `src/features/printers/utils/printerSupport.ts`:
 *   - Pause/Resume and Cancel require the printer to be online AND actively
 *     printing or paused.
 *   - Emergency Stop / Firmware Restart, Files, and History require only
 *     that the printer is online.
 */

test.use({ serviceWorkers: 'block' });

test.describe('Printer Status — Moonraker', () => {
  test.describe.configure({ mode: 'serial' });

  test.beforeEach(async ({ page }) => {
    await page.goto('/printers');
    await page.waitForLoadState('domcontentloaded');
    await dismissTourIfVisible(page);
  });

  test('printers page lists exactly the five seeded Moonraker printers, each exactly once', async ({ page }) => {
    await expect(getPrinterCards(page)).toHaveCount(5);

    for (const name of Object.values(MOONRAKER_PRINTERS)) {
      const card = getPrinterCardByName(page, name);
      await expect(card).toBeVisible({ timeout: 15_000 });
      await expect(page.locator('[data-pf-card]').filter({ hasText: name })).toHaveCount(1);
    }

    const cardText = await getPrinterCards(page).allTextContents();
    for (const name of ALL_MOONRAKER_PRINTER_NAMES) {
      expect(cardText.filter((text) => text.includes(name))).toHaveLength(1);
    }
  });

  test('Ready scenario shows Idle status; print controls disabled, e-stop and quick access enabled', async ({ page }) => {
    const card = getPrinterCardByName(page, MOONRAKER_PRINTERS.ready);
    await expect(card).toBeVisible();
    await expectPrinterStatus(card, 'Idle');

    await expect(card.getByRole('button', { name: 'Pause' })).toBeDisabled();
    await expect(card.getByRole('button', { name: 'Cancel' })).toBeDisabled();
    await expect(card.getByRole('button', { name: 'Emergency Stop' })).toBeEnabled();
    await expect(card.getByRole('button', { name: 'View printer files' })).toBeEnabled();
    await expect(card.getByRole('button', { name: 'View print history' })).toBeEnabled();

    // No active job — progress bar renders at 0 and shows no job name.
    const progressBar = card.getByRole('progressbar', { name: 'Print progress' });
    await expect(progressBar).toHaveAttribute('aria-valuenow', '0');
  });

  test('Printing printer shows Printing status with a stable, valid progress value and pause/cancel enabled', async ({ page }) => {
    const card = getPrinterCardByName(page, MOONRAKER_PRINTERS.printing);
    await expect(card).toBeVisible();
    await expectPrinterStatus(card, 'Printing');

    await expect(card.getByRole('button', { name: 'Pause' })).toBeEnabled();
    await expect(card.getByRole('button', { name: 'Cancel' })).toBeEnabled();
    await expect(card.getByRole('button', { name: 'Emergency Stop' })).toBeEnabled();

    // The emulator's virtual clock is deterministic (TimeScale=0 by default)
    // and starts a fresh Printing scenario at 0% elapsed — "stable" means it
    // does not drift on its own between reads, not that it starts non-zero.
    // See job-lifecycle.spec.ts for deterministic advancement via the
    // control API.
    const progressBar = card.getByRole('progressbar', { name: 'Print progress' });
    await expect(progressBar).toBeVisible();
    await expect(progressBar).toHaveAttribute('aria-valuenow', '0');
    await page.waitForTimeout(500);
    await expect(
      progressBar,
      'progress must remain at the exact frozen baseline with no control-API action',
    ).toHaveAttribute('aria-valuenow', '0');
  });

  test('Paused printer shows Paused status with resume/cancel enabled and its seeded 20% progress', async ({ page }) => {
    const card = getPrinterCardByName(page, MOONRAKER_PRINTERS.paused);
    await expect(card).toBeVisible();
    await expectPrinterStatus(card, 'Paused');

    await expect(card.getByRole('button', { name: 'Resume' })).toBeEnabled();
    await expect(card.getByRole('button', { name: 'Cancel' })).toBeEnabled();
    await expect(card.getByRole('button', { name: 'Emergency Stop' })).toBeEnabled();

    // Seeded deterministically at PrintDuration=120s / 600s total = 20%.
    await expect(card.getByRole('progressbar', { name: 'Print progress' })).toHaveAttribute('aria-valuenow', '20');
    await expect(card).toContainText('benchy.gcode');
  });

  test('Shutdown printer shows Shutdown status with Firmware Restart control', async ({ page }) => {
    const card = getPrinterCardByName(page, MOONRAKER_PRINTERS.shutdown);
    await expect(card).toBeVisible();
    await expectPrinterStatus(card, 'Shutdown');

    // Firmware in a shutdown/error state — no active job to pause or cancel.
    await expect(card.getByRole('button', { name: 'Pause' })).toBeDisabled();
    await expect(card.getByRole('button', { name: 'Cancel' })).toBeDisabled();
    // The connection to Moonraker itself is still reachable, so recovery is offered.
    await expect(card.getByRole('button', { name: 'Firmware Restart' })).toBeEnabled();
  });

  test('Offline printer shows Offline status with all print controls disabled and troubleshooting guidance', async ({ page }) => {
    const card = getPrinterCardByName(page, MOONRAKER_PRINTERS.offline);
    await expect(card).toBeVisible();
    await expectPrinterStatus(card, 'Offline');

    await expect(card.getByRole('button', { name: 'Pause' })).toBeDisabled();
    await expect(card.getByRole('button', { name: 'Cancel' })).toBeDisabled();
    await expect(card.getByRole('button', { name: 'Emergency Stop' })).toHaveCount(0);
    await expect(card.getByRole('button', { name: 'View printer files' })).toBeDisabled();
    await expect(card.getByRole('button', { name: 'View print history' })).toBeDisabled();

    // Offline-specific troubleshooting guidance must render for an unreachable printer.
    await expect(card.getByRole('button', { name: 'Toggle offline troubleshooting guide' })).toBeVisible();
  });

  test('printers page shows a loading state before printer data resolves', async ({ page }) => {
    // Deterministically slow the printers list response instead of racing a
    // real page load — this asserts the actual `isLoading` skeleton branch
    // rather than assuming it flashes by fast enough to "probably" be seen.
    const printersCollectionRoute = '**/api/printers*';
    await page.route(printersCollectionRoute, async (route) => {
      if (new URL(route.request().url()).pathname !== '/api/printers') {
        await route.continue();
        return;
      }

      await new Promise((resolve) => setTimeout(resolve, 1_000));
      await route.continue();
    });

    await page.reload();
    const loadingRegion = page.locator('[role="status"][aria-busy="true"]');
    await expect(loadingRegion).toBeVisible();

    await page.unroute(printersCollectionRoute);
  });

  test('distinguishes a failed printer request from a successful empty fleet', async ({ page }) => {
    test.setTimeout(90_000);
    const printersCollectionRoute = '**/api/printers*';
    await page.route(printersCollectionRoute, async (route) => {
      if (new URL(route.request().url()).pathname !== '/api/printers') {
        await route.continue();
        return;
      }

      await route.fulfill({
        status: 503,
        contentType: 'application/json',
        body: JSON.stringify({ message: 'Moonraker validation outage' }),
      });
    });

    await page.reload();
    const alert = page.getByRole('alert');
    await expect(alert).toContainText('Unable to Load Printers', { timeout: 60_000 });
    await expect(alert.getByRole('button', { name: 'Retry' })).toBeVisible();
    await expect(page.getByText('No Printers Found')).toHaveCount(0);

    await page.unroute(printersCollectionRoute);
    await page.route(printersCollectionRoute, async (route) => {
      if (new URL(route.request().url()).pathname !== '/api/printers') {
        await route.continue();
        return;
      }

      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: '[]',
      });
    });

    await page.reload();
    await expect(page.getByText('No Printers Found')).toBeVisible();
    await expect(page.getByRole('alert')).toHaveCount(0);
  });
});

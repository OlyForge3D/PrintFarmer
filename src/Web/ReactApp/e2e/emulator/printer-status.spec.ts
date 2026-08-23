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
    // Track the exact delayed request's own completion. This is what lets us
    // safely unroute below without racing this handler's own pending
    // `route.continue()` call (see comment near `page.unroute` for why that
    // race existed — issue #1895).
    const printersResponse = page.waitForResponse(
      (response) => new URL(response.url()).pathname === '/api/printers'
    );
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

    // Wait for the delayed handler's own `route.continue()` to actually
    // finish before unrouting. Unrouting while that continue() call is still
    // pending races Playwright's route-cleanup (triggered by `unroute`)
    // against this handler's in-flight completion: the cleanup settles the
    // route first, and this handler's own subsequent `route.continue()` call
    // then throws "Route is already handled!" — an unhandled rejection that
    // surfaces asynchronously and was bleeding into the *next* test
    // (issue #1895). Awaiting the response here guarantees the handler has
    // already completed before we unroute.
    await printersResponse;
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
    // The shared query client retries a 503 up to 3 times with exponential backoff
    // before the error branch renders, so the alert (and everything inside it,
    // including the nested Retry button — both mount in the same React commit)
    // can legitimately take close to the full backoff window to appear. Waiting on
    // the alert container's own visibility first — with the same generous,
    // backoff-aware budget used for the text assertion — before touching anything
    // nested inside it keeps every assertion here on one consistent wait budget.
    // Previously the nested Retry button check fell back to Playwright's default
    // ~5s timeout, a much narrower budget than the text assertion right above it
    // for state that settles together; under CI scheduling jitter near the tail of
    // that backoff window, the button check could start its own countdown late and
    // time out even though the button was about to mount (issue #1574).
    await expect(alert).toBeVisible({ timeout: 60_000 });
    await expect(alert).toContainText('Unable to Load Printers', { timeout: 60_000 });
    await expect(alert.getByRole('button', { name: 'Retry' })).toBeVisible({ timeout: 60_000 });
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

import { dismissTourIfVisible } from '../fixtures/emulator-setup';
import { test, expect, getPrinterCardByName } from '../fixtures/moonraker';

/**
 * Printer Discovery E2E Tests — Moonraker emulator-backed.
 *
 * The seed contract's deterministic, injected discovery source is
 * `PrinterDiscovery.Services.DeterministicDiscoveryFixtureProvider`, which
 * landed in this worktree while these specs were being written. It returns
 * exactly two fixed candidates, both backend Moonraker, filtered to exclude
 * any already-registered printer by server URL
 * (`StreamingDiscoveryService.ScanDeterministicFixturesAsync`):
 *
 *   - "Discovered Voron V2.4"
 *   - "Discovered Prusa MK4S"
 *
 * Neither name collides with the five seeded farm printers, so "not already
 * added" holds by construction. Because registering a candidate permanently
 * excludes it from later scans (no control-API reset for discovery), the
 * "add" test intentionally runs last so the earlier exact-count/name
 * assertions see both candidates still available — this assumes a single
 * fresh run against an ephemeral validation environment, matching this
 * project's daily-validation topology (see `e2e/README.md` and
 * `docs/MOONRAKER_EMULATOR_VALIDATION.md`).
 *
 * These tests require the "Discover Printers" action to actually be
 * present (gated by `useDiscoveryAvailable`, which needs the discovery
 * service's `NetworkDiscovery` settings enabled with a fresh heartbeat) and
 * require exactly these two discovered candidates — a missing action,
 * empty result set, or wrong candidate set fails the test rather than being
 * treated as an acceptable "soft" outcome.
 */

const EXPECTED_DISCOVERY_CANDIDATES = ['Discovered Voron V2.4', 'Discovered Prusa MK4S'];

test.describe('Printer Discovery — Moonraker', () => {
  test.describe.configure({ mode: 'serial' });

  test.beforeEach(async ({ page }) => {
    await page.goto('/printers');
    await page.waitForLoadState('networkidle');
    await dismissTourIfVisible(page);
  });

  test('the Discover Printers action is available to an admin', async ({ page }) => {
    const discoverButton = page.getByRole('button', { name: 'Discover Printers on the local network' });
    await expect(
      discoverButton,
      'The "Discover Printers" action must be visible for an admin session. If this fails, the ' +
        'NetworkDiscovery settings heartbeat is stale or discovery is disabled — see useDiscoveryAvailable.'
    ).toBeVisible({ timeout: 15_000 });
  });

  test('starting a scan shows progress and completes with exactly the two deterministic candidates', async ({ page }) => {
    await page.getByRole('button', { name: 'Discover Printers on the local network' }).click();

    const modal = page.getByRole('dialog', { name: 'Discover Printers' });
    await expect(modal).toBeVisible();

    await modal.getByRole('button', { name: 'Start Scan' }).click();

    // Hard assertion: the scan announces progress via the discovery stream.
    await expect(modal.getByText(/Session:/)).toBeVisible({ timeout: 10_000 });

    // Hard assertion: the scan must complete and report exactly two found printers.
    await expect(modal.getByText('Found 2 printers', { exact: true })).toBeVisible({ timeout: 30_000 });

    for (const name of EXPECTED_DISCOVERY_CANDIDATES) {
      await expect(modal.getByRole('checkbox', { name: `Select printer ${name}` })).toBeVisible();
    }
  });

  test('discovered candidates are all backend Moonraker', async ({ page }) => {
    await page.getByRole('button', { name: 'Discover Printers on the local network' }).click();
    const modal = page.getByRole('dialog', { name: 'Discover Printers' });
    await modal.getByRole('button', { name: 'Start Scan' }).click();
    await expect(modal.getByText('Found 2 printers', { exact: true })).toBeVisible({ timeout: 30_000 });

    for (const printerName of EXPECTED_DISCOVERY_CANDIDATES) {
      const checkbox = modal.getByRole('checkbox', { name: `Select printer ${printerName}` });
      await expect(checkbox).toBeVisible();
      const candidate = checkbox.locator(
        'xpath=ancestor::div[contains(concat(" ", normalize-space(@class), " "), " p-4 ")][1]'
      );
      await expect(candidate.getByText('Moonraker', { exact: true })).toHaveCount(1);
    }
  });

  test('can add a discovered candidate to the farm', async ({ page }) => {
    test.setTimeout(60_000);
    await page.getByRole('button', { name: 'Discover Printers on the local network' }).click();
    const modal = page.getByRole('dialog', { name: 'Discover Printers' });
    await modal.getByRole('button', { name: 'Start Scan' }).click();
    await expect(modal.getByText('Found 2 printers', { exact: true })).toBeVisible({ timeout: 30_000 });

    const targetName = EXPECTED_DISCOVERY_CANDIDATES[0];
    const checkbox = modal.getByRole('checkbox', { name: `Select printer ${targetName}` });
    await expect(checkbox).toBeVisible();
    await checkbox.check();
    await modal.getByRole('button', { name: 'Add 1 Selected Printer' }).click();

    // The modal closes itself once the printer is registered.
    await expect(modal).toBeHidden({ timeout: 45_000 });

    // The newly added printer must now be a real card on the farm.
    await expect(getPrinterCardByName(page, targetName)).toBeVisible({ timeout: 15_000 });
  });
});

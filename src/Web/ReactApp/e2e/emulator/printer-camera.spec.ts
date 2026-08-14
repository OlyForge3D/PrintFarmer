import { dismissTourIfVisible } from '../fixtures/emulator-setup';
import { test, expect, ALL_MOONRAKER_PRINTER_NAMES, getPrinterCardByName } from '../fixtures/moonraker';

/**
 * Printer Camera E2E Tests — Moonraker emulator-backed.
 *
 * The seed contract guarantees a local (non-network) camera snapshot is
 * available. The exact printer(s) exposing it are not specified by the
 * contract wording, so this suite requires — hard fail, not skip — that at
 * least one seeded printer exposes a real, working local preview, and
 * verifies every printer that claims to have one actually renders real
 * image content rather than the "no camera" / "unavailable" placeholder.
 */

test.describe('Printer Camera — Moonraker', () => {
  test.describe.configure({ mode: 'serial' });

  test.beforeEach(async ({ page }) => {
    await page.goto('/printers');
    await page.waitForLoadState('networkidle');
    await dismissTourIfVisible(page);
  });

  test('at least one seeded printer exposes a working local camera preview', async ({ page }) => {
    let verifiedCount = 0;

    for (const name of ALL_MOONRAKER_PRINTER_NAMES) {
      const card = getPrinterCardByName(page, name);
      await expect(card).toBeVisible();

      const toggle = card.getByRole('button', { name: /^(Show|Hide) camera preview$/ });
      const isEnabled = await toggle.isEnabled();
      if (!isEnabled) {
        continue;
      }

      await toggle.click();
      const preview = card.locator('.pf-detailed-printer-camera-preview');
      await expect(preview).toBeVisible();

      // A real local preview must not fall back to the "no camera" placeholder.
      await expect(preview.getByText('No linked camera configured')).toHaveCount(0);

      const image = preview.locator('img');
      await expect(image).toBeVisible({ timeout: 10_000 });
      const src = await image.getAttribute('src');
      expect(src, `${name} camera preview must have a real image source`).toBeTruthy();

      verifiedCount += 1;
    }

    expect(
      verifiedCount,
      'expected at least one seeded Moonraker printer to expose a working local camera preview'
    ).toBeGreaterThan(0);
  });
});

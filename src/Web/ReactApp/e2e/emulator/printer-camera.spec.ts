import { dismissTourIfVisible } from '../fixtures/emulator-setup';
import { test, expect, MOONRAKER_PRINTERS, getPrinterCardByName } from '../fixtures/moonraker';

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
    await expect(getPrinterCardByName(page, MOONRAKER_PRINTERS.ready)).toBeVisible({ timeout: 15_000 });
    await dismissTourIfVisible(page);
  });

  test('every reachable scenario exposes a decoded local camera preview', async ({ page }) => {
    for (const name of [
      MOONRAKER_PRINTERS.ready,
      MOONRAKER_PRINTERS.printing,
      MOONRAKER_PRINTERS.paused,
      MOONRAKER_PRINTERS.shutdown,
    ]) {
      const card = getPrinterCardByName(page, name);
      await expect(card).toBeVisible();

      const toggle = card.getByRole('button', { name: /^(Show|Hide) camera preview$/ });
      await expect(toggle).toBeEnabled();

      await toggle.click();
      const preview = card.locator('.pf-detailed-printer-camera-preview');
      await expect(preview).toBeVisible();

      // A real local preview must not fall back to the "no camera" placeholder.
      await expect(preview.getByText('No linked camera configured')).toHaveCount(0);

      const image = preview.locator('img');
      await expect(image).toBeVisible({ timeout: 10_000 });
      const src = await image.getAttribute('src');
      expect(src, `${name} camera preview must have a real image source`).toBeTruthy();
      expect(src).toMatch(/^blob:/);
      expect(src).not.toContain('moonraker-');

      await expect.poll(
        () => image.evaluate((element: HTMLImageElement) =>
          element.complete && element.naturalWidth > 0
        ),
        { message: `${name} camera should decode a deterministic local image` }
      ).toBe(true);
    }

    await expect(
      getPrinterCardByName(page, MOONRAKER_PRINTERS.offline)
        .getByRole('button', { name: /^(Show|Hide) camera preview$/ })
    ).toBeDisabled();
  });
});

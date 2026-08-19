import { test, expect, dismissTourIfVisible } from '../fixtures/emulator-setup';

/**
 * Add Printer Modal — footer overlap / scroll regression (issue #1708).
 *
 * Root cause: Chromium intercepts a real (trusted) mouse-wheel event over a
 * *focused* `<input type="number">` and uses it to increment/decrement the
 * field's value instead of letting the event bubble to the modal's
 * scrollable content container. The Add Printer modal has several number
 * inputs (Backend Port, Frontend Port, Wattage, Machine Hourly Rate)
 * positioned above the Notes/Cost Settings fields, so a user who tries to
 * scroll while hovering one of them got stuck — the sticky footer then
 * visually appears to permanently overlap the unreachable lower fields.
 *
 * `page.mouse.wheel()` (unlike a synthetic `dispatchEvent('wheel', ...)`)
 * issues a real, OS-level trusted wheel event via the CDP input pipeline,
 * so it reproduces Chromium's native default action exactly like a real
 * mouse would — this is what actually exercises the bug (and the fix).
 *
 * Covers the exact reproduction viewports from the issue report.
 */

const VIEWPORTS = [
  { width: 1366, height: 768 },
  { width: 1026, height: 877 },
] as const;

for (const vp of VIEWPORTS) {
  test(
    `Add Printer modal: wheel-scrolling over Frontend Port does not change its value ` +
    `and the modal keeps scrolling to reveal Notes/Cost Settings at ${vp.width}x${vp.height}`,
    async ({ page }) => {
      await page.setViewportSize({ width: vp.width, height: vp.height });
      await page.goto('/printers');
      await page.waitForLoadState('networkidle');
      await dismissTourIfVisible(page);

      await page.getByRole('button', { name: 'Add Printer' }).click();

      const dialog = page.getByRole('dialog', { name: 'Add New Printer' });
      await expect(dialog).toBeVisible({ timeout: 10_000 });

      const frontendPort = dialog.locator('input[aria-label="Frontend port"]');
      await expect(frontendPort).toBeVisible();

      const initialValue = await frontendPort.inputValue();

      // Focus the number input, then scroll with the mouse positioned over
      // it — exactly the interaction from the bug report.
      await frontendPort.click();
      await expect(frontendPort).toBeFocused();
      await frontendPort.hover();
      await page.mouse.wheel(0, 300);

      // The fix (Input.tsx) blurs a focused number input on wheel before
      // Chromium's native "wheel changes value" default action can apply,
      // so the value must be unchanged and focus must have moved away.
      await expect(frontendPort).not.toBeFocused();
      expect(await frontendPort.inputValue()).toBe(initialValue);

      // Keep scrolling (mouse no longer needs to be over the number input —
      // it lost focus already) until the fields the issue said were
      // unreachable are actually in view, proving the sticky footer no
      // longer permanently overlaps them.
      const notesField = dialog.locator('textarea[aria-label="Printer notes"]');
      const costSettingsHeading = dialog.getByRole('heading', { name: 'Cost Settings' });
      await expect(async () => {
        await page.mouse.wheel(0, 400);
        await expect(notesField).toBeInViewport();
        await expect(costSettingsHeading).toBeInViewport();
      }).toPass({ timeout: 5_000 });
    }
  );
}

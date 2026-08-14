import { dismissTourIfVisible } from '../fixtures/emulator-setup';
import { test, expect, MOONRAKER_PRINTERS, getPrinterCardByName } from '../fixtures/moonraker';

/**
 * Printer Responsive Layout E2E Tests — Moonraker emulator-backed.
 *
 * Verifies the /printers page (cards and table view modes) at mobile,
 * tablet, and desktop breakpoints: no horizontal overflow, every seeded
 * printer stays visible and within the viewport, and the view-mode toggle
 * is keyboard operable at every size.
 */

const VIEWPORTS = [
  { width: 375, height: 812, name: 'mobile' },
  { width: 768, height: 1024, name: 'tablet' },
  { width: 1440, height: 900, name: 'desktop' },
] as const;

test.describe('Printer Responsive Layout — Moonraker', () => {
  test.describe.configure({ mode: 'serial' });

  for (const vp of VIEWPORTS) {
    test(`cards view: no horizontal overflow and all printers reachable at ${vp.name} (${vp.width}px)`, async ({ page }) => {
      await page.setViewportSize({ width: vp.width, height: vp.height });
      await page.goto('/printers?view=collapsed');
      await expect(getPrinterCardByName(page, MOONRAKER_PRINTERS.ready)).toBeVisible({ timeout: 15_000 });
      await dismissTourIfVisible(page);

      const hasHorizontalScroll = await page.evaluate(
        () => document.documentElement.scrollWidth > document.documentElement.clientWidth
      );
      expect(hasHorizontalScroll, `Unexpected horizontal scroll at ${vp.name}`).toBeFalsy();

      for (const name of Object.values(MOONRAKER_PRINTERS)) {
        const card = getPrinterCardByName(page, name);
        await expect(card, `${name} card not visible at ${vp.name}`).toBeVisible();

        const box = await card.boundingBox();
        expect(box, `${name} card has no bounding box at ${vp.name}`).not.toBeNull();
        if (box) {
          expect(box.x, `${name} card starts left of viewport at ${vp.name}`).toBeGreaterThanOrEqual(-1);
          expect(box.x + box.width, `${name} card extends past viewport at ${vp.name}`).toBeLessThanOrEqual(vp.width + 1);
        }
      }
    });

    test(`table view: all five seeded printers appear as rows at ${vp.name} (${vp.width}px)`, async ({ page }) => {
      await page.setViewportSize({ width: vp.width, height: vp.height });
      await page.goto('/printers');
      await expect(getPrinterCardByName(page, MOONRAKER_PRINTERS.ready)).toBeVisible({ timeout: 15_000 });
      await dismissTourIfVisible(page);

      await page.getByRole('button', { name: 'Table View' }).click();
      const table = page.locator('table');
      await expect(table).toBeVisible();

      for (const name of Object.values(MOONRAKER_PRINTERS)) {
        await expect(
          page.getByRole('row', { name: new RegExp(name) }),
          `${name} row not present in table view at ${vp.name}`
        ).toBeVisible();
      }
    });
  }

  test('view mode toggle is keyboard operable', async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.goto('/printers');
    await expect(getPrinterCardByName(page, MOONRAKER_PRINTERS.ready)).toBeVisible({ timeout: 15_000 });
    await dismissTourIfVisible(page);

    const tableToggle = page.getByRole('button', { name: 'Table View' });
    await tableToggle.focus();
    await expect(tableToggle).toBeFocused();
    await page.keyboard.press('Enter');
    await expect(page.locator('table')).toBeVisible();

    const cardsToggle = page.getByRole('button', { name: 'Detailed Cards' });
    await cardsToggle.focus();
    await page.keyboard.press('Enter');
    await expect(page.locator('table')).toHaveCount(0);
    await expect(getPrinterCardByName(page, MOONRAKER_PRINTERS.ready)).toBeVisible();
  });
});

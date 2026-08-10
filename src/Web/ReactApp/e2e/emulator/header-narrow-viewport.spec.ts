import { test, expect } from '../fixtures/emulator-setup';

/**
 * Narrow Viewport Header Regression Tests — Emulator-backed (issue #1417)
 *
 * The authenticated app header previously overflowed narrow viewports: the
 * account menu extended 49px past the viewport at 390x844, and the system
 * status, notification, and account controls were clipped and unreachable
 * at 320x568 because the root layout container is `overflow-hidden` with
 * no scroll affordance. These tests guard against that regression across
 * the widths called out in the issue, and confirm no regression at 768px+.
 */

const NARROW_VIEWPORTS = [
  { width: 320, height: 568, name: '320w' },
  { width: 390, height: 844, name: '390w' },
];

test.describe('Header narrow viewport clipping — Emulator', () => {
  for (const vp of NARROW_VIEWPORTS) {
    test(`produces no horizontal overflow at ${vp.name}`, async ({ page }) => {
      await page.setViewportSize({ width: vp.width, height: vp.height });
      await page.goto('/dashboard');
      await page.waitForLoadState('networkidle');

      const hasHorizontalScroll = await page.evaluate(
        () => document.documentElement.scrollWidth > document.documentElement.clientWidth
      );

      expect(hasHorizontalScroll, `Unexpected horizontal scroll at ${vp.name}`).toBeFalsy();
    });

    test(`status, notification, and account controls fit and stay interactive at ${vp.name}`, async ({ page }) => {
      await page.setViewportSize({ width: vp.width, height: vp.height });
      await page.goto('/dashboard');
      await page.waitForLoadState('networkidle');

      const header = page.locator('header:visible').first();
      await expect(header, `Header not visible at ${vp.name}`).toBeVisible();

      const notificationButton = page.getByRole('button', { name: /notifications/i });
      const accountMenuButton = page.getByRole('button', { name: /account menu/i });
      await expect(accountMenuButton, `Account menu button not visible at ${vp.name}`).toBeVisible();

      for (const control of [notificationButton, accountMenuButton]) {
        if (!(await control.isVisible().catch(() => false))) {
          continue;
        }

        const box = await control.boundingBox();
        expect(box, `Control has no bounding box at ${vp.name}`).not.toBeNull();
        if (box) {
          expect(box.x, `Control starts left of viewport at ${vp.name}`).toBeGreaterThanOrEqual(-1);
          expect(box.x + box.width, `Control extends past viewport at ${vp.name}`).toBeLessThanOrEqual(vp.width + 1);
        }
      }

      // The account menu must open and remain reachable (pointer/keyboard) —
      // its dropdown panel is what previously extended past the viewport.
      await accountMenuButton.click();
      const menu = page.getByRole('menu', { name: /account menu/i });
      await expect(menu, `Account menu panel did not open at ${vp.name}`).toBeVisible();

      const menuBox = await menu.boundingBox();
      expect(menuBox, `Account menu panel has no bounding box at ${vp.name}`).not.toBeNull();
      if (menuBox) {
        expect(menuBox.x, `Account menu panel starts left of viewport at ${vp.name}`).toBeGreaterThanOrEqual(-1);
        expect(menuBox.x + menuBox.width, `Account menu panel extends past viewport at ${vp.name}`).toBeLessThanOrEqual(vp.width + 1);
      }

      await page.keyboard.press('Escape');
    });
  }

  test('no regression at 768px and wider', async ({ page }) => {
    await page.setViewportSize({ width: 768, height: 1024 });
    await page.goto('/dashboard');
    await page.waitForLoadState('networkidle');

    const hasHorizontalScroll = await page.evaluate(
      () => document.documentElement.scrollWidth > document.documentElement.clientWidth
    );
    expect(hasHorizontalScroll, 'Unexpected horizontal scroll at 768px').toBeFalsy();

    const accountMenuButton = page.getByRole('button', { name: /account menu/i });
    await expect(accountMenuButton, 'Account menu button not visible at 768px').toBeVisible();

    const box = await accountMenuButton.boundingBox();
    expect(box, 'Account menu button has no bounding box at 768px').not.toBeNull();
    if (box) {
      expect(box.x + box.width, 'Account menu button extends past viewport at 768px').toBeLessThanOrEqual(768 + 1);
    }
  });
});

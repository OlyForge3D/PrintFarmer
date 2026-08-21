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

      // All three controls named in the issue's acceptance criteria must be
      // visible and fully within the viewport — none of these are optional,
      // so no assertion here is skipped based on a runtime visibility check.
      const systemStatusButton = header.getByRole('button', {
        name: /^System(?:, (?:Healthy|Degraded|Critical) health| status degraded, view system status)$/i,
      });
      const notificationButton = header.getByRole('button', {
        name: /^Notifications(?: \(\d+ unread\))?$/,
      });
      const accountMenuButton = header.getByRole('button', { name: / account menu$/i });

      for (const [label, control] of [
        ['System status', systemStatusButton],
        ['Notification bell', notificationButton],
        ['Account menu', accountMenuButton],
      ] as const) {
        await expect(control, `${label} control not visible at ${vp.name}`).toBeVisible();

        const box = await control.boundingBox();
        expect(box, `${label} control has no bounding box at ${vp.name}`).not.toBeNull();
        if (box) {
          expect(box.x, `${label} control starts left of viewport at ${vp.name}`).toBeGreaterThanOrEqual(-1);
          expect(box.x + box.width, `${label} control extends past viewport at ${vp.name}`).toBeLessThanOrEqual(vp.width + 1);
        }
      }

      // The account menu must open and remain reachable by pointer — its
      // dropdown panel is what previously extended past the viewport.
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
      await expect(menu, `Account menu panel did not close on Escape at ${vp.name}`).not.toBeVisible();

      // Keyboard reachability: the account control must actually receive
      // keyboard focus and be operable with Enter, not just clickable, per
      // the "reachable by pointer, keyboard, and touch" acceptance criterion.
      await accountMenuButton.focus();
      await expect(accountMenuButton, `Account menu control did not receive keyboard focus at ${vp.name}`).toBeFocused();
      await page.keyboard.press('Enter');
      await expect(menu, `Account menu panel did not open via keyboard at ${vp.name}`).toBeVisible();
      await page.keyboard.press('Escape');
      await expect(menu, `Account menu panel did not close via keyboard at ${vp.name}`).not.toBeVisible();
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

    const header = page.locator('header:visible');
    const systemStatusButton = header.getByRole('button', {
      name: /^System(?:, (?:Healthy|Degraded|Critical) health| status degraded, view system status)$/i,
    });
    const accountMenuButton = header.getByRole('button', { name: / account menu$/i });
    await expect(accountMenuButton, 'Account menu button not visible at 768px').toBeVisible();

    const box = await accountMenuButton.boundingBox();
    expect(box, 'Account menu button has no bounding box at 768px').not.toBeNull();
    if (box) {
      expect(box.x + box.width, 'Account menu button extends past viewport at 768px').toBeLessThanOrEqual(768 + 1);
    }

    // The mobile "compact" mode (which visually hides the "System" text
    // label) must not leak onto the wide desktop header — its label stays
    // visible here, matching pre-#1417 desktop behavior exactly.
    await expect(systemStatusButton, 'System status control not visible at 768px').toBeVisible();
    await expect(systemStatusButton.getByText('System', { exact: true })).toBeVisible();
  });
});

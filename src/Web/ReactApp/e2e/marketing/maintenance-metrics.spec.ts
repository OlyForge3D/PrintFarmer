import { expect, test } from '@playwright/test';
import { visualScenes } from './visual-scenes';
import {
  assertNoScenarioNetworkViolations,
  assertPinnedFonts,
  assertRenderingEnvironment,
  prepareVisualScene,
} from './visual-test-helpers';

const baseScene = visualScenes.find(scene =>
  scene.id === 'web.inventory.maintenance-dashboard.populated.dark.desktop');
if (!baseScene) throw new Error('Missing maintenance dashboard scenario');

for (const theme of ['light', 'dark'] as const) {
  for (const viewport of [{ width: 390, height: 844 }, { width: 1440, height: 1000 }]) {
    test(`maintenance metric captions remain readable in ${theme} at ${viewport.width}px`, async ({ browser, context, page }) => {
      const scene = { ...baseScene, theme, colorScheme: theme, viewport };
      const { violations, pageErrors } = await prepareVisualScene(context, page, scene);
      await assertRenderingEnvironment(browser, page, scene);
      await assertPinnedFonts(page);

      for (const label of ['Printers', 'Online', 'Alerts', 'Overdue', 'Due Soon', 'Needs Attention']) {
        const caption = page.getByRole('main').getByText(label, { exact: true }).first();
        await expect(caption).toBeVisible();
        await expect(caption).toBeInViewport({ ratio: 1 });
        const geometry = await caption.evaluate(element => {
          const range = document.createRange();
          range.selectNodeContents(element);
          const text = range.getBoundingClientRect();
          const bounds = element.getBoundingClientRect();
          const value = element.previousElementSibling;
          const valueBounds = value?.getBoundingClientRect();
          return {
            clipped: element.scrollWidth > element.clientWidth || element.scrollHeight > element.clientHeight,
            textFits: text.left >= bounds.left - 1 && text.right <= bounds.right + 1
              && text.top >= bounds.top - 1 && text.bottom <= bounds.bottom + 1,
            valueAbove: !!valueBounds && valueBounds.bottom <= bounds.top + 1,
            value: value?.textContent,
          };
        });
        expect(geometry, `The full ${label} caption must fit beside its associated metric, not merely exist in the DOM`).toMatchObject({
          clipped: false, textFits: true, valueAbove: true,
        });
        expect(geometry.value).toMatch(/^\d+$/);
      }

      expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBeLessThanOrEqual(1);
      assertNoScenarioNetworkViolations(violations, pageErrors);
    });
  }
}

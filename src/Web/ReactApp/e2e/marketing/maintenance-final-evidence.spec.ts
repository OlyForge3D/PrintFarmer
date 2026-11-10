import { expect, test } from '@playwright/test';
import { printers, captureTime } from './fixtures';
import { MaintenanceAlertStatus } from '../../src/types/maintenance';
import { visualScenes } from './visual-scenes';
import {
  assertAccessibility,
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
    test(`maintenance evidence ${theme} ${viewport.width}px`, async ({ browser, context, page }, testInfo) => {
      await page.route(/\/api\/printers(?:\/summary)?(?:\?|$)/, route => route.fulfill({
        json: printers.map(printer => ({ ...printer, inMaintenance: printer.id === 'demo-1' })),
      }));
      await page.route(/\/api\/maintenance\/alerts(?:\?|$)/, route => route.fulfill({
        json: [{
          id: 'evidence-alert', printerId: 'demo-1', title: 'Inspect nozzle',
          message: 'Synthetic maintenance evidence.', severity: 4, status: MaintenanceAlertStatus.Active,
          printerHoursAtTrigger: 120, createdAt: captureTime, updatedAt: captureTime,
        }],
      }));
      const scene = { ...baseScene, theme, colorScheme: theme, viewport };
      const { violations, pageErrors } = await prepareVisualScene(context, page, scene);
      await assertRenderingEnvironment(browser, page, scene);
      await assertPinnedFonts(page);
      const card = page.getByRole('button', { name: 'Atlas Online In Maintenance 1 alert Critical alerts: 1', exact: true });
      await expect(card).toBeVisible();
      await assertAccessibility(page);
      await page.screenshot({ path: testInfo.outputPath(`maintenance-${theme}-${viewport.width}-page.png`), fullPage: true });
      await card.scrollIntoViewIfNeeded();
      await card.screenshot({ path: testInfo.outputPath(`maintenance-${theme}-${viewport.width}-card.png`) });
      assertNoScenarioNetworkViolations(violations, pageErrors);
    });
  }
}

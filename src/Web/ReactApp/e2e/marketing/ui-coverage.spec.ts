import { test } from '@playwright/test';
import { readFile } from 'node:fs/promises';
import { expect } from '@playwright/test';
import { captureOrigin } from './fixtures';
import { assertAccessibility, assertNoScenarioNetworkViolations, assertPinnedFonts, assertRenderingEnvironment, prepareVisualScene, saveCandidate } from './visual-test-helpers';
import { visualScenes } from './visual-scenes';

const catalog = JSON.parse(await readFile(new URL('../ui-coverage/catalog.json', import.meta.url), 'utf8'));
const catalogScenarioIds = [...new Set(catalog.entries
  .filter((entry: { disposition: string }) => entry.disposition === 'candidate')
  .flatMap((entry: { scenarioIds?: string[] }) => entry.scenarioIds ?? []))].sort();
const executableScenarioIds = visualScenes.map(scene => scene.id).sort();

test('executable Playwright scenarios exactly match catalog candidates', () => {
  expect(executableScenarioIds).toEqual(catalogScenarioIds);
});

for (const scene of visualScenes) {
  test(`candidate ${scene.id} (not a golden)`, async ({ page, context, browser }, testInfo) => {
    testInfo.annotations.push({ type: 'scenario-id', description: scene.id });
    const sceneContext = scene.coarsePointer
      ? await browser.newContext({
          baseURL: captureOrigin,
          viewport: scene.viewport,
          deviceScaleFactor: 1,
          locale: 'en-US',
          timezoneId: 'UTC',
          colorScheme: scene.colorScheme,
          reducedMotion: 'reduce',
          hasTouch: true,
          isMobile: true,
          serviceWorkers: 'block',
        })
      : context;
    const scenePage = scene.coarsePointer ? await sceneContext.newPage() : page;
    try {
      const { violations, pageErrors, releasePendingRequest } = await prepareVisualScene(sceneContext, scenePage, scene);
      await assertRenderingEnvironment(browser, scenePage, scene);
      if (scene.surface === 'app') await assertPinnedFonts(scenePage);
      if (scene.reflowEquivalentZoom === 2) {
        const horizontalOverflow = await scenePage.evaluate(() =>
          Math.max(document.documentElement.scrollWidth, document.body.scrollWidth) - document.documentElement.clientWidth,
        );
        expect(horizontalOverflow, 'The 200%-equivalent CSS viewport must not introduce horizontal overflow').toBeLessThanOrEqual(1);
      }
      await saveCandidate(scenePage, testInfo, scene.id);
      await assertAccessibility(scenePage);
      if (scene.routeChunkFailure === 'once') {
        const reload = scenePage.waitForNavigation();
        await scenePage.getByRole('button', { name: 'Reload page', exact: true }).click();
        await reload;
        await expect(scenePage.getByRole('heading', { name: 'Workers & Jobs', exact: true })).toBeVisible();
      }
      if (scene.routeChunkLoading || scene.slicerCapabilityState === 'loading') {
        if (typeof releasePendingRequest !== 'function') {
          throw new Error('Deferred scenarios expose a stable release callback before the request is released.');
        }
        const deferredPath = scene.routeChunkLoading
          ? '/src/features/slicer/pages/WorkerManagementPage.tsx'
          : '/api/system/capabilities';
        const deferredResponse = scenePage.waitForResponse(response => new URL(response.url()).pathname === deferredPath);
        releasePendingRequest();
        const response = await deferredResponse;
        expect(response.ok(), `Deferred fixture response for ${deferredPath} succeeds`).toBe(true);
        if (scene.routeChunkLoading) {
          await expect(scenePage.getByRole('heading', { name: 'Workers & Jobs', exact: true })).toBeVisible();
        }
        if (scene.slicerCapabilityState === 'loading') {
          await expect(scenePage.getByRole('heading', { name: 'Get started with slicing', exact: true })).toBeVisible();
        }
      }
      assertNoScenarioNetworkViolations(violations, pageErrors);

      if (scene.surface === 'site') await scenePage.locator('main').waitFor();
    } finally {
      if (scene.coarsePointer) await sceneContext.close();
    }
  });
}

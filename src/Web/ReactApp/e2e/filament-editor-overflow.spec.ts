import { expect, test } from '@playwright/test';
import path from 'node:path';

for (const width of [1440, 390]) {
  test(`filament temperature rows fit at ${width}px`, async ({ page }) => {
    await page.setViewportSize({ width, height: 900 });
    await page.route('**/src/main.tsx', route => route.fulfill({
      contentType: 'application/javascript',
      body: "import '/src/index.css';",
    }));
    await page.route(url => url.pathname.startsWith('/api/'), route => route.abort());
    await page.goto('/');
    await page.evaluate(() => {
      localStorage.setItem('printfarmer-slicer-viewmode', 'advanced');
      document.body.innerHTML = '<div id="filament-editor-root"></div>';
    });
    await page.evaluate(async () => {
      const harness = '/e2e/fixtures/filament-editor-harness.jsx';
      const { mountFilamentEditor } = await import(/* @vite-ignore */ harness);
      mountFilamentEditor();
    });
    const firstLayer = page.getByTitle('Cool Plate first layer', { exact: true });
    await expect(firstLayer).toBeVisible();
    await firstLayer.scrollIntoViewIfNeeded();
    const panel = page.getByRole('tabpanel', { name: 'Filament' });
    if (process.env.FILAMENT_SCREENSHOT_DIR && process.env.FILAMENT_SCREENSHOT_STAGE) {
      await page.screenshot({
        path: path.join(process.env.FILAMENT_SCREENSHOT_DIR, `${process.env.FILAMENT_SCREENSHOT_STAGE}-${width}.png`),
      });
    }
    const dimensions = await panel.evaluate(element => ({
      clientWidth: element.clientWidth,
      scrollWidth: element.scrollWidth,
    }));
    expect(dimensions.scrollWidth).toBeLessThanOrEqual(dimensions.clientWidth);
    for (const layer of ['first', 'other']) {
      const input = page.getByTitle(`Cool Plate ${layer} layer${layer === 'other' ? 's' : ''}`, { exact: true });
      const box = await input.boundingBox();
      const panelBox = await panel.boundingBox();
      expect(box).not.toBeNull();
      expect(panelBox).not.toBeNull();
      expect(box!.width).toBeGreaterThanOrEqual(36);
      expect(box!.x).toBeGreaterThanOrEqual(panelBox!.x);
      expect(box!.x + box!.width).toBeLessThanOrEqual(panelBox!.x + panelBox!.width);
    }
  });
}

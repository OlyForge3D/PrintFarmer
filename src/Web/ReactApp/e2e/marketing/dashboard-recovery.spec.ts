import { expect, test } from '@playwright/test';
import { captureTime, isolateCapture, printers } from './fixtures';

for (const viewport of [
  { width: 1440, height: 1100 },
  { width: 1024, height: 1000 },
  { width: 768, height: 1000 },
  { width: 390, height: 844 },
]) {
  test(`dashboard grid and refresh dismissals at ${viewport.width}px`, async ({ page, context }, testInfo) => {
    const errors: string[] = [];
    let revision = captureTime;
    page.on('pageerror', error => errors.push(error.message));
    const isolation = await isolateCapture(context, { dashboard: true });
    await context.route('**/api/printers/summary', route => route.fulfill({ json: printers.map((printer, index) => ({
      ...printer, hasCatalogUpdate: index === 0, catalogUpdateRevision: revision,
    })) }));
    await context.route('**/api/maintenance/alerts', route => route.fulfill({ json: [{
      id: 'fixture-maintenance', printerId: printers[0].id, severity: 2, status: 'Active',
      title: 'Nozzle inspection overdue', message: 'Inspect the nozzle before the next long print.',
      printerHoursAtTrigger: 120, createdAt: captureTime, updatedAt: captureTime,
    }] }));
    await context.route(/\/api\/tasks(?:\?|$)/, route => route.fulfill({ json: {
      generatedAt: captureTime, groups: [{ anchorKind: 'anytimeToday', tasks: [
        ['FlashForge Flashforge AD5X', 'fixture-ad5x'],
        ['Snapmaker Snapmaker U1', 'fixture-u1'],
        ['Prusa Prusa CORE One L', 'fixture-core'],
      ].map(([model, id]) => ({
        id, taskType: 'ProfileImport', entityType: 'PrinterModel', entityId: id,
        title: `Import slicer profiles for ${model}`,
        description: '1 printer waiting for slicer profiles', status: 'Pending',
        priority: 'High', createdAt: captureTime, relatedEntityCount: 1,
        anchorKind: 'anytimeToday', sourceKind: 'unspecified',
      })) }],
    } }));
    await page.setViewportSize(viewport);
    await page.emulateMedia({ colorScheme: 'dark', reducedMotion: 'reduce' });
    await page.clock.setFixedTime(new Date(captureTime));
    await page.goto('/dashboard');
    await page.addScriptTag({ url: '/e2e/marketing/ui-coverage-fonts.ts', type: 'module' });
    await page.evaluate(() => document.fonts.ready);
    await expect(page.getByRole('heading', { name: 'Printer Dashboard', exact: true })).toBeVisible();
    await expect(page.getByText('Import slicer profiles for FlashForge Flashforge AD5X')).toBeVisible();
    await expect(page.getByText(/1 printer has a configuration update/)).toBeVisible();
    await expect(page.getByText(/1 overdue maintenance/)).toBeVisible();
    await expect(page.getByText('Profile import tasks mean missing imported profiles, not profile updates.')).toBeVisible();
    const grid = page.getByTestId('dashboard-widgets');
    await expect(grid.locator(':scope > *')).toHaveCount(6);
    const columns = viewport.width >= 1024 ? 3 : viewport.width >= 768 ? 2 : 1;
    expect(await grid.evaluate(element => getComputedStyle(element).gridTemplateColumns.split(' ').length)).toBe(columns);
    const cards = await grid.locator(':scope > *').evaluateAll(elements => elements.map(element => ({
      x: element.getBoundingClientRect().x,
      y: element.getBoundingClientRect().y,
      width: element.clientWidth,
      contentWidth: element.scrollWidth,
    })));
    for (const [index, card] of cards.entries()) {
      expect(card.contentWidth, `card ${index} should not overflow`).toBeLessThanOrEqual(card.width + 1);
      expect(Math.abs(card.x - cards[index % columns].x)).toBeLessThan(1);
      expect(Math.abs(card.y - cards[Math.floor(index / columns) * columns].y)).toBeLessThan(1);
    }
    const taskRows = page.getByTestId('tasks-widget-row');
    for (const row of await taskRows.all()) {
      const title = row.getByRole('heading', { level: 4 });
      await title.scrollIntoViewIfNeeded();
      await expect(title).toBeInViewport();
      const titleSize = await title.evaluate(element => ({
        width: element.clientWidth, contentWidth: element.scrollWidth,
        height: element.clientHeight, contentHeight: element.scrollHeight,
      }));
      expect(titleSize.contentWidth, await title.textContent() ?? 'task title').toBeLessThanOrEqual(titleSize.width);
      expect(titleSize.contentHeight).toBeLessThanOrEqual(titleSize.height);
      await expect(row.getByTestId('tasks-widget-dismiss')).toHaveAccessibleDescription(
        'Dismissing an import task hides it farm-wide for this model/printer group, not just for you.',
      );
      const bounds = await row.evaluate(element => {
        const rowBounds = element.getBoundingClientRect();
        return [...element.querySelectorAll('button')].map(button => ({
          left: button.getBoundingClientRect().left - rowBounds.left,
          right: button.getBoundingClientRect().right - rowBounds.left,
          rowWidth: rowBounds.width,
        }));
      });
      for (const button of bounds) {
        expect(button.left).toBeGreaterThanOrEqual(0);
        expect(button.right).toBeLessThanOrEqual(button.rowWidth + 1);
      }
    }
    await grid.evaluate(element => {
      for (const descendant of element.querySelectorAll<HTMLElement>('*')) {
        descendant.scrollTop = 0;
      }
      for (let ancestor: Element | null = element; ancestor; ancestor = ancestor.parentElement) {
        ancestor.scrollTop = 0;
      }
    });
    await page.screenshot({
      path: testInfo.outputPath(`after-${viewport.width}.png`),
      fullPage: true,
    });
    await page.locator('[data-tour="tasks-widget"]').evaluate(element => element.scrollIntoView({ block: 'start' }));
    await page.screenshot({ path: testInfo.outputPath(`after-widgets-${viewport.width}.png`), fullPage: true });

    const taskDismiss = taskRows.first().getByTestId('tasks-widget-dismiss');
    await taskDismiss.focus();
    await expect(taskDismiss.locator('..')).toHaveCSS('opacity', '1');
    await taskDismiss.click({ trial: true });

    const maintenanceDismiss = page.getByRole('button', { name: /Dismiss 1 overdue/ });
    await maintenanceDismiss.focus();
    await page.keyboard.press('Enter');
    await page.getByRole('button', { name: /Dismiss 1 printer/ }).click();
    await page.reload();
    await expect(page.getByText('Import slicer profiles for FlashForge Flashforge AD5X')).toBeVisible();
    await expect(page.getByText(/overdue maintenance task/)).toHaveCount(0);
    await expect(page.getByText(/configuration update available/)).toHaveCount(0);

    revision = '2026-11-07T12:00:00Z';
    await page.reload();
    await expect(page.getByText(/configuration update available/)).toBeVisible();
    await expect(page.getByText(/overdue maintenance task/)).toHaveCount(0);
    expect(errors).toEqual([]);
    expect(isolation.violations).toEqual([]);
  });
}

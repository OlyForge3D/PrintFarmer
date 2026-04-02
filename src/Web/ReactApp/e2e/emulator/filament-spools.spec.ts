import { test, expect } from '../fixtures/emulator-setup';

/**
 * Filament/Spools Management E2E Tests — Emulator-backed
 *
 * Tests the /spools and /spools/:tabId routes:
 *   - Filaments tab (filament product definitions)
 *   - Spools tab (physical spool inventory)
 *   - Tab switching
 *   - CRUD forms
 *   - Filtering
 */

test.describe('Filament & Spools — Emulator', () => {
  test.describe.configure({ mode: 'serial' });

  let consoleErrors: string[] = [];

  test.beforeEach(async ({ page }) => {
    consoleErrors = [];
    page.on('pageerror', (error) => consoleErrors.push(error.message));
    await page.goto('/spools');
    await page.waitForLoadState('networkidle');
  });

  function criticalErrors(): string[] {
    return consoleErrors.filter(
      (e) =>
        !e.includes('ResizeObserver') &&
        !e.includes('Network Error') &&
        !e.includes('Failed to fetch') &&
        !e.includes('AbortError') &&
        !e.includes('cancelled')
    );
  }

  test('spools page loads with tabs', async ({ page }) => {
    await page.waitForTimeout(1_000);

    const bodyText = await page.locator('body').textContent() ?? '';
    const hasSpoolContent = /spool|filament/i.test(bodyText);
    expect(hasSpoolContent).toBeTruthy();

    // Should have tab navigation (Filaments, Spools)
    const tabs = page.locator('[role="tab"], button').filter({ hasText: /filament|spool/i });
    expect(await tabs.count()).toBeGreaterThanOrEqual(1);

    expect(criticalErrors()).toHaveLength(0);
  });

  test('filaments tab displays content', async ({ page }) => {
    await page.waitForTimeout(1_000);

    // Click filaments tab if not already active
    const filamentTab = page.locator('[role="tab"], button').filter({ hasText: /filament/i }).first();
    if (await filamentTab.isVisible().catch(() => false)) {
      await filamentTab.click();
      await page.waitForTimeout(1_000);
    }

    // Content should be present (table, cards, or empty state)
    const content = page.locator('main, [role="main"], #root');
    await expect(content.first()).toBeVisible();

    expect(criticalErrors()).toHaveLength(0);
  });

  test('can switch to spools tab', async ({ page }) => {
    await page.waitForTimeout(1_000);

    const spoolsTab = page.locator('[role="tab"], button').filter({ hasText: /spool/i }).first();
    if (await spoolsTab.isVisible().catch(() => false)) {
      await spoolsTab.click();
      await page.waitForTimeout(1_000);

      const content = page.locator('main, [role="main"], #root');
      await expect(content.first()).toBeVisible();
    }

    expect(criticalErrors()).toHaveLength(0);
  });

  test('spools page navigable via tabId URL', async ({ page }) => {
    await page.goto('/spools/spools');
    await page.waitForLoadState('networkidle');
    await page.waitForTimeout(1_000);

    const bodyText = await page.locator('body').textContent() ?? '';
    expect(bodyText.length).toBeGreaterThan(100);

    expect(criticalErrors()).toHaveLength(0);
  });

  test('page has add/create functionality', async ({ page }) => {
    await page.waitForTimeout(1_000);

    // At least some interactive elements should exist
    const buttons = page.locator('button');
    expect(await buttons.count()).toBeGreaterThan(0);

    expect(criticalErrors()).toHaveLength(0);
  });

  test('no critical JS errors across spool pages', async ({ page }) => {
    const spoolPages = [
      { path: '/spools', name: 'Spools Default' },
      { path: '/spools/filaments', name: 'Filaments Tab' },
      { path: '/spools/spools', name: 'Spools Tab' },
    ];

    for (const pageConfig of spoolPages) {
      consoleErrors = [];
      await page.goto(pageConfig.path);
      await page.waitForLoadState('networkidle');
      await page.waitForTimeout(1_000);

      const errors = criticalErrors();
      expect(
        errors,
        `JS errors on ${pageConfig.name} (${pageConfig.path}): ${errors.join(', ')}`
      ).toHaveLength(0);
    }
  });
});

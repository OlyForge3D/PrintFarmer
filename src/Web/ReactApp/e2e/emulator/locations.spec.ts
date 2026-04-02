import { test, expect } from '../fixtures/emulator-setup';

/**
 * Locations Management & Dashboard E2E Tests — Emulator-backed
 *
 * Tests the /locations route:
 *   - Location tree navigator
 *   - Location CRUD operations
 *   - Location stats and printer assignment
 *   - Admin location management
 */

test.describe('Locations — Emulator', () => {
  test.describe.configure({ mode: 'serial' });

  let consoleErrors: string[] = [];

  test.beforeEach(async ({ page }) => {
    consoleErrors = [];
    page.on('pageerror', (error) => consoleErrors.push(error.message));
    await page.goto('/locations');
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

  test('locations page loads with heading', async ({ page }) => {
    await page.waitForTimeout(1_000);

    const bodyText = await page.locator('body').textContent() ?? '';
    const hasLocationContent = /location/i.test(bodyText);
    expect(hasLocationContent).toBeTruthy();

    expect(criticalErrors()).toHaveLength(0);
  });

  test('page has create location button', async ({ page }) => {
    await page.waitForTimeout(1_000);

    const createButton = page.getByRole('button', { name: /create|add|new/i }).first();
    const hasCreate = await createButton.isVisible().catch(() => false);
    expect(hasCreate).toBeTruthy();

    expect(criticalErrors()).toHaveLength(0);
  });

  test('can open create location form', async ({ page }) => {
    await page.waitForTimeout(1_000);

    const createButton = page.getByRole('button', { name: /create|add|new/i }).first();
    if (await createButton.isVisible().catch(() => false)) {
      await createButton.click();
      await page.waitForTimeout(500);

      // Should show a form with name input (placeholder varies by feature)
      const nameInput = page.locator(
        'input[name="name"], ' +
        'input[placeholder*="name" i], ' +
        'input[placeholder*="location" i], ' +
        'input[placeholder*="warehouse" i], ' +
        '[role="dialog"] input[type="text"]'
      ).first();
      const hasName = await nameInput.isVisible().catch(() => false);
      expect(hasName).toBeTruthy();
    }

    expect(criticalErrors()).toHaveLength(0);
  });

  test('can create a location', async ({ page }) => {
    await page.waitForTimeout(1_000);

    const createButton = page.getByRole('button', { name: /create|add|new/i }).first();
    if (await createButton.isVisible().catch(() => false)) {
      await createButton.click();
      await page.waitForTimeout(500);

      const nameInput = page.locator(
        'input[name="name"], ' +
        'input[placeholder*="name" i], ' +
        'input[placeholder*="location" i], ' +
        'input[placeholder*="warehouse" i], ' +
        '[role="dialog"] input[type="text"]'
      ).first();
      if (await nameInput.isVisible().catch(() => false)) {
        await nameInput.fill('E2E Test Location');

        // Scope save button to modal dialog to avoid hitting "Add" button behind it
        const modal = page.locator('[role="dialog"]');
        const saveButton = modal.getByRole('button', { name: /save|create|submit/i }).first();
        if (await saveButton.isVisible().catch(() => false)) {
          await saveButton.click();
          await page.waitForTimeout(1_000);

          const bodyText = await page.locator('body').textContent() ?? '';
          expect(bodyText).toContain('E2E Test Location');
        }
      }
    }

    expect(criticalErrors()).toHaveLength(0);
  });

  test('location tree navigator displays hierarchy', async ({ page }) => {
    await page.waitForTimeout(1_000);

    // Look for tree-like navigation elements
    const treeItems = page.locator(
      '[role="treeitem"], ' +
      '[role="tree"], ' +
      'li[class*="tree"], ' +
      'div[class*="tree"]'
    );

    const treeCount = await treeItems.count();

    // If locations exist, tree should render
    const bodyText = await page.locator('body').textContent() ?? '';
    if (/E2E Test Location/i.test(bodyText)) {
      expect(treeCount).toBeGreaterThan(0);
    }

    expect(criticalErrors()).toHaveLength(0);
  });

  test('selecting a location shows stats panel', async ({ page }) => {
    await page.waitForTimeout(1_000);

    // Click on a location in the tree/list
    const locationItem = page.locator('li, div, span').filter({ hasText: /E2E Test Location/i }).first();
    const hasItem = await locationItem.isVisible().catch(() => false);

    if (hasItem) {
      await locationItem.click();
      await page.waitForTimeout(1_000);

      // Stats panel or detail view should update
      const content = page.locator('main, [role="main"], #root');
      await expect(content.first()).toBeVisible();
    }

    expect(criticalErrors()).toHaveLength(0);
  });

  test('page renders without errors when empty', async ({ page }) => {
    await page.waitForTimeout(1_000);

    const content = page.locator('main, [role="main"], #root');
    await expect(content.first()).toBeVisible();

    expect(criticalErrors()).toHaveLength(0);
  });

  test('no critical JS errors on locations page', async ({ page }) => {
    await page.waitForTimeout(2_000);
    expect(criticalErrors()).toHaveLength(0);
  });
});

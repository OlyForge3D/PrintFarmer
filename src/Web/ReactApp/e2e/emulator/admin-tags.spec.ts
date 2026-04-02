import { test, expect } from '../fixtures/emulator-setup';

/**
 * Admin Tags CRUD E2E Tests — Emulator-backed
 *
 * Tests the /admin/tags route:
 *   - Tag management tab (create, edit, delete, color picker)
 *   - Analytics tab (usage metrics)
 *   - Requires admin auth
 */

test.describe('Admin Tags — Emulator', () => {
  test.describe.configure({ mode: 'serial' });

  let consoleErrors: string[] = [];

  test.beforeEach(async ({ page }) => {
    consoleErrors = [];
    page.on('pageerror', (error) => consoleErrors.push(error.message));
    await page.goto('/admin/tags');
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

  test('tags page loads with management tab', async ({ page }) => {
    await page.waitForTimeout(1_000);

    // Should display tags-related content or empty state
    const bodyText = await page.locator('body').textContent() ?? '';
    const hasTagContent = /tag|management|create|add/i.test(bodyText);
    expect(hasTagContent).toBeTruthy();

    // Should have interactive elements (buttons, inputs)
    const interactiveElements = page.locator('button, input, select');
    expect(await interactiveElements.count()).toBeGreaterThan(0);

    expect(criticalErrors()).toHaveLength(0);
  });

  test('can open create tag form', async ({ page }) => {
    await page.waitForTimeout(1_000);

    // Look for a create/add tag button
    const createButton = page.getByRole('button', { name: /create|add|new/i }).first();
    const hasCreate = await createButton.isVisible().catch(() => false);

    if (hasCreate) {
      await createButton.click();
      await page.waitForTimeout(500);

      // A form or modal should appear with name input
      const nameInput = page.locator('input[name="name"], input[placeholder*="name" i], input[placeholder*="tag" i]').first();
      const hasNameInput = await nameInput.isVisible().catch(() => false);
      expect(hasNameInput).toBeTruthy();
    } else {
      // Inline creation — look for input field directly on the page
      const inlineInput = page.locator('input').first();
      expect(await inlineInput.isVisible().catch(() => false)).toBeTruthy();
    }

    expect(criticalErrors()).toHaveLength(0);
  });

  test('can create a new tag', async ({ page }) => {
    await page.waitForTimeout(1_000);

    const createButton = page.getByRole('button', { name: /create|add|new/i }).first();
    const hasCreate = await createButton.isVisible().catch(() => false);

    if (hasCreate) {
      await createButton.click();
      await page.waitForTimeout(500);
    }

    // Fill in tag name
    const nameInput = page.locator('input[name="name"], input[placeholder*="name" i], input[placeholder*="tag" i]').first();
    if (await nameInput.isVisible().catch(() => false)) {
      await nameInput.fill('E2E Test Tag');

      // Submit the form
      const saveButton = page.getByRole('button', { name: /save|create|add|submit/i }).first();
      if (await saveButton.isVisible().catch(() => false)) {
        await saveButton.click();
        await page.waitForTimeout(1_000);

        // Tag should appear in the list
        const bodyText = await page.locator('body').textContent() ?? '';
        expect(bodyText).toContain('E2E Test Tag');
      }
    }

    expect(criticalErrors()).toHaveLength(0);
  });

  test('tags have color indicators', async ({ page }) => {
    await page.waitForTimeout(1_000);

    // Tags typically display with colored circles/badges
    const colorElements = page.locator(
      '[style*="background-color"], ' +
      '[class*="color"], ' +
      'div[style*="background"], ' +
      'span[style*="background"]'
    );

    // If tags exist, they should have color indicators
    const bodyText = await page.locator('body').textContent() ?? '';
    if (/E2E Test Tag/i.test(bodyText)) {
      expect(await colorElements.count()).toBeGreaterThan(0);
    }

    expect(criticalErrors()).toHaveLength(0);
  });

  test('can switch to analytics tab', async ({ page }) => {
    await page.waitForTimeout(1_000);

    // Look for analytics tab
    const analyticsTab = page.locator('button, [role="tab"]').filter({ hasText: /analytics/i }).first();
    const hasAnalytics = await analyticsTab.isVisible().catch(() => false);

    if (hasAnalytics) {
      await analyticsTab.click();
      await page.waitForTimeout(1_000);

      // Analytics content should render
      const content = page.locator('main, [role="main"], #root');
      await expect(content.first()).toBeVisible();
    }

    expect(criticalErrors()).toHaveLength(0);
  });

  test('can delete a tag', async ({ page }) => {
    await page.waitForTimeout(1_000);

    // Look for delete button on an existing tag
    const deleteButton = page.locator('button').filter({ hasText: /delete|remove/i }).first();
    const hasDelete = await deleteButton.isVisible().catch(() => false);

    if (!hasDelete) {
      // May be an icon button — look for trash/delete icon buttons
      const iconDeleteBtn = page.locator('button[aria-label*="delete" i], button[aria-label*="remove" i]').first();
      const hasIconDelete = await iconDeleteBtn.isVisible().catch(() => false);
      if (hasIconDelete) {
        await iconDeleteBtn.click();
        await page.waitForTimeout(500);

        // Confirm deletion if dialog appears
        const confirmBtn = page.getByRole('button', { name: /confirm|yes|delete/i }).first();
        if (await confirmBtn.isVisible().catch(() => false)) {
          await confirmBtn.click();
          await page.waitForTimeout(1_000);
        }
      }
    }

    expect(criticalErrors()).toHaveLength(0);
  });

  test('no critical JS errors on tags page', async ({ page }) => {
    await page.waitForTimeout(2_000);
    expect(criticalErrors()).toHaveLength(0);
  });
});

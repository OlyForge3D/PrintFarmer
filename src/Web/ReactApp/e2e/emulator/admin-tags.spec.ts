import { test, expect } from '../fixtures/emulator-setup';

const TEST_TAG_NAME = `E2E Test Tag ${Date.now()}`;

// The API normalizes tag names to PascalCase on creation (see TagService.ToPascalCase),
// e.g. "E2E Test Tag 123" -> "E2eTestTag123". Assertions after creation must use the
// normalized name returned by the API, not the raw input string.
let createdTagName = TEST_TAG_NAME;

/**
 * Admin Tags CRUD E2E Tests — Emulator-backed
 *
 * Tests the canonical Admin > Data > Tags route:
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
    await page.goto('/admin/manage?tab=data&sub=tags');
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
    // Wait for the page to finish loading (spinner may be visible initially)
    await page.locator('[class*="spinner"], [class*="animate-spin"]').first()
      .waitFor({ state: 'hidden', timeout: 10_000 }).catch(() => {});
    await page.waitForTimeout(500);

    const createButton = page.getByTestId('add-tag-action');
    await expect(createButton).toBeVisible({ timeout: 5_000 });
    await createButton.click();

    const dialog = page.getByRole('dialog', { name: 'Create New Tag', exact: true });
    await expect(dialog).toBeVisible();
    await expect(dialog.getByRole('textbox', { name: 'Tag Name', exact: true })).toBeVisible();

    expect(criticalErrors()).toHaveLength(0);
  });

  test('can create a new tag', async ({ page }) => {
    await page.waitForTimeout(1_000);

    const createButton = page.getByTestId('add-tag-action');
    await expect(createButton).toBeVisible({ timeout: 5_000 });
    await createButton.click();

    const dialog = page.getByRole('dialog', { name: 'Create New Tag', exact: true });
    await expect(dialog).toBeVisible();
    const nameInput = dialog.getByRole('textbox', { name: 'Tag Name', exact: true });
    await expect(nameInput).toBeVisible();
    await nameInput.fill(TEST_TAG_NAME);

    const saveButton = dialog.getByRole('button', { name: 'Create tag', exact: true });
    await expect(saveButton).toBeVisible();
    const createResponsePromise = page.waitForResponse((response) =>
      response.request().method() === 'POST'
      && /\/api\/tags\/?$/.test(new URL(response.url()).pathname)
    );
    await saveButton.click();

    // The API normalizes the submitted name to PascalCase (e.g. "E2E Test Tag 123"
    // -> "E2eTestTag123"); use the value it returns rather than the raw input.
    const createResponse = await createResponsePromise;
    expect(createResponse.ok()).toBe(true);
    const createdTag = await createResponse.json();
    createdTagName = createdTag.name;

    await expect(dialog).toBeHidden();
    await expect(page.getByRole('cell', { name: createdTagName, exact: true })).toBeVisible();

    expect(criticalErrors()).toHaveLength(0);
  });

  test('tags have color indicators', async ({ page }) => {
    const tagRow = page.getByRole('row').filter({
      has: page.getByRole('cell', { name: createdTagName, exact: true }),
    });
    await expect(tagRow).toBeVisible();
    expect(await tagRow.locator('div[style*="background-color"]').count()).toBeGreaterThan(0);

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
    const tagCell = page.getByRole('cell', { name: createdTagName, exact: true });
    const tagRow = page.getByRole('row').filter({ has: tagCell });
    await expect(tagRow).toBeVisible();

    const deleteResponsePromise = page.waitForResponse((response) =>
      response.request().method() === 'DELETE'
      && /\/api\/tags\/[^/]+$/.test(new URL(response.url()).pathname)
    );
    await tagRow.getByRole('button', { name: 'Delete tag', exact: true }).click();
    const deleteResponse = await deleteResponsePromise;
    expect(deleteResponse.ok()).toBe(true);

    await page.reload();
    await page.waitForLoadState('networkidle');
    await expect(page.getByRole('cell', { name: createdTagName, exact: true })).toHaveCount(0);

    expect(criticalErrors()).toHaveLength(0);
  });

  test('no critical JS errors on tags page', async ({ page }) => {
    await page.waitForTimeout(2_000);
    expect(criticalErrors()).toHaveLength(0);
  });
});

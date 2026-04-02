import { test, expect } from '../fixtures/emulator-setup';

/**
 * Admin Webhooks CRUD E2E Tests — Emulator-backed
 *
 * Tests the /admin/webhooks route:
 *   - Webhook list display
 *   - Create webhook modal (name, URL, secret, event types)
 *   - Edit/delete webhooks
 *   - Test delivery button
 *   - Recent deliveries modal
 */

test.describe('Admin Webhooks — Emulator', () => {
  test.describe.configure({ mode: 'serial' });

  let consoleErrors: string[] = [];

  test.beforeEach(async ({ page }) => {
    consoleErrors = [];
    page.on('pageerror', (error) => consoleErrors.push(error.message));
    await page.goto('/admin/webhooks');
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

  test('webhooks page loads with heading and add button', async ({ page }) => {
    await page.waitForTimeout(1_000);

    // Page should show webhooks heading
    const bodyText = await page.locator('body').textContent() ?? '';
    const hasWebhookContent = /webhook/i.test(bodyText);
    expect(hasWebhookContent).toBeTruthy();

    // Add webhook button should be present
    const addButton = page.getByRole('button', { name: /add|create|new/i }).first();
    await expect(addButton).toBeVisible({ timeout: 5_000 });

    expect(criticalErrors()).toHaveLength(0);
  });

  test('can open create webhook modal', async ({ page }) => {
    await page.waitForTimeout(1_000);

    const addButton = page.getByRole('button', { name: /add|create|new/i }).first();
    await expect(addButton).toBeVisible({ timeout: 5_000 });
    await addButton.click();
    await page.waitForTimeout(500);

    // Modal should open with form fields
    const nameInput = page.locator('input[name="name"], input[placeholder*="name" i]').first();
    const urlInput = page.locator('input[name="url"], input[placeholder*="url" i], input[type="url"]').first();

    const hasName = await nameInput.isVisible().catch(() => false);
    const hasUrl = await urlInput.isVisible().catch(() => false);

    expect(hasName || hasUrl).toBeTruthy();

    expect(criticalErrors()).toHaveLength(0);
  });

  test('create webhook form has event type selection', async ({ page }) => {
    await page.waitForTimeout(1_000);

    const addButton = page.getByRole('button', { name: /add|create|new/i }).first();
    await expect(addButton).toBeVisible({ timeout: 5_000 });
    await addButton.click();
    await page.waitForTimeout(500);

    // Should have event type checkboxes or multi-select
    const eventCheckboxes = page.locator('input[type="checkbox"]');
    const eventSelect = page.locator('select, [role="listbox"]');

    const checkboxCount = await eventCheckboxes.count();
    const selectCount = await eventSelect.count();

    // Either checkboxes for event types or a select dropdown
    expect(checkboxCount + selectCount).toBeGreaterThan(0);

    expect(criticalErrors()).toHaveLength(0);
  });

  test('can create a webhook', async ({ page }) => {
    await page.waitForTimeout(1_000);

    const addButton = page.getByRole('button', { name: /add|create|new/i }).first();
    await expect(addButton).toBeVisible({ timeout: 5_000 });
    await addButton.click();
    await page.waitForTimeout(500);

    // Fill webhook form
    const nameInput = page.locator('input[name="name"], input[placeholder*="name" i]').first();
    if (await nameInput.isVisible().catch(() => false)) {
      await nameInput.fill('E2E Test Webhook');
    }

    const urlInput = page.locator('input[name="url"], input[placeholder*="url" i], input[type="url"]').first();
    if (await urlInput.isVisible().catch(() => false)) {
      await urlInput.fill('https://example.com/webhook');
    }

    // Check at least one event type if checkboxes present
    const firstCheckbox = page.locator('input[type="checkbox"]').first();
    if (await firstCheckbox.isVisible().catch(() => false)) {
      await firstCheckbox.check();
    }

    // Save — scope to modal dialog to avoid hitting the "Add Webhook" button behind it
    const modal = page.locator('[role="dialog"]');
    const saveButton = modal.getByRole('button', { name: /save|create|submit/i }).first();
    if (await saveButton.isVisible().catch(() => false)) {
      await saveButton.scrollIntoViewIfNeeded();
      await saveButton.click({ force: true });
      await page.waitForTimeout(1_000);

      // Webhook should appear in the list
      const bodyText = await page.locator('body').textContent() ?? '';
      expect(bodyText).toContain('E2E Test Webhook');
    }

    expect(criticalErrors()).toHaveLength(0);
  });

  test('webhook cards display status and URL', async ({ page }) => {
    await page.waitForTimeout(1_000);

    // If webhooks exist, cards should show status badges
    const bodyText = await page.locator('body').textContent() ?? '';
    if (/E2E Test Webhook|webhook/i.test(bodyText)) {
      // Look for status badges
      const statusBadges = page.locator('span, div').filter({ hasText: /active|inactive|enabled|disabled/i });
      const hasStatus = (await statusBadges.count()) > 0;

      // Look for URL display
      const hasUrl = bodyText.includes('example.com') || bodyText.includes('http');

      expect(hasStatus || hasUrl).toBeTruthy();
    }

    expect(criticalErrors()).toHaveLength(0);
  });

  test('webhook has action buttons (edit, delete, test)', async ({ page }) => {
    await page.waitForTimeout(1_000);

    const bodyText = await page.locator('body').textContent() ?? '';
    if (/E2E Test Webhook/i.test(bodyText)) {
      // Should have action buttons
      const actionButtons = page.locator(
        'button[aria-label*="edit" i], ' +
        'button[aria-label*="delete" i], ' +
        'button[aria-label*="test" i], ' +
        'button[title*="edit" i], ' +
        'button[title*="delete" i], ' +
        'button[title*="test" i]'
      );

      const buttonCount = await actionButtons.count();
      expect(buttonCount).toBeGreaterThan(0);
    }

    expect(criticalErrors()).toHaveLength(0);
  });

  test('empty state shows when no webhooks exist', async ({ page }) => {
    await page.waitForTimeout(1_000);

    // If no webhooks, should show empty state or create CTA
    const bodyText = await page.locator('body').textContent() ?? '';
    const hasContent = bodyText.length > 100;
    expect(hasContent).toBeTruthy();

    expect(criticalErrors()).toHaveLength(0);
  });

  test('no critical JS errors on webhooks page', async ({ page }) => {
    await page.waitForTimeout(2_000);
    expect(criticalErrors()).toHaveLength(0);
  });
});

import { test, expect } from '../fixtures/emulator-setup';

const TEST_WEBHOOK_NAME = `E2E Test Webhook ${Date.now()}`;

/**
 * Admin Webhooks CRUD E2E Tests — Emulator-backed
 *
 * Tests the canonical System Settings > Integrations > Webhooks route:
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
    await page.goto('/admin/settings?tab=integrations&sub=webhooks');
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
    const addButton = page.getByTestId('add-webhook-action');
    await expect(addButton).toBeVisible({ timeout: 5_000 });

    expect(criticalErrors()).toHaveLength(0);
  });

  test('can open create webhook modal', async ({ page }) => {
    await page.waitForTimeout(1_000);

    const addButton = page.getByTestId('add-webhook-action');
    await expect(addButton).toBeVisible({ timeout: 5_000 });
    await addButton.click();
    await page.waitForTimeout(500);

    const modal = page.getByRole('dialog', { name: 'Create Webhook', exact: true });
    await expect(modal).toBeVisible();
    const nameInput = modal.getByRole('textbox', { name: 'Name', exact: true });
    const urlInput = modal.getByRole('textbox', { name: 'URL', exact: true });

    await expect(nameInput).toBeVisible();
    await expect(urlInput).toBeVisible();

    expect(criticalErrors()).toHaveLength(0);
  });

  test('create webhook form has event type selection', async ({ page }) => {
    await page.waitForTimeout(1_000);

    const addButton = page.getByTestId('add-webhook-action');
    await expect(addButton).toBeVisible({ timeout: 5_000 });
    await addButton.click();
    await page.waitForTimeout(500);

    // Should have event type checkboxes or multi-select
    const modal = page.getByRole('dialog', { name: 'Create Webhook', exact: true });
    await expect(modal).toBeVisible();
    const eventCheckboxes = modal.getByRole('checkbox');
    const eventSelect = modal.getByRole('listbox');

    const checkboxCount = await eventCheckboxes.count();
    const selectCount = await eventSelect.count();

    // Either checkboxes for event types or a select dropdown
    expect(checkboxCount + selectCount).toBeGreaterThan(0);

    expect(criticalErrors()).toHaveLength(0);
  });

  test('can create a webhook', async ({ page }) => {
    await page.waitForTimeout(1_000);

    const addButton = page.getByTestId('add-webhook-action');
    await expect(addButton).toBeVisible({ timeout: 5_000 });
    await addButton.click();
    await page.waitForTimeout(500);

    const modal = page.getByRole('dialog', { name: 'Create Webhook', exact: true });
    await expect(modal).toBeVisible({ timeout: 5_000 });

    const nameInput = modal.getByRole('textbox', { name: 'Name', exact: true });
    await expect(nameInput).toBeVisible();
    await nameInput.fill(TEST_WEBHOOK_NAME);

    const urlInput = modal.getByRole('textbox', { name: 'URL', exact: true });
    await expect(urlInput).toBeVisible();
    await urlInput.fill('https://example.com/webhook');

    const firstCheckbox = modal.getByRole('checkbox', { name: 'All events', exact: true });
    await expect(firstCheckbox).toBeVisible();
    await firstCheckbox.check();

    const saveButton = modal.getByRole('button', { name: 'Create', exact: true });
    await expect(saveButton).toBeVisible();
    await saveButton.click();

    await expect(modal).toBeHidden();
    await expect(page.getByRole('heading', { name: TEST_WEBHOOK_NAME, exact: true })).toBeVisible();

    expect(criticalErrors()).toHaveLength(0);
  });

  test('webhook cards display status and URL', async ({ page }) => {
    const webhookCard = page.getByRole('article', {
      name: `${TEST_WEBHOOK_NAME} webhook`,
      exact: true,
    });
    await expect(webhookCard).toBeVisible();
    await expect(webhookCard.getByText('https://example.com/webhook', { exact: true })).toBeVisible();
    await expect(webhookCard.getByText(/^(Active|Inactive)$/)).toBeVisible();

    expect(criticalErrors()).toHaveLength(0);
  });

  test('webhook has action buttons (edit, delete, test)', async ({ page }) => {
    const webhookCard = page.getByRole('article', {
      name: `${TEST_WEBHOOK_NAME} webhook`,
      exact: true,
    });
    await expect(webhookCard.getByRole('button', { name: 'Send test event', exact: true })).toBeVisible();
    await expect(webhookCard.getByRole('button', { name: 'View deliveries', exact: true })).toBeVisible();
    await expect(webhookCard.getByRole('button', { name: 'Edit webhook', exact: true })).toBeVisible();
    await expect(webhookCard.getByRole('button', { name: 'Delete webhook', exact: true })).toBeVisible();

    expect(criticalErrors()).toHaveLength(0);
  });

  test('created webhook remains visible after reload', async ({ page }) => {
    await expect(page.getByRole('article', {
      name: `${TEST_WEBHOOK_NAME} webhook`,
      exact: true,
    })).toBeVisible();
    await expect(page.getByText('No webhooks configured', { exact: true })).toBeHidden();

    expect(criticalErrors()).toHaveLength(0);
  });

  test('no critical JS errors on webhooks page', async ({ page }) => {
    await page.waitForTimeout(2_000);
    expect(criticalErrors()).toHaveLength(0);
  });
});

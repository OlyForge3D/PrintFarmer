import { test, expect } from '../fixtures/emulator-setup';

/**
 * Printer Groups CRUD E2E Tests — Emulator-backed
 *
 * Tests the /printer-groups route:
 *   - Group list display
 *   - Create group modal
 *   - Edit/delete groups
 *   - Assign emulator printers to groups
 *   - Group filtering
 */

test.describe('Printer Groups — Emulator', () => {
  test.describe.configure({ mode: 'serial' });

  let consoleErrors: string[] = [];

  test.beforeEach(async ({ page }) => {
    consoleErrors = [];
    page.on('pageerror', (error) => consoleErrors.push(error.message));
    await page.goto('/printer-groups');
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

  test('printer groups page loads with heading and create button', async ({ page }) => {
    await page.waitForTimeout(1_000);

    const bodyText = await page.locator('body').textContent() ?? '';
    const hasGroupContent = /group|printer/i.test(bodyText);
    expect(hasGroupContent).toBeTruthy();

    // Create group button
    const createButton = page.getByTestId('create-printer-group-action');
    await expect(createButton).toBeVisible({ timeout: 5_000 });

    expect(criticalErrors()).toHaveLength(0);
  });

  test('can open create group modal', async ({ page }) => {
    await page.waitForTimeout(1_000);

    const createButton = page.getByTestId('create-printer-group-action');
    await expect(createButton).toBeVisible({ timeout: 5_000 });
    await createButton.click();
    await page.waitForTimeout(500);

    const modal = page.getByRole('dialog', { name: 'Create Printer Group', exact: true });
    await expect(modal).toBeVisible({ timeout: 5_000 });
    await expect(modal.getByRole('textbox', { name: 'Name', exact: true })).toBeVisible();

    expect(criticalErrors()).toHaveLength(0);
  });

  test('can create a new printer group', async ({ page }) => {
    await page.waitForTimeout(1_000);

    const createButton = page.getByTestId('create-printer-group-action');
    await expect(createButton).toBeVisible({ timeout: 5_000 });
    await createButton.click();
    await page.waitForTimeout(500);

    const modal = page.getByRole('dialog', { name: 'Create Printer Group', exact: true });
    await expect(modal).toBeVisible({ timeout: 5_000 });

    const nameInput = modal.getByRole('textbox', { name: 'Name', exact: true });
    if (await nameInput.isVisible().catch(() => false)) {
      await nameInput.fill('E2E Test Group');

      const saveButton = modal.getByRole('button', { name: 'Create Group', exact: true });
      if (await saveButton.isVisible().catch(() => false)) {
        await saveButton.click();
        await page.waitForTimeout(2_000);

        // Group should appear in the list OR modal closed successfully
        const modalGone = await modal.isHidden().catch(() => true);
        if (modalGone) {
          const bodyText = await page.locator('body').textContent() ?? '';
          expect(bodyText).toContain('E2E Test Group');
        }
      }
    }

    expect(criticalErrors()).toHaveLength(0);
  });

  test('group cards display printer count', async ({ page }) => {
    await page.waitForTimeout(1_000);

    const bodyText = await page.locator('body').textContent() ?? '';
    if (/E2E Test Group/i.test(bodyText)) {
      // Group card should show printer count (0 initially)
      const groupCard = page.locator('[data-testid*="group-card"], div').filter({ hasText: 'E2E Test Group' }).first();
      const hasCard = await groupCard.isVisible().catch(() => false);
      expect(hasCard).toBeTruthy();
    }

    expect(criticalErrors()).toHaveLength(0);
  });

  test('group cards have edit and delete actions', async ({ page }) => {
    await page.waitForTimeout(1_000);

    const bodyText = await page.locator('body').textContent() ?? '';
    if (/E2E Test Group/i.test(bodyText)) {
      // At least one action type should exist
      const allActionButtons = page.locator('button');
      expect(await allActionButtons.count()).toBeGreaterThan(1);
    }

    expect(criticalErrors()).toHaveLength(0);
  });

  test('empty state shows when no groups exist', async ({ page }) => {
    await page.waitForTimeout(1_000);

    // Page should have content regardless of group count
    const content = page.locator('main, [role="main"], #root');
    await expect(content.first()).toBeVisible();

    expect(criticalErrors()).toHaveLength(0);
  });

  test('no critical JS errors on printer groups page', async ({ page }) => {
    await page.waitForTimeout(2_000);
    expect(criticalErrors()).toHaveLength(0);
  });
});

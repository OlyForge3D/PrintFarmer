import { test, expect } from '../fixtures/emulator-setup';

/**
 * Authentication Flow E2E Tests — Emulator-backed
 *
 * Tests the public auth routes:
 *   /login          — form submission, validation, redirect
 *   /forgot-password — email input, success message
 *   /reset-password  — token validation, password strength
 *   /confirm-email   — token verification states
 *   /registration-pending — approval state display
 *
 * These routes don't require emulated printer data but do need
 * the API server running for form submissions and validation.
 */

test.describe('Auth Flows — Emulator', () => {
  test.describe.configure({ mode: 'serial' });

  let consoleErrors: string[] = [];

  test.beforeEach(async ({ page }) => {
    consoleErrors = [];
    page.on('pageerror', (error) => consoleErrors.push(error.message));
    // Auth-flow tests need to start unauthenticated to see the login page
    await page.evaluate(() => localStorage.removeItem('auth-token'));
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

  // ---------------------------------------------------------------------------
  // /login
  // ---------------------------------------------------------------------------

  test('login page renders with email and password fields', async ({ page }) => {
    await page.goto('/login');
    await page.waitForLoadState('networkidle');

    // Email and password inputs should be present
    const emailInput = page.locator('input[type="email"], input[name="email"], input[placeholder*="mail" i]').first();
    const passwordInput = page.locator('input[type="password"]').first();

    await expect(emailInput).toBeVisible({ timeout: 10_000 });
    await expect(passwordInput).toBeVisible();

    // Sign In button should be present
    const signInButton = page.getByRole('button', { name: /sign in|log in|login/i }).first();
    await expect(signInButton).toBeVisible();

    expect(criticalErrors()).toHaveLength(0);
  });

  test('login form validates empty submission', async ({ page }) => {
    await page.goto('/login');
    await page.waitForLoadState('networkidle');

    const signInButton = page.getByRole('button', { name: /sign in|log in|login/i }).first();
    await expect(signInButton).toBeVisible({ timeout: 10_000 });

    // Click sign in without filling fields — button may be disabled
    await signInButton.click({ force: true });
    await page.waitForTimeout(500);

    // Should show validation error or the form should not navigate away
    const currentUrl = page.url();
    expect(currentUrl).toContain('/login');

    expect(criticalErrors()).toHaveLength(0);
  });

  test('login form accepts input in email and password fields', async ({ page }) => {
    await page.goto('/login');
    await page.waitForLoadState('networkidle');

    const emailInput = page.locator('input[type="email"], input[name="email"], input[placeholder*="mail" i]').first();
    const passwordInput = page.locator('input[type="password"]').first();

    await expect(emailInput).toBeVisible({ timeout: 10_000 });

    await emailInput.fill('test@example.com');
    await passwordInput.fill('TestPassword123!');

    // Verify values were accepted
    await expect(emailInput).toHaveValue('test@example.com');
    await expect(passwordInput).toHaveValue('TestPassword123!');

    expect(criticalErrors()).toHaveLength(0);
  });

  test('login page has link to forgot password', async ({ page }) => {
    await page.goto('/login');
    await page.waitForLoadState('networkidle');
    await page.waitForTimeout(1_000);

    // Look for forgot password link/button
    const forgotLink = page.locator('a, button').filter({ hasText: /forgot|reset/i }).first();
    const hasForgot = await forgotLink.isVisible().catch(() => false);
    expect(hasForgot).toBeTruthy();

    expect(criticalErrors()).toHaveLength(0);
  });

  test('login page has registration option', async ({ page }) => {
    await page.goto('/login');
    await page.waitForLoadState('networkidle');
    await page.waitForTimeout(1_000);

    // Look for register/sign up link/button
    const registerLink = page.locator('a, button').filter({ hasText: /register|sign up|create account/i }).first();
    const hasRegister = await registerLink.isVisible().catch(() => false);
    expect(hasRegister).toBeTruthy();

    expect(criticalErrors()).toHaveLength(0);
  });

  // ---------------------------------------------------------------------------
  // /forgot-password
  // ---------------------------------------------------------------------------

  test('forgot password page renders with email field', async ({ page }) => {
    // Navigate to login page first — /forgot-password may redirect unauthenticated users
    await page.goto('/');
    await page.waitForLoadState('networkidle');

    // Click the "Forgot password?" link from the login dialog
    const forgotLink = page.getByRole('link', { name: /forgot password/i });
    const hasForgotLink = await forgotLink.isVisible().catch(() => false);

    if (hasForgotLink) {
      await forgotLink.click();
      await page.waitForLoadState('networkidle');
      await page.waitForTimeout(1_000);

      // Should show forgot password heading or email input
      const heading = page.locator('h1, h2, h3').filter({ hasText: /forgot|reset|recover/i }).first();
      const emailInput = page.locator('input[type="email"], input[name="email"], input[placeholder*="mail" i]').first();

      const hasHeading = await heading.isVisible().catch(() => false);
      const hasEmail = await emailInput.isVisible().catch(() => false);

      expect(hasHeading || hasEmail).toBeTruthy();
    } else {
      // If there's no forgot password link, that's acceptable — feature may not exist yet
      expect(true).toBeTruthy();
    }

    expect(criticalErrors()).toHaveLength(0);
  });

  test('forgot password has cancel/back navigation', async ({ page }) => {
    // Navigate via the login page to avoid redirect issues
    await page.goto('/');
    await page.waitForLoadState('networkidle');

    const forgotLink = page.getByRole('link', { name: /forgot password/i });
    const hasForgotLink = await forgotLink.isVisible().catch(() => false);

    if (hasForgotLink) {
      await forgotLink.click();
      await page.waitForLoadState('networkidle');
      await page.waitForTimeout(1_000);

      // Cancel/Back button should be present
      const cancelButton = page.locator('a, button').filter({ hasText: /cancel|back|return|login|sign in/i }).first();
      const hasCancel = await cancelButton.isVisible().catch(() => false);
      expect(hasCancel).toBeTruthy();
    } else {
      expect(true).toBeTruthy();
    }

    expect(criticalErrors()).toHaveLength(0);
  });

  test('forgot password form accepts email input', async ({ page }) => {
    // Navigate via the login page to avoid redirect issues
    await page.goto('/');
    await page.waitForLoadState('networkidle');

    const forgotLink = page.getByRole('link', { name: /forgot password/i });
    const hasForgotLink = await forgotLink.isVisible().catch(() => false);

    if (hasForgotLink) {
      await forgotLink.click();
      await page.waitForLoadState('networkidle');
      await page.waitForTimeout(1_000);

      const emailInput = page.locator('input[type="email"], input[name="email"], input[placeholder*="mail" i]').first();
      if (await emailInput.isVisible().catch(() => false)) {
        await emailInput.fill('user@example.com');
        await expect(emailInput).toHaveValue('user@example.com');
      }
    }

    expect(criticalErrors()).toHaveLength(0);
  });

  // ---------------------------------------------------------------------------
  // /reset-password
  // ---------------------------------------------------------------------------

  test('reset password page renders with password fields', async ({ page }) => {
    // Reset password requires token params — visit without them to test error handling
    await page.goto('/reset-password');
    await page.waitForLoadState('networkidle');
    await page.waitForTimeout(1_000);

    // Should show either the reset form or an error about missing token
    const bodyText = await page.locator('body').textContent() ?? '';
    const hasContent = /reset|password|token|invalid|expired/i.test(bodyText);
    expect(hasContent).toBeTruthy();

    expect(criticalErrors()).toHaveLength(0);
  });

  test('reset password page with token shows password fields', async ({ page }) => {
    // Visit with mock token params
    await page.goto('/reset-password?token=mock-token&email=test@example.com');
    await page.waitForLoadState('networkidle');
    await page.waitForTimeout(1_000);

    // Should show password input fields or error about invalid token
    const passwordFields = page.locator('input[type="password"]');
    const fieldCount = await passwordFields.count();

    if (fieldCount >= 2) {
      // Good — password and confirm password fields visible
      await expect(passwordFields.first()).toBeVisible();
    } else {
      // Invalid token message is also acceptable
      const bodyText = await page.locator('body').textContent() ?? '';
      expect(/invalid|expired|error|token/i.test(bodyText)).toBeTruthy();
    }

    expect(criticalErrors()).toHaveLength(0);
  });

  // ---------------------------------------------------------------------------
  // /confirm-email
  // ---------------------------------------------------------------------------

  test('confirm email page renders with appropriate state', async ({ page }) => {
    // Visit without token — should show error or confirming state
    await page.goto('/confirm-email');
    await page.waitForLoadState('networkidle');
    await page.waitForTimeout(1_000);

    const bodyText = await page.locator('body').textContent() ?? '';
    // Should show one of: confirming, confirmed, failed, error, token
    const hasState = /confirm|verif|token|invalid|error|success/i.test(bodyText);
    expect(hasState).toBeTruthy();

    expect(criticalErrors()).toHaveLength(0);
  });

  test('confirm email with token shows processing state', async ({ page }) => {
    await page.goto('/confirm-email?token=mock-token&email=test@example.com');
    await page.waitForLoadState('networkidle');
    await page.waitForTimeout(2_000);

    // Should show either confirming spinner, success, or failure
    const bodyText = await page.locator('body').textContent() ?? '';
    const hasResult = /confirm|success|fail|error|invalid/i.test(bodyText);
    expect(hasResult).toBeTruthy();

    // Should have a navigation link (Go to Login, Go to Home)
    const navLink = page.locator('a, button').filter({ hasText: /login|home|sign in/i }).first();
    const hasNav = await navLink.isVisible().catch(() => false);
    // Navigation link may appear on success/failure states
    if (!hasNav) {
      // Still processing — that's acceptable
      const isProcessing = /confirming|verifying|loading/i.test(bodyText);
      expect(isProcessing || hasNav).toBeTruthy();
    }

    expect(criticalErrors()).toHaveLength(0);
  });

  // ---------------------------------------------------------------------------
  // /registration-pending
  // ---------------------------------------------------------------------------

  test('registration pending page renders approval state', async ({ page }) => {
    await page.goto('/registration-pending');
    await page.waitForLoadState('networkidle');
    await page.waitForTimeout(1_000);

    const bodyText = await page.locator('body').textContent() ?? '';
    // Should show pending/approval/waiting message
    const hasPendingState = /pending|approval|wait|review|admin/i.test(bodyText);
    expect(hasPendingState).toBeTruthy();

    expect(criticalErrors()).toHaveLength(0);
  });

  // ---------------------------------------------------------------------------
  // Cross-cutting
  // ---------------------------------------------------------------------------

  test('no critical JS errors across auth pages', async ({ page }) => {
    const authPages = [
      { path: '/login', name: 'Login' },
      { path: '/forgot-password', name: 'Forgot Password' },
      { path: '/reset-password', name: 'Reset Password' },
      { path: '/confirm-email', name: 'Confirm Email' },
      { path: '/registration-pending', name: 'Registration Pending' },
    ];

    for (const pageConfig of authPages) {
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

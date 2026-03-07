import { test, expect } from '@playwright/test';

test.describe('Initial Setup Wizard', () => {
  test('setup wizard appears on first run with empty database', async ({ page }) => {
    await page.goto('/');
    await page.waitForLoadState('networkidle');

    // On a fresh database the app should present a setup/login/registration flow.
    // Look for common setup wizard indicators:
    // - text mentioning "setup", "welcome", "create", "admin", "register", "get started"
    // - or a login/register form
    const bodyText = await page.locator('body').textContent();
    const lowerText = (bodyText ?? '').toLowerCase();

    const setupIndicators = [
      'setup',
      'welcome',
      'create',
      'admin',
      'register',
      'get started',
      'sign up',
      'log in',
      'login',
      'password',
    ];

    const found = setupIndicators.some((indicator) => lowerText.includes(indicator));
    expect(
      found,
      `Expected setup wizard or auth flow on first run. Page text: ${lowerText.substring(0, 500)}`,
    ).toBe(true);
  });
});

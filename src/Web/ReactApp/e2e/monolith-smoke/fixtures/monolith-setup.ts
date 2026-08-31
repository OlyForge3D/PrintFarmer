/**
 * Auth fixture for the monolith browser/API smoke journeys (issue #2286).
 *
 * Deliberately independent of `../../fixtures/emulator-setup.ts`'s
 * `emulatorReady` auto-fixture: that fixture's `POST
 * /api/test/moonraker-emulator/reset` call is Moonraker-emulator-specific
 * and is documented as required "in the isolated validation stack" — it
 * has no meaning (and may not even be enabled) against a plain monolith
 * API + seeded SQLite DB with no Moonraker emulator attached. This suite
 * only needs authentication, so it reuses the emulator fixture's already
 * generic, non-Moonraker-specific exports (`provisionAdminAndLogin`,
 * `API_BASE_URL`, `BASE_URL`, `dismissTourIfVisible`) rather than
 * duplicating that logic.
 *
 * No file-based token cache is used here (unlike `emulator-setup.ts`)
 * because this suite always runs with `--workers=1` against its own
 * freshly seeded database — there is no multi-worker SQLite contention to
 * guard against, and `provisionAdminAndLogin` is safe to call once per
 * test: after the first successful self-provision, `GET
 * /api/setup/status` reports `needsSetup: false` and subsequent calls
 * simply log in.
 */
import { test as base, expect, type Page } from '@playwright/test';
import {
  API_BASE_URL,
  BASE_URL,
  dismissTourIfVisible,
  provisionAdminAndLogin,
} from '../../fixtures/emulator-setup';

export { API_BASE_URL, BASE_URL };

/**
 * Read the JWT the `monolithReady` fixture injected into `localStorage`,
 * for specs that need to make authenticated `page.request` calls directly
 * (the app's own axios client attaches this automatically, but
 * `page.request` does not).
 */
export async function getStoredAuthToken(page: Page): Promise<string | undefined> {
  const token = await page.evaluate(() => localStorage.getItem('auth-token'));
  return token ?? undefined;
}

type MonolithFixtures = {
  /** Ensures the API is healthy and the browser is authenticated before each test. */
  monolithReady: void;
};

export const test = base.extend<MonolithFixtures>({
  monolithReady: [async ({ page }, use) => {
    // 1. Verify the monolith API is reachable.
    const healthResponse = await page.request.get(`${API_BASE_URL}/healthz`);
    expect(healthResponse.ok(), `API health check failed at ${API_BASE_URL}/healthz`).toBeTruthy();

    // 2. Self-provision (pristine DB) or log in as the test admin account.
    const token = await provisionAdminAndLogin(page.request, (ms) => page.waitForTimeout(ms));
    expect(token, 'Failed to obtain auth token for test admin').toBeTruthy();

    // 3. Inject the JWT into localStorage before the app loads, and
    //    suppress onboarding tours that would otherwise intercept clicks.
    await page.goto(BASE_URL);
    await page.evaluate((t: string) => {
      localStorage.setItem('auth-token', t);
      localStorage.setItem('pf-tour-seen-dashboard', 'true');
      localStorage.setItem('pf-tour-seen-printers', 'true');
    }, token!);

    // 4. Reload so the React app picks up the token from localStorage.
    await page.reload();
    await page.waitForLoadState('networkidle');

    // 5. Dismiss any onboarding tour popover that may still appear.
    await dismissTourIfVisible(page);

    await use();
  }, { auto: true }],
});

export { expect };

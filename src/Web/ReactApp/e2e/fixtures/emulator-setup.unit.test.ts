import { afterEach, describe, expect, it, vi } from 'vitest';
import { provisionAdminAndLogin, resolveAdminCredentials } from './emulator-setup';

/** Minimal fake satisfying the `{ get, post }` shape `provisionAdminAndLogin` needs. */
function fakeResponse(body: unknown, ok = true) {
  return { ok: () => ok, status: () => (ok ? 200 : 500), json: async () => body };
}

/**
 * Unit coverage for the credential-resolution seam behind issue #1586: the
 * Moonraker Playwright fixture hardcoded `e2e-admin` and only ever created
 * it when `/api/setup/status` reported `needsSetup: true`, so a harness
 * that had already provisioned its own admin (leaving `needsSetup: false`)
 * could never authenticate. `resolveAdminCredentials` is the pure decision
 * point the fixture calls before both the self-provisioning
 * (`needsSetup: true`) and login (`needsSetup: false`) paths, so it is
 * exercised directly here rather than through the full Playwright fixture,
 * which requires a live API and browser context.
 *
 * This file intentionally uses the `.unit.test.ts` suffix (not `.spec.ts`)
 * so Vitest picks it up while Playwright's own runner ignores it — see
 * `playwright.config.ts`'s `testIgnore` and `vitest.config.ts`.
 */
describe('resolveAdminCredentials', () => {
  it('falls back to the fixture defaults when no env vars are set (pristine-database / needsSetup:true self-setup path)', () => {
    const credentials = resolveAdminCredentials({});

    expect(credentials).toEqual({
      username: 'e2e-admin',
      email: 'e2e-admin@printfarmer.test',
      password: 'E2eTestAdmin123!',
      firstName: 'E2E',
      lastName: 'Admin',
      isExternal: false,
    });
  });

  it('uses the externally supplied admin account when both username and password are set (needsSetup:false path)', () => {
    const credentials = resolveAdminCredentials({
      E2E_ADMIN_USERNAME: 'daily-smoke-admin',
      E2E_ADMIN_PASSWORD: 'Sm0ke!super-secret-Aa1',
    });

    expect(credentials).toEqual({
      username: 'daily-smoke-admin',
      email: 'e2e-admin@printfarmer.test',
      password: 'Sm0ke!super-secret-Aa1',
      firstName: 'E2E',
      lastName: 'Admin',
      isExternal: true,
    });
  });

  it('honors an explicit external email when provided alongside username/password', () => {
    const credentials = resolveAdminCredentials({
      E2E_ADMIN_USERNAME: 'daily-smoke-admin',
      E2E_ADMIN_PASSWORD: 'Sm0ke!super-secret-Aa1',
      E2E_ADMIN_EMAIL: 'daily-smoke-admin@printfarmer.local',
    });

    expect(credentials.email).toBe('daily-smoke-admin@printfarmer.local');
    expect(credentials.isExternal).toBe(true);
  });

  it('trims whitespace from externally supplied username and email', () => {
    const credentials = resolveAdminCredentials({
      E2E_ADMIN_USERNAME: '  daily-smoke-admin  ',
      E2E_ADMIN_PASSWORD: 'Sm0ke!super-secret-Aa1',
      E2E_ADMIN_EMAIL: '  daily-smoke-admin@printfarmer.local  ',
    });

    expect(credentials.username).toBe('daily-smoke-admin');
    expect(credentials.email).toBe('daily-smoke-admin@printfarmer.local');
  });

  it('falls back to defaults when only the username is set (misconfigured harness)', () => {
    const credentials = resolveAdminCredentials({ E2E_ADMIN_USERNAME: 'daily-smoke-admin' });

    expect(credentials.isExternal).toBe(false);
    expect(credentials.username).toBe('e2e-admin');
  });

  it('falls back to defaults when only the password is set (misconfigured harness)', () => {
    const credentials = resolveAdminCredentials({ E2E_ADMIN_PASSWORD: 'Sm0ke!super-secret-Aa1' });

    expect(credentials.isExternal).toBe(false);
    expect(credentials.password).toBe('E2eTestAdmin123!');
  });

  it('falls back to defaults when the username is only whitespace', () => {
    const credentials = resolveAdminCredentials({
      E2E_ADMIN_USERNAME: '   ',
      E2E_ADMIN_PASSWORD: 'Sm0ke!super-secret-Aa1',
    });

    expect(credentials.isExternal).toBe(false);
    expect(credentials.username).toBe('e2e-admin');
  });
});

/**
 * Branch coverage for the actual `needsSetup` decision the fixture makes
 * (issue #1586), exercised against a fake `request` client rather than a
 * real Playwright `Page`/API server. `getOrCreateToken` (the real fixture
 * entry point) is not itself exported since it also owns disk-based
 * token-cache/lock bookkeeping unrelated to this bug; `provisionAdminAndLogin`
 * is the extracted seam that owns exactly the `needsSetup` branch plus login.
 */
describe('provisionAdminAndLogin', () => {
  afterEach(() => {
    vi.unstubAllEnvs();
    vi.resetModules();
  });

  it('needsSetup:true — self-provisions the default admin, then logs in (pristine-database path, unchanged behavior)', async () => {
    const get = vi.fn().mockResolvedValue(fakeResponse({ needsSetup: true }));
    const post = vi.fn()
      .mockResolvedValueOnce(fakeResponse({})) // POST /api/setup/initial-admin
      .mockResolvedValueOnce(fakeResponse({ success: true, token: 'default-admin-token' })); // POST /api/auth/login

    const token = await provisionAdminAndLogin({ get, post });

    expect(token).toBe('default-admin-token');
    expect(post).toHaveBeenCalledTimes(2);
    expect(post.mock.calls[0][0]).toContain('/api/setup/initial-admin');
    expect(post.mock.calls[0][1]?.data).toMatchObject({ username: 'e2e-admin' });
    expect(post.mock.calls[1][0]).toContain('/api/auth/login');
    expect(post.mock.calls[1][1]?.data).toMatchObject({ usernameOrEmail: 'e2e-admin' });
  });

  it('needsSetup:false — skips admin creation entirely and logs in directly as the externally provisioned admin', async () => {
    // A pre-existing admin already exists (e.g. the daily immutable-image
    // harness's own smoke admin) — simulate the fixture having resolved an
    // external account by re-importing the module with the env vars set,
    // since `ADMIN` is computed once at module load time.
    vi.resetModules();
    vi.stubEnv('E2E_ADMIN_USERNAME', 'daily-smoke-admin');
    vi.stubEnv('E2E_ADMIN_PASSWORD', 'Sm0ke!super-secret-Aa1');

    const { provisionAdminAndLogin: provisionAdminAndLoginWithExternalEnv } =
      await import('./emulator-setup');

    const get = vi.fn().mockResolvedValue(fakeResponse({ needsSetup: false }));
    const post = vi.fn().mockResolvedValue(fakeResponse({ success: true, token: 'external-admin-token' }));

    const token = await provisionAdminAndLoginWithExternalEnv({ get, post });

    expect(token).toBe('external-admin-token');
    // The admin already exists — no /api/setup/initial-admin call should occur.
    expect(post).toHaveBeenCalledTimes(1);
    expect(post.mock.calls[0][0]).toContain('/api/auth/login');
    expect(post.mock.calls[0][1]?.data).toMatchObject({
      usernameOrEmail: 'daily-smoke-admin',
      password: 'Sm0ke!super-secret-Aa1',
    });
  });

  it('retries login with backoff and eventually gives up if every attempt fails', async () => {
    const get = vi.fn().mockResolvedValue(fakeResponse({ needsSetup: false }));
    const post = vi.fn().mockResolvedValue(fakeResponse({}, false));
    const wait = vi.fn().mockResolvedValue(undefined);

    const token = await provisionAdminAndLogin({ get, post }, wait);

    expect(token).toBeUndefined();
    expect(post).toHaveBeenCalledTimes(10);
    expect(wait).toHaveBeenCalledTimes(10);
  });
});

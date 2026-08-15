import { describe, expect, it } from 'vitest';
import { resolveAdminCredentials } from './emulator-setup';

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

import { test as base, expect, type Page, type Locator } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { fileURLToPath } from 'node:url';

export const API_BASE_URL = process.env.API_BASE_URL || 'http://127.0.0.1:5245';
export const BASE_URL = process.env.BASE_URL || 'http://localhost:3000';

const ADMIN_USERNAME = 'e2e-admin';
const ADMIN_EMAIL = 'e2e-admin@printfarmer.test';
const ADMIN_PASSWORD = 'E2eTestAdmin123!';

// File-based token cache so multiple workers share a single JWT
// instead of all racing to login against SQLite simultaneously.
const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);
const TOKEN_CACHE_DIR = path.join(__dirname, '..', '.auth');
const TOKEN_CACHE_FILE = path.join(TOKEN_CACHE_DIR, 'e2e-token.json');

/**
 * Shared fixture for emulator-backed E2E tests.
 *
 * Verifies the API server is reachable, ensures a test admin account
 * exists, authenticates, injects the JWT into localStorage, and
 * provides helper methods for common printer-related assertions.
 */

// ---------------------------------------------------------------------------
// Helpers (exported for direct use outside fixtures)
// ---------------------------------------------------------------------------

/**
 * Wait for a printer card's live-updated content (status badge, progress
 * bar, temperatures, etc.) to change via a real-time SignalR broadcast.
 *
 * Scoped by printer **name** (the deterministic Moonraker seed contract
 * guarantees unique names) rather than a DOM data attribute, since no
 * `data-printer-id` attribute is rendered on printer cards. This makes a
 * hard assertion — the card's text content must actually change within
 * `timeoutMs` — rather than a fixed, unconditional sleep standing in for a
 * real check.
 */
export async function waitForPrinterUpdate(page: Page, printerName: string, timeoutMs = 10_000): Promise<void> {
  const card = getPrinterCards(page).filter({ hasText: printerName }).first();
  await expect(card).toBeVisible({ timeout: timeoutMs });

  const initialText = await card.textContent() ?? '';
  await expect(async () => {
    const current = await card.textContent() ?? '';
    expect(current).not.toBe(initialText);
  }).toPass({ timeout: timeoutMs });
}

/**
 * Dismiss any visible driver.js tour popover so it doesn't block clicks.
 */
export async function dismissTourIfVisible(page: Page): Promise<void> {
  const closeBtn = page.locator('.driver-popover-close-btn');
  for (let i = 0; i < 5; i++) {
    if (await closeBtn.isVisible({ timeout: 500 }).catch(() => false)) {
      await closeBtn.click();
      await page.waitForTimeout(300);
    } else {
      break;
    }
  }
}

/**
 * Return all visible printer card locators on the current page.
 */
export function getPrinterCards(page: Page): Locator {
  return page.locator('[data-pf-card]');
}

/**
 * Open a printer's detail sidebar via its card's "Open details sidebar"
 * button (the only element that actually opens it — the card itself has no
 * click handler) and wait for the `complementary` landmark to render.
 */
export async function navigateToPrinter(page: Page, printerName: string): Promise<Locator> {
  const card = getPrinterCards(page).filter({ hasText: printerName }).first();

  await expect(card).toBeVisible({ timeout: 10_000 });
  await card.getByRole('button', { name: 'Open details sidebar' }).click();

  const sidebar = page.getByRole('complementary', { name: `${printerName} details` });
  await expect(sidebar).toBeVisible({ timeout: 10_000 });
  return sidebar;
}

// ---------------------------------------------------------------------------
// Playwright fixture extension
// ---------------------------------------------------------------------------

/**
 * Read the JWT the `emulatorReady` fixture injected into `localStorage`.
 * Used by dependent fixture modules (e.g. Moonraker contract helpers) that
 * need to make authenticated `page.request` calls against the PrintFarmer
 * API directly, since `page.request` does not automatically attach tokens
 * stored in `localStorage` the way the app's own axios client does.
 */
export async function getStoredAuthToken(page: Page): Promise<string | undefined> {
  const token = await page.evaluate(() => localStorage.getItem('auth-token'));
  return token ?? undefined;
}

type EmulatorFixtures = {
  /** Ensures the API is healthy and the emulator is active before each test. */
  emulatorReady: void;
};

export const test = base.extend<EmulatorFixtures>({
  emulatorReady: [async ({ page }, use) => {
    // 1. Verify API is reachable
    const healthResponse = await page.request.get(`${API_BASE_URL}/healthz`);
    expect(healthResponse.ok(), `API health check failed at ${API_BASE_URL}/healthz`).toBeTruthy();

    // 2. Get or create a cached JWT token (avoids SQLite contention
    //    when multiple Playwright workers all try to login simultaneously)
    const token = await getOrCreateToken(page);
    expect(token, 'Failed to obtain auth token for test admin').toBeTruthy();

    // 3. Restore application-owned dispatch state. Resetting Moonraker alone
    // cannot release durable queue claims created by a prior browser test.
    const resetResponse = await page.request.post(
      `${API_BASE_URL}/api/test/moonraker-emulator/reset`,
      { headers: { Authorization: `Bearer ${token}` } }
    );
    expect(
      resetResponse.status(),
      'PrintFarmer Moonraker application-state reset endpoint must be enabled in the isolated validation stack'
    ).toBe(204);

    // 4. Inject auth token into localStorage before the app loads
    await page.goto(BASE_URL);
    await page.evaluate((t: string) => {
      localStorage.setItem('auth-token', t);
      localStorage.setItem('printerViewMode', 'detailed');
    }, token!);

    // 5. Reload so the React app picks up the token from localStorage
    await page.reload();
    await page.waitForLoadState('networkidle');

    // 6. Dismiss any onboarding tour popover that may appear
    await dismissTourIfVisible(page);

    await use();
  }, { auto: true }],
});

/**
 * Obtain a JWT token, using a file cache to prevent multiple workers
 * from racing against the login endpoint (SQLite write contention).
 *
 * First worker: creates admin → logs in → caches token to disk.
 * Subsequent workers: read the cached token file.
 */
async function getOrCreateToken(page: Page): Promise<string | undefined> {
  // Try reading cached token first
  const cached = readCachedToken();
  if (cached && hasUsableTokenLifetime(cached) && await isTokenValid(page, cached)) return cached;
  if (cached) {
    try { fs.unlinkSync(TOKEN_CACHE_FILE); } catch { /* another worker removed it */ }
  }

  // No cache — we need to create the admin and login.
  // Use a simple file-lock approach: first writer wins.
  const lockFile = TOKEN_CACHE_FILE + '.lock';

  try {
    // Try to acquire lock (atomic create)
    fs.mkdirSync(TOKEN_CACHE_DIR, { recursive: true });
    fs.writeFileSync(lockFile, String(process.pid), { flag: 'wx' });
  } catch {
    // Another worker holds the lock — poll for the cached token
    for (let i = 0; i < 30; i++) {
      await page.waitForTimeout(500);
      const t = readCachedToken();
      if (t && hasUsableTokenLifetime(t) && await isTokenValid(page, t)) return t;
    }
    // Fallback: try login directly
    return await loginDirect(page);
  }

  try {
    // We hold the lock — create admin and login
    const setupStatus = await page.request.get(`${API_BASE_URL}/api/setup/status`);
    const setupData = await setupStatus.json();

    if (setupData.needsSetup) {
      await page.request.post(`${API_BASE_URL}/api/setup/initial-admin`, {
        data: {
          username: ADMIN_USERNAME,
          email: ADMIN_EMAIL,
          password: ADMIN_PASSWORD,
          firstName: 'E2E',
          lastName: 'Admin',
        },
      });
    }

    const token = await loginDirect(page);
    if (token) {
      fs.writeFileSync(TOKEN_CACHE_FILE, JSON.stringify({ token, ts: Date.now() }));
    }
    return token;
  } finally {
    try { fs.unlinkSync(lockFile); } catch { /* ignore */ }
  }
}

function hasUsableTokenLifetime(token: string): boolean {
  try {
    const payloadSegment = token.split('.')[1];
    if (!payloadSegment) {
      return false;
    }

    const payload = JSON.parse(Buffer.from(payloadSegment, 'base64url').toString('utf8')) as {
      exp?: number;
    };
    return typeof payload.exp === 'number' && payload.exp > Math.floor(Date.now() / 1000) + 120;
  } catch {
    return false;
  }
}

async function isTokenValid(page: Page, token: string): Promise<boolean> {
  try {
    const response = await page.request.get(`${API_BASE_URL}/api/auth/me`, {
      headers: { Authorization: `Bearer ${token}` },
    });
    return response.ok();
  } catch {
    return false;
  }
}

/** Read the cached JWT from disk if it exists and is recent (< 30 min). */
function readCachedToken(): string | undefined {
  try {
    const raw = fs.readFileSync(TOKEN_CACHE_FILE, 'utf-8');
    const data = JSON.parse(raw);
    if (data.token && Date.now() - data.ts < 30 * 60 * 1000) {
      return data.token;
    }
  } catch { /* no cache or corrupt */ }
  return undefined;
}

/** Login directly via the API (retry up to 10 times with backoff). */
async function loginDirect(page: Page): Promise<string | undefined> {
  for (let attempt = 0; attempt < 10; attempt++) {
    try {
      const resp = await page.request.post(`${API_BASE_URL}/api/auth/login`, {
        data: { usernameOrEmail: ADMIN_USERNAME, password: ADMIN_PASSWORD },
      });
      if (resp.ok()) {
        const data = await resp.json();
        if (data.success && data.token) return data.token;
      }
    } catch { /* retry */ }
    await page.waitForTimeout(300 * (attempt + 1));
  }
  return undefined;
}

export { expect };

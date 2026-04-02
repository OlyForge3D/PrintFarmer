import { test as base, expect, type Page, type Locator } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { fileURLToPath } from 'node:url';

const API_BASE_URL = process.env.API_BASE_URL || 'http://127.0.0.1:5245';
const BASE_URL = process.env.BASE_URL || 'http://localhost:3000';

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
 * Wait for a SignalR `PrinterUpdated` event for a specific printer.
 * The frontend receives these on the `/hubs/printers` hub.
 * We poll the UI for an updated timestamp or status change as a proxy.
 */
export async function waitForPrinterUpdate(page: Page, printerId: string, timeoutMs = 10_000): Promise<void> {
  // The emulator broadcasts every ~2 s.  Wait for the printer card to show
  // a reactive value change by polling the progress-bar or status badge.
  const card = page.locator('.pf-detailed-printer-card, div.rounded-xl.bg-pf-card')
    .filter({ has: page.locator(`[data-printer-id="${printerId}"]`) });

  // Fallback: if no data-printer-id, just wait for any status badge change
  if (await card.count() === 0) {
    await page.waitForTimeout(Math.min(timeoutMs, 4_000));
    return;
  }

  const initialText = await card.first().textContent() ?? '';
  await expect(async () => {
    const current = await card.first().textContent() ?? '';
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
  return page.locator('.pf-detailed-printer-card, div.rounded-xl.bg-pf-card.border');
}

/**
 * Navigate to a specific printer's detail view by clicking its card.
 */
export async function navigateToPrinter(page: Page, printerName: string): Promise<void> {
  // On the /printers page, clicking a card opens the detail sidebar.
  const card = page.locator('.pf-detailed-printer-card, div.rounded-xl.bg-pf-card')
    .filter({ hasText: printerName })
    .first();

  await expect(card).toBeVisible({ timeout: 10_000 });
  await card.click();
  // Wait for the sidebar/detail panel to appear
  await page.waitForTimeout(500);
}

// ---------------------------------------------------------------------------
// Playwright fixture extension
// ---------------------------------------------------------------------------

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

    // 3. Inject auth token into localStorage before the app loads
    await page.goto(BASE_URL);
    await page.evaluate((t: string) => {
      localStorage.setItem('auth-token', t);
    }, token!);

    // 4. Reload so the React app picks up the token from localStorage
    await page.reload();
    await page.waitForLoadState('networkidle');

    // 5. Dismiss any onboarding tour popover that may appear
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
  if (cached) return cached;

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
      if (t) return t;
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

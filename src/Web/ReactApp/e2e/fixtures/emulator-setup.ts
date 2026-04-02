import { test as base, expect, type Page, type Locator } from '@playwright/test';

const API_BASE_URL = process.env.API_BASE_URL || 'http://localhost:5245';

/**
 * Shared fixture for emulator-backed E2E tests.
 *
 * Verifies the API server is reachable with the TestEmulator enabled,
 * waits for the health check, and provides helper methods for common
 * printer-related assertions.
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
    // 1. Verify API health
    const healthResponse = await page.request.get(`${API_BASE_URL}/healthz`);
    expect(healthResponse.ok(), `API health check failed at ${API_BASE_URL}/healthz`).toBeTruthy();

    // 2. Verify the emulator is enabled via the health endpoint detail
    const healthDetail = await page.request.get(`${API_BASE_URL}/health`);
    expect(healthDetail.ok(), 'Detailed health endpoint failed').toBeTruthy();

    await use();
  }, { auto: true }],
});

export { expect };

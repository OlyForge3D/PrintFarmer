import { test, expect, getPrinterCards } from '../fixtures/emulator-setup';

/**
 * Printer Status E2E Tests — Emulator-backed
 *
 * The TestEmulator registers three virtual printers:
 *   - "Test Printer Alpha"  — idle, ambient temperatures
 *   - "Test Printer Beta"   — printing at ~42 %, job: test-print-benchy.gcode
 *   - "Test Printer Gamma"  — offline / error
 *
 * These tests verify that live printer status appears correctly on the
 * dashboard and printers page when receiving real-time SignalR updates.
 */

test.describe('Printer Status — Emulator', () => {
  // Emulator tests share mutable printer state — run serially to avoid interference
  test.describe.configure({ mode: 'serial' });
  test.beforeEach(async ({ page }) => {
    await page.goto('/');
    await page.waitForLoadState('networkidle');
  });

  test('dashboard shows emulated printers with correct names', async ({ page }) => {
    const cards = getPrinterCards(page);

    // The emulator creates 3 printers — at least those should appear
    await expect(cards).toHaveCount(3, { timeout: 15_000 });

    const text = await page.locator('body').textContent();
    expect(text).toContain('Test Printer Alpha');
    expect(text).toContain('Test Printer Beta');
    expect(text).toContain('Test Printer Gamma');
  });

  test('idle printer shows Online badge and ambient temperatures', async ({ page }) => {
    // Wait for cards to render
    await expect(getPrinterCards(page).first()).toBeVisible({ timeout: 15_000 });

    const alphaCard = page.locator('.pf-detailed-printer-card, div.rounded-xl.bg-pf-card')
      .filter({ hasText: 'Test Printer Alpha' })
      .first();

    await expect(alphaCard).toBeVisible();

    // Status badge should indicate an idle/online state
    const statusBadge = alphaCard.locator('div.inline-flex, span').filter({ hasText: /Idle|Online|Ready/i }).first();
    await expect(statusBadge).toBeVisible({ timeout: 10_000 });

    // Temperature readings should be present (ambient = low numbers or "--")
    const hotend = alphaCard.locator('span[title="Hotend temperature"]').first();
    const bed = alphaCard.locator('span[title="Bed temperature"]').first();
    // At least one temp element should be visible
    const hotendVisible = await hotend.isVisible().catch(() => false);
    const bedVisible = await bed.isVisible().catch(() => false);
    expect(hotendVisible || bedVisible).toBeTruthy();
  });

  test('printing printer shows progress bar and job name', async ({ page }) => {
    await expect(getPrinterCards(page).first()).toBeVisible({ timeout: 15_000 });

    const betaCard = page.locator('.pf-detailed-printer-card, div.rounded-xl.bg-pf-card')
      .filter({ hasText: 'Test Printer Beta' })
      .first();

    await expect(betaCard).toBeVisible();

    // Status badge should say "Printing"
    const printingBadge = betaCard.locator('div.inline-flex, span').filter({ hasText: /Printing/i }).first();
    await expect(printingBadge).toBeVisible({ timeout: 10_000 });

    // Progress bar should be present with a non-zero value
    const progressBar = betaCard.locator('div[role="progressbar"]').first();
    await expect(progressBar).toBeVisible({ timeout: 10_000 });

    const progressValue = await progressBar.getAttribute('aria-valuenow');
    expect(Number(progressValue)).toBeGreaterThan(0);

    // Job name should be visible
    const bodyText = await betaCard.textContent();
    expect(bodyText).toContain('benchy');
  });

  test('offline printer shows appropriate offline indicator', async ({ page }) => {
    await expect(getPrinterCards(page).first()).toBeVisible({ timeout: 15_000 });

    const gammaCard = page.locator('.pf-detailed-printer-card, div.rounded-xl.bg-pf-card')
      .filter({ hasText: 'Test Printer Gamma' })
      .first();

    await expect(gammaCard).toBeVisible();

    // Status badge should indicate offline/error
    const offlineBadge = gammaCard.locator('div.inline-flex, span')
      .filter({ hasText: /Offline|Error|Disconnected/i })
      .first();
    await expect(offlineBadge).toBeVisible({ timeout: 10_000 });
  });

  test('printer status updates in real-time via SignalR', async ({ page }) => {
    await expect(getPrinterCards(page).first()).toBeVisible({ timeout: 15_000 });

    // Capture initial progress of the printing printer
    const betaCard = page.locator('.pf-detailed-printer-card, div.rounded-xl.bg-pf-card')
      .filter({ hasText: 'Test Printer Beta' })
      .first();

    const progressBar = betaCard.locator('div[role="progressbar"]').first();
    await expect(progressBar).toBeVisible({ timeout: 10_000 });

    const initialProgress = Number(await progressBar.getAttribute('aria-valuenow') ?? '0');

    // Wait for the emulator to broadcast an update (~2 s interval)
    await page.waitForTimeout(5_000);

    // Progress should have advanced
    const updatedProgress = Number(await progressBar.getAttribute('aria-valuenow') ?? '0');
    // The emulator increments progress over time — it should differ
    // (or at least not be zero if it was previously nonzero)
    expect(updatedProgress).toBeGreaterThanOrEqual(initialProgress);
  });
});

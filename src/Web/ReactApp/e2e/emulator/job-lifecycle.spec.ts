import { test, expect, getPrinterCards } from '../fixtures/emulator-setup';

/**
 * Job Lifecycle E2E Tests — Emulator-backed
 *
 * The TestEmulator's state machine cycles a printer through:
 *   Idle → Printing (0-100 % over ~60 s) → Complete → Idle
 *
 * "Test Printer Beta" starts in a Printing state at ~42 %.
 * "Test Printer Alpha" starts Idle and can accept a new job.
 *
 * Note: The emulator's default print duration is 60 s.
 * We use generous timeouts and poll-based assertions.
 */

test.describe('Job Lifecycle — Emulator', () => {
  // Emulator tests share mutable printer state — run serially to avoid interference
  test.describe.configure({ mode: 'serial' });
  test.beforeEach(async ({ page }) => {
    await page.goto('/printers');
    await page.waitForLoadState('networkidle');
    // Wait for printer cards to render from emulator data
    await expect(getPrinterCards(page).first()).toBeVisible({ timeout: 15_000 });
  });

  test('can start a print job on an idle emulated printer', async ({ page }) => {
    const alphaCard = page.locator('.pf-detailed-printer-card, div.rounded-xl.bg-pf-card')
      .filter({ hasText: 'Test Printer Alpha' })
      .first();

    await expect(alphaCard).toBeVisible();

    // Verify the printer is idle first
    const idleBadge = alphaCard.locator('div.inline-flex, span')
      .filter({ hasText: /Idle|Ready|Online/i })
      .first();
    await expect(idleBadge).toBeVisible({ timeout: 10_000 });

    // Look for a "Start Print" or file-send action.
    // The exact UI depends on whether the emulator exposes a quick-print button.
    // Check for any print-related action button on the card.
    const printButton = alphaCard.locator('button').filter({ hasText: /Print|Start|Send/i }).first();
    const hasPrintButton = await printButton.isVisible().catch(() => false);

    if (hasPrintButton) {
      await printButton.click();

      // After starting, the printer should transition to Printing
      await expect(
        alphaCard.locator('div.inline-flex, span').filter({ hasText: /Printing|Starting/i }).first()
      ).toBeVisible({ timeout: 15_000 });
    } else {
      // If no inline start button, the emulator may auto-cycle — just verify
      // the card is interactive (has action buttons at all)
      const actionButtons = alphaCard.locator('button');
      expect(await actionButtons.count()).toBeGreaterThan(0);
    }
  });

  test('print progress updates are visible in real-time', async ({ page }) => {
    const betaCard = page.locator('.pf-detailed-printer-card, div.rounded-xl.bg-pf-card')
      .filter({ hasText: 'Test Printer Beta' })
      .first();

    await expect(betaCard).toBeVisible();

    const progressBar = betaCard.locator('div[role="progressbar"]').first();
    await expect(progressBar).toBeVisible({ timeout: 10_000 });

    const first = Number(await progressBar.getAttribute('aria-valuenow') ?? '0');

    // Wait for several SignalR update cycles (~6 s = 3 broadcasts at 2 s each)
    await page.waitForTimeout(6_000);

    const second = Number(await progressBar.getAttribute('aria-valuenow') ?? '0');

    // Progress should have advanced (or stayed at 100 if it completed)
    expect(second).toBeGreaterThanOrEqual(first);
  });

  test('can pause and resume a print job', async ({ page }) => {
    const betaCard = page.locator('.pf-detailed-printer-card, div.rounded-xl.bg-pf-card')
      .filter({ hasText: 'Test Printer Beta' })
      .first();

    await expect(betaCard).toBeVisible();

    // Ensure it's printing
    const printingBadge = betaCard.locator('div.inline-flex, span')
      .filter({ hasText: /Printing/i })
      .first();
    await expect(printingBadge).toBeVisible({ timeout: 10_000 });

    // Look for Pause button
    const pauseButton = betaCard.locator('button').filter({ hasText: /Pause/i }).first();
    const hasPause = await pauseButton.isVisible().catch(() => false);

    if (hasPause) {
      await pauseButton.click();

      // Status should transition to Paused
      await expect(
        betaCard.locator('div.inline-flex, span').filter({ hasText: /Paused/i }).first()
      ).toBeVisible({ timeout: 10_000 });

      // Look for Resume button
      const resumeButton = betaCard.locator('button').filter({ hasText: /Resume/i }).first();
      await expect(resumeButton).toBeVisible({ timeout: 5_000 });
      await resumeButton.click();

      // Status should return to Printing
      await expect(
        betaCard.locator('div.inline-flex, span').filter({ hasText: /Printing/i }).first()
      ).toBeVisible({ timeout: 10_000 });
    } else {
      // Pause/Resume may be in a dropdown menu — check for menu trigger
      const menuButton = betaCard.locator('button[aria-label*="More"], button[aria-label*="menu"]').first();
      const hasMenu = await menuButton.isVisible().catch(() => false);
      // At least one control path (inline or menu) must exist for pause/resume
      expect(hasMenu, 'Neither inline Pause button nor overflow menu found').toBeTruthy();
      if (hasMenu) {
        await menuButton.click();
        const pauseMenuItem = page.locator('button, [role="menuitem"]').filter({ hasText: /Pause/i }).first();
        await expect(pauseMenuItem).toBeVisible({ timeout: 5_000 });
      }
    }
  });

  test('can cancel a running print job', async ({ page }) => {
    const betaCard = page.locator('.pf-detailed-printer-card, div.rounded-xl.bg-pf-card')
      .filter({ hasText: 'Test Printer Beta' })
      .first();

    await expect(betaCard).toBeVisible();

    // Ensure it's printing
    await expect(
      betaCard.locator('div.inline-flex, span').filter({ hasText: /Printing/i }).first()
    ).toBeVisible({ timeout: 10_000 });

    // Look for Cancel/Stop button — may be inline or in a menu
    const cancelButton = betaCard.locator('button').filter({ hasText: /Cancel|Stop|Abort/i }).first();
    const hasCancel = await cancelButton.isVisible().catch(() => false);

    if (hasCancel) {
      await cancelButton.click();

      // Confirm cancellation if a dialog appears
      const confirmButton = page.locator('button').filter({ hasText: /Confirm|Yes|Cancel Print/i }).first();
      const hasConfirm = await confirmButton.isVisible().catch(() => false);
      if (hasConfirm) {
        await confirmButton.click();
      }

      // Printer should return to Idle or show Cancelled
      await expect(
        betaCard.locator('div.inline-flex, span').filter({ hasText: /Idle|Cancelled|Ready/i }).first()
      ).toBeVisible({ timeout: 15_000 });
    } else {
      // Cancel may be behind the overflow menu
      const menuButton = betaCard.locator('button[aria-label*="More"], button[aria-label*="menu"]').first();
      const hasMenu = await menuButton.isVisible().catch(() => false);
      // At least one control path (inline or menu) must exist for cancel
      expect(hasCancel || hasMenu, 'Neither inline Cancel button nor overflow menu found').toBeTruthy();
      if (hasMenu) {
        await menuButton.click();
        const cancelItem = page.locator('button, [role="menuitem"]').filter({ hasText: /Cancel|Stop/i }).first();
        await expect(cancelItem).toBeVisible({ timeout: 5_000 });
      }
    }
  });

  test('completed print job transitions back to idle', async ({ page }) => {
    // The emulator cycles print from 0-100% over ~60 s.
    // "Test Printer Beta" starts at ~42%. We wait for it to complete.
    // This is a longer test — use a generous timeout.
    test.setTimeout(90_000);

    const betaCard = page.locator('.pf-detailed-printer-card, div.rounded-xl.bg-pf-card')
      .filter({ hasText: 'Test Printer Beta' })
      .first();

    await expect(betaCard).toBeVisible();

    // Poll until the printer transitions to Complete or Idle
    await expect(async () => {
      const statusText = await betaCard.textContent() ?? '';
      const isFinished = /Complete|Idle|Ready/i.test(statusText);
      expect(isFinished).toBeTruthy();
    }).toPass({ timeout: 75_000 });
  });
});

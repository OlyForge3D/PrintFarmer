import { test, expect } from '../fixtures/emulator-setup';

const CAMERA_STREAM_ROUTE = '**/api/cameras/*/stream';

/**
 * Cameras Page E2E Tests — Emulator-backed
 *
 * Tests the canonical top-level /cameras route (#3332):
 *   - Camera grid display with mock camera URLs from emulator
 *   - View/Manage tab switching and All/By location/By printer views
 *   - Camera cards with health badges
 *   - Admin camera management
 */

test.describe('Cameras — Emulator', () => {
  test.describe.configure({ mode: 'serial' });

  let consoleErrors: string[] = [];

  test.beforeEach(async ({ page }) => {
    consoleErrors = [];
    page.on('pageerror', (error) => consoleErrors.push(error.message));
    const jpeg = await page.evaluate(() => {
      const canvas = document.createElement('canvas');
      canvas.width = canvas.height = 2;
      const context = canvas.getContext('2d');
      if (!context) throw new Error('Canvas unavailable for deterministic camera fixture');
      context.fillStyle = '#123456';
      context.fillRect(0, 0, 2, 2);
      return canvas.toDataURL('image/jpeg').split(',')[1];
    });
    await page.route(CAMERA_STREAM_ROUTE, async (route) => {
      await route.fulfill({
        status: 200,
        contentType: 'image/jpeg',
        body: Buffer.from(jpeg, 'base64'),
      });
    });
    await page.goto('/cameras');
    await page.waitForLoadState('networkidle');
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

  test('cameras page loads with heading', async ({ page }) => {
    // Cameras is a first-class top-level page, no longer an admin settings sub-page.
    await expect(page.getByRole('heading', { level: 1, name: 'Cameras', exact: true })).toBeVisible();
    await expect(page).toHaveURL('/cameras');
    await expect(page.getByRole('group', { name: 'Camera view' })).toBeVisible();

    expect(criticalErrors()).toHaveLength(0);
  });

  test('cameras page starts in view mode', async ({ page }) => {
    await expect(page.getByRole('button', { name: 'Manage', exact: true })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Refresh', exact: true })).toBeVisible();

    expect(criticalErrors()).toHaveLength(0);
  });

  test('cameras display grid or empty state', async ({ page }) => {
    await page.waitForTimeout(1_500);

    const cameraCards = page.getByRole('article', { name: / camera$/ });
    const emptyState = page.getByRole('heading', { name: 'No Cameras Configured', exact: true });

    const hasCards = (await cameraCards.count()) > 0;
    const hasEmpty = await emptyState.isVisible().catch(() => false);

    // One of these should be true
    expect(hasCards || hasEmpty).toBeTruthy();

    expect(criticalErrors()).toHaveLength(0);
  });

  test('admin can see manage tab', async ({ page }) => {
    const manageTab = page.getByRole('button', { name: 'Manage', exact: true });
    await expect(manageTab).toBeVisible();
    await manageTab.click();

    await expect(page.getByRole('button', { name: 'View Cameras', exact: true })).toBeVisible();
    await expect(page.getByRole('heading', { name: 'Standalone Cameras', exact: true })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Add Camera', exact: true })).toBeVisible();

    expect(criticalErrors()).toHaveLength(0);
  });

  test('camera cards show health indicators', async ({ page }) => {
    await page.waitForTimeout(1_500);

    const cameraCards = page.getByRole('article', { name: / camera$/ });
    const cameraCount = await cameraCards.count();
    expect(cameraCount).toBeGreaterThan(0);

    for (let index = 0; index < cameraCount; index++) {
      await expect(
        cameraCards.nth(index).getByRole('status', { name: /^(Probe (healthy|degraded|unhealthy|unknown)|Preview failed · probe (healthy|degraded|unhealthy|unknown))$/ }),
      ).toBeVisible();
      await expect(cameraCards.nth(index).getByText('Standalone', { exact: true })).toHaveCount(0);
    }

    expect(criticalErrors()).toHaveLength(0);
  });

  test('camera stream frames decode a deterministic media fixture', async ({ page }) => {
    const cameras = page.getByRole('article', { name: / camera$/ });
    await expect.poll(() => cameras.count()).toBeGreaterThan(0);

    const streamCount = await cameras.count();
    for (let index = 0; index < streamCount; index++) {
      const camera = cameras.nth(index);
      await camera.scrollIntoViewIfNeeded();
      const streamMode = camera.getByRole('button', { name: 'Stream mode', exact: true });
      await streamMode.click();
      await expect(streamMode).toHaveAttribute('aria-pressed', 'true');
      const image = camera.getByRole('img', { name: / live camera feed$/ });
      await expect(image).toBeVisible();
      await expect.poll(
        () => image.evaluate((element: HTMLImageElement) =>
          element.complete && element.naturalWidth === 2 && element.naturalHeight === 2
            && element.src.startsWith('blob:')
        ),
        { message: `Camera stream fixture ${index + 1} should decode through the authenticated stream` },
      ).toBe(true);
    }

    expect(criticalErrors()).toHaveLength(0);
  });

  test('no critical JS errors on cameras page', async ({ page }) => {
    await page.waitForTimeout(2_000);
    expect(criticalErrors()).toHaveLength(0);
  });
});

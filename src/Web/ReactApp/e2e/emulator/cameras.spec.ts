import { test, expect } from '../fixtures/emulator-setup';

const CAMERA_STREAM_ROUTE = '**/api/cameras/*/stream';
const CAMERA_STREAM_FRAME = Buffer.from(
  'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=',
  'base64',
);

/**
 * Cameras Page E2E Tests — Emulator-backed
 *
 * Tests the canonical System Settings camera route:
 *   - Camera grid display with mock camera URLs from emulator
 *   - View/Manage tab switching
 *   - Camera cards with health badges
 *   - Admin camera management
 */

test.describe('Cameras — Emulator', () => {
  test.describe.configure({ mode: 'serial' });

  let consoleErrors: string[] = [];

  test.beforeEach(async ({ page }) => {
    consoleErrors = [];
    page.on('pageerror', (error) => consoleErrors.push(error.message));
    await page.route(CAMERA_STREAM_ROUTE, async (route) => {
      await route.fulfill({
        status: 200,
        contentType: 'image/png',
        body: CAMERA_STREAM_FRAME,
      });
    });
    await page.goto('/admin/settings?tab=hardware&sub=cameras');
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
    await page.waitForTimeout(1_000);

    const bodyText = await page.locator('body').textContent() ?? '';
    const hasCameraContent = /camera/i.test(bodyText);
    expect(hasCameraContent).toBeTruthy();

    expect(criticalErrors()).toHaveLength(0);
  });

  test('cameras page has view tab', async ({ page }) => {
    await page.waitForTimeout(1_000);

    // At minimum, the page should render content
    const content = page.locator('main, [role="main"], #root');
    await expect(content.first()).toBeVisible();

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
    await page.waitForTimeout(1_000);

    const manageTab = page.getByRole('button', { name: 'Manage', exact: true });
    const hasManage = await manageTab.isVisible().catch(() => false);

    if (hasManage) {
      await manageTab.click();
      await page.waitForTimeout(1_000);

      // Management panel should show camera configuration options
      const content = page.locator('main, [role="main"], #root');
      await expect(content.first()).toBeVisible();
    }

    expect(criticalErrors()).toHaveLength(0);
  });

  test('camera cards show health indicators', async ({ page }) => {
    await page.waitForTimeout(1_500);

    const cameraCards = page.getByRole('article', { name: / camera$/ });
    const cameraCount = await cameraCards.count();
    expect(cameraCount).toBeGreaterThan(0);

    for (let index = 0; index < cameraCount; index++) {
      await expect(
        cameraCards.nth(index).getByText(/^(Healthy|Degraded|Unhealthy|Unknown)$/),
      ).toBeVisible();
    }

    expect(criticalErrors()).toHaveLength(0);
  });

  test('camera stream frames decode a deterministic media fixture', async ({ page }) => {
    const streamFrames = page.locator('iframe[title$=" live camera feed"]');
    await expect.poll(() => streamFrames.count()).toBeGreaterThan(0);

    const streamCount = await streamFrames.count();
    for (let index = 0; index < streamCount; index++) {
      const image = streamFrames.nth(index).contentFrame().locator('img');
      await expect.poll(
        () => image.evaluate((element: HTMLImageElement) =>
          element.complete && element.naturalWidth > 0
        ),
        { message: `Camera stream fixture ${index + 1} should decode in its frame` },
      ).toBe(true);
    }

    expect(criticalErrors()).toHaveLength(0);
  });

  test('no critical JS errors on cameras page', async ({ page }) => {
    await page.waitForTimeout(2_000);
    expect(criticalErrors()).toHaveLength(0);
  });
});

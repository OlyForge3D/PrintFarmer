import { expect, test } from '@playwright/test';
import type { Page, Route } from '@playwright/test';

// Regression test for #1753: the first-run setup wizard is rendered in place
// of the whole app shell (see App.tsx) before any authenticated layout - and
// its own internal scroll regions - exist. It therefore inherits #root's
// `height:100vh; overflow:hidden` from App.css directly. The wizard's
// previous markup centered its card with `min-h-screen flex items-center
// justify-center` on that *same* element, which also would have needed to be
// the scrollable one - a classic CSS bug where a flex container that both
// centers its content and clips overflow can never reveal content that
// overflows past its "start" edge, no matter how tall that content grows.
// On narrow viewports the (comparatively tall) multi-field account step
// overflowed upward past the top of the fixed-height #root with no way to
// scroll to it, clipping the heading and leaving the "Create Admin &
// Continue" submit button unreachable, per the issue's screenshot and repro
// steps at 320x568.
//
// This test drives the real app (not a synthetic harness) so `#root` carries
// its real App.css rules, and only stubs the network calls the setup flow
// makes on mount so `needsSetup: true` is deterministic without a live API.
async function mockSetupApi(page: Page): Promise<void> {
  await page.route('/api/**', (route: Route) => {
    const url = new URL(route.request().url());

    if (url.pathname.endsWith('/api/setup/status')) {
      return route.fulfill({
        contentType: 'application/json',
        body: JSON.stringify({ needsSetup: true }),
      });
    }
    if (url.pathname.endsWith('/api/health')) {
      return route.fulfill({
        contentType: 'application/json',
        body: JSON.stringify({ status: 'ok' }),
      });
    }
    if (url.pathname.endsWith('/api/settings/NetworkDiscovery')) {
      return route.fulfill({
        contentType: 'application/json',
        body: JSON.stringify({
          enableDiscovery: true,
          discoverySubnets: ['10.0.0.0/24'],
          clientTimeoutMs: 200,
          requestDelayMs: 100,
          maxConcurrentRequests: 20,
          maxRetries: 2,
          ports: [80],
        }),
      });
    }
    if (url.pathname.endsWith('/api/setup/bootstrap')) {
      // 404 is handled gracefully by SetupWizard (no baseUrl to prefill) and
      // keeps this test focused on layout rather than Spoolman bootstrapping.
      return route.fulfill({ status: 404, contentType: 'application/json', body: '{}' });
    }

    // Any other call the wizard doesn't make on mount (Spoolman scan,
    // account creation, etc.) is irrelevant to this layout regression test.
    return route.fulfill({ status: 404, contentType: 'application/json', body: '{}' });
  });
}

test.describe('Setup wizard mobile viewport scrolling (#1753)', () => {
  test('heading and submit button are reachable by scrolling at 320x568', async ({ page }) => {
    test.setTimeout(60_000);
    await page.setViewportSize({ width: 320, height: 568 });
    await mockSetupApi(page);
    await page.goto('/');

    const heading = page.getByRole('heading', { name: 'Welcome to PrintFarmer' });
    const submitButton = page.getByRole('button', { name: 'Create Admin & Continue' });
    await expect(heading).toBeAttached();
    await expect(submitButton).toBeAttached();

    // #root itself stays fixed at 100vh with overflow:hidden (App.css) - by
    // design, since the authenticated app shell manages its own internal
    // scroll panes. The fix's scroll container is the wizard's own
    // `overflow-y-auto` div nested inside #root, which must be able to grow
    // taller than the viewport and expose a working scroll path - the root
    // cause of #1753 was that this element also centered its content
    // (`flex items-center`), which made that impossible regardless of
    // content height.
    const scrollContainer = page.locator('#root > .overflow-y-auto').first();
    await expect(scrollContainer).toBeAttached();
    const { scrollHeight, clientHeight } = await scrollContainer.evaluate((el) => ({
      scrollHeight: el.scrollHeight,
      clientHeight: el.clientHeight,
    }));
    expect(
      scrollHeight,
      'setup wizard content must be able to overflow its scroll container so it can be scrolled to',
    ).toBeGreaterThan(clientHeight);

    // Scroll the wizard's own scroll container all the way down and confirm
    // both the heading and the submit button - at opposite ends of the form
    // - end up in the viewport.
    await scrollContainer.evaluate((el) => {
      el.scrollTop = 0;
    });
    await expect(heading).toBeInViewport();

    await scrollContainer.evaluate((el) => {
      el.scrollTop = el.scrollHeight;
    });
    await expect(submitButton).toBeInViewport();

    // No horizontal scroll should be introduced by the fix.
    const hasHorizontalScroll = await page.evaluate(
      () => document.documentElement.scrollWidth > document.documentElement.clientWidth,
    );
    expect(hasHorizontalScroll, 'setup wizard must not introduce horizontal scroll').toBeFalsy();
  });

  test('desktop layout remains centered and unscrolled at 1280x800', async ({ page }) => {
    test.setTimeout(60_000);
    await page.setViewportSize({ width: 1280, height: 800 });
    await mockSetupApi(page);
    await page.goto('/');

    const heading = page.getByRole('heading', { name: 'Welcome to PrintFarmer' });
    const submitButton = page.getByRole('button', { name: 'Create Admin & Continue' });

    // On desktop the whole account step fits comfortably within the
    // viewport without any scrolling, so both ends of the form should be
    // reachable immediately - this is the "desktop layout remains
    // unchanged" acceptance criterion.
    await expect(heading).toBeInViewport();
    await expect(submitButton).toBeInViewport();
  });
});

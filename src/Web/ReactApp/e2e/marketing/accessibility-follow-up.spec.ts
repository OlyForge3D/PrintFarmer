import { expect, test, type Browser, type Page } from '@playwright/test';
import { captureOrigin, isolateCapture, type CaptureTheme } from './fixtures';

type MotionPreference = 'no-preference' | 'reduce';
type ContrastPreference = 'normal' | 'high';

const routeCases = [
  { name: 'dashboard', path: '/dashboard', heading: 'Printer Dashboard', dashboard: true },
  { name: 'printers', path: '/printers', heading: 'Printers' },
  {
    name: 'settings-appearance',
    path: '/settings?scope=user&tab=profile&sub=appearance',
    heading: /Settings/,
  },
] as const;

const themeContrastCases = [
  { route: routeCases[0], theme: 'dark', contrast: 'normal' },
  { route: routeCases[1], theme: 'light', contrast: 'normal' },
  { route: routeCases[2], theme: 'forge', contrast: 'high' },
] as const;

async function openIsolatedRoute(
  browser: Browser,
  route: (typeof routeCases)[number],
  theme: CaptureTheme,
  contrast: ContrastPreference,
  motion: MotionPreference,
) {
  const context = await browser.newContext({
    baseURL: captureOrigin,
    viewport: { width: 1440, height: 1000 },
    deviceScaleFactor: 1,
    locale: 'en-US',
    timezoneId: 'UTC',
    colorScheme: theme === 'light' ? 'light' : 'dark',
    reducedMotion: motion,
    serviceWorkers: 'block',
  });
  const page = await context.newPage();
  const { violations } = await isolateCapture(context, {
    theme,
    contrastPreference: contrast,
    dashboard: 'dashboard' in route && route.dashboard,
  });
  await page.emulateMedia({
    colorScheme: theme === 'light' ? 'light' : 'dark',
    reducedMotion: motion,
  });
  await page.goto(route.path);
  await expect(page.getByRole('heading', { name: route.heading, exact: true })).toBeVisible();
  return { context, page, violations };
}

test.describe('Accessibility follow-up evidence (#3734)', () => {
  test('distinguishes normal motion from reduced motion on a real route', async ({ browser }) => {
    const normal = await openIsolatedRoute(browser, routeCases[0], 'dark', 'normal', 'no-preference');
    const reduced = await openIsolatedRoute(browser, routeCases[0], 'dark', 'normal', 'reduce');

    try {
      const readMotionState = (page: Page) => page.evaluate(() => ({
        media: matchMedia('(prefers-reduced-motion: reduce)').matches,
        motionSafe: document.documentElement.style.getPropertyValue('--motion-safe'),
        theme: document.documentElement.dataset.theme,
        contrast: document.documentElement.dataset.contrast,
      }));
      await expect.poll(() => readMotionState(normal.page)).toEqual({
        media: false,
        motionSafe: '',
        theme: 'dark',
        contrast: 'normal',
      });
      await expect.poll(() => readMotionState(reduced.page)).toEqual({
        media: true,
        motionSafe: '0',
        theme: 'dark',
        contrast: 'normal',
      });
      expect(normal.violations).toEqual([]);
      expect(reduced.violations).toEqual([]);
    } finally {
      await normal.context.close();
      await reduced.context.close();
    }
  });

  for (const { route, theme, contrast } of themeContrastCases) {
    test(`${route.name} renders in representative ${theme}/${contrast} combination`, async ({ browser }) => {
      const isolated = await openIsolatedRoute(browser, route, theme, contrast, 'reduce');
      try {
        await expect(isolated.page.locator('html')).toHaveAttribute('data-theme', theme);
        await expect(isolated.page.locator('html')).toHaveAttribute(
          'data-contrast',
          contrast,
        );
        const horizontalOverflow = await isolated.page.evaluate(() =>
          Math.max(document.documentElement.scrollWidth, document.body.scrollWidth) -
          document.documentElement.clientWidth,
        );
        expect(horizontalOverflow, `${route.name} ${theme}/${contrast} overflow`).toBeLessThanOrEqual(1);
        expect(isolated.violations).toEqual([]);
      } finally {
        await isolated.context.close();
      }
    });
  }
});

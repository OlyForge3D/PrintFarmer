import { expect, test } from '@playwright/test';
import type { Page } from '@playwright/test';
import { SELECTABLE_THEMES } from '../src/design-system/themes/registry';

const AA_NORMAL_TEXT = 4.5;

interface ColorPair {
  background: string;
  foreground: string;
}

const parseRgb = (color: string): readonly [number, number, number] => {
  const channels = color.match(/^rgba?\((\d+),\s*(\d+),\s*(\d+)(?:,\s*1)?\)$/);
  if (!channels) throw new Error(`Expected an opaque rgb color, received "${color}"`);
  return [Number(channels[1]), Number(channels[2]), Number(channels[3])];
};

const relativeLuminance = (color: string): number => {
  const linear = (channel: number): number => {
    const normalized = channel / 255;
    return normalized <= 0.04045
      ? normalized / 12.92
      : ((normalized + 0.055) / 1.055) ** 2.4;
  };
  const [red, green, blue] = parseRgb(color).map(linear);
  return 0.2126 * red + 0.7152 * green + 0.0722 * blue;
};

const contrastRatio = ({ background, foreground }: ColorPair): number => {
  const backgroundLuminance = relativeLuminance(background);
  const foregroundLuminance = relativeLuminance(foreground);
  const lighter = Math.max(backgroundLuminance, foregroundLuminance);
  const darker = Math.min(backgroundLuminance, foregroundLuminance);
  return (lighter + 0.05) / (darker + 0.05);
};

async function mountGcodeFileBrowser(page: Page) {
  await page.route('/', (route) =>
    route.fulfill({
      contentType: 'text/html',
      body: `<!doctype html>
        <html data-theme="dark">
          <head><link rel="stylesheet" href="/src/index.css"></head>
          <body>
            <div id="gcode-root"></div>
            <span id="error-text-reference" class="text-pf-error-text"></span>
          </body>
        </html>`,
    }),
  );
  await page.route('/api/**', (route) =>
    route.fulfill({
      contentType: 'application/json',
      body: JSON.stringify({ files: [], folders: [], items: [], totalCount: 0 }),
    }),
  );
  await page.goto('/');
  await page.addScriptTag({
    type: 'module',
    content: `
      import RefreshRuntime from '/@react-refresh';
      RefreshRuntime.injectIntoGlobalHook(window);
      window.$RefreshReg$ = () => {};
      window.$RefreshSig$ = () => (type) => type;
      window.__vite_plugin_react_preamble_installed__ = true;
    `,
  });
  await page.addStyleTag({
    content: '* { transition: none !important; animation: none !important; }',
  });
  await page.addScriptTag({
    type: 'module',
    content: `
      import React from '/node_modules/.vite/deps/react.js';
      import ReactDom from '/node_modules/.vite/deps/react-dom_client.js';
      import {
        QueryClient,
        QueryClientProvider,
      } from '/node_modules/.vite/deps/@tanstack_react-query.js';
      import { AuthContext } from '/src/common/contexts/AuthContext.tsx';
      import { GcodeFileBrowser } from '/src/features/gcode/components/GcodeFileBrowser.tsx';

      const auth = {
        hasPermission: () => true,
        hasRole: () => true,
        user: { id: 'contrast-fixture', username: 'contrast-fixture', roles: [] },
        isAuthenticated: true,
        isLoading: false,
      };
      const queryClient = new QueryClient({
        defaultOptions: { queries: { retry: false } },
      });

      ReactDom.createRoot(document.getElementById('gcode-root')).render(
        React.createElement(
          QueryClientProvider,
          { client: queryClient },
          React.createElement(
            AuthContext.Provider,
            { value: auth },
            React.createElement(GcodeFileBrowser, {
              selectedFileIds: ['contrast-fixture.gcode'],
            }),
          ),
        ),
      );
    `,
  });
}

const readColors = async (page: Page): Promise<ColorPair> =>
  page.getByRole('button', { name: 'Delete (1)' }).evaluate((button) => {
    const style = getComputedStyle(button);
    return {
      background: style.backgroundColor,
      foreground: style.color,
    };
  });

test('real bulk-delete button text clears AA at rest and hover in every palette', async ({
  page,
}) => {
  await mountGcodeFileBrowser(page);
  const button = page.getByRole('button', { name: 'Delete (1)' });
  await expect(button).toBeVisible();

  for (const theme of SELECTABLE_THEMES) {
    await page.evaluate((selectedTheme) => {
      document.documentElement.dataset.theme = selectedTheme;
    }, theme);
    await page.mouse.move(0, 0);
    const rest = await readColors(page);
    const errorForeground = await page.locator('#error-text-reference').evaluate(
      (element) => getComputedStyle(element).color,
    );

    await button.hover();
    const hover = await readColors(page);

    expect(
      contrastRatio(rest),
      `${theme} rest: ${rest.foreground} on ${rest.background}`,
    ).toBeGreaterThanOrEqual(AA_NORMAL_TEXT);
    expect(
      contrastRatio(hover),
      `${theme} hover: ${hover.foreground} on ${hover.background}`,
    ).toBeGreaterThanOrEqual(AA_NORMAL_TEXT);
    expect(rest.foreground, `${theme} rest uses the semantic destructive foreground`).toBe(
      errorForeground,
    );
    expect(hover.foreground, `${theme} hover uses the semantic destructive foreground`).toBe(
      errorForeground,
    );
    expect(hover, `${theme} retains visible hover feedback`).not.toEqual(rest);
  }
});

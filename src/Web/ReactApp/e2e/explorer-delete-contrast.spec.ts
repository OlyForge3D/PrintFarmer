import { expect, test } from '@playwright/test';
import type { Locator, Page } from '@playwright/test';
import { SELECTABLE_THEMES } from '../src/design-system/themes/registry';

const AA_NORMAL_TEXT = 4.5;

interface Rgba {
  red: number;
  green: number;
  blue: number;
  alpha: number;
}

interface ContrastSnapshot {
  foreground: Rgba;
  background: Rgba;
  ratio: number;
  boxShadow: string;
  outlineStyle: string;
}

const relativeLuminance = ({ red, green, blue }: Rgba): number => {
  const channels = [red, green, blue].map((channel) => {
    const value = channel / 255;
    return value <= 0.04045 ? value / 12.92 : ((value + 0.055) / 1.055) ** 2.4;
  });

  return 0.2126 * channels[0] + 0.7152 * channels[1] + 0.0722 * channels[2];
};

const contrastRatio = (foreground: Rgba, background: Rgba): number => {
  const foregroundLuminance = relativeLuminance(foreground);
  const backgroundLuminance = relativeLuminance(background);
  const lighter = Math.max(foregroundLuminance, backgroundLuminance);
  const darker = Math.min(foregroundLuminance, backgroundLuminance);
  return (lighter + 0.05) / (darker + 0.05);
};

async function mountExplorerHarness(page: Page): Promise<Locator> {
  await page.goto('/');
  await page.evaluate(() => {
    document.body.innerHTML = '<div id="explorer-contrast-root" style="height: 600px"></div>';
  });
  await page.addStyleTag({
    content: '* { transition: none !important; animation: none !important; }',
  });
  await page.addScriptTag({
    type: 'module',
    content: `
      import React from '/node_modules/.vite/deps/react.js';
      import ReactDom from '/node_modules/.vite/deps/react-dom_client.js';
      import { ExplorerView } from '/src/features/fileBrowser/components/ExplorerView.tsx';

      const noop = () => {};
      const folders = [{
        path: '/',
        name: 'Root',
        children: [{ path: '/models', name: 'Models', children: [] }],
      }];
      const props = {
        folders,
        files: [],
        selectedIds: [],
        onToggle: noop,
        onSelectAll: noop,
        onNavigate: noop,
        currentPath: '/',
        renderItemActions: () => null,
        sortBy: 'fileName',
        sortOrder: 'asc',
        onSort: noop,
        page: 1,
        totalPages: 1,
        onPageChange: noop,
        pageSize: 25,
        onPageSizeChange: noop,
        columns: [{ key: 'fileName', label: 'Name', sortable: true }],
      };

      ReactDom.createRoot(document.getElementById('explorer-contrast-root')).render(
        React.createElement(ExplorerView, props)
      );
      requestAnimationFrame(() => requestAnimationFrame(() => {
        window.__explorerContrastHarnessReady = true;
      }));
    `,
  });
  await page.waitForFunction(() => {
    return (window as Window & { __explorerContrastHarnessReady?: boolean })
      .__explorerContrastHarnessReady === true;
  });

  await page.getByRole('button', { name: 'Models', exact: true }).click({ button: 'right' });
  const deleteAction = page.getByRole('button', { name: 'Delete Folder', exact: true });
  await expect(deleteAction).toBeVisible();
  return deleteAction;
}

async function readContrast(element: Locator): Promise<ContrastSnapshot> {
  const colors = await element.evaluate((target) => {
    const parseColor = (value: string): Rgba => {
      const channels = value.match(/[\d.]+/g)?.map(Number);
      if (!channels || channels.length < 3) {
        throw new Error(`Unsupported computed color: ${value}`);
      }
      return {
        red: channels[0],
        green: channels[1],
        blue: channels[2],
        alpha: channels[3] ?? 1,
      };
    };
    const composite = (foreground: Rgba, background: Rgba): Rgba => {
      const alpha = foreground.alpha + background.alpha * (1 - foreground.alpha);
      if (alpha === 0) return { red: 0, green: 0, blue: 0, alpha: 0 };
      return {
        red: (
          foreground.red * foreground.alpha
          + background.red * background.alpha * (1 - foreground.alpha)
        ) / alpha,
        green: (
          foreground.green * foreground.alpha
          + background.green * background.alpha * (1 - foreground.alpha)
        ) / alpha,
        blue: (
          foreground.blue * foreground.alpha
          + background.blue * background.alpha * (1 - foreground.alpha)
        ) / alpha,
        alpha,
      };
    };

    let background: Rgba = { red: 0, green: 0, blue: 0, alpha: 0 };
    let current: Element | null = target;
    while (current && background.alpha < 1) {
      background = composite(background, parseColor(getComputedStyle(current).backgroundColor));
      current = current.parentElement;
    }

    const style = getComputedStyle(target);
    return {
      foreground: composite(parseColor(style.color), background),
      background,
      boxShadow: style.boxShadow,
      outlineStyle: style.outlineStyle,
    };
  });

  return {
    ...colors,
    ratio: contrastRatio(colors.foreground, colors.background),
  };
}

test.describe('ExplorerView delete contrast (#1141)', () => {
  test('real resting, hover, and keyboard-focus states clear WCAG AA in every palette', async ({
    page,
  }) => {
    test.setTimeout(60_000);
    const deleteAction = await mountExplorerHarness(page);
    const measurements: string[] = [];

    for (const theme of SELECTABLE_THEMES) {
      await page.evaluate((selectedTheme) => {
        document.documentElement.dataset.theme = selectedTheme;
        (document.activeElement as HTMLElement | null)?.blur();
      }, theme);
      await page.mouse.move(0, 0);
      const resting = await readContrast(deleteAction);

      await deleteAction.hover();
      const hover = await readContrast(deleteAction);

      await page.mouse.move(1200, 700);
      await deleteAction.focus();
      await page.keyboard.press('Shift+Tab');
      await page.keyboard.press('Tab');
      await expect(deleteAction).toBeFocused();
      const focus = await readContrast(deleteAction);

      measurements.push(
        `${theme}: rest ${resting.ratio.toFixed(2)}:1, `
        + `hover ${hover.ratio.toFixed(2)}:1, focus ${focus.ratio.toFixed(2)}:1`,
      );
      expect(resting.ratio, `${theme} resting contrast`).toBeGreaterThanOrEqual(AA_NORMAL_TEXT);
      expect(hover.ratio, `${theme} hover contrast`).toBeGreaterThanOrEqual(AA_NORMAL_TEXT);
      expect(focus.ratio, `${theme} focus contrast`).toBeGreaterThanOrEqual(AA_NORMAL_TEXT);
      expect(hover.background, `${theme} hover affordance`).not.toEqual(resting.background);
      expect(
        focus.boxShadow !== 'none' || focus.outlineStyle !== 'none',
        `${theme} keyboard focus indicator`,
      ).toBe(true);
    }

    await test.info().attach('contrast-measurements', {
      body: measurements.join('\n'),
      contentType: 'text/plain',
    });
  });
});

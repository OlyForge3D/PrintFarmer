import { expect, test } from '@playwright/test';
import type { Page } from '@playwright/test';
import { SELECTABLE_THEMES } from '../src/design-system/themes/registry';

interface ChipStyle {
  backgroundColor: string;
  borderColor: string;
  containerBackgroundColor: string;
  scale: string;
}

const MIN_NON_TEXT_CONTRAST = 3;

const relativeLuminance = (color: string): number => {
  const channels = color.match(/\d+(?:\.\d+)?/g)?.slice(0, 3).map(Number);
  if (!channels || channels.length !== 3) {
    throw new Error(`Unsupported computed color: ${color}`);
  }
  const [red, green, blue] = channels.map((channel) => {
    const normalized = channel / 255;
    return normalized <= 0.04045
      ? normalized / 12.92
      : ((normalized + 0.055) / 1.055) ** 2.4;
  });
  return 0.2126 * red + 0.7152 * green + 0.0722 * blue;
};

const contrastRatio = (first: string, second: string): number => {
  const firstLuminance = relativeLuminance(first);
  const secondLuminance = relativeLuminance(second);
  const lighter = Math.max(firstLuminance, secondLuminance);
  const darker = Math.min(firstLuminance, secondLuminance);
  return (lighter + 0.05) / (darker + 0.05);
};

async function mountModelFiltersBar(page: Page) {
  await page.goto('/');
  await page.evaluate(() => {
    document.body.innerHTML = '<div id="model-filters-root"></div>';
  });
  await page.addStyleTag({
    content: '* { transition: none !important; animation: none !important; }',
  });
  await page.addScriptTag({
    type: 'module',
    content: `
      import React from '/node_modules/.vite/deps/react.js';
      import ReactDom from '/node_modules/.vite/deps/react-dom_client.js';
      import ModelFiltersBar from '/src/features/queue/components/ModelFiltersBar.tsx';

      ReactDom.createRoot(document.getElementById('model-filters-root')).render(
        React.createElement(ModelFiltersBar, {
          models: [],
          selectedModel: null,
          onModelChange: () => {},
          selectedStatuses: ['queued'],
          onStatusChange: () => {},
          sortBy: 'name',
          onSortChange: () => {},
          onRefresh: () => {},
          isLoading: false,
        })
      );
      requestAnimationFrame(() => requestAnimationFrame(() => {
        window.__modelFiltersHarnessReady = true;
      }));
    `,
  });
  await page.waitForFunction(() => {
    return (window as Window & { __modelFiltersHarnessReady?: boolean })
      .__modelFiltersHarnessReady === true;
  });
}

async function readChipStyle(page: Page): Promise<ChipStyle> {
  return page.getByRole('button', { name: 'Printing' }).evaluate((button) => {
    const container = button.closest('.bg-pf-bg-1');
    if (!container) throw new Error('ModelFiltersBar container was not found');
    const buttonStyle = getComputedStyle(button);
    return {
      backgroundColor: buttonStyle.backgroundColor,
      borderColor: buttonStyle.borderColor,
      containerBackgroundColor: getComputedStyle(container).backgroundColor,
      scale: buttonStyle.scale,
    };
  });
}

test.describe('ModelFiltersBar status-chip contrast (#1139)', () => {
  test('computed boundary and hover affordance pass in every shipping palette', async ({
    page,
  }) => {
    await mountModelFiltersBar(page);
    const chip = page.getByRole('button', { name: 'Printing' });

    for (const theme of SELECTABLE_THEMES) {
      await page.mouse.move(0, 0);
      await page.evaluate((selectedTheme) => {
        document.documentElement.dataset.theme = selectedTheme;
      }, theme);

      const rest = await readChipStyle(page);
      expect(
        contrastRatio(rest.borderColor, rest.backgroundColor),
        `${theme} rest boundary against chip fill`,
      ).toBeGreaterThanOrEqual(MIN_NON_TEXT_CONTRAST);
      expect(
        contrastRatio(rest.borderColor, rest.containerBackgroundColor),
        `${theme} rest boundary against container`,
      ).toBeGreaterThanOrEqual(MIN_NON_TEXT_CONTRAST);

      await chip.hover();
      const hover = await readChipStyle(page);
      expect(
        contrastRatio(hover.borderColor, hover.backgroundColor),
        `${theme} hover boundary against chip fill`,
      ).toBeGreaterThanOrEqual(MIN_NON_TEXT_CONTRAST);
      expect(
        contrastRatio(hover.borderColor, hover.containerBackgroundColor),
        `${theme} hover boundary against container`,
      ).toBeGreaterThanOrEqual(MIN_NON_TEXT_CONTRAST);
      expect(hover.backgroundColor, `${theme} hover fill`).not.toBe(
        rest.backgroundColor,
      );
      expect(hover.scale, `${theme} hover scale`).not.toBe(
        rest.scale,
      );
    }
  });
});

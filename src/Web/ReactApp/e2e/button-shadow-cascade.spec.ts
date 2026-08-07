import { expect, test } from '@playwright/test';
import type { Page } from '@playwright/test';

const SHADOWED_VARIANTS = [
  'primary',
  'secondary',
  'danger',
  'success',
  'subtle',
  'tab',
  'toggle',
] as const;
const EXEMPT_VARIANTS = ['ghost', 'link', 'unstyled'] as const;
const CALLER_SHADOWS = ['shadow-none', 'shadow-sm', 'shadow-md', 'shadow-lg'] as const;

interface ShadowSnapshot {
  buttons: Record<string, string>;
  references: Record<string, string>;
}

async function mountButtonHarness(page: Page) {
  await page.goto('/');
  await page.evaluate(() => {
    document.body.innerHTML = '<div id="button-shadow-root"></div>';
  });
  await page.addStyleTag({
    content: '* { transition: none !important; animation: none !important; }',
  });
  await page.addScriptTag({
    type: 'module',
    content: `
      import React from '/node_modules/.vite/deps/react.js';
      import ReactDom from '/node_modules/.vite/deps/react-dom_client.js';
      import { Button } from '/src/common/components/ui/Button.tsx';

      const variants = ${JSON.stringify([...SHADOWED_VARIANTS, ...EXEMPT_VARIANTS])};
      const shadows = ${JSON.stringify(['default', ...CALLER_SHADOWS])};
      const references = ['shadow-xs', ...${JSON.stringify(CALLER_SHADOWS)}];
      const elements = [
        ...references.map((shadow) =>
          React.createElement('span', {
            key: 'reference-' + shadow,
            className: shadow,
            'data-shadow-reference': shadow,
          })
        ),
        ...variants.flatMap((variant) =>
          shadows.map((shadow) =>
            React.createElement(Button, {
              key: variant + '-' + shadow,
              variant,
              className: shadow === 'default' ? undefined : shadow,
              'data-shadow-case': variant + '-' + shadow,
            }, variant + '-' + shadow)
          )
        ),
      ];
      ReactDom.createRoot(document.getElementById('button-shadow-root')).render(
        React.createElement('main', null, elements)
      );
      requestAnimationFrame(() => requestAnimationFrame(() => {
        window.__buttonShadowHarnessReady = true;
      }));
    `,
  });
  await page.waitForFunction(() => {
    return (window as Window & { __buttonShadowHarnessReady?: boolean })
      .__buttonShadowHarnessReady === true;
  });
}

async function readShadows(
  page: Page,
  theme: string,
): Promise<ShadowSnapshot> {
  return page.evaluate((selectedTheme) => {
    document.documentElement.dataset.theme = selectedTheme;
    const buttons = Object.fromEntries(
      [...document.querySelectorAll<HTMLElement>('[data-shadow-case]')].map((element) => [
        element.dataset.shadowCase ?? '',
        getComputedStyle(element).boxShadow,
      ]),
    );
    const references = Object.fromEntries(
      [...document.querySelectorAll<HTMLElement>('[data-shadow-reference]')].map((element) => [
        element.dataset.shadowReference ?? '',
        getComputedStyle(element).boxShadow,
      ]),
    );
    return { buttons, references };
  }, theme);
}

test.describe('Button shadow cascade (#1127)', () => {
  test('caller utilities win for every variant in Forge and Blueprint', async ({ page }) => {
    await mountButtonHarness(page);

    for (const theme of ['forge', 'blueprint']) {
      const { buttons, references } = await readShadows(page, theme);

      for (const variant of SHADOWED_VARIANTS) {
        expect(buttons[`${variant}-default`], `${theme} ${variant} default`).toBe(
          references['shadow-xs'],
        );
        for (const shadow of CALLER_SHADOWS) {
          expect(buttons[`${variant}-${shadow}`], `${theme} ${variant} ${shadow}`).toBe(
            references[shadow],
          );
        }
      }

      for (const variant of EXEMPT_VARIANTS) {
        expect(buttons[`${variant}-default`], `${theme} ${variant} default`).toBe('none');
        for (const shadow of CALLER_SHADOWS) {
          expect(buttons[`${variant}-${shadow}`], `${theme} ${variant} ${shadow}`).toBe(
            references[shadow],
          );
        }
      }
    }
  });
});

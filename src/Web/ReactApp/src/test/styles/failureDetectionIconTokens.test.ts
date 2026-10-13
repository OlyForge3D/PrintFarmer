import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';
import { colorDistance } from '@/common/utils/colorDistance';
import { SELECTABLE_THEMES } from '@/design-system/themes/registry';
import { getFailureDetectionIconColorClass } from '@/features/printers/utils/failureDetectionStatus';

/**
 * Guards issue #3338: the spaghetti-detection shield must not read as an
 * error, warning, or success when the printer is merely Ready, checking, or
 * disabled. Class-name inequality is not enough — a brand token such as
 * `accent` can resolve to the same hex as a status hue in some themes — so this
 * resolves the shipped CSS values (including high-contrast overrides) and
 * requires a visible CIEDE2000 separation.
 */

const SOURCE_ROOT = resolve(__dirname, '../..');
const THEME_ROOT = resolve(SOURCE_ROOT, 'design-system/themes');
const contrastCss = readFileSync(resolve(SOURCE_ROOT, 'design-system/contrast.css'), 'utf8');
const indexCss = readFileSync(resolve(SOURCE_ROOT, 'index.css'), 'utf8');

const MIN_STATUS_DISTANCE = 10;
const STATUS_TOKENS = ['success', 'warning', 'error'] as const;
const NEUTRAL_CASES = [
  ['idle', 'idle', false],
  ['checking', undefined, true],
  ['disabled', 'disabled', false],
  ['unknown (disabled)', undefined, false],
] as const;

const parseDeclarations = (css: string): Map<string, string> =>
  new Map(
    [...css.matchAll(/--pf-([\w-]+):\s*(#[0-9a-f]{3,8})\b/gi)].map(([, token, value]) => [
      token,
      value.toLowerCase(),
    ]),
  );

const themeTokens = (theme: string): Map<string, string> =>
  parseDeclarations(readFileSync(resolve(THEME_ROOT, `${theme}.css`), 'utf8'));

const highContrastTokens = (theme: string): Map<string, string> => {
  const merged = themeTokens(theme);
  const blockPattern = new RegExp(
    `\\[data-theme=['"]${theme}['"]\\]\\[data-contrast=['"]high['"]\\]\\s*\\{([^}]*)\\}`,
    'g',
  );
  for (const [, block] of contrastCss.matchAll(blockPattern)) {
    for (const [token, value] of parseDeclarations(block)) merged.set(token, value);
  }
  return merged;
};

const tokenOf = (className: string): string => className.replace(/^text-pf-/, '');

const VARIANTS = SELECTABLE_THEMES.flatMap((theme) => [
  { label: theme, tokens: themeTokens(theme) },
  { label: `${theme} + high contrast`, tokens: highContrastTokens(theme) },
]);

describe('failure-detection shield neutral tones', () => {
  it.each(NEUTRAL_CASES)('%s tone is a mapped Tailwind color', (_name, state, enabled) => {
    const token = tokenOf(getFailureDetectionIconColorClass(state, enabled));
    expect(indexCss).toContain(`--color-pf-${token}: var(--pf-${token});`);
  });

  describe.each(VARIANTS)('$label', ({ tokens }) => {
    it.each(NEUTRAL_CASES)('%s tone is visibly distinct from success, warning and error', (_name, state, enabled) => {
      const token = tokenOf(getFailureDetectionIconColorClass(state, enabled));
      const neutral = tokens.get(token);
      expect(neutral, `--pf-${token}`).toBeDefined();

      for (const status of STATUS_TOKENS) {
        const statusHex = tokens.get(status);
        expect(statusHex, `--pf-${status}`).toBeDefined();
        expect(neutral).not.toBe(statusHex);
        expect(
          colorDistance(neutral!, statusHex!),
          `--pf-${token} ${neutral} vs --pf-${status} ${statusHex}`,
        ).toBeGreaterThanOrEqual(MIN_STATUS_DISTANCE);
      }
    });
  });
});

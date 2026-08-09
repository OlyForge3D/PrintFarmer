import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it, afterEach } from 'vitest';

/**
 * Per-theme `prefers-contrast: high` token values (#1298).
 *
 * Scope: `light`, `dark`, `matrix` only — `blueprint`/`ratos`/`voron`/`farm`/
 * `forge` are #1299, tracked separately. The 13-token contract and mechanism
 * are fixed by decision issue #1296; the cascade itself is wired by #1297.
 *
 * These tests exercise the actual shipped `design-system/contrast.css`
 * content (not a fixture), so a value removed or renamed here fails loudly,
 * matching #1298's acceptance criteria:
 *   - getComputedStyle shows changed values for every contract token, for
 *     all three themes, under `[data-theme][data-contrast="high"]`.
 *   - Every override clears WCAG AA against its paired token.
 *   - Normal-mode (non-high-contrast) rendering is untouched by this file.
 */

const SOURCE_ROOT = resolve(__dirname, '../..');
const CONTRAST_CSS_PATH = resolve(SOURCE_ROOT, 'design-system/contrast.css');
const contrastCss = readFileSync(CONTRAST_CSS_PATH, 'utf8');

const THEMES = ['light', 'dark', 'matrix'] as const;

/** The fixed 13-token contract from decision issue #1296. */
const CONTRACT_TOKENS = [
  'bg-0',
  'panel',
  'text-primary',
  'text-secondary',
  'border',
  'border-strong',
  'control-border-focus',
  'focus-ring',
  'accent',
  'accent-bg',
  'on-accent',
  'control-bg',
  'control-text',
] as const;

/** AA pairing partner for each contract token, per the #1296 table. */
const AA_PAIRS: ReadonlyArray<{
  readonly token: (typeof CONTRACT_TOKENS)[number];
  readonly partner: (typeof CONTRACT_TOKENS)[number];
  readonly minRatio: number;
}> = [
  { token: 'text-primary', partner: 'bg-0', minRatio: 4.5 },
  { token: 'text-primary', partner: 'panel', minRatio: 4.5 },
  { token: 'text-secondary', partner: 'panel', minRatio: 3 },
  { token: 'border', partner: 'panel', minRatio: 3 },
  { token: 'border-strong', partner: 'panel', minRatio: 3 },
  { token: 'control-border-focus', partner: 'panel', minRatio: 3 },
  { token: 'focus-ring', partner: 'panel', minRatio: 3 },
  { token: 'accent', partner: 'bg-0', minRatio: 4.5 },
  { token: 'on-accent', partner: 'accent-bg', minRatio: 4.5 },
  { token: 'control-text', partner: 'control-bg', minRatio: 4.5 },
];

const parseThemeTokens = (theme: (typeof THEMES)[number]): ReadonlyMap<string, string> => {
  // `dark` has two rule blocks sharing this selector: the #1297 canary block
  // and the #1298 value block below it. Both apply — same selector, same
  // layer, so per-property the later declaration in source order wins and
  // distinct properties from each block both take effect. Merge every
  // matching block, in source order, to mirror that.
  const selector = `[data-theme='${theme}'][data-contrast='high']`;
  const blockPattern = new RegExp(
    `\\[data-theme='${theme}'\\]\\[data-contrast='high'\\]\\s*\\{([^}]*)\\}`,
    'g',
  );
  const blocks = [...contrastCss.matchAll(blockPattern)];
  expect(blocks.length, `${selector} block not found in contrast.css`).toBeGreaterThan(0);

  const merged = new Map<string, string>();
  for (const [, block] of blocks) {
    for (const [, token, value] of block.matchAll(/--pf-([\w-]+):\s*(#[0-9a-f]{3,8})/gi)) {
      merged.set(token, value.toLowerCase());
    }
  }
  return merged;
};

const relativeLuminance = (hex: string): number => {
  const value = hex.slice(1);
  const expanded =
    value.length === 3 ? value.split('').map((c) => `${c}${c}`).join('') : value;
  const linearChannel = (offset: number): number => {
    const channel = Number.parseInt(expanded.slice(offset, offset + 2), 16) / 255;
    return channel <= 0.04045 ? channel / 12.92 : ((channel + 0.055) / 1.055) ** 2.4;
  };
  return 0.2126 * linearChannel(0) + 0.7152 * linearChannel(2) + 0.0722 * linearChannel(4);
};

const contrastRatio = (first: string, second: string): number => {
  const a = relativeLuminance(first);
  const b = relativeLuminance(second);
  const lighter = Math.max(a, b);
  const darker = Math.min(a, b);
  return (lighter + 0.05) / (darker + 0.05);
};

describe('prefers-contrast: high token values for light/dark/matrix (#1298)', () => {
  it.each(THEMES)('declares every contract token for %s', (theme) => {
    const tokens = parseThemeTokens(theme);
    const missing = CONTRACT_TOKENS.filter((token) => !tokens.has(token));
    expect(missing).toEqual([]);
  });

  it.each(THEMES)('%s high-contrast pairs clear WCAG AA', (theme) => {
    const tokens = parseThemeTokens(theme);
    const failures = AA_PAIRS.flatMap(({ token, partner, minRatio }) => {
      const tokenValue = tokens.get(token);
      const partnerValue = tokens.get(partner);
      if (!tokenValue || !partnerValue) return [`${token}/${partner}: missing token`];
      const ratio = contrastRatio(tokenValue, partnerValue);
      return ratio < minRatio ? [`${token}/${partner}: ${ratio.toFixed(2)}:1 (needs ${minRatio}:1)`] : [];
    });

    expect(failures).toEqual([]);
  });

  describe('cascade applies via getComputedStyle', () => {
    afterEach(() => {
      document.documentElement.removeAttribute('data-theme');
      document.documentElement.removeAttribute('data-contrast');
    });

    it.each(THEMES)('%s: every contract token changes under data-contrast=high', (theme) => {
      // Minimal stand-in for the theme's own base block: real base values,
      // scoped to `[data-theme="<theme>"]` with no `data-contrast` qualifier,
      // exactly mirroring how design-system/themes/<theme>.css declares them.
      const base = document.createElement('style');
      base.textContent = `
        [data-theme='${theme}'] {
          ${CONTRACT_TOKENS.map((token) => `--pf-${token}: initial;`).join('\n')}
        }
      `;
      const override = document.createElement('style');
      override.textContent = contrastCss;

      document.head.appendChild(base);
      document.head.appendChild(override);

      try {
        document.documentElement.setAttribute('data-theme', theme);

        document.documentElement.setAttribute('data-contrast', 'normal');
        const normalStyle = getComputedStyle(document.documentElement);
        for (const token of CONTRACT_TOKENS) {
          expect(normalStyle.getPropertyValue(`--pf-${token}`).trim(), `--pf-${token}`).toBe('initial');
        }

        document.documentElement.setAttribute('data-contrast', 'high');
        const highStyle = getComputedStyle(document.documentElement);
        const expected = parseThemeTokens(theme);
        for (const token of CONTRACT_TOKENS) {
          const actual = highStyle.getPropertyValue(`--pf-${token}`).trim().toLowerCase();
          expect(actual, `--pf-${token}`).toBe(expected.get(token));
          expect(actual, `--pf-${token} should differ from the normal-mode value`).not.toBe('initial');
        }
      } finally {
        document.head.removeChild(base);
        document.head.removeChild(override);
      }
    });
  });
});

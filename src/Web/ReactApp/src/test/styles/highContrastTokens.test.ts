import { readdirSync, readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it, afterEach } from 'vitest';

/**
 * Automated WCAG contrast-ratio verification for `prefers-contrast: high`
 * across ALL themes (issue #1300).
 *
 * This suite deliberately does NOT hardcode which themes have high-contrast
 * overrides. `light`/`dark`/`matrix` land via #1298 and
 * `blueprint`/`ratos`/`voron`/`farm`/`forge` land via #1299 — both add
 * `[data-theme='<id>'][data-contrast='high']` blocks to
 * `design-system/contrast.css`, on separate branches, in parallel. Instead:
 *
 *   1. The full theme id list is read from `design-system/themes/*.css`
 *      (excluding `base.css`, which is shared plumbing, not a selectable
 *      theme) — so a new theme file automatically gets a coverage row here
 *      with no test-suite change.
 *   2. `design-system/contrast.css` is parsed for whichever of those theme
 *      ids currently declare a high-contrast override block. A theme with no
 *      block yet (not merged) is reported as pending, not failed — this
 *      suite must pass both before and after #1298/#1299 land, strengthening
 *      automatically as each theme's tokens are added.
 *   3. Every theme that DOES declare an override is held to the full #1296
 *      contract: all 13 tokens must be present, and every AA-paired
 *      combination must clear its required ratio (4.5:1 normal text, 3:1
 *      large text/UI). A partial rollout (some tokens declared, others
 *      missing) fails loudly rather than silently passing.
 *
 * Ratios are computed directly from the shipped CSS custom-property values
 * (relative luminance per WCAG 2.x), not approximated, so a color edit that
 * regresses contrast fails this suite immediately.
 */

const SOURCE_ROOT = resolve(__dirname, '../..');
const THEMES_DIR = resolve(SOURCE_ROOT, 'design-system/themes');
const CONTRAST_CSS_PATH = resolve(SOURCE_ROOT, 'design-system/contrast.css');
const contrastCss = readFileSync(CONTRAST_CSS_PATH, 'utf8');

/** Every theme id known to the design system, discovered from disk. */
const ALL_THEME_IDS = readdirSync(THEMES_DIR)
  .filter((file) => file.endsWith('.css') && file !== 'base.css')
  .map((file) => file.replace(/\.css$/, ''))
  .sort();

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

/** AA/AAA pairing partner for each contract token, per the #1296 table. */
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

/**
 * Parses every `[data-theme='<theme>'][data-contrast='high']` block for the
 * given theme id and merges their `--pf-*` hex declarations in source order
 * (later blocks/properties win, mirroring real cascade behavior for
 * same-selector, same-layer rules — e.g. `dark`'s #1297 canary block plus its
 * #1298 value block).
 */
const parseThemeTokens = (theme: string, css: string = contrastCss): ReadonlyMap<string, string> => {
  const blockPattern = new RegExp(
    `\\[data-theme=['"]${theme}['"]\\]\\[data-contrast=['"]high['"]\\]\\s*\\{([^}]*)\\}`,
    'g',
  );
  const blocks = [...css.matchAll(blockPattern)];

  const merged = new Map<string, string>();
  for (const [, block] of blocks) {
    for (const [, token, value] of block.matchAll(/--pf-([\w-]+):\s*(#[0-9a-f]{6}|#[0-9a-f]{3})\b/gi)) {
      merged.set(token, value.toLowerCase());
    }
  }
  return merged;
};

/** Themes that currently declare at least one contract token override. */
const THEMES_WITH_OVERRIDES = ALL_THEME_IDS.filter((theme) => {
  const tokens = parseThemeTokens(theme);
  return CONTRACT_TOKENS.some((token) => tokens.has(token));
});

/** Themes with no high-contrast override block yet (pending #1298/#1299). */
const PENDING_THEMES = ALL_THEME_IDS.filter((theme) => !THEMES_WITH_OVERRIDES.includes(theme));

const relativeLuminance = (hex: string): number => {
  const value = hex.slice(1);
  const expanded =
    value.length === 3 ? value.split('').map((c) => `${c}${c}`).join('') : value;
  if (expanded.length !== 6) {
    throw new Error(`relativeLuminance: expected a 3 or 6-digit hex color, got "${hex}"`);
  }
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

describe('prefers-contrast: high token coverage (#1300)', () => {
  it('discovers at least one theme from design-system/themes/', () => {
    expect(ALL_THEME_IDS.length).toBeGreaterThan(0);
  });

  it('parser rejects a selector with swapped attribute order or a mistyped token', () => {
    // Regression guard for the actual regex/parsing logic (not a tautology
    // about THEMES_WITH_OVERRIDES/PENDING_THEMES, which are algebraic
    // complements of each other by construction and can't independently
    // verify the parser). Exercises parseThemeTokens against inline fixture
    // CSS so a broken selector or a typo'd token name in a real theme's
    // override block is caught here rather than silently reclassifying that
    // theme as "pending" forever.
    const swappedAttributeOrder = `
      [data-contrast='high'][data-theme='fixture'] {
        --pf-bg-0: #000000;
      }
    `;
    expect(parseThemeTokens('fixture', swappedAttributeOrder).size).toBe(0);

    const mistypedToken = `
      [data-theme='fixture'][data-contrast='high'] {
        --pf-txt-primary: #000000;
      }
    `;
    expect(parseThemeTokens('fixture', mistypedToken).has('text-primary')).toBe(false);

    const wellFormed = `
      [data-theme='fixture'][data-contrast='high'] {
        --pf-text-primary: #000000;
      }
    `;
    expect(parseThemeTokens('fixture', wellFormed).get('text-primary')).toBe('#000000');
  });

  it('parser excludes malformed-length hex values instead of letting them through as NaN', () => {
    // Regression guard for the NaN-swallowing bug: a 4/5/7/8-digit hex value
    // (e.g. alpha-shorthand `#1a2b`) must NOT be captured as a token value.
    // If it were, `relativeLuminance` would either throw (now) or previously
    // produced NaN, and `NaN < minRatio` is `false` — silently passing a
    // garbage contrast ratio instead of failing loudly. Excluding malformed
    // hex at the parse step means it instead surfaces as a "missing token"
    // failure in the AA-pairing test, which is the intended fail-loud path.
    const fourDigitAlphaHex = `
      [data-theme='fixture'][data-contrast='high'] {
        --pf-text-primary: #1a2b;
      }
    `;
    expect(parseThemeTokens('fixture', fourDigitAlphaHex).has('text-primary')).toBe(false);

    const eightDigitAlphaHex = `
      [data-theme='fixture'][data-contrast='high'] {
        --pf-text-primary: #ff0000ff;
      }
    `;
    expect(parseThemeTokens('fixture', eightDigitAlphaHex).has('text-primary')).toBe(false);

    // relativeLuminance itself must also reject malformed input directly,
    // as a defense-in-depth backstop independent of the parser regex.
    expect(() => relativeLuminance('#1a2b')).toThrow();
    expect(() => relativeLuminance('#ff0000ff')).toThrow();
    expect(() => relativeLuminance('#000000')).not.toThrow();
    expect(() => relativeLuminance('#fff')).not.toThrow();
  });

  it.each(PENDING_THEMES)('%s: high-contrast override not landed yet (tracked by #1298/#1299)', () => {
    // No assertion body: this test intentionally reports as a distinct,
    // named row per pending theme so CI output honestly shows "0 of N themes
    // enforced" instead of blending into an all-green run. Each row
    // disappears (replaced by real WCAG assertions below) as that theme's
    // contrast.css override block lands.
  });
});

describe.each(THEMES_WITH_OVERRIDES)('prefers-contrast: high — %s theme', (theme) => {
  it('declares every contract token', () => {
    const tokens = parseThemeTokens(theme);
    const missing = CONTRACT_TOKENS.filter((token) => !tokens.has(token));
    expect(missing, `${theme}: missing high-contrast tokens`).toEqual([]);
  });

  it('every AA-paired combination clears its required WCAG ratio', () => {
    const tokens = parseThemeTokens(theme);
    const failures = AA_PAIRS.flatMap(({ token, partner, minRatio }) => {
      const tokenValue = tokens.get(token);
      const partnerValue = tokens.get(partner);
      if (!tokenValue || !partnerValue) return [`${token}/${partner}: missing token`];
      const ratio = contrastRatio(tokenValue, partnerValue);
      return ratio < minRatio
        ? [`${token}/${partner}: ${ratio.toFixed(2)}:1 (needs ${minRatio}:1)`]
        : [];
    });

    expect(failures, `${theme}: WCAG contrast failures`).toEqual([]);
  });

  describe('cascade applies via getComputedStyle', () => {
    afterEach(() => {
      document.documentElement.removeAttribute('data-theme');
      document.documentElement.removeAttribute('data-contrast');
    });

    it('every contract token changes under data-contrast=high', () => {
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
          expect(normalStyle.getPropertyValue(`--pf-${token}`).trim(), `--pf-${token}`).toBe(
            'initial',
          );
        }

        document.documentElement.setAttribute('data-contrast', 'high');
        const highStyle = getComputedStyle(document.documentElement);
        const expected = parseThemeTokens(theme);
        for (const token of CONTRACT_TOKENS) {
          const actual = highStyle.getPropertyValue(`--pf-${token}`).trim().toLowerCase();
          expect(actual, `--pf-${token}`).toBe(expected.get(token));
          expect(actual, `--pf-${token} should differ from the normal-mode value`).not.toBe(
            'initial',
          );
        }
      } finally {
        document.head.removeChild(base);
        document.head.removeChild(override);
      }
    });
  });
});

import { readdirSync, readFileSync } from 'node:fs';
import { relative, resolve } from 'node:path';
import { describe, expect, it } from 'vitest';
import { colorDistance, INVALID_COLOR_DISTANCE } from '@/common/utils/colorDistance';
import { SELECTABLE_THEMES } from '@/design-system/themes/registry';

const SOURCE_ROOT = resolve(__dirname, '../..');
const THEME_ROOT = resolve(SOURCE_ROOT, 'design-system/themes');
const AA_NORMAL_TEXT = 4.5;
const MIN_ACCENT_HOVER_DISTANCE = 5;
const SEMANTIC_PAIRS = [
  ['accent-bg', 'on-accent'],
  ['success', 'text-inverse'],
  ['error', 'text-inverse'],
  ['warning', 'text-inverse'],
  ['info', 'text-inverse'],
  ['success-bg', 'success-text'],
  ['status-online-bg', 'status-online-text'],
  ['error-bg', 'error-text'],
  ['warning-bg', 'warning-text'],
  ['control-bg', 'control-placeholder'],
] as const;
// The status `*-hover` tokens have no single foreground that clears AA in all
// themes. These controls retain their accessible surface and use scale/shadow
// for hover feedback; the source guard below rejects unsafe inverse pairings.
const INTERACTIVE_STATUS_STATES = [
  ['success rest', 'success', 'text-inverse'],
  ['success hover', 'success', 'text-inverse'],
  ['error rest', 'error-bg', 'error-text'],
  ['error hover', 'error-bg', 'error-text'],
] as const;
const SECONDARY_ACTION_STATES = [
  ['secondary rest', 'bg-2', 'text-primary'],
  ['secondary hover', 'bg-1', 'text-primary'],
] as const;
const DANGER_ACTION_STATES = [
  ['danger rest', 'button-danger-bg', 'on-danger'],
  ['danger hover', 'button-danger-hover', 'on-danger'],
] as const;
const SUCCESS_ACTION_STATES = [
  ['success action rest', 'button-success-bg', 'button-success-text'],
  ['success action hover', 'button-success-hover', 'button-success-text'],
] as const;
const ACCENT_SURFACES = [
  'accent-bg',
  'accent-hover',
  'button-primary-bg',
  'button-primary-hover',
] as const;
const ACCENT_TEXT_SURFACES = ['bg-0', 'bg-1', 'bg-2', 'panel', 'card-bg'] as const;
const STANDARD_SURFACES = ['bg-0', 'bg-1', 'bg-2', 'panel', 'card-bg'] as const;
const ACCENT_TEXT_CALL_SITES = [
  'features/admin/components/SystemLogsContent.tsx',
  'features/maintenance/components/FleetStatisticsTable.tsx',
] as const;

const parseTokens = (path: string): ReadonlyMap<string, string> => {
  const source = readFileSync(path, 'utf8');
  return new Map(
    [...source.matchAll(/--pf-([\w-]+):\s*(#[0-9a-f]{3,8})/gi)].map(([, token, value]) => [
      token,
      value,
    ] as const),
  );
};

const parseThemeTokens = (theme: string): ReadonlyMap<string, string> =>
  parseTokens(resolve(THEME_ROOT, `${theme}.css`));

const relativeLuminance = (hex: string): number => {
  const value = hex.slice(1);
  const expanded = value.length === 3
    ? value.split('').map((character) => `${character}${character}`).join('')
    : value;
  const linearChannel = (offset: number): number => {
    const channel = Number.parseInt(expanded.slice(offset, offset + 2), 16) / 255;
    return channel <= 0.04045
      ? channel / 12.92
      : ((channel + 0.055) / 1.055) ** 2.4;
  };

  return 0.2126 * linearChannel(0) + 0.7152 * linearChannel(2) + 0.0722 * linearChannel(4);
};

const contrastRatio = (first: string, second: string): number => {
  const firstLuminance = relativeLuminance(first);
  const secondLuminance = relativeLuminance(second);
  const lighter = Math.max(firstLuminance, secondLuminance);
  const darker = Math.min(firstLuminance, secondLuminance);
  return (lighter + 0.05) / (darker + 0.05);
};

const requireToken = (
  tokens: ReadonlyMap<string, string>,
  theme: string,
  token: string,
): string => {
  const value = tokens.get(token);
  if (!value) throw new Error(`${theme} is missing --pf-${token}`);
  return value;
};

const sourceFiles = (directory: string): string[] =>
  readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    if (entry.isDirectory()) {
      return entry.name === 'test' || entry.name === '__tests__'
        ? []
        : sourceFiles(resolve(directory, entry.name));
    }

    return /\.[jt]sx?$/.test(entry.name) ? [resolve(directory, entry.name)] : [];
  });

const sourceOffenders = (
  pattern: RegExp,
  excluded: ReadonlySet<string> = new Set(),
): string[] => sourceFiles(SOURCE_ROOT).flatMap((file) => {
  const path = relative(SOURCE_ROOT, file).replaceAll('\\', '/');
  if (excluded.has(path)) return [];

  const source = readFileSync(file, 'utf8');
  return [...source.matchAll(pattern)].map((match) => {
    const line = source.slice(0, match.index).split('\n').length;
    return `${path}:${line}: ${match[0].replace(/\s+/g, ' ').trim()}`;
  });
});

describe('semantic foregrounds on themed surfaces (#1101, #1103, #1110, #1128)', () => {
  it.each(SELECTABLE_THEMES)('%s semantic pairs clear WCAG AA for normal text', (theme) => {
    const tokens = parseThemeTokens(theme);
    const failures = SEMANTIC_PAIRS.flatMap(([background, foreground]) => {
      const backgroundValue = tokens.get(background);
      const foregroundValue = tokens.get(foreground);

      if (!backgroundValue || !foregroundValue) {
        return [`${background}/${foreground}: missing token`];
      }

      const ratio = contrastRatio(backgroundValue, foregroundValue);
      return ratio < AA_NORMAL_TEXT
        ? [`${background}/${foreground}: ${ratio.toFixed(2)}:1`]
        : [];
    });

    expect(failures).toEqual([]);
  });

  it.each(SELECTABLE_THEMES)('%s accent rest and hover pairs clear WCAG AA', (theme) => {
    const tokens = parseThemeTokens(theme);
    const foreground = requireToken(tokens, theme, 'on-accent');
    const failures = ACCENT_SURFACES.flatMap((background) => {
      const ratio = contrastRatio(requireToken(tokens, theme, background), foreground);
      return ratio < AA_NORMAL_TEXT
        ? [`${background}/on-accent: ${ratio.toFixed(2)}:1`]
        : [];
    });

    expect(tokens.get('button-primary-bg')).toBe(tokens.get('accent-bg'));
    expect(tokens.get('button-primary-hover')).toBe(tokens.get('accent-hover'));
    expect(failures).toEqual([]);
  });

  it.each(SELECTABLE_THEMES)('%s accent text clears WCAG AA on page and panel surfaces', (theme) => {
    const tokens = parseThemeTokens(theme);
    const foreground = requireToken(tokens, theme, 'accent-text');
    const failures = ACCENT_TEXT_SURFACES.flatMap((background) => {
      const ratio = contrastRatio(requireToken(tokens, theme, background), foreground);
      return ratio < AA_NORMAL_TEXT
        ? [`accent-text/${background}: ${ratio.toFixed(2)}:1`]
        : [];
    });

    expect(failures).toEqual([]);
  });

  it.each(SELECTABLE_THEMES)('%s accent text remains perceptibly distinct on hover', (theme) => {
    const tokens = parseThemeTokens(theme);
    const distance = colorDistance(
      requireToken(tokens, theme, 'accent'),
      requireToken(tokens, theme, 'accent-text'),
    );

    expect(distance).not.toBe(INVALID_COLOR_DISTANCE);
    expect(distance).toBeGreaterThanOrEqual(MIN_ACCENT_HOVER_DISTANCE);
  });

  it('exposes accent text through Tailwind and uses it only at the two affected Button hovers', () => {
    const indexCss = readFileSync(resolve(SOURCE_ROOT, 'index.css'), 'utf8');
    expect(indexCss).toContain('--color-pf-accent-text: var(--pf-accent-text);');

    for (const path of ACCENT_TEXT_CALL_SITES) {
      const source = readFileSync(resolve(SOURCE_ROOT, path), 'utf8');
      expect(source, path).toContain('hover:text-pf-accent-text');
      expect(source, path).not.toContain('hover:text-pf-accent-hover');
    }
  });

  it.each(SELECTABLE_THEMES)('%s interactive status states clear WCAG AA', (theme) => {
    const tokens = parseThemeTokens(theme);
    const failures = INTERACTIVE_STATUS_STATES.flatMap(([state, background, foreground]) => {
      const ratio = contrastRatio(
        requireToken(tokens, theme, background),
        requireToken(tokens, theme, foreground),
      );
      return ratio < AA_NORMAL_TEXT
        ? [`${state} ${background}/${foreground}: ${ratio.toFixed(2)}:1`]
        : [];
    });

    expect(failures).toEqual([]);
  });

  it.each(SELECTABLE_THEMES)('%s secondary action states clear WCAG AA', (theme) => {
    const tokens = parseThemeTokens(theme);
    const failures = SECONDARY_ACTION_STATES.flatMap(([state, background, foreground]) => {
      const ratio = contrastRatio(
        requireToken(tokens, theme, background),
        requireToken(tokens, theme, foreground),
      );
      return ratio < AA_NORMAL_TEXT
        ? [`${state} ${background}/${foreground}: ${ratio.toFixed(2)}:1`]
        : [];
    });

    expect(failures).toEqual([]);
  });

  it.each(SELECTABLE_THEMES)('%s danger action states clear WCAG AA', (theme) => {
    const tokens = parseThemeTokens(theme);
    const failures = DANGER_ACTION_STATES.flatMap(([state, background, foreground]) => {
      const ratio = contrastRatio(
        requireToken(tokens, theme, background),
        requireToken(tokens, theme, foreground),
      );
      return ratio < AA_NORMAL_TEXT
        ? [`${state} ${background}/${foreground}: ${ratio.toFixed(2)}:1`]
        : [];
    });

    expect(failures).toEqual([]);
  });

  it.each(SELECTABLE_THEMES)('%s success action states clear WCAG AA', (theme) => {
    const tokens = parseThemeTokens(theme);
    const failures = SUCCESS_ACTION_STATES.flatMap(([state, background, foreground]) => {
      const ratio = contrastRatio(
        requireToken(tokens, theme, background),
        requireToken(tokens, theme, foreground),
      );
      return ratio < AA_NORMAL_TEXT
        ? [`${state} ${background}/${foreground}: ${ratio.toFixed(2)}:1`]
        : [];
    });

    expect(failures).toEqual([]);
  });

  it.each(SELECTABLE_THEMES)('%s tertiary text clears WCAG AA on standard surfaces', (theme) => {
    const tokens = parseThemeTokens(theme);
    const foreground = requireToken(tokens, theme, 'text-tertiary');
    const failures = STANDARD_SURFACES.flatMap((background) => {
      const ratio = contrastRatio(requireToken(tokens, theme, background), foreground);
      return ratio < AA_NORMAL_TEXT
        ? [`text-tertiary/${background}: ${ratio.toFixed(2)}:1`]
        : [];
    });

    expect(failures).toEqual([]);
  });

  it('does not hardcode white on semantic accent or status surfaces', () => {
    const hardcodedWhite = /(?:bg-pf-(?:accent(?:-bg)?|success(?:-bg)?|error(?:-bg)?|warning(?:-bg)?|info(?:-bg)?)[^"'`]{0,240}text-white!?|text-white!?[^"'`]{0,240}bg-pf-(?:accent(?:-bg)?|success(?:-bg)?|error(?:-bg)?|warning(?:-bg)?|info(?:-bg)?))/g;
    const excluded = new Set([
      // The shared success variant is intentionally outside #1103 and preserved
      // by the integrated #1102 cascade fix.
      'common/components/ui/Button.tsx',
    ]);

    const offenders = sourceOffenders(hardcodedWhite, excluded);

    expect(offenders).toEqual([]);
  });

  it('does not pair inverse status text with incompatible hover tokens', () => {
    const inverseStatusHover = /(?:hover:bg-pf-(?:success|error|warning|info)-hover!?[^"'`]{0,240}text-\[var\(--pf-text-inverse\)\]!?|text-\[var\(--pf-text-inverse\)\]!?[^"'`]{0,240}hover:bg-pf-(?:success|error|warning|info)-hover!?)/g;

    expect(sourceOffenders(inverseStatusHover)).toEqual([]);
  });
});

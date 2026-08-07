import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { SELECTABLE_THEMES } from '@/design-system/themes/registry';
import ModelFiltersBar from '../ModelFiltersBar';

const MIN_NON_TEXT_CONTRAST = 3;
const THEME_ROOT = resolve(__dirname, '../../../../design-system/themes');
const ADJACENT_SURFACES = ['bg-0', 'bg-1', 'bg-2'] as const;

const parseThemeTokens = (theme: string): ReadonlyMap<string, string> => {
  const source = readFileSync(resolve(THEME_ROOT, `${theme}.css`), 'utf8');
  return new Map(
    [...source.matchAll(/--pf-([\w-]+):\s*(#[0-9a-f]{3,8})/gi)].map(
      ([, token, value]) => [token, value] as const,
    ),
  );
};

const relativeLuminance = (hex: string): number => {
  const value = hex.slice(1);
  const expanded =
    value.length === 3
      ? value
          .split('')
          .map((character) => `${character}${character}`)
          .join('')
      : value;
  const linearChannel = (offset: number): number => {
    const channel = Number.parseInt(expanded.slice(offset, offset + 2), 16) / 255;
    return channel <= 0.04045
      ? channel / 12.92
      : ((channel + 0.055) / 1.055) ** 2.4;
  };

  return (
    0.2126 * linearChannel(0) +
    0.7152 * linearChannel(2) +
    0.0722 * linearChannel(4)
  );
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

describe('ModelFiltersBar unselected status contrast', () => {
  it('uses the semantic control boundary and a distinct hover surface', () => {
    render(
      <ModelFiltersBar
        models={[]}
        selectedModel={null}
        onModelChange={vi.fn()}
        selectedStatuses={['queued']}
        onStatusChange={vi.fn()}
        sortBy="name"
        onSortChange={vi.fn()}
        onRefresh={vi.fn()}
        isLoading={false}
      />,
    );

    expect(screen.getByRole('button', { name: 'Printing' })).toHaveClass(
      '!border-pf-control-boundary',
      'enabled:hover:!bg-pf-bg-2',
      'enabled:hover:scale-105',
      'enabled:hover:shadow-sm',
    );
  });

  it.each(SELECTABLE_THEMES)(
    '%s boundary clears SC 1.4.11 against rest, container, and hover surfaces',
    (theme) => {
      const tokens = parseThemeTokens(theme);
      const boundary = requireToken(tokens, theme, 'control-boundary');
      const failures = ADJACENT_SURFACES.flatMap((surface) => {
        const ratio = contrastRatio(
          boundary,
          requireToken(tokens, theme, surface),
        );
        return ratio < MIN_NON_TEXT_CONTRAST
          ? [`${surface}: ${ratio.toFixed(2)}:1`]
          : [];
      });

      expect(failures).toEqual([]);
    },
  );
});

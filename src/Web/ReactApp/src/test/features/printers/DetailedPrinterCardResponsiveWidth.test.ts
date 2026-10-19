import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

const TEST_DIR = dirname(fileURLToPath(import.meta.url));
const CSS_PATH = resolve(
  TEST_DIR,
  '../../../features/printers/components/DetailedPrinterCard.css',
);
const GRID_PATH = resolve(
  TEST_DIR,
  '../../../features/printers/components/PrinterCardGrid.tsx',
);

/**
 * #1584 moved the job/history detail surface inline into the detailed printer card,
 * which makes the detailed card the required reading surface on every viewport —
 * including phones.
 *
 * `PrinterCardGrid` renders detailed cards as a single full-width `grid-cols-1`
 * column below Tailwind's `sm` (640px) breakpoint. Above it, flexible tracks
 * prefer a 24rem minimum but can shrink to the available content width.
 * A card-local minimum would override that shrink behavior and force overflow,
 * including when an open navigation panel leaves a narrow content area.
 *
 * jsdom performs no layout and never applies this stylesheet, so the guard is
 * asserted at the source, with browser layout coverage in printer-card-density.spec.ts.
 */
describe('DetailedPrinterCard.css — the density floor must not overflow narrow viewports', () => {
  const css = readFileSync(CSS_PATH, 'utf8');
  const grid = readFileSync(GRID_PATH, 'utf8');

  const unconditionalCardRule = (): string => {
    // Strip every media block first, so what remains is the unconditional cascade.
    const withoutMediaBlocks = css.replace(/@media[^{]*\{(?:[^{}]*\{[^{}]*\})*[^{}]*\}/g, '');
    const match = withoutMediaBlocks.match(/\.pf-detailed-printer-card\s*\{([^}]*)\}/);
    expect(match, 'expected an unconditional .pf-detailed-printer-card rule').not.toBeNull();
    return match![1];
  };

  it('does not pin a desktop-width floor outside a media query', () => {
    const body = unconditionalCardRule();
    const minWidth = body.match(/min-width\s*:\s*([^;]+);?/);

    expect(minWidth, 'expected an explicit unconditional min-width').not.toBeNull();

    expect(minWidth![1].trim()).toBe('0');
  });

  it('leaves the flexible desktop minimum to the grid, without a card-local floor', () => {
    const withoutComments = css.replace(/\/\*[\s\S]*?\*\//g, '');
    const minimums = [...withoutComments.matchAll(/min-width\s*:\s*([^;}]+)/g)];
    expect(minimums.map(match => match[1].trim())).toEqual(['0']);
    expect(grid).toContain('grid-cols-1 sm:grid-cols-[repeat(auto-fill,minmax(min(100%,24rem),1fr))]');
    expect(grid).toContain('const DETAILED_CARD_WIDTH_REM = 24;');
  });
});

import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

const TEST_DIR = dirname(fileURLToPath(import.meta.url));
const CSS_PATH = resolve(
  TEST_DIR,
  '../../../features/printers/components/DetailedPrinterCard.css',
);

/**
 * #1584 moved the job/history detail surface inline into the detailed printer card,
 * which makes the detailed card the required reading surface on every viewport —
 * including phones.
 *
 * `PrinterCardGrid` renders detailed cards as a single full-width `grid-cols-1`
 * column and only switches to `repeat(auto-fill, 26rem)` tracks at Tailwind's `sm`
 * (640px) breakpoint. An unconditional `min-width: 26rem` (416px) therefore forces
 * horizontal overflow on any viewport narrower than that — a 375px phone loses 41px
 * of the card off-screen.
 *
 * jsdom performs no layout and never applies this stylesheet, so the guard is
 * asserted at the source: the desktop density floor must live behind the same
 * breakpoint the grid uses, and the unconditional rule must not reintroduce it.
 */
describe('DetailedPrinterCard.css — the density floor must not overflow narrow viewports', () => {
  const css = readFileSync(CSS_PATH, 'utf8');

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

    const value = minWidth![1].trim();
    const rem = value.endsWith('rem') ? Number.parseFloat(value) : 0;

    expect(rem).toBeLessThan(20);
  });

  it('restores the 26rem density floor at the grid breakpoint', () => {
    const mediaBlock = css.match(
      /@media\s*\(min-width:\s*640px\)\s*\{[^}]*\.pf-detailed-printer-card\s*\{([^}]*)\}/,
    );

    expect(
      mediaBlock,
      'expected the 26rem floor to be gated behind the 640px grid breakpoint',
    ).not.toBeNull();
    expect(mediaBlock![1]).toMatch(/min-width\s*:\s*26rem/);
  });
});

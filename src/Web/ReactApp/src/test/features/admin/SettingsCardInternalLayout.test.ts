import fs from 'node:fs';
import path from 'node:path';
import { describe, it, expect } from 'vitest';

/**
 * Layout inside a settings card must not key off the viewport (#1026).
 *
 * A settings card is laid out by `SettingsPage`'s `columns-*` flow, so its real
 * width has no fixed relationship to the viewport. Measured on
 * `?scope=system&tab=general&sub=automation` before the fix, the Obico
 * extension's container was 700px at a 1280px viewport, 860px at 1440px, 645px
 * at 1920px and 627px at 2560px — it gets *narrower* as the window grows,
 * because a wider page opens more columns and each column is shorter.
 *
 * `lg:grid-cols-2` therefore fired unconditionally and split a 645px container
 * into two 315px halves, which in turn forced `sm:grid-cols-4` to squeeze four
 * metric tiles into 61px each. Every tile label overflowed its box at every
 * width tested. The remedy is `repeat(auto-fit, minmax(...))`, which resolves
 * against the element's own width and needs no container-query ancestor.
 *
 * These files are asserted as source text because jsdom does not lay out CSS
 * and the defect is a class string, not behaviour. Geometry was verified
 * separately in Chromium at 1280/1440/1920/2560px: no label overflows and the
 * `Registered Obico ML Servers` heading sits on one line at all four.
 */

const ROOT = path.resolve(__dirname, '../../..');

const CARD_INTERNAL_FILES = [
  'features/admin/settings/section-renderers.tsx',
  'features/admin/components/FailureDetectionStatusCard.tsx',
  'features/admin/components/ObicoServersSection.tsx',
];

/** `sm:` / `md:` / `lg:` / `xl:` / `2xl:` applied to a grid-template utility. */
const VIEWPORT_GRID = /\b(?:sm|md|lg|xl|2xl):grid-cols-/g;

function read(relative: string): string {
  return fs.readFileSync(path.join(ROOT, relative), 'utf8');
}

describe('settings card internal layout (#1026)', () => {
  it.each(CARD_INTERNAL_FILES)('%s uses no viewport grid breakpoints', relative => {
    const offenders = read(relative).match(VIEWPORT_GRID) ?? [];
    expect(offenders).toEqual([]);
  });

  it('the Obico extension sizes its two panels by their own width', () => {
    // 28rem: below that the status card cannot hold four legible metric tiles
    // side by side, so a single 645px column is the correct answer.
    expect(read(CARD_INTERNAL_FILES[0])).toContain('grid-cols-[repeat(auto-fit,minmax(28rem,1fr))]');
  });

  it('metric tiles reserve the widest label word', () => {
    // 7.5rem = 82px ("CONFIGURED" at 12px semibold uppercase, tracking-wide)
    // plus px-4 and the 1px borders. Narrower and the word overflows the tile,
    // which is what shipped: 61px tiles holding an 82px word.
    expect(read(CARD_INTERNAL_FILES[1])).toContain('grid-cols-[repeat(auto-fit,minmax(7.5rem,1fr))]');
  });

  it('the Obico servers heading can drop its action button to a second line', () => {
    const source = read(CARD_INTERNAL_FILES[2]);
    // Without `flex-wrap` the `Add Server` button squeezes the heading until
    // "Registered Obico ML Servers" breaks mid-phrase.
    expect(source).toContain('flex flex-wrap items-start justify-between');
  });
});

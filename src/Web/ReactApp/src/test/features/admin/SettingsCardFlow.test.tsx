import '@testing-library/jest-dom';
import fs from 'node:fs';
import path from 'node:path';
import React from 'react';
import { render, screen, waitFor, cleanup } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { describe, it, expect, vi, beforeEach } from 'vitest';

/**
 * Layout contract for the settings page flow.
 *
 * Originally written for #1011 (density-aware card flow, container-query field
 * rows). Rewritten because #1011's remedy could not fire: it decided the column
 * count *inside* a band, and a settings band routinely holds exactly one
 * section, so the flow resolved to `columns-1` at every width. Measured on
 * `System Config` before the fix — 1440px window: 814px flow, 1 column; 1920px:
 * 1294px, 1 column; 2560px: 1934px, 1 column with the card capped at 1024px and
 * 910px of the page blank.
 *
 * The unit that flows is therefore the band. Cards keep a flow of their own,
 * with identical thresholds, which resolves against the band's width — so a
 * page of many bands stacks its cards inside narrow band columns, and a page
 * with a single full-width band still flows its cards. Neither flow needs to
 * know about the other.
 *
 * jsdom does not lay out CSS, so these tests assert the *contract* the CSS
 * hangs off rather than measured geometry: which utilities are emitted, and —
 * the part that is real logic rather than a class-string tautology — that the
 * column count is capped by how many bands/cards actually exist and that
 * filtered-out bands do not open columns they will never fill.
 *
 * Geometry was verified separately in Chromium at 430/768/1280/1440/1920/2560px.
 */

type Prop = {
  name: string;
  type: string;
  attributes: string[];
  display?: Record<string, unknown>;
};

type Section = {
  key: string;
  className: string;
  displayName: string;
  group: string;
  order: number;
  properties: Prop[];
};

let sections: Section[] = [];

function makeSection(index: number, group: string): Section {
  return {
    key: `Section${index}`,
    className: `Section${index}`,
    displayName: `Section ${index}`,
    group,
    order: index + 1,
    properties: [
      {
        name: 'retentionDays',
        type: 'number',
        attributes: [],
        display: { name: `Retention ${index}`, inputType: 'Number' },
      },
    ],
  };
}

/** One section per band — the shape that broke #1011's remedy. */
function makeBands(count: number) {
  return Array.from({ length: count }, (_, i) => makeSection(i, `Group${i}`));
}

/** All sections in a single band. */
function makeCardsInOneBand(count: number) {
  return Array.from({ length: count }, (_, i) => makeSection(i, 'System'));
}

vi.mock('@/services/settingsApi', () => ({
  fetchSettingsMetadata: vi.fn(() => Promise.resolve(sections)),
  fetchSettingsGroups: vi.fn(() =>
    Promise.resolve(
      Array.from(new Set(sections.map((s) => s.group))).map((key, i) => ({
        key,
        displayName: key,
        order: i + 1,
      })),
    ),
  ),
  fetchSettingsUnified: vi.fn(() =>
    Promise.resolve(
      Object.fromEntries(sections.map((s) => [s.key, { retentionDays: 30 }])),
    ),
  ),
  saveSettingsValues: vi.fn(),
}));

vi.mock('@/hooks/useSlicer', () => ({
  useSlicer: () => ({ isSlicerAvailable: true, workerCount: 1 }),
}));
vi.mock('@/common/hooks/usePageTour', () => ({
  usePageTour: () => ({ startTour: vi.fn(), hasSeenTour: true, resetTour: vi.fn() }),
}));
vi.mock('@/features/admin/tours/settings.tour', () => ({ settingsTour: [] }));
vi.mock('@/features/admin/components/ObicoServersSection', () => ({
  ObicoServersSection: () => React.createElement('div'),
}));
vi.mock('@/features/admin/components/FailureDetectionStatusCard', () => ({
  FailureDetectionStatusCard: () => React.createElement('div'),
}));

import { SettingsPage } from '@/features/admin/pages/SettingsPage';
import { SettingsPagelet, type SettingMetadata } from '@/common/components/SettingsPagelet';

async function renderPage(fixture: Section[]) {
  sections = fixture;
  const { container } = render(
    <MemoryRouter>
      <SettingsPage />
    </MemoryRouter>,
  );
  await waitFor(() => {
    expect(screen.getByLabelText('Retention 0')).toBeInTheDocument();
  });
  return container;
}

async function renderBands(bandCount: number) {
  await renderPage(makeBands(bandCount));
  return screen.getByTestId('settings-band-flow');
}

async function renderCardsInOneBand(cardCount: number) {
  await renderPage(makeCardsInOneBand(cardCount));
  return screen.getByTestId('settings-card-flow');
}

describe('settings band flow density', () => {
  beforeEach(() => {
    window.localStorage.setItem('pf.settings.mode', 'everything');
  });

  it('never opens a second column for a single band', async () => {
    const flow = await renderBands(1);
    expect(flow.className).not.toContain('columns-2');
    expect(flow.className).not.toContain('columns-3');
  });

  it('opens a second column — but not a third — for two bands', async () => {
    const flow = await renderBands(2);
    expect(flow.className).toContain('@[59rem]:columns-2');
    expect(flow.className).not.toContain('columns-3');
  });

  it('opens up to three columns once there are three bands', async () => {
    const flow = await renderBands(3);
    expect(flow.className).toContain('@[59rem]:columns-2');
    expect(flow.className).toContain('@[89rem]:columns-3');
  });

  it('flows bands rather than only the cards inside one band', async () => {
    // The regression this file exists for. Every band on a real settings page
    // holds one section, so a flow that only ever subdivided *within* a band
    // resolved to one full-width column at every viewport width.
    const flow = await renderBands(3);
    expect(flow.className).toContain('columns-');
    const bands = Array.from(flow.children);
    expect(bands).toHaveLength(3);
  });

  it('keeps a band whole across a column break', async () => {
    // A band split across columns would strand its caption above one card and
    // its remaining cards in the next column.
    const flow = await renderBands(3);
    for (const band of Array.from(flow.children)) {
      expect(band.className).toContain('break-inside-avoid');
    }
  });

  it('sizes columns from the content width, not the viewport', async () => {
    // The space available depends on the app rail and the settings sidebar. A
    // viewport breakpoint would be wrong the moment either changes.
    const flow = await renderBands(3);
    expect(flow.parentElement?.className).toContain('@container');
    expect(flow.className).not.toMatch(/\b(sm|md|lg|xl|2xl):columns-/);
  });
});

describe('settings card flow density', () => {
  beforeEach(() => {
    window.localStorage.setItem('pf.settings.mode', 'everything');
  });

  it('caps the measure of a single-card band at the two-column threshold', async () => {
    const flow = await renderCardsInOneBand(1);
    expect(flow.className).toContain('max-w-[59rem]');
    expect(flow.className).not.toContain('columns-2');
  });

  it('leaves no width where a lone card sits short of the page chrome', async () => {
    // The filter row and section headings above the cards are full-bleed. If
    // the single-card cap is below the two-column threshold, every window
    // between them renders a card that stops short of the chrome directly
    // above it, against bare background. When the cap was 64rem against a 74rem
    // threshold that was a 150px strip on a 1728px window.
    //
    // Pinning the cap *to* the threshold closes it by construction: the card
    // grows until the exact width a second column appears. These two numbers
    // have to move together, so assert them against one source.
    const lone = (await renderCardsInOneBand(1)).className;
    cleanup();
    const many = (await renderCardsInOneBand(3)).className;

    const cap = /max-w-\[(\d+(?:\.\d+)?)rem\]/.exec(lone)?.[1];
    const twoCol = /@\[(\d+(?:\.\d+)?)rem\]:columns-2/.exec(many)?.[1];

    expect(cap).toBeDefined();
    expect(twoCol).toBeDefined();
    expect(Number(cap)).toBe(Number(twoCol));
  });

  it('caps the pagelet at the same width the card flow does', async () => {
    // The two caps live in different modules — `cardFlowClass` in SettingsPage
    // and the `@container` element in SettingsPagelet — and a divergence is
    // invisible until someone widens one of them. The pagelet's cap is the
    // inner bound; if it ever falls below the flow's, the field rules stop
    // short of the card's right edge, which is the exact fault #1032 removed.
    //
    // Reading them out of two different rendered elements is the point: the
    // sibling test above compares `cardFlowClass` to itself and cannot catch
    // this.
    const flow = await renderCardsInOneBand(1);
    const pagelet = screen.getAllByTestId('settings-pagelet-measure')[0];

    const flowCap = /max-w-\[(\d+(?:\.\d+)?)rem\]/.exec(flow.className)?.[1];
    const pageletCap = /max-w-\[(\d+(?:\.\d+)?)rem\]/.exec(pagelet.className)?.[1];

    expect(flowCap).toBeDefined();
    expect(pageletCap).toBeDefined();
    expect(Number(pageletCap)).toBe(Number(flowCap));
  });

  it('lets a lone band flow its own cards', async () => {
    // A page with one band gives that band the full content width, so its
    // cards are the thing with room to flow. The shared thresholds resolve
    // against the band's `@container`, so this needs no extra coordination.
    const flow = await renderCardsInOneBand(3);
    expect(flow.className).toContain('@[59rem]:columns-2');
    expect(flow.className).toContain('@[89rem]:columns-3');
    expect(flow.className).not.toContain('max-w-[59rem]');
  });

  it('keeps cards whole across a column break', async () => {
    const flow = await renderCardsInOneBand(3);
    for (const card of Array.from(flow.children)) {
      expect(card.className).toContain('break-inside-avoid');
    }
  });
});

const metadata: SettingMetadata = {
  key: 'Demo',
  className: 'Demo',
  displayName: 'Demo',
  properties: [
    { name: 'count', type: 'Int32', attributes: [], display: { name: 'Count', inputType: 'Number' } },
    { name: 'host', type: 'string', attributes: [], display: { name: 'Host', inputType: 'Hostname' } },
    { name: 'label', type: 'string', attributes: [], display: { name: 'Label', inputType: 'Text' } },
  ],
};

function renderPagelet() {
  return render(
    <SettingsPagelet
      metadata={metadata}
      values={{ count: 5, host: 'printer.local', label: 'Front row' }}
      onChange={vi.fn()}
      compact
    />,
  );
}

/**
 * The widest label the app ships, and the width its *track* would need to hold
 * it on one line.
 *
 * ⚠️ **This is not a floor the layout must clear (#1030).** The label track is
 * a `13.5rem` (216px) cap, per the ACC proposal, and a label wider than that
 * wraps onto a second line. Sizing the track against this figure cost 96px on
 * every row in the application to keep one label on one line on one tab.
 *
 * The figure dropped sharply in #1025, which moved unit parentheticals out of
 * label text and into control adornments. Before that the widest was "Print
 * Warmup Grace Period (seconds)" at 275px + 22px of `InfoTooltip`, and ten
 * labels wrapped; nine of them did so *only* because of their unit suffix.
 * Measured live at 1600px after the move, exactly one label still wraps, and
 * it does so by landing precisely on the cap: 194px of text + 22px of tooltip
 * affordance is 216px against a 216px track. That is an accepted outcome for
 * one label — the alternative is charging every row in the app for it, which
 * is what #1030 removed.
 *
 * It is kept as the tripwire for that convention. If a longer label appears,
 * the likely cause is a new setting that wrote its unit into the name instead
 * of using `Unit`, and `has not gained a label longer than the one that was
 * measured` fails so that is visible rather than silent.
 *
 * Update this only from a fresh measurement, never to make a test pass.
 */
const LONGEST_LABEL = 'Spool Burn-Rate Minimum Samples';
const LONGEST_LABEL_PX = 194 + 22;

/**
 * Padding + border a card spends before its field grid starts.
 *
 * `1px` border each side plus the row's own `px-4` (16px each side). The
 * horizontal padding moved from the card body onto the rows in #1032 so the
 * `divide-y` rules run edge to edge, as they do in the proposal — the total a
 * card spends is unchanged, but it is now spent inside the container query's
 * measuring box rather than outside it.
 */
const CARD_CHROME_PX = 2 + 32;

/** Column gap between bands and between cards. `gap-4`. */
const COLUMN_GAP_PX = 16;

/**
 * The label track **cap** in `FIELD_ROW_CLASS`. `13.5rem`, from the ACC
 * proposal's `.p-f` rule. A cap, not a floor: the track may shrink below this
 * and a long label may wrap. See the note on `LONGEST_LABEL_PX`.
 */
const LABEL_TRACK_CAP_PX = 216;

/** Column gap inside a field row. `gap-x-3`, matching the proposal's 12px. */
const FIELD_GAP_PX = 12;

/**
 * The narrowest control that still holds the widest content the app puts in
 * one: a `255.255.255.255/32` CIDR in the mono face plus its clear button.
 */
const MIN_CONTROL_PX = 200;

/**
 * The narrowest card **inner** width a column may produce — the grid box, after
 * the row's own padding. Derived, not chosen: the label track cap, the in-row
 * gap, and a control that still fits its content. 216 + 12 + 200 = 428px.
 */
const NARROWEST_CARD_INNER = LABEL_TRACK_CAP_PX + FIELD_GAP_PX + MIN_CONTROL_PX;

/**
 * The same card measured the way the container query measures it.
 *
 * `@[26rem]` on a field row resolves against `SettingsPagelet`'s `@container`
 * element, which *contains* the row's `px-4`. So the row threshold and
 * `NARROWEST_CARD_INNER` are in different boxes and must not be compared
 * directly: at the 416px switch point the grid is 384px, not 428px.
 *
 * The column math spends 34px of chrome (`CARD_CHROME_PX`); 2px of that is the
 * card's border, which sits outside the container element, and 32px is the row
 * padding, which sits inside it. So the container is 2px narrower than the
 * card: 462 − 2 = 460px.
 */
const NARROWEST_CARD_CONTAINER = NARROWEST_CARD_INNER + CARD_CHROME_PX - 2;

describe('field rows', () => {
  it('has no fixed-width label gutter left anywhere', () => {
    // The `w-64` this replaced left roughly 164px for the control inside a
    // 420px card. Any fixed width on the label reintroduces that failure —
    // the label track must come from the grid, which can respond to the card.
    const { container } = renderPagelet();
    const labels = Array.from(container.querySelectorAll('label'));
    expect(labels.length).toBeGreaterThan(0);
    for (const label of labels) {
      expect(label.className).not.toMatch(/(^|\s)w-/);
      expect(label.className).not.toMatch(/(^|\s)(min|max)-w-/);
    }
  });

  it('drives the label/control split from the card width, not the viewport', () => {
    const { container } = renderPagelet();
    const list = container.firstElementChild as HTMLElement;
    expect(list.className).toContain('@container');
    const row = container.querySelector('[data-setting-property]') as HTMLElement;
    expect(row.className).toMatch(/@\[\d+(\.\d+)?rem\]:grid-cols-/);
    expect(row.className).not.toMatch(/\b(sm|md|lg|xl|2xl):grid-cols-/);
  });

  it('keeps rows side-by-side in the narrowest card the flow can produce', () => {
    // The property being asserted is that no card the column flow produces is
    // ever narrow enough to stack its rows. That is a claim about the *container*
    // the query measures, so it is checked against `NARROWEST_CARD_CONTAINER`
    // (460px) — not against `NARROWEST_CARD_INNER`, which is the grid box and
    // is 32px smaller. Comparing the threshold to 428 happens to pass but
    // asserts a relationship that does not exist.
    const row = renderPagelet().container.querySelector(
      '[data-setting-property]',
    ) as HTMLElement;
    const threshold = /@\[([\d.]+)rem\]:grid-cols-\[minmax\(/.exec(row.className);
    expect(threshold).not.toBeNull();
    expect(Number(threshold![1]) * 16).toBeLessThanOrEqual(NARROWEST_CARD_CONTAINER);
  });

  it('caps the label track rather than flooring it', () => {
    // #1030. The distinction is the whole point: `minmax(0, 13.5rem)` may
    // shrink, `minmax(19.5rem, ...)` may not. The floor bought a guarantee
    // ("no label ever wraps") that the proposal never asked for and charged
    // 96px per row across the app to keep it.
    const row = renderPagelet().container.querySelector(
      '[data-setting-property]',
    ) as HTMLElement;
    const track = /grid-cols-\[minmax\(([\d.]+(?:rem)?),\s*([\d.]+)rem\)/.exec(row.className);
    expect(track).not.toBeNull();
    expect(track![1]).toBe('0');
    expect(Number(track![2]) * 16).toBe(LABEL_TRACK_CAP_PX);
  });

  it('gives the control the rest of the row, unweighted', () => {
    // `minmax(0, 1fr)`, not a `0.64fr` share. A ratio only makes sense against
    // a track that can grow; against a hard cap it just moves the label away
    // from the control it names.
    const row = renderPagelet().container.querySelector(
      '[data-setting-property]',
    ) as HTMLElement;
    expect(row.className).toContain('minmax(0,1fr)');
    expect(row.className).not.toContain('fr)_minmax(0,0.64fr)');
  });

  it('has exactly one side-by-side rule, so cap and threshold cannot disagree', () => {
    // #1020 shipped three times with a floor, a ratio and an inversion cap
    // chosen independently; twice they disagreed and wide cards wrapped while
    // narrow ones stayed clean. A cap cannot be stretched by an `fr` ratio, so
    // the inversion band that guarded that crossover is gone — and must stay
    // gone, or the same class of bug is available again.
    const row = renderPagelet().container.querySelector(
      '[data-setting-property]',
    ) as HTMLElement;
    const rules = row.className.match(/@\[[\d.]+rem\]:grid-cols-/g) ?? [];
    expect(rules).toHaveLength(1);
  });

  it('separates rows with a hairline so a card reads as a spec sheet', () => {
    const list = renderPagelet().container.firstElementChild as HTMLElement;
    expect(list.className).toContain('divide-y');
    expect(list.className).toContain('divide-pf-border-divider');
  });

  it('renders machine values in the mono face with tabular figures', () => {
    renderPagelet();
    for (const label of ['Count', 'Host']) {
      const el = screen.getByLabelText(label);
      expect(el.className).toContain('font-pf-mono');
      expect(el.className).toContain('tabular-nums');
    }
  });

  it('leaves prose fields in the body face', () => {
    renderPagelet();
    expect(screen.getByLabelText('Label').className).not.toContain('font-pf-mono');
  });
});

/**
 * The two halves of #1020's fix live in different files — `bandFlowClass` in
 * `SettingsPage.tsx` decides how many columns open, `FIELD_ROW_CLASS` in
 * `SettingsPagelet.tsx` decides how a row inside the resulting card splits.
 * Neither imports the other, so nothing but this block stops someone lowering
 * a column threshold, halving the card width, and silently reintroducing the
 * three-line labels the epic was reopened to fix.
 *
 * Rather than restate the numbers, these derive the narrowest card each flow
 * can produce from the flow's own emitted class names.
 */
describe('column thresholds and field rows stay coupled (#1020)', () => {
  beforeEach(() => {
    window.localStorage.setItem('pf.settings.mode', 'everything');
  });

  /** Narrowest card a flow yields at `cols`, given its `@[Nrem]` threshold. */
  function narrowestCardOuter(thresholdRem: number, cols: number): number {
    return (thresholdRem * 16 - (cols - 1) * COLUMN_GAP_PX) / cols;
  }

  it('never opens a column narrower than a legible field row', async () => {
    const flow = await renderBands(3);
    const two = /@\[(\d+)rem\]:columns-2/.exec(flow.className);
    const three = /@\[(\d+)rem\]:columns-3/.exec(flow.className);
    expect(two).not.toBeNull();
    expect(three).not.toBeNull();

    for (const [match, cols] of [
      [two!, 2],
      [three!, 3],
    ] as const) {
      const inner = narrowestCardOuter(Number(match[1]), cols) - CARD_CHROME_PX;
      // The row must still be side by side...
      expect(inner).toBeGreaterThanOrEqual(NARROWEST_CARD_INNER);
      // ...and the control must still hold the widest content the app puts in
      // one, or the row reads as a label with a sliver attached. The track is
      // now a hard cap (#1030), so it contributes a constant, not a ratio.
      expect(inner - LABEL_TRACK_CAP_PX - FIELD_GAP_PX).toBeGreaterThanOrEqual(MIN_CONTROL_PX);
    }
  });

  it('keeps the band flow and the card flow on identical thresholds', async () => {
    // A band holding one card is the common case, so the two flows are the
    // same layout seen at two scales. Letting them diverge means a card inside
    // a lone band breaks at a different width than the bands around it.
    const bandFlow = await renderBands(3);
    const bandThresholds = bandFlow.className.match(/@\[\d+rem\]:columns-\d/g)?.sort();

    cleanup();
    const cardFlow = await renderCardsInOneBand(3);
    const cardThresholds = cardFlow.className.match(/@\[\d+rem\]:columns-\d/g)?.sort();

    expect(bandThresholds).toBeDefined();
    expect(bandThresholds).toEqual(cardThresholds);
  });
});

/**
 * The whole geometry in this file rests on one number that cannot be computed
 * here: how wide the widest label renders. That needs a browser and a font.
 *
 * #1020 shipped twice with that number measured off whichever tab happened to
 * be open, and both times the unit tests passed while the app visibly wrapped.
 * The constant is the weak seam, so it gets its own guard: read the labels back
 * out of the C# they are declared in and fail if one appears that the pinned
 * measurement never covered.
 *
 * Character count is not width — "Print Warmup Grace Period (seconds)" and
 * "Active Sync Initial Delay (Seconds)" are both 35 characters and differ by
 * 21px. It is used only as a *tripwire*: a longer label is a signal to go
 * re-measure, not a claim about pixels. The floor carries 15px of headroom on
 * top, which covers roughly two average characters of drift.
 */
describe('guards the measurement this file is built on (#1020)', () => {
  /** Every `[SettingDisplay(Name = "...")]` value in the backend. */
  function shippedSettingLabels(): string[] {
    const skip = new Set(['obj', 'bin', 'node_modules', '.git', 'Web']);
    const roots = [path.resolve(process.cwd(), '..', '..')];
    const names = new Set<string>();

    while (roots.length > 0) {
      const dir = roots.pop()!;
      for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
        if (entry.isDirectory()) {
          if (!skip.has(entry.name)) roots.push(path.join(dir, entry.name));
          continue;
        }
        if (!entry.name.endsWith('.cs')) continue;
        const source = fs.readFileSync(path.join(dir, entry.name), 'utf8');
        // Split on the attribute rather than matching `SettingDisplay(Name =`
        // directly. Two blind spots to avoid, both of which have already bitten
        // this guard's subject matter:
        //
        //   - the attribute is written inline *and* across several lines, so a
        //     line-oriented match misses ObicoSettings entirely
        //   - `Name` is the first argument in all of today's usages, but
        //     `[SettingDisplay(Group = ..., Name = ...)]` is legal and would
        //     slip an anchored regex
        //
        // Taking the first `Name = "..."` after each occurrence can only ever
        // over-match, never under-match, which is the safe direction: a spurious
        // hit fails the tripwire and prompts a re-measure, where a miss ships a
        // wrapping label.
        for (const chunk of source.split('SettingDisplay(').slice(1)) {
          const m = /\bName\s*=\s*"([^"]+)"/.exec(chunk);
          if (m) names.add(m[1]);
        }
      }
    }
    return [...names];
  }

  it('still finds the label the row was measured against', () => {
    const labels = shippedSettingLabels();
    // A sanity floor: if the scan silently matched nothing, every other
    // assertion here would pass vacuously.
    expect(labels.length).toBeGreaterThan(100);
    expect(labels).toContain(LONGEST_LABEL);
  });

  it('has not gained a label longer than the one that was measured', () => {
    const longer = shippedSettingLabels()
      .filter((name) => name.length > LONGEST_LABEL.length)
      .sort((a, b) => b.length - a.length);

    expect(
      longer,
      `New setting label(s) longer than the ${LONGEST_LABEL.length}-character label the row was `
      + `measured against (${JSON.stringify(LONGEST_LABEL)} = ${LONGEST_LABEL_PX}px incl. tooltip):\n`
      + `${longer.map((n) => `  ${n.length}  ${n}`).join('\n')}\n\n`
      + 'Since #1030 the label track is a 13.5rem CAP, so a long label wraps rather than widening '
      + 'every row in the app — this is not a layout break. It is a prompt to check the label '
      + 'itself: labels get long because they carry their units ("(seconds)", "(minutes)"), and '
      + 'those belong in a control adornment (#1025), not in the name. Either shorten the label or '
      + 'give it an adornment, then update LONGEST_LABEL / LONGEST_LABEL_PX from a fresh '
      + 'measurement. Do NOT raise the label track cap to accommodate it.',
    ).toEqual([]);
  });
});

import '@testing-library/jest-dom';
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
    expect(flow.className).toContain('@[68rem]:columns-2');
    expect(flow.className).not.toContain('columns-3');
  });

  it('opens up to three columns once there are three bands', async () => {
    const flow = await renderBands(3);
    expect(flow.className).toContain('@[68rem]:columns-2');
    expect(flow.className).toContain('@[102rem]:columns-3');
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

  it('caps the measure of a single-card band', async () => {
    const flow = await renderCardsInOneBand(1);
    expect(flow.className).toContain('max-w-[64rem]');
    expect(flow.className).not.toContain('columns-2');
  });

  it('lets a lone band flow its own cards', async () => {
    // A page with one band gives that band the full content width, so its
    // cards are the thing with room to flow. The shared thresholds resolve
    // against the band's `@container`, so this needs no extra coordination.
    const flow = await renderCardsInOneBand(3);
    expect(flow.className).toContain('@[68rem]:columns-2');
    expect(flow.className).toContain('@[102rem]:columns-3');
    expect(flow.className).not.toContain('max-w-[64rem]');
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
 * The width the label *track* must hold, measured in Chromium against the real
 * `System Config` labels. The label is a flex row of two children, and the
 * second one is easy to forget:
 *
 *   196px  text span   "Enable Background Scanning"  <- widest
 *    22px  InfoTooltip  16px icon + its own ml-1.5
 *
 * A first pass at #1020 sized the floor from the 196px alone and left two
 * widths still wrapping by two pixels. Update this only from a fresh
 * measurement, never to make a test pass.
 */
const LONGEST_LABEL_PX = 196 + 22;

/** Padding + border a card spends before its field grid starts. */
const CARD_CHROME_PX = 49;

/** Column gap between bands and between cards. `gap-4`. */
const COLUMN_GAP_PX = 16;

/** The label track floor in `FIELD_ROW_CLASS`. `14.5rem`. */
const LABEL_TRACK_FLOOR_PX = 232;

/**
 * The narrowest card inner width a column may produce. Derived, not chosen:
 * below it the control is narrower than the label beside it and the row stops
 * reading as a pair. `inner - floor - gap >= floor`.
 */
const NARROWEST_CARD_INNER = LABEL_TRACK_FLOOR_PX * 2 + COLUMN_GAP_PX;

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
    // The threshold is set by layout, not taste, and the number it has to
    // clear moved when #1020 retuned the column thresholds for legibility.
    // `bandFlowClass` now floors a card at ~529px outer / ~480px inner, so a
    // `30rem` (480px) row threshold keeps every row on a real page side by
    // side. The coupling between the two is asserted in its own test rather
    // than left implicit.
    const row = renderPagelet().container.querySelector(
      '[data-setting-property]',
    ) as HTMLElement;
    const threshold = /@\[([\d.]+)rem\]:grid-cols-\[minmax\(/.exec(row.className);
    expect(threshold).not.toBeNull();
    expect(Number(threshold![1]) * 16).toBeLessThanOrEqual(NARROWEST_CARD_INNER);
  });

  it('floors the label track above the longest label the app ships', () => {
    // The defect #1020 was filed for. The old `9rem` (144px) floor was
    // justified as stopping labels shredding "one word per line" and did not:
    // measured in Chromium, the `Enable Background Scanning` label block is
    // 218px and wrapped to three lines against it. Anything at or below the
    // longest label reintroduces that, so assert the floor clears it.
    const row = renderPagelet().container.querySelector(
      '[data-setting-property]',
    ) as HTMLElement;
    const floor = /grid-cols-\[minmax\(([\d.]+)rem,/.exec(row.className);
    expect(floor).not.toBeNull();
    expect(Number(floor![1]) * 16).toBe(LABEL_TRACK_FLOOR_PX);
    expect(LABEL_TRACK_FLOOR_PX).toBeGreaterThanOrEqual(LONGEST_LABEL_PX);
  });

  it('gives the control the majority of the row', () => {
    const row = renderPagelet().container.querySelector(
      '[data-setting-property]',
    ) as HTMLElement;
    expect(row.className).toContain('0.64fr');
  });

  it('stops the label drifting away from its control in wide cards', () => {
    // Above 52rem the ratio inverts into a problem: 36% of a 1000px card is
    // 360px of dead space between a label and the control it names.
    const row = renderPagelet().container.querySelector(
      '[data-setting-property]',
    ) as HTMLElement;
    expect(row.className).toContain('@[52rem]:grid-cols-[minmax(0,16rem)_minmax(0,1fr)]');
  });

  it('cannot reintroduce wrapping via the wide-card inversion', () => {
    // The 52rem branch drops the label track from 36% to a hard 16rem cap. If
    // that cap ever fell below the longest label, wide cards would start
    // wrapping while narrow ones stayed clean — the least findable version of
    // this bug.
    const row = renderPagelet().container.querySelector(
      '[data-setting-property]',
    ) as HTMLElement;
    const cap = /@\[52rem\]:grid-cols-\[minmax\(0,([\d.]+)rem\)/.exec(row.className);
    expect(cap).not.toBeNull();
    expect(Number(cap![1]) * 16).toBeGreaterThanOrEqual(LONGEST_LABEL_PX);
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
      // ...and the label must still fit on one line. The track is
      // `max(14.5rem, 0.36 * inner)`, so the floor governs at these widths.
      const track = Math.max(LABEL_TRACK_FLOOR_PX, 0.36 * inner);
      expect(track).toBeGreaterThanOrEqual(LONGEST_LABEL_PX);
      // ...and the control must not end up narrower than the label beside it,
      // which is what makes a row read as a pair rather than a label with a
      // sliver attached.
      expect(inner - track - COLUMN_GAP_PX).toBeGreaterThanOrEqual(track);
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

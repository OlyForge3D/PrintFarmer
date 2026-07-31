import '@testing-library/jest-dom';
import React from 'react';
import { render, screen, waitFor } from '@testing-library/react';
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
    expect(flow.className).toContain('@[52rem]:columns-2');
    expect(flow.className).not.toContain('columns-3');
  });

  it('opens up to three columns once there are three bands', async () => {
    const flow = await renderBands(3);
    expect(flow.className).toContain('@[52rem]:columns-2');
    expect(flow.className).toContain('@[78rem]:columns-3');
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
    expect(flow.className).toContain('@[52rem]:columns-2');
    expect(flow.className).toContain('@[78rem]:columns-3');
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
    expect(row.className).toContain('@[23rem]:grid-cols-');
    expect(row.className).not.toMatch(/\b(sm|md|lg|xl|2xl):grid-cols-/);
  });

  it('keeps rows side-by-side in the narrowest card the flow can produce', () => {
    // The threshold is set by layout, not taste. Three band columns in a
    // 1440px window land a card at ~435px outer / ~401px inner. A threshold
    // above that would collapse every row on the page back to stacked, which
    // is the exact failure the label/control ratio exists to prevent.
    const row = renderPagelet().container.querySelector(
      '[data-setting-property]',
    ) as HTMLElement;
    const threshold = /@\[(\d+)rem\]:grid-cols-\[minmax\(9rem/.exec(row.className);
    expect(threshold).not.toBeNull();
    expect(Number(threshold![1]) * 16).toBeLessThanOrEqual(401);
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

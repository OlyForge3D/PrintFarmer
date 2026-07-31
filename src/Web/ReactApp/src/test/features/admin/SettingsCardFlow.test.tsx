import '@testing-library/jest-dom';
import React from 'react';
import { render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { describe, it, expect, vi, beforeEach } from 'vitest';

/**
 * Layout contract for issue #1011 — density-aware card flow and
 * container-query field rows.
 *
 * jsdom does not lay out CSS, so these tests assert the *contract* the CSS
 * hangs off rather than measured geometry: which utilities are emitted, and —
 * the part that is real logic rather than a class-string tautology — that the
 * column count is capped by how many cards actually exist.
 *
 * Geometry was verified separately in Chromium at 430/768/1280/1440/1600/1920/
 * 2560px. Recorded there: 1 column below 58rem of flow width, 2 above, 3 above
 * 88rem; field rows side-by-side above a 26rem card; controls holding 62% of
 * the row at every side-by-side size; no horizontal overflow at 430px; and the
 * flow's own height falling 3744 -> 2312 -> 1779px as columns are added, which
 * is the ragged-bottom whitespace this issue set out to remove.
 */

type Prop = {
  name: string;
  type: string;
  attributes: string[];
  display?: Record<string, unknown>;
};

let sections: Array<{
  key: string;
  className: string;
  displayName: string;
  group: string;
  order: number;
  properties: Prop[];
}> = [];

function makeSections(count: number) {
  return Array.from({ length: count }, (_, i) => ({
    key: `Section${i}`,
    className: `Section${i}`,
    displayName: `Section ${i}`,
    group: 'System',
    order: i + 1,
    properties: [
      {
        name: 'retentionDays',
        type: 'number',
        attributes: [],
        display: { name: `Retention ${i}`, inputType: 'Number' },
      },
    ],
  }));
}

vi.mock('@/services/settingsApi', () => ({
  fetchSettingsMetadata: vi.fn(() => Promise.resolve(sections)),
  fetchSettingsGroups: vi.fn(() =>
    Promise.resolve([{ key: 'System', displayName: 'System', order: 1 }]),
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

async function renderFlow(cardCount: number) {
  sections = makeSections(cardCount);
  const { container } = render(
    <MemoryRouter>
      <SettingsPage />
    </MemoryRouter>,
  );
  await waitFor(() => {
    expect(screen.getByLabelText('Retention 0')).toBeInTheDocument();
  });
  const flow = container.querySelector('[class*="columns-1"]');
  expect(flow).not.toBeNull();
  return flow as HTMLElement;
}

describe('#1011 — card flow density', () => {
  beforeEach(() => {
    window.localStorage.setItem('pf.settings.mode', 'everything');
  });

  it('never opens a second column for a single card', async () => {
    // CSS multicol fills column one first, so a lone card in a two-column flow
    // renders at half width with the other half blank. Settings bands very
    // often hold exactly one section, so this is the common case.
    const flow = await renderFlow(1);
    expect(flow.className).not.toContain('columns-2');
    expect(flow.className).not.toContain('columns-3');
  });

  it('caps the measure of a single-card flow', async () => {
    const flow = await renderFlow(1);
    expect(flow.className).toContain('max-w-[64rem]');
  });

  it('opens a second column — but not a third — for two cards', async () => {
    const flow = await renderFlow(2);
    expect(flow.className).toContain('@[58rem]:columns-2');
    expect(flow.className).not.toContain('columns-3');
    expect(flow.className).not.toContain('max-w-[64rem]');
  });

  it('opens up to three columns once there are three cards', async () => {
    const flow = await renderFlow(3);
    expect(flow.className).toContain('@[58rem]:columns-2');
    expect(flow.className).toContain('@[88rem]:columns-3');
  });

  it('keeps cards whole across a column break', async () => {
    const flow = await renderFlow(3);
    for (const card of Array.from(flow.children)) {
      expect(card.className).toContain('break-inside-avoid');
    }
  });

  it('sizes columns from the content width, not the viewport', async () => {
    // The space available to cards depends on the app rail and the settings
    // sidebar. A viewport breakpoint would be wrong the moment either changes.
    const flow = await renderFlow(3);
    expect(flow.parentElement?.className).toContain('@container');
    expect(flow.className).not.toMatch(/\b(sm|md|lg|xl|2xl):columns-/);
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

describe('#1011 — field rows', () => {
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
    expect(row.className).toContain('@[26rem]:grid-cols-');
    expect(row.className).not.toMatch(/\b(sm|md|lg|xl|2xl):grid-cols-/);
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

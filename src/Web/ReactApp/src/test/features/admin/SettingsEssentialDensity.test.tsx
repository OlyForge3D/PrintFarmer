import '@testing-library/jest-dom';
import React from 'react';
import { render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { describe, it, expect, vi, beforeEach } from 'vitest';

/**
 * #1012 tier 2 — "Essential density".
 *
 * The issue specifies three ordering tiers applied in sequence: attention,
 * then essential density, then the declared backend order. Tier 2 shipped
 * missing: bands sorted by attention and then straight to declared order, so
 * Essential mode put a band with two useful fields below a band with none.
 *
 * Density is a *ratio*, not a count — a three-field band that is entirely
 * essential belongs above a forty-field band with six. And it applies only in
 * Essential mode: in Everything mode nothing is filtered, so the notion of
 * "mostly essential" carries no meaning and the declared order must stand.
 *
 * Section keys below are real `SectionName`s, because `isEssentialProperty`
 * looks up the manifest by that key — a fictional key would score 0 density
 * for every band and the test would pass without proving anything.
 */

const saveSettingsMock = vi.fn();

function prop(name: string, label: string) {
  return {
    name,
    type: 'number',
    attributes: [],
    display: { name: label, inputType: 'Number', minValue: 1, maxValue: 100000 },
  };
}

vi.mock('@/services/settingsApi', async () => {
  return {
    fetchSettingsMetadata: vi.fn().mockResolvedValue([
      {
        // Declared FIRST, but only 2 of 5 properties are essential (0.4).
        key: 'SystemLog',
        className: 'SystemLogSettings',
        displayName: 'System Log',
        description: '',
        group: 'Diluted',
        order: 1,
        properties: [
          prop('enabled', 'Enabled'),
          prop('retentionDays', 'Retention Days'),
          prop('filler1', 'Filler One'),
          prop('filler2', 'Filler Two'),
          prop('filler3', 'Filler Three'),
        ],
      },
      {
        // Declared SECOND, but every property is essential (1.0).
        key: 'NetworkDiscovery',
        className: 'NetworkDiscoverySettings',
        displayName: 'Network Discovery',
        description: '',
        group: 'Dense',
        order: 2,
        properties: [
          prop('enableDiscovery', 'Enable Discovery'),
          prop('backgroundScanEnabled', 'Background Scan Enabled'),
        ],
      },
    ]),
    fetchSettingsGroups: vi.fn().mockResolvedValue([
      { key: 'Diluted', displayName: 'Diluted', order: 1 },
      { key: 'Dense', displayName: 'Dense', order: 2 },
    ]),
    fetchSettingsUnified: vi.fn().mockResolvedValue({
      SystemLog: { enabled: 1, retentionDays: 30, filler1: 1, filler2: 1, filler3: 1 },
      NetworkDiscovery: { enableDiscovery: 1, backgroundScanEnabled: 1 },
    }),
    saveSettingsValues: (...args: unknown[]) => saveSettingsMock(...args),
  };
});

vi.mock('@/hooks/useSlicer', () => ({
  useSlicer: () => ({ isSlicerAvailable: true, workerCount: 1 }),
}));

vi.mock('@/common/hooks/usePageTour', () => ({
  usePageTour: () => ({ startTour: vi.fn(), hasSeenTour: true, resetTour: vi.fn() }),
}));

vi.mock('@/features/admin/tours/settings.tour', () => ({ settingsTour: [] }));

vi.mock('@/features/admin/components/ObicoServersSection', () => ({
  ObicoServersSection: () => React.createElement('div', null, 'ObicoServersMock'),
}));
vi.mock('@/features/admin/components/FailureDetectionStatusCard', () => ({
  FailureDetectionStatusCard: () => React.createElement('div', null, 'FailureDetectionMock'),
}));

import { SettingsPage } from '@/features/admin/pages/SettingsPage';

async function renderPage(mode: 'essential' | 'everything') {
  window.localStorage.setItem('pf.settings.mode', mode);
  render(
    <MemoryRouter>
      <SettingsPage />
    </MemoryRouter>,
  );
  await waitFor(() => expect(screen.getByTestId('settings-band-flow')).toBeInTheDocument());
}

/** Band headings, top to bottom, as rendered. */
function bandOrder(): string[] {
  const flow = screen.getByTestId('settings-band-flow');
  return Array.from(flow.querySelectorAll('h3')).map((h) => (h.textContent ?? '').trim());
}

describe('SettingsPage — essential density orders bands (#1012 tier 2)', () => {
  beforeEach(() => {
    saveSettingsMock.mockReset();
    window.localStorage.clear();
  });

  it('floats the denser band above one declared before it, in Essential mode', () => {
    return renderPage('essential').then(() => {
      const order = bandOrder();
      expect(order.indexOf('Dense')).toBeLessThan(order.indexOf('Diluted'));
    });
  });

  it('leaves the declared order alone in Everything mode', () => {
    return renderPage('everything').then(() => {
      const order = bandOrder();
      // Nothing is hidden here, so "mostly essential" means nothing and the
      // backend's declared order is the only sensible arrangement.
      expect(order.indexOf('Diluted')).toBeLessThan(order.indexOf('Dense'));
    });
  });
});

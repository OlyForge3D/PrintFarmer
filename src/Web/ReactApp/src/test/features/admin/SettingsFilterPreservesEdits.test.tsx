import '@testing-library/jest-dom';
import React from 'react';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { describe, it, expect, vi, beforeEach } from 'vitest';

/**
 * Hicks (round 3) #1 — filtering a group out must not throw away its edits.
 *
 * `GroupSaveBlock` owns its dirty state in a `useDirtyState` local to the block.
 * The comment at the top of that block reasons that the parent only unmounts
 * blocks while `loading` is true, so a remount always gets a correct baseline.
 * The search/mode filter broke that invariant: the band flow returned `null` for
 * any group whose sections were all filtered out, unmounting the block and
 * destroying `state.values` with it.
 *
 * The sequence that lost data: edit a field, type a filter that excludes its
 * group, clear the filter. The block remounts from `initialValues` — which is
 * still the server payload, because field edits are never lifted to the page —
 * and the edit is gone with no warning and no dirty indicator.
 *
 * The fix hides the band with `display:none` instead of unmounting it, so the
 * block stays mounted, the page save bar keeps counting the edit, and the value
 * is still there when the filter clears.
 *
 * This needs two groups: one to edit, and one for the filter to match so the
 * page does not fall through to its "nothing matches" empty state.
 */

const saveSettingsMock = vi.fn();

function numberProp(name: string, label: string) {
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
        key: 'SystemLogSettings',
        className: 'SystemLogSettings',
        displayName: 'System Log',
        description: 'Log retention config.',
        group: 'System',
        order: 1,
        properties: [numberProp('retentionDays', 'Retention Days')],
      },
      {
        key: 'PrinterPollingSettings',
        className: 'PrinterPollingSettings',
        displayName: 'Printer Polling',
        description: 'Polling cadence.',
        group: 'Printers',
        order: 1,
        properties: [numberProp('pollIntervalSeconds', 'Poll Interval Seconds')],
      },
    ]),
    fetchSettingsGroups: vi.fn().mockResolvedValue([
      { key: 'System', displayName: 'System', order: 1 },
      { key: 'Printers', displayName: 'Printers', order: 2 },
    ]),
    fetchSettingsUnified: vi.fn().mockResolvedValue({
      SystemLogSettings: { retentionDays: 30 },
      PrinterPollingSettings: { pollIntervalSeconds: 5 },
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

async function renderPage() {
  const result = render(
    <MemoryRouter>
      <SettingsPage />
    </MemoryRouter>,
  );
  await waitFor(() => {
    expect(screen.getByLabelText('Retention Days')).toBeInTheDocument();
    expect(screen.getByLabelText('Poll Interval Seconds')).toBeInTheDocument();
  });
  return result;
}

function filterFor(text: string) {
  fireEvent.change(screen.getByPlaceholderText('Filter fields on this page…'), {
    target: { value: text },
  });
}

describe('SettingsPage — filtering a group out keeps its unsaved edits (Hicks #1)', () => {
  beforeEach(() => {
    window.localStorage.setItem('pf.settings.mode', 'everything');
    saveSettingsMock.mockReset();
    saveSettingsMock.mockResolvedValue(undefined);
  });

  it('still has the edit after the group is filtered out and back in', async () => {
    await renderPage();

    fireEvent.change(screen.getByLabelText('Retention Days'), { target: { value: '99' } });
    expect(screen.getByLabelText('Retention Days')).toHaveValue(99);

    // Matches only the Printers group, so the System band is filtered out.
    filterFor('poll');
    await waitFor(() => {
      expect(screen.getByLabelText('Poll Interval Seconds')).toBeInTheDocument();
    });

    filterFor('');
    await waitFor(() => {
      expect(screen.getByLabelText('Retention Days')).toBeInTheDocument();
    });

    expect(screen.getByLabelText('Retention Days')).toHaveValue(99);
  });

  it('still saves the edit that was filtered out and back in', async () => {
    await renderPage();

    fireEvent.change(screen.getByLabelText('Retention Days'), { target: { value: '99' } });
    filterFor('poll');
    await waitFor(() => {
      expect(screen.getByLabelText('Poll Interval Seconds')).toBeInTheDocument();
    });
    filterFor('');
    await waitFor(() => {
      expect(screen.getByLabelText('Retention Days')).toBeInTheDocument();
    });

    fireEvent.click(await screen.findByRole('button', { name: /^save/i }));

    await waitFor(() => expect(saveSettingsMock).toHaveBeenCalled());
    expect(saveSettingsMock).toHaveBeenCalledWith('SystemLogSettings', { retentionDays: 99 });
  });

  it('keeps the band mounted but hidden while it is filtered out', async () => {
    await renderPage();

    filterFor('poll');
    await waitFor(() => {
      expect(screen.getByLabelText('Poll Interval Seconds')).toBeInTheDocument();
    });

    // The System band is still in the DOM (so its dirty state survives) but is
    // display:none, so it takes no space in the column flow. Assert on the band's
    // own <section> rather than "some descendant is hidden" — AdminSection puts
    // `className` on its root, and a refactor that moved it to an inner wrapper
    // would leave the card occupying a column slot while looking correct.
    const band = document.getElementById('group-System')?.closest('section');
    expect(band).not.toBeNull();
    expect(band).toHaveClass('hidden');

    // The band the filter matched is, of course, still visible.
    const visibleBand = document.getElementById('group-Printers')?.closest('section');
    expect(visibleBand).not.toHaveClass('hidden');
  });
});

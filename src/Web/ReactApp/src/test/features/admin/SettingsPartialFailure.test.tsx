import '@testing-library/jest-dom';
import React from 'react';
import { render, screen, fireEvent, waitFor, act } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { describe, it, expect, vi, beforeEach } from 'vitest';

/**
 * Hicks #2 — a partial failure has to report what *saved*, not just what broke.
 *
 * A group is one dirty-state unit but several sections, and each section is its
 * own `POST /api/settings/{keyName}`. So the interesting failure is not "this
 * group failed" — it is "section A landed, section B 400'd, and they were in the
 * same group". The old code returned only `failedLabels` on that path, so the
 * page reported "Failed to save B" and never mentioned that A was already
 * persisted. A user reading that re-enters A's changes and saves again.
 *
 * `SettingsSaveBar.test.tsx` cannot express this: every section in its fixture
 * sits in its own group, so a "partial" failure there is really one whole group
 * failing beside another whole group succeeding — a path that was always
 * correct, and which passes with or without the fix. Hence this fixture: two
 * sections, one group.
 */

const saveSettingsMock = vi.fn();
const toastSuccessMock = vi.fn();
const toastErrorMock = vi.fn();

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
      // Both of these live in the SAME group, which is the whole point.
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
        key: 'DatabaseSettings',
        className: 'DatabaseSettings',
        displayName: 'Database',
        description: 'Storage behaviour.',
        group: 'System',
        order: 2,
        properties: [numberProp('backupIntervalHours', 'Backup Interval Hours')],
      },
    ]),
    fetchSettingsGroups: vi.fn().mockResolvedValue([
      { key: 'System', displayName: 'System', order: 1 },
    ]),
    fetchSettingsUnified: vi.fn().mockResolvedValue({
      SystemLogSettings: { retentionDays: 30 },
      DatabaseSettings: { backupIntervalHours: 24 },
    }),
    saveSettingsValues: (...args: unknown[]) => saveSettingsMock(...args),
  };
});

vi.mock('@/common/components/admin', async () => {
  const actual = await vi.importActual<typeof import('@/common/components/admin')>(
    '@/common/components/admin',
  );
  return {
    ...actual,
    adminToast: {
      success: (msg: string) => toastSuccessMock(msg),
      error: (msg: string) => toastErrorMock(msg),
      info: vi.fn(),
      warning: vi.fn(),
    },
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
    expect(screen.getByLabelText('Backup Interval Hours')).toBeInTheDocument();
  });
  return result;
}

function edit(label: string, value: string) {
  fireEvent.change(screen.getByLabelText(label), { target: { value } });
}

describe('SettingsPage — one group, one section fails (Hicks #2)', () => {
  beforeEach(() => {
    window.localStorage.setItem('pf.settings.mode', 'everything');
    saveSettingsMock.mockReset();
    toastSuccessMock.mockReset();
    toastErrorMock.mockReset();
  });

  it('names the section that saved as well as the one that failed', async () => {
    saveSettingsMock.mockImplementation((key: string) =>
      key === 'DatabaseSettings'
        ? Promise.reject(new Error('boom'))
        : Promise.resolve(undefined),
    );

    await renderPage();
    edit('Retention Days', '45');
    edit('Backup Interval Hours', '12');

    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: /save changes/i }));
    });

    await waitFor(() => expect(toastErrorMock).toHaveBeenCalled());
    const report = String(toastErrorMock.mock.calls.at(-1)?.[0] ?? '');

    // Both halves of the truth, by name. "Failed to save Database" alone is the
    // bug: it implies System Log did not persist, and it did.
    expect(report).toContain('System Log');
    expect(report).toContain('Database');
    expect(report).toMatch(/^Saved System Log\. Failed to save Database$/);
  });

  it('still reports a clean total failure as a total failure', async () => {
    // Guard against over-correcting: when nothing saved, the message must not
    // claim something did.
    saveSettingsMock.mockRejectedValue(new Error('boom'));

    await renderPage();
    edit('Retention Days', '45');
    edit('Backup Interval Hours', '12');

    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: /save changes/i }));
    });

    await waitFor(() => expect(toastErrorMock).toHaveBeenCalled());
    const report = String(toastErrorMock.mock.calls.at(-1)?.[0] ?? '');
    expect(report).not.toMatch(/^Saved/);
    expect(report).toContain('Failed to save');
  });
});

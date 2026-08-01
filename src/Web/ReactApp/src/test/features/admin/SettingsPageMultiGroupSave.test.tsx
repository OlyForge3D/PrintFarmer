import '@testing-library/jest-dom';
import React from 'react';
import { render, screen, fireEvent, waitFor, act } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';

/**
 * Epic #939 — per-group save scoping (#935), re-presented behind one page-level
 * save bar (#1013).
 *
 * The pre-existing SettingsPage.test.tsx covers two sections in the *same*
 * group. The scarier regression is two *different* groups both dirty at once.
 *
 * The bar those groups feed is now shared, but the isolation underneath it is
 * unchanged and is what this file guards:
 *   - Each GroupSaveBlock still owns its own dirty state.
 *   - A save still POSTs each group's sections through that group's own path,
 *     and never touches a section the user did not edit.
 *   - When one group's write fails, the other group's write still lands, and
 *     the failed group keeps the user's edit intact for a retry.
 *
 * The "which bar am I looking at" confusion the group-specific `Save {Group}`
 * label existed to prevent is gone with the extra bars, so that assertion is
 * replaced by its successor: one bar, naming the sections it will write.
 */

const saveSettingsMock = vi.fn();
const toastSuccessMock = vi.fn();
const toastErrorMock = vi.fn();

vi.mock('@/services/settingsApi', async () => {
  return {
    fetchSettingsMetadata: vi.fn().mockResolvedValue([
      {
        key: 'SystemLog',
        className: 'SystemLogSettings',
        displayName: 'System Log',
        description: 'Log retention.',
        group: 'System',
        order: 1,
        properties: [
          {
            name: 'retentionDays',
            type: 'number',
            attributes: [],
            display: {
              name: 'Retention Days',
              inputType: 'Number',
              minValue: 1,
              maxValue: 365,
            },
          },
        ],
      },
      {
        key: 'SlicerDefaults',
        className: 'SlicerDefaultSettings',
        displayName: 'Slicer Defaults',
        description: 'Default slicing parameters.',
        group: 'Slicing',
        order: 1,
        properties: [
          {
            name: 'layerHeight',
            type: 'number',
            attributes: [],
            display: {
              name: 'Layer Height',
              inputType: 'Number',
              minValue: 0.05,
              maxValue: 1,
            },
          },
        ],
      },
    ]),
    fetchSettingsGroups: vi.fn().mockResolvedValue([
      { key: 'System', displayName: 'System', order: 1 },
      { key: 'Slicing', displayName: 'Slicing', order: 2 },
    ]),
    fetchSettingsUnified: vi.fn().mockResolvedValue({
      SystemLog: { retentionDays: 30 },
      SlicerDefaults: { layerHeight: 0.2 },
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
    expect(screen.getByLabelText('Layer Height')).toBeInTheDocument();
  });
  return result;
}

describe('SettingsPage — save-scoping across independent groups (#939)', () => {
  beforeEach(() => {
    // Force Everything so both single-property fixtures render. The essential
    // manifest doesn't cover these synthetic sections.
    window.localStorage.setItem('pf.settings.mode', 'everything');
    saveSettingsMock.mockReset().mockResolvedValue(undefined);
    toastSuccessMock.mockReset();
    toastErrorMock.mockReset();
  });

  afterEach(() => {
    window.localStorage.removeItem('pf.settings.mode');
  });

  it('renders one bar for two dirty groups, naming both sections', async () => {
    await renderPage();

    fireEvent.change(screen.getByLabelText('Retention Days'), { target: { value: '60' } });
    fireEvent.change(screen.getByLabelText('Layer Height'), { target: { value: '0.16' } });

    await waitFor(() => expect(screen.queryAllByTestId('admin-save-bar')).toHaveLength(1));
    expect(screen.getAllByRole('button', { name: /save changes/i })).toHaveLength(1);
    expect(
      screen.getByText('2 changes in System Log and Slicer Defaults'),
    ).toBeInTheDocument();
    // No global "Save All" button — the batch endpoint stayed retired.
    expect(screen.queryByRole('button', { name: /save all/i })).not.toBeInTheDocument();
  });

  it('writes each dirty group through its own section endpoint', async () => {
    await renderPage();

    fireEvent.change(screen.getByLabelText('Retention Days'), { target: { value: '90' } });
    fireEvent.change(screen.getByLabelText('Layer Height'), { target: { value: '0.12' } });

    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: /save changes/i }));
    });

    // Two writes, one per group, each carrying only its own section's values.
    await waitFor(() => expect(saveSettingsMock).toHaveBeenCalledTimes(2));
    expect(saveSettingsMock).toHaveBeenCalledWith('SystemLog', { retentionDays: 90 });
    expect(saveSettingsMock).toHaveBeenCalledWith('SlicerDefaults', { layerHeight: 0.12 });

    await waitFor(() => {
      expect(toastSuccessMock).toHaveBeenCalledWith('Saved System Log, Slicer Defaults');
    });
    await waitFor(() => expect(screen.queryAllByTestId('admin-save-bar')).toHaveLength(0));
  });

  it('a failing group keeps its edit while the sibling group still commits', async () => {
    // The isolation guarantee, exercised through the only path that can still
    // separate the two groups now that one button saves both.
    saveSettingsMock.mockImplementation((key: string) => (
      key === 'SlicerDefaults'
        ? Promise.reject(new Error('boom'))
        : Promise.resolve(undefined)
    ));

    await renderPage();

    fireEvent.change(screen.getByLabelText('Retention Days'), { target: { value: '77' } });
    fireEvent.change(screen.getByLabelText('Layer Height'), { target: { value: '0.28' } });

    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: /save changes/i }));
    });

    await waitFor(() => expect(saveSettingsMock).toHaveBeenCalledTimes(2));
    await waitFor(() => {
      expect(toastErrorMock).toHaveBeenCalledWith(
        'Saved System Log. Failed to save Slicer Defaults',
      );
    });

    // The group that succeeded is clean and keeps its saved value.
    expect((screen.getByLabelText('Retention Days') as HTMLInputElement).value).toBe('77');
    // The group that failed keeps the user's edit, and is all the bar now names.
    expect((screen.getByLabelText('Layer Height') as HTMLInputElement).value).toBe('0.28');
    await waitFor(() => {
      expect(screen.getByText('1 change in Slicer Defaults')).toBeInTheDocument();
    });
  });

  it('discard reverts both groups at once', async () => {
    await renderPage();

    fireEvent.change(screen.getByLabelText('Retention Days'), { target: { value: '77' } });
    fireEvent.change(screen.getByLabelText('Layer Height'), { target: { value: '0.28' } });
    expect(await screen.findByTestId('admin-save-bar')).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: /discard/i }));

    await waitFor(() => expect(screen.queryAllByTestId('admin-save-bar')).toHaveLength(0));
    expect((screen.getByLabelText('Retention Days') as HTMLInputElement).value).toBe('30');
    expect((screen.getByLabelText('Layer Height') as HTMLInputElement).value).toBe('0.2');
    expect(saveSettingsMock).not.toHaveBeenCalled();
  });
});

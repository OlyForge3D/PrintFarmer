import '@testing-library/jest-dom';
import React from 'react';
import { render, screen, fireEvent, waitFor, act } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';

/**
 * Epic #939 — per-group save scoping (#935).
 *
 * The pre-existing SettingsPage.test.tsx covers two sections in the *same*
 * group. The scarier regression is two *different* groups both dirty at once:
 *   - Each GroupSaveBlock must own its own dirty state.
 *   - Saving group A must POST only A's sections, leaving B still dirty.
 *   - The `Save {Group}` label must be group-specific so a user with two
 *     open save bars can't get confused about which they're saving.
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
    saveAllSettings: vi.fn(),
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

  it('renders a Save bar per group, each labelled with its own group name', async () => {
    await renderPage();

    fireEvent.change(screen.getByLabelText('Retention Days'), { target: { value: '60' } });
    fireEvent.change(screen.getByLabelText('Layer Height'), { target: { value: '0.16' } });

    // Two independent save buttons, each named for its group.
    expect(await screen.findByRole('button', { name: /save system/i })).toBeInTheDocument();
    expect(await screen.findByRole('button', { name: /save slicing/i })).toBeInTheDocument();
    // No global "Save All" button — the batch flow was retired.
    expect(screen.queryByRole('button', { name: /save all/i })).not.toBeInTheDocument();
  });

  it('saving one group hits only its section endpoint and leaves the other block dirty', async () => {
    await renderPage();

    fireEvent.change(screen.getByLabelText('Retention Days'), { target: { value: '90' } });
    fireEvent.change(screen.getByLabelText('Layer Height'), { target: { value: '0.12' } });

    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: /save system/i }));
    });

    // Exactly one save happened, targeting only the System section.
    await waitFor(() => expect(saveSettingsMock).toHaveBeenCalledTimes(1));
    expect(saveSettingsMock).toHaveBeenCalledWith('SystemLog', { retentionDays: 90 });
    expect(saveSettingsMock.mock.calls.map((c) => c[0])).not.toContain('SlicerDefaults');

    // System toast fires — Slicing toast does not.
    await waitFor(() => expect(toastSuccessMock).toHaveBeenCalledWith('System settings saved'));
    expect(toastSuccessMock).not.toHaveBeenCalledWith('Slicing settings saved');

    // The Slicing block is still dirty — its edit was not clobbered by the
    // sibling save, and its save button is still available.
    expect(screen.getByRole('button', { name: /save slicing/i })).toBeInTheDocument();
    expect((screen.getByLabelText('Layer Height') as HTMLInputElement).value).toBe('0.12');
  });

  it('discarding one group leaves the other group dirty and untouched', async () => {
    await renderPage();

    fireEvent.change(screen.getByLabelText('Retention Days'), { target: { value: '77' } });
    fireEvent.change(screen.getByLabelText('Layer Height'), { target: { value: '0.28' } });

    const systemBar = screen.getByRole('button', { name: /save system/i }).closest('[data-testid="admin-save-bar"]');
    expect(systemBar).toBeTruthy();
    const discardWithinSystem = systemBar!.querySelector('button[type="button"]');
    // Explicitly click the Discard within the system save bar, not the slicing one.
    const systemDiscard = Array.from(systemBar!.querySelectorAll('button')).find(
      (b) => b.textContent && /discard/i.test(b.textContent),
    );
    expect(systemDiscard).toBeTruthy();
    if (!systemDiscard) throw new Error('unreachable — assertion above'); // narrow for TS
    fireEvent.click(systemDiscard);
    // suppress unused warning if TS-strict picks it up
    void discardWithinSystem;

    // System field reverted, System bar hidden.
    expect((screen.getByLabelText('Retention Days') as HTMLInputElement).value).toBe('30');
    expect(screen.queryByRole('button', { name: /save system/i })).not.toBeInTheDocument();

    // Slicing block untouched — still dirty with its edited value.
    expect((screen.getByLabelText('Layer Height') as HTMLInputElement).value).toBe('0.28');
    expect(screen.getByRole('button', { name: /save slicing/i })).toBeInTheDocument();
    expect(saveSettingsMock).not.toHaveBeenCalled();
  });

  it('never invokes the retired batch endpoint even with multiple groups dirty', async () => {
    // saveAllSettings still exists in the API wrapper for backward compatibility
    // but nothing in production should reach it. If a future change reintroduces
    // a "save all" affordance, this catches it before it ships.
    const settingsApi = await import('@/services/settingsApi');
    const saveAllSpy = vi.spyOn(settingsApi, 'saveAllSettings');

    await renderPage();
    fireEvent.change(screen.getByLabelText('Retention Days'), { target: { value: '55' } });
    fireEvent.change(screen.getByLabelText('Layer Height'), { target: { value: '0.15' } });

    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: /save system/i }));
    });
    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: /save slicing/i }));
    });

    await waitFor(() => expect(saveSettingsMock).toHaveBeenCalledTimes(2));
    expect(saveAllSpy).not.toHaveBeenCalled();
  });
});

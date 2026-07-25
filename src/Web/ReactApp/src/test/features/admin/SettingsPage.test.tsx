import '@testing-library/jest-dom';
import React from 'react';
import { render, screen, fireEvent, waitFor, act } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';

/**
 * These tests cover the per-group save model that replaced the single
 * "Save All Settings" button in issue #935. The important invariants:
 *  - Editing a field flips only that group's block into a dirty state.
 *  - Saving hits the per-section endpoint for each *changed* section (not the
 *    batch endpoint, and not sections that weren't touched).
 *  - Discard reverts to the original values.
 *  - The `beforeunload` handler is installed while dirty and torn down on save.
 *  - Save failures raise `adminToast.error`, keep the block dirty, and leave the
 *    save bar visible so the user can retry.
 */

const saveSettingsMock = vi.fn();
const toastSuccessMock = vi.fn();
const toastErrorMock = vi.fn();

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
        key: 'NotificationSettings',
        className: 'NotificationSettings',
        displayName: 'Notifications',
        description: 'Where to send alerts.',
        group: 'System',
        order: 2,
        properties: [
          {
            name: 'emailEnabled',
            type: 'Boolean',
            attributes: [],
            display: {
              name: 'Email Enabled',
              inputType: 'Boolean',
            },
          },
        ],
      },
    ]),
    fetchSettingsGroups: vi.fn().mockResolvedValue([
      { key: 'System', displayName: 'System', order: 1 },
    ]),
    fetchSettingsUnified: vi.fn().mockResolvedValue({
      SystemLogSettings: { retentionDays: 30 },
      NotificationSettings: { emailEnabled: false },
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

vi.mock('@/features/admin/tours/settings.tour', () => ({
  settingsTour: [],
}));

// Feature components that would otherwise pull in React Query / SignalR wiring.
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
  });
  return result;
}

describe('SettingsPage — per-group save', () => {
  beforeEach(() => {
    // Force Everything mode so the pre-existing tests see the same fields they
    // did before the Essential/Everything toggle (#937) — the fixture uses
    // synthetic section keys that don't appear in the essential manifest.
    window.localStorage.setItem('pf.settings.mode', 'everything');
    saveSettingsMock.mockReset();
    toastSuccessMock.mockReset();
    toastErrorMock.mockReset();
    saveSettingsMock.mockResolvedValue(undefined);
  });

  afterEach(() => {
    window.localStorage.removeItem('pf.settings.mode');
    // Detach any lingering beforeunload handler leftovers.
    // useDirtyState cleans up on unmount but the guard against leaks is cheap.
  });

  it('is clean on first render — no save bar visible', async () => {
    await renderPage();
    expect(screen.queryByTestId('admin-save-bar')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /save all settings/i })).not.toBeInTheDocument();
  });

  it('shows the save bar once a single field is edited', async () => {
    await renderPage();
    const retentionInput = screen.getByLabelText('Retention Days');
    fireEvent.change(retentionInput, { target: { value: '45' } });
    expect(await screen.findByTestId('admin-save-bar')).toBeInTheDocument();
    // AdminSaveBar renders the changed section names when changedLabels is provided.
    expect(screen.getByText(/system log changed/i)).toBeInTheDocument();
  });

  it('saves only the changed section via the per-section endpoint', async () => {
    await renderPage();
    fireEvent.change(screen.getByLabelText('Retention Days'), { target: { value: '45' } });

    const saveBtn = await screen.findByRole('button', { name: /save system/i });
    await act(async () => {
      fireEvent.click(saveBtn);
    });

    await waitFor(() => expect(saveSettingsMock).toHaveBeenCalledTimes(1));
    expect(saveSettingsMock).toHaveBeenCalledWith('SystemLogSettings', { retentionDays: 45 });
    // Untouched section (NotificationSettings) is not persisted.
    expect(saveSettingsMock.mock.calls.map((c) => c[0])).not.toContain('NotificationSettings');
    // Success toast fires.
    await waitFor(() => expect(toastSuccessMock).toHaveBeenCalledWith('System settings saved'));
    // Bar collapses.
    await waitFor(() => expect(screen.queryByTestId('admin-save-bar')).not.toBeInTheDocument());
  });

  it('discard reverts working values and hides the save bar', async () => {
    await renderPage();
    fireEvent.change(screen.getByLabelText('Retention Days'), { target: { value: '99' } });
    expect(await screen.findByTestId('admin-save-bar')).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: /discard/i }));

    await waitFor(() => expect(screen.queryByTestId('admin-save-bar')).not.toBeInTheDocument());
    expect((screen.getByLabelText('Retention Days') as HTMLInputElement).value).toBe('30');
    expect(saveSettingsMock).not.toHaveBeenCalled();
  });

  it('installs a beforeunload guard while dirty and removes it after save', async () => {
    const addSpy = vi.spyOn(window, 'addEventListener');
    const removeSpy = vi.spyOn(window, 'removeEventListener');
    try {
      await renderPage();
      fireEvent.change(screen.getByLabelText('Retention Days'), { target: { value: '42' } });

      // Wait for the effect to attach the listener.
      await waitFor(() => {
        const beforeCalls = addSpy.mock.calls.filter((c) => c[0] === 'beforeunload');
        expect(beforeCalls.length).toBeGreaterThan(0);
      });

      await act(async () => {
        fireEvent.click(screen.getByRole('button', { name: /save system/i }));
      });

      // Wait for the save to complete and the dirty flag to flip false, at which
      // point the effect cleanup detaches the handler.
      await waitFor(() => expect(saveSettingsMock).toHaveBeenCalledTimes(1));
      await waitFor(() => expect(screen.queryByTestId('admin-save-bar')).not.toBeInTheDocument());
      await waitFor(() => {
        const removeCalls = removeSpy.mock.calls.filter((c) => c[0] === 'beforeunload');
        expect(removeCalls.length).toBeGreaterThan(0);
      });
    } finally {
      addSpy.mockRestore();
      removeSpy.mockRestore();
    }
  });

  it('save failure keeps the bar visible, keeps state dirty, and shows an error toast', async () => {
    saveSettingsMock.mockRejectedValueOnce({
      response: { data: { errors: { retentionDays: 'Must be different' } } },
    });

    await renderPage();
    // Use a value that passes client-side validation (1..365) so the request
    // actually reaches the mocked API and the server-side rejection path is tested.
    fireEvent.change(screen.getByLabelText('Retention Days'), { target: { value: '100' } });
    const saveBtn = await screen.findByRole('button', { name: /save system/i });

    await act(async () => {
      fireEvent.click(saveBtn);
    });

    await waitFor(() => expect(saveSettingsMock).toHaveBeenCalledTimes(1));
    await waitFor(() => expect(toastErrorMock).toHaveBeenCalled());
    // Bar stays visible so user can fix + retry.
    expect(screen.getByTestId('admin-save-bar')).toBeInTheDocument();
    // Value stays as the user typed it.
    expect((screen.getByLabelText('Retention Days') as HTMLInputElement).value).toBe('100');
    // Inline field error surfaced.
    expect(screen.getByText(/must be different/i)).toBeInTheDocument();
  });
});

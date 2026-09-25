import '@testing-library/jest-dom';
import React from 'react';
import { render, screen, fireEvent, waitFor, act } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';

/**
 * These tests cover the per-group save model that replaced the single
 * "Save All Settings" button in issue #935, as re-presented behind one
 * page-level save bar in #1013. The important invariants:
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
import { fetchSettingsUnified } from '@/services/settingsApi';

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
    vi.mocked(fetchSettingsUnified).mockReset().mockResolvedValue({
      SystemLogSettings: { retentionDays: 30 },
      NotificationSettings: { emailEnabled: false },
    });
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

  it('keeps in-flight edits and advances only the successful save revision', async () => {
    vi.mocked(fetchSettingsUnified).mockResolvedValueOnce({
      SystemLogSettings: { retentionDays: 30, rowVersion: 'absent' },
      NotificationSettings: { emailEnabled: false, rowVersion: 'other-v1' },
    });
    let finish!: (value: unknown) => void;
    saveSettingsMock.mockImplementationOnce(() => new Promise(resolve => { finish = resolve; }));
    await renderPage();
    fireEvent.change(screen.getByLabelText('Retention Days'), { target: { value: '45' } });
    fireEvent.click(screen.getByRole('button', { name: /save changes/i }));
    await waitFor(() => expect(saveSettingsMock).toHaveBeenCalledWith('SystemLogSettings', { retentionDays: 45, rowVersion: 'absent' }));
    fireEvent.change(screen.getByLabelText('Retention Days'), { target: { value: '60' } });
    await act(async () => finish({ retentionDays: 45, rowVersion: 'v2' }));
    expect(screen.getByLabelText('Retention Days')).toHaveValue(60);
    expect(screen.getByTestId('admin-save-bar')).toBeInTheDocument();
    saveSettingsMock.mockResolvedValueOnce({ retentionDays: 60, rowVersion: 'v3' });
    fireEvent.click(screen.getByRole('button', { name: /save changes/i }));
    await waitFor(() => expect(saveSettingsMock).toHaveBeenLastCalledWith('SystemLogSettings', { retentionDays: 60, rowVersion: 'v2' }));
    await waitFor(() => expect(screen.queryByTestId('admin-save-bar')).not.toBeInTheDocument());
  });

  it.each([409, 412])('preserves a conflicted draft on %s until explicit reload', async (statusCode) => {
    vi.mocked(fetchSettingsUnified).mockResolvedValueOnce({
      SystemLogSettings: { retentionDays: 30, rowVersion: 'v1' },
    }).mockResolvedValueOnce({
      SystemLogSettings: { retentionDays: 80, rowVersion: 'remote-v2' },
    });
    saveSettingsMock.mockRejectedValueOnce({ statusCode, message: 'Concurrent change' });
    await renderPage();
    fireEvent.change(screen.getByLabelText('Retention Days'), { target: { value: '45' } });
    fireEvent.click(screen.getByRole('button', { name: /save changes/i }));
    await screen.findByRole('button', { name: 'Reload settings (discard all page edits)' });
    expect(screen.getByLabelText('Retention Days')).toHaveValue(45);
    expect(toastSuccessMock).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: /save changes/i }));
    await waitFor(() => expect(screen.getByRole('button', { name: /save changes/i })).toBeEnabled());
    expect(saveSettingsMock).toHaveBeenCalledTimes(1);
    fireEvent.click(await screen.findByRole('button', { name: 'Reload settings (discard all page edits)' }));
    await waitFor(() => expect(screen.getByLabelText('Retention Days')).toHaveValue(80));
    fireEvent.change(screen.getByLabelText('Retention Days'), { target: { value: '90' } });
    fireEvent.click(screen.getByRole('button', { name: /save changes/i }));
    await waitFor(() => expect(saveSettingsMock).toHaveBeenLastCalledWith('SystemLogSettings', { retentionDays: 90, rowVersion: 'remote-v2' }));
  });

  it('adopts normalized server values without treating the renewed token as an edit', async () => {
    vi.mocked(fetchSettingsUnified).mockResolvedValueOnce({
      SystemLogSettings: { retentionDays: 30, rowVersion: 'v1' },
    });
    saveSettingsMock.mockResolvedValueOnce({ retentionDays: 40, rowVersion: 'v2' });
    await renderPage();
    fireEvent.change(screen.getByLabelText('Retention Days'), { target: { value: '45' } });
    fireEvent.click(screen.getByRole('button', { name: /save changes/i }));
    await waitFor(() => expect(screen.getByLabelText('Retention Days')).toHaveValue(40));
    expect(screen.queryByTestId('admin-save-bar')).not.toBeInTheDocument();
    fireEvent.change(screen.getByLabelText('Retention Days'), { target: { value: '50' } });
    fireEvent.click(screen.getByRole('button', { name: /save changes/i }));
    await waitFor(() => expect(saveSettingsMock).toHaveBeenLastCalledWith(
      'SystemLogSettings', { retentionDays: 50, rowVersion: 'v2' },
    ));
  });

  it('shows the save bar once a single field is edited', async () => {
    await renderPage();
    const retentionInput = screen.getByLabelText('Retention Days');
    fireEvent.change(retentionInput, { target: { value: '45' } });
    expect(await screen.findByTestId('admin-save-bar')).toBeInTheDocument();
    // The bar names the section the change lives in, not just a bare count.
    expect(screen.getByText('1 change in System Log')).toBeInTheDocument();
  });

  it('keeps a conflicted draft and recovery control when reload fails', async () => {
    vi.mocked(fetchSettingsUnified).mockResolvedValueOnce({
      SystemLogSettings: { retentionDays: 30, rowVersion: 'v1' },
    }).mockRejectedValueOnce(new Error('offline'));
    saveSettingsMock.mockRejectedValueOnce({ statusCode: 409 });
    await renderPage();
    fireEvent.change(screen.getByLabelText('Retention Days'), { target: { value: '45' } });
    fireEvent.click(screen.getByRole('button', { name: /save changes/i }));
    fireEvent.click(await screen.findByRole('button', { name: 'Reload settings (discard all page edits)' }));
    await waitFor(() => expect(toastErrorMock).toHaveBeenCalledWith('Could not reload settings. Your edits are preserved.'));
    expect(screen.getByLabelText('Retention Days')).toHaveValue(45);
    expect(screen.getByRole('button', { name: 'Reload settings (discard all page edits)' })).toBeEnabled();
    expect(saveSettingsMock).toHaveBeenCalledTimes(1);
  });

  it('saves only the changed section via the per-section endpoint', async () => {
    await renderPage();
    fireEvent.change(screen.getByLabelText('Retention Days'), { target: { value: '45' } });

    const saveBtn = await screen.findByRole('button', { name: /save changes/i });
    await act(async () => {
      fireEvent.click(saveBtn);
    });

    await waitFor(() => expect(saveSettingsMock).toHaveBeenCalledTimes(1));
    expect(saveSettingsMock).toHaveBeenCalledWith('SystemLogSettings', { retentionDays: 45 });
    // Untouched section (NotificationSettings) is not persisted.
    expect(saveSettingsMock.mock.calls.map((c) => c[0])).not.toContain('NotificationSettings');
    // Success toast names what was written.
    await waitFor(() => expect(toastSuccessMock).toHaveBeenCalledWith('Saved System Log'));
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
        fireEvent.click(screen.getByRole('button', { name: /save changes/i }));
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
    const saveBtn = await screen.findByRole('button', { name: /save changes/i });

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

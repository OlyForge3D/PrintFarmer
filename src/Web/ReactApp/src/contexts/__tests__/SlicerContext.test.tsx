import React, { StrictMode } from 'react';
import { act, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { SlicerProvider } from '@/contexts/SlicerContext';
import { useSlicer } from '@/hooks/useSlicer';
import { AUTH_SESSION_ESTABLISHED_EVENT } from '@/services/authEvents';
import type { SlicerDto } from '@/services/slicerRegistry';

const apiTestState = vi.hoisted(() => ({
  getSettings: vi.fn(),
  getSlicers: vi.fn(),
}));

vi.mock('@/services/api', () => ({
  apiClient: {
    getSettings: apiTestState.getSettings,
  },
}));

vi.mock('@/services/slicerRegistry', () => ({
  slicerRegistry: {
    getSlicers: apiTestState.getSlicers,
  },
}));

const WORKERS: SlicerDto[] = [{ id: 'worker-1', name: 'Orca worker' }];

interface Deferred<T> {
  promise: Promise<T>;
  resolve: (value: T) => void;
}

function deferred<T>(): Deferred<T> {
  let resolve: (value: T) => void = () => {};
  const promise = new Promise<T>(promiseResolve => {
    resolve = promiseResolve;
  });
  return { promise, resolve };
}

function SlicerStateProbe() {
  const state = useSlicer();
  return (
    <output
      data-testid="slicer-state"
      data-setting-enabled={state.settingEnabled}
      data-worker-count={state.workerCount}
      data-slicer-available={state.isSlicerAvailable}
      data-loading={state.isLoading}
    />
  );
}

function renderProvider({ strictMode = false }: { strictMode?: boolean } = {}) {
  const tree = (
    <SlicerProvider>
      <SlicerStateProbe />
    </SlicerProvider>
  );
  return render(strictMode ? <StrictMode>{tree}</StrictMode> : tree);
}

function readState() {
  const state = screen.getByTestId('slicer-state').dataset;
  return {
    settingEnabled: state.settingEnabled === 'true',
    workerCount: Number(state.workerCount),
    isSlicerAvailable: state.slicerAvailable === 'true',
    isLoading: state.loading === 'true',
  };
}

describe('SlicerProvider authenticated settings loading', () => {
  beforeEach(() => {
    localStorage.clear();
    apiTestState.getSettings.mockReset().mockResolvedValue({ enabled: true });
    apiTestState.getSlicers.mockReset().mockResolvedValue(WORKERS);
  });

  it('does not request authenticated slicer settings during a logged-out initial mount', async () => {
    renderProvider();

    await waitFor(() => expect(readState().isLoading).toBe(false));

    expect(apiTestState.getSettings).not.toHaveBeenCalled();
    expect(apiTestState.getSlicers).toHaveBeenCalledOnce();
    expect(readState()).toEqual({
      settingEnabled: true,
      workerCount: 1,
      isSlicerAvailable: true,
      isLoading: false,
    });
  });

  it('loads authoritative settings after SPA authentication, including enabled false', async () => {
    renderProvider();
    await waitFor(() => expect(readState().isLoading).toBe(false));
    apiTestState.getSettings.mockResolvedValueOnce({ enabled: false });

    await act(async () => {
      localStorage.setItem('auth-token', 'authenticated-token');
      window.dispatchEvent(new Event(AUTH_SESSION_ESTABLISHED_EVENT));
    });

    await waitFor(() => expect(readState().settingEnabled).toBe(false));
    expect(apiTestState.getSettings).toHaveBeenCalledWith('Slicer');
    expect(readState().isSlicerAvailable).toBe(false);
  });

  it('loads settings immediately for an already-authenticated initial mount', async () => {
    localStorage.setItem('auth-token', 'existing-token');
    apiTestState.getSettings.mockResolvedValueOnce({ enabled: false });

    renderProvider();

    await waitFor(() => expect(readState().isLoading).toBe(false));
    expect(apiTestState.getSettings).toHaveBeenCalledOnce();
    expect(readState().settingEnabled).toBe(false);
  });

  it('coalesces repeated auth events with an in-flight load and applies its authoritative result', async () => {
    const settingsRequest = deferred<{ enabled: boolean }>();
    localStorage.setItem('auth-token', 'existing-token');
    apiTestState.getSettings.mockReturnValueOnce(settingsRequest.promise);

    renderProvider();
    window.dispatchEvent(new Event(AUTH_SESSION_ESTABLISHED_EVENT));
    window.dispatchEvent(new Event(AUTH_SESSION_ESTABLISHED_EVENT));

    expect(apiTestState.getSettings).toHaveBeenCalledOnce();

    await act(async () => {
      settingsRequest.resolve({ enabled: false });
    });

    await waitFor(() => expect(readState().settingEnabled).toBe(false));
    expect(readState().isSlicerAvailable).toBe(false);
  });

  it('removes the auth listener and ignores pending responses after unmount', async () => {
    const settingsRequest = deferred<{ enabled: boolean }>();
    localStorage.setItem('auth-token', 'existing-token');
    apiTestState.getSettings.mockReturnValueOnce(settingsRequest.promise);
    const { unmount } = renderProvider();

    unmount();
    window.dispatchEvent(new Event(AUTH_SESSION_ESTABLISHED_EVENT));

    expect(apiTestState.getSettings).toHaveBeenCalledOnce();
    await act(async () => {
      settingsRequest.resolve({ enabled: false });
    });
    expect(apiTestState.getSettings).toHaveBeenCalledOnce();
  });

  it('uses explicit defaults and logs when authenticated data is unavailable', async () => {
    const settingsError = new Error('settings unavailable');
    const workersError = new Error('workers unavailable');
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => {});
    localStorage.setItem('auth-token', 'existing-token');
    apiTestState.getSettings.mockRejectedValueOnce(settingsError);
    apiTestState.getSlicers.mockRejectedValueOnce(workersError);

    renderProvider();

    await waitFor(() => expect(readState().isLoading).toBe(false));
    expect(readState()).toEqual({
      settingEnabled: true,
      workerCount: 0,
      isSlicerAvailable: false,
      isLoading: false,
    });
    expect(warn).toHaveBeenCalledWith(
      '[SlicerContext] Failed to fetch slicer settings, using defaults:',
      settingsError,
    );
    expect(warn).toHaveBeenCalledWith(
      '[SlicerContext] Failed to fetch slicer workers, using defaults:',
      workersError,
    );
    warn.mockRestore();
  });

  it('reloads settings for successive login sessions', async () => {
    renderProvider();
    await waitFor(() => expect(readState().isLoading).toBe(false));
    apiTestState.getSettings
      .mockResolvedValueOnce({ enabled: false })
      .mockResolvedValueOnce({ enabled: true });

    localStorage.setItem('auth-token', 'first-session');
    window.dispatchEvent(new Event(AUTH_SESSION_ESTABLISHED_EVENT));
    await waitFor(() => expect(readState().settingEnabled).toBe(false));

    localStorage.removeItem('auth-token');
    localStorage.setItem('auth-token', 'second-session');
    window.dispatchEvent(new Event(AUTH_SESSION_ESTABLISHED_EVENT));
    await waitFor(() => expect(readState().settingEnabled).toBe(true));

    expect(apiTestState.getSettings).toHaveBeenCalledTimes(2);
  });

  it('shares the initial request across a StrictMode remount', async () => {
    const settingsRequest = deferred<{ enabled: boolean }>();
    localStorage.setItem('auth-token', 'existing-token');
    apiTestState.getSettings.mockReturnValueOnce(settingsRequest.promise);

    renderProvider({ strictMode: true });

    expect(apiTestState.getSettings).toHaveBeenCalledOnce();
    expect(apiTestState.getSlicers).toHaveBeenCalledOnce();

    await act(async () => {
      settingsRequest.resolve({ enabled: false });
    });
    await waitFor(() => expect(readState().settingEnabled).toBe(false));
  });
});

import { act, render, screen, waitFor } from '@testing-library/react';
import { onlineManager, QueryClient, QueryClientProvider } from '@tanstack/react-query';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { inventory } from '@/test/features/system/serviceInventoryFixture';
import { UpdateChannelSaveRejectedError } from '@/features/admin/utils/updateChannelSaveErrors';
import type { ServiceInventory, UpdateChannelSettings } from '@/types/api';


interface InstallerPagePropsSnapshot {
  inventory: ServiceInventory | null | undefined;
  observation: 'connected' | 'unknown';
  updateChannelSettings?: UpdateChannelSettings;
  updateChannelIsLoading?: boolean;
  updateChannelIsError?: boolean;
  onRetryUpdateChannel?: () => void;
  onSaveUpdateChannel?: (settings: UpdateChannelSettings) => Promise<UpdateChannelSettings>;
}

const {
  getSystemInfo,
  getUpdateChannelSettings,
  updateUpdateChannelSettings,
  setGetSystemInfoImpl,
  setGetUpdateChannelSettingsImpl,
  setUpdateUpdateChannelSettingsImpl,
  installerPropsRef,
} = vi.hoisted(() => {
  let getSystemInfoImpl: () => Promise<unknown> = () => Promise.resolve(undefined);
  let getUpdateChannelSettingsImpl: () => Promise<unknown> = () => Promise.resolve(undefined);
  let updateUpdateChannelSettingsImpl: (settings: unknown) => Promise<unknown> = () => Promise.resolve(undefined);

  const installerPropsRef: { current: InstallerPagePropsSnapshot | null } = { current: null };

  return {
    getSystemInfo: vi.fn(() => getSystemInfoImpl()),
    getUpdateChannelSettings: vi.fn(() => getUpdateChannelSettingsImpl()),
    updateUpdateChannelSettings: vi.fn((settings: unknown) => updateUpdateChannelSettingsImpl(settings)),
    setGetSystemInfoImpl: (impl: () => Promise<unknown>) => { getSystemInfoImpl = impl; },
    setGetUpdateChannelSettingsImpl: (impl: () => Promise<unknown>) => { getUpdateChannelSettingsImpl = impl; },
    setUpdateUpdateChannelSettingsImpl: (impl: (settings: unknown) => Promise<unknown>) => { updateUpdateChannelSettingsImpl = impl; },
    installerPropsRef,
  };
});

vi.mock('@/features/auth/hooks/useAuth', () => ({
  useAuth: () => ({ hasPermission: () => true }),
}));
vi.mock('@/services/api', () => ({
  apiClient: { getSystemInfo, getUpdateChannelSettings, updateUpdateChannelSettings },
}));
vi.mock('@/features/admin/components/InstallerUpdatesExperience', () => ({
  InstallerUpdatesExperience: (props: InstallerPagePropsSnapshot) => {
    installerPropsRef.current = props;
    return (
      <div
        data-testid="installer-updates"
        data-observation={props.observation}
        data-update-channel-loading={props.updateChannelIsLoading ? 'true' : 'false'}
        data-update-channel-error={props.updateChannelIsError ? 'true' : 'false'}
        data-update-channel={props.updateChannelSettings?.channel ?? ''}
      >
        {props.observation === 'unknown' && (
          <div role="status" aria-live="polite" aria-label="Connection observation unknown">
            The browser is disconnected.
          </div>
        )}
      </div>
    );
  },
}));


const stableSettings: UpdateChannelSettings = { channel: 'stable', insiderAcknowledged: false };
const insiderSettings: UpdateChannelSettings = { channel: 'insider', insiderAcknowledged: true };

async function renderPage() {
  const { InstallerUpdatesPage } = await import('@/features/admin/pages/InstallerUpdatesPage');
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false, refetchOnWindowFocus: false } },
  });
  return render(
    <QueryClientProvider client={queryClient}>
      <InstallerUpdatesPage />
    </QueryClientProvider>,
  );
}

describe('InstallerUpdatesPage reconnect reconciliation', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    Object.defineProperty(navigator, 'onLine', { configurable: true, value: true });
    onlineManager.setOnline(true);
    setGetSystemInfoImpl(() => Promise.resolve({ inventory: inventory() }));
    setGetUpdateChannelSettingsImpl(() => Promise.resolve(stableSettings));
    setUpdateUpdateChannelSettingsImpl(() => Promise.resolve(undefined));
  });

  it('makes exactly one explicit request for one online event', async () => {
    await renderPage();
    await screen.findByTestId('installer-updates');
    expect(getSystemInfo).toHaveBeenCalledOnce();
    expect(getUpdateChannelSettings).toHaveBeenCalledOnce();

    await act(async () => { window.dispatchEvent(new Event('online')); });
    await waitFor(() => expect(getSystemInfo).toHaveBeenCalledTimes(2));
    // Flush query notifications too: automatic reconnect is disabled for this query.
    await act(async () => { await new Promise((resolve) => setTimeout(resolve, 0)); });
    expect(getSystemInfo).toHaveBeenCalledTimes(2);
    expect(getUpdateChannelSettings).toHaveBeenCalledOnce();
  });

  it('shows an explicit unknown state for an initially offline paused query', async () => {
    Object.defineProperty(navigator, 'onLine', { configurable: true, value: false });
    await renderPage();

    expect(await screen.findByRole('status', { name: 'Connection observation unknown' })).toHaveTextContent(/browser is disconnected/);
    expect(screen.queryByRole('button', { name: 'Retry installation observation' })).not.toBeInTheDocument();
  });

  it('keeps the observation unknown when an explicit retry fails', async () => {
    setGetSystemInfoImpl(() => Promise.reject(new Error('network unavailable')));
    await renderPage();

    await screen.findByRole('button', { name: 'Retry installation observation' });
    await userEvent.setup().click(screen.getByRole('button', { name: 'Retry installation observation' }));
    expect(await screen.findByRole('status', { name: 'Update observation unknown' })).toHaveTextContent(/snapshot is unknown/);
    expect(getSystemInfo.mock.calls.length).toBeGreaterThanOrEqual(2);
  });

  it('keeps a failed reconciliation unknown and allows a successful retry', async () => {
    {
      const responses = [
        () => Promise.resolve({ inventory: inventory() }),
        () => Promise.reject(new Error('network unavailable')),
        () => Promise.resolve({ inventory: inventory() }),
      ];
      setGetSystemInfoImpl(() => responses.shift()?.() ?? Promise.resolve({ inventory: inventory() }));
    }
    await renderPage();
    await screen.findByTestId('installer-updates');

    await act(async () => { window.dispatchEvent(new Event('online')); });
    expect(await screen.findByText('Update observation unknown')).toBeVisible();
    await userEvent.setup().click(screen.getByRole('button', { name: 'Retry installation observation' }));
    await screen.findByTestId('installer-updates');
    expect(getSystemInfo).toHaveBeenCalledTimes(3);
  });

  it('does not let an in-flight reconnect revive connected after an offline event', async () => {
    let completeReconnect: (() => void) | undefined;
    {
      const responses = [
        () => Promise.resolve({ inventory: inventory() }),
        () => new Promise((resolve) => {
          completeReconnect = () => resolve({ inventory: inventory() });
        }),
      ];
      setGetSystemInfoImpl(() => responses.shift()?.() ?? Promise.resolve({ inventory: inventory() }));
    }
    await renderPage();
    await screen.findByTestId('installer-updates');

    await act(async () => { window.dispatchEvent(new Event('online')); });
    await waitFor(() => expect(getSystemInfo).toHaveBeenCalledTimes(2));
    Object.defineProperty(navigator, 'onLine', { configurable: true, value: false });
    await act(async () => { window.dispatchEvent(new Event('offline')); });
    await act(async () => { completeReconnect?.(); });

    expect(await screen.findByRole('status', { name: 'Connection observation unknown' })).toHaveTextContent(/browser is disconnected/);
  });

  it('propagates pending UpdateChannel state with a stable pre-load default', async () => {
    let resolveSettings: ((settings: UpdateChannelSettings) => void) | undefined;
    setGetUpdateChannelSettingsImpl(() => new Promise((resolve) => { resolveSettings = resolve; }));
    await renderPage();

    await screen.findByTestId('installer-updates');
    expect(installerPropsRef.current?.updateChannelSettings).toBeUndefined();
    expect(installerPropsRef.current?.updateChannelIsLoading).toBe(true);
    expect(screen.getByTestId('installer-updates')).toHaveAttribute('data-update-channel', '');

    await act(async () => { resolveSettings?.(stableSettings); });
    await waitFor(() => expect(installerPropsRef.current?.updateChannelSettings).toEqual(stableSettings));
    expect(installerPropsRef.current?.updateChannelIsLoading).toBe(false);
  });

  it('propagates retryable UpdateChannel GET failure and successful retry data', async () => {
    setGetUpdateChannelSettingsImpl(() => Promise.reject(new Error('settings unavailable')));
    await renderPage();

    await waitFor(() => expect(installerPropsRef.current?.updateChannelIsError).toBe(true));
    expect(installerPropsRef.current?.updateChannelSettings).toBeUndefined();
    vi.clearAllMocks();
    setGetUpdateChannelSettingsImpl(() => Promise.resolve(insiderSettings));

    await act(async () => { installerPropsRef.current?.onRetryUpdateChannel?.(); });

    await waitFor(() => expect(installerPropsRef.current?.updateChannelSettings).toEqual(insiderSettings));
    expect(installerPropsRef.current?.updateChannelIsError).toBe(false);
    expect(getUpdateChannelSettings).toHaveBeenCalledOnce();
  });

  it('propagates successful authoritative UpdateChannel GET data', async () => {
    setGetUpdateChannelSettingsImpl(() => Promise.resolve(insiderSettings));
    await renderPage();

    await waitFor(() => expect(installerPropsRef.current?.updateChannelSettings).toEqual(insiderSettings));
    expect(installerPropsRef.current?.updateChannelIsLoading).toBe(false);
    expect(installerPropsRef.current?.updateChannelIsError).toBe(false);
  });

  it('rejects save when POST succeeds but authoritative UpdateChannel refetch fails', async () => {
    await renderPage();
    await waitFor(() => expect(installerPropsRef.current?.updateChannelSettings).toEqual(stableSettings));
    vi.clearAllMocks();
    setGetUpdateChannelSettingsImpl(() => Promise.reject(new Error('confirmation unavailable')));

    await expect(installerPropsRef.current?.onSaveUpdateChannel?.({ channel: 'insider', insiderAcknowledged: true }))
      .rejects.toThrow(/could not be confirmed/);

    expect(updateUpdateChannelSettings).toHaveBeenCalledWith({ channel: 'insider', insiderAcknowledged: true });
    expect(getUpdateChannelSettings).toHaveBeenCalledOnce();
  });

  it('rejects the save as unchanged when the authoritative refetch disagrees with the request, even though the refetch itself succeeded', async () => {
    // Renamed: this used to be named as if a mismatched refetch were a
    // legitimate "server-normalized" success. A refetch that succeeds but
    // disagrees with the request is a confirmed rejection/unchanged state,
    // not a success, regardless of whether the POST promise itself resolved.
    await renderPage();
    await waitFor(() => expect(installerPropsRef.current?.updateChannelSettings).toEqual(stableSettings));
    vi.clearAllMocks();
    const unchanged = { channel: 'stable', insiderAcknowledged: false } satisfies UpdateChannelSettings;
    setGetUpdateChannelSettingsImpl(() => Promise.resolve(unchanged));

    const save = installerPropsRef.current?.onSaveUpdateChannel?.({ channel: 'insider', insiderAcknowledged: true });
    await expect(save).rejects.toBeInstanceOf(UpdateChannelSaveRejectedError);
    await expect(save).rejects.toMatchObject({ authoritative: unchanged });

    expect(updateUpdateChannelSettings).toHaveBeenCalledWith({ channel: 'insider', insiderAcknowledged: true });
    expect(getUpdateChannelSettings).toHaveBeenCalledOnce();
  });

  it('attempts an authoritative refetch even when the POST rejects, and confirms success once it matches the request (response-loss scenario)', async () => {
    // Simulates a lost/timed-out POST response: the request promise
    // rejects, but the server actually applied the change. The refetch must
    // still be attempted, and the outcome must be derived only from
    // comparing the authoritative refetch to the request -- never from
    // whether the POST promise itself resolved or rejected.
    await renderPage();
    await waitFor(() => expect(installerPropsRef.current?.updateChannelSettings).toEqual(stableSettings));
    vi.clearAllMocks();
    setUpdateUpdateChannelSettingsImpl(() => Promise.reject(new Error('response lost')));
    const requested = { channel: 'insider', insiderAcknowledged: true } satisfies UpdateChannelSettings;
    setGetUpdateChannelSettingsImpl(() => Promise.resolve(requested));

    await expect(installerPropsRef.current?.onSaveUpdateChannel?.(requested)).resolves.toEqual(requested);

    expect(updateUpdateChannelSettings).toHaveBeenCalledWith(requested);
    expect(getUpdateChannelSettings).toHaveBeenCalledOnce();
  });

  it('keeps the outcome unknown, but still attempts the refetch, when a rejected POST is followed by a failed refetch', async () => {
    await renderPage();
    await waitFor(() => expect(installerPropsRef.current?.updateChannelSettings).toEqual(stableSettings));
    vi.clearAllMocks();
    setUpdateUpdateChannelSettingsImpl(() => Promise.reject(new Error('network unavailable')));
    setGetUpdateChannelSettingsImpl(() => Promise.reject(new Error('confirmation unavailable')));

    await expect(installerPropsRef.current?.onSaveUpdateChannel?.({ channel: 'insider', insiderAcknowledged: true }))
      .rejects.toThrow(/could not be confirmed/);

    expect(updateUpdateChannelSettings).toHaveBeenCalledWith({ channel: 'insider', insiderAcknowledged: true });
    // The refetch is still attempted after a rejected POST, not skipped.
    expect(getUpdateChannelSettings).toHaveBeenCalledOnce();
  });
});

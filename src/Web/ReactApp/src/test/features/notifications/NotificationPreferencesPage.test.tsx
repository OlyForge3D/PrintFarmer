import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider, onlineManager } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router';
import { NotificationPreferencesPage } from '@/features/notifications/pages/NotificationPreferencesPage';
import { NotificationFrequency, NotificationPreferenceEventType } from '@/types/api';
import type {
  NotificationCapabilitiesResponse,
  NotificationPreferencesDto,
  UpdateNotificationPreferencesRequest,
} from '@/types/api';
import { apiClient } from '@/services/api';

const mockUsePushSubscription = vi.fn();

vi.mock('sonner', () => ({
  toast: {
    success: vi.fn(),
    error: vi.fn(),
  },
}));

vi.mock('@/services/api', () => ({
  apiClient: {
    getNotificationPreferences: vi.fn(),
    getNotificationCapabilities: vi.fn(),
    updateNotificationPreferences: vi.fn(),
  },
}));

vi.mock('@/features/notifications/hooks/usePushSubscription', () => ({
  usePushSubscription: () => mockUsePushSubscription(),
}));

function createLegacyPreferences(): NotificationPreferencesDto {
  return {
    userId: 'u1',
    enableEmailNotifications: true,
    enablePushNotifications: true,
    enableInAppNotifications: true,
    enableTelegramNotifications: false,
    notifyOnCompletion: true,
    notifyOnFailure: true,
    notifyOnStart: false,
    notifyOnPause: true,
    eventChannelPreferences: [
      { eventType: NotificationPreferenceEventType.JobStarted, inApp: false, email: false, push: false, telegram: false },
      { eventType: NotificationPreferenceEventType.JobCompleted, inApp: true, email: true, push: true, telegram: false },
      { eventType: NotificationPreferenceEventType.JobFailed, inApp: true, email: true, push: true, telegram: false },
      { eventType: NotificationPreferenceEventType.JobPaused, inApp: true, email: true, push: true, telegram: false },
    ],
    frequency: NotificationFrequency.RealTime,
    retentionDays: 30,
  };
}

function createCapablePreferences(): NotificationPreferencesDto {
  const p = createLegacyPreferences();
  p.eventChannelPreferences = [
    ...p.eventChannelPreferences,
    { eventType: NotificationPreferenceEventType.FilamentRunout, inApp: true, email: true, push: false, telegram: false },
    { eventType: NotificationPreferenceEventType.HarvestReady, inApp: true, email: false, push: true, telegram: false },
    { eventType: NotificationPreferenceEventType.MaintenanceDue, inApp: false, email: true, push: false, telegram: false },
    { eventType: NotificationPreferenceEventType.PrinterOffline, inApp: true, email: true, push: true, telegram: true },
  ];
  return p;
}

const CAPABLE_CAPABILITIES = {
  supportedEventTypes: [
    NotificationPreferenceEventType.JobStarted,
    NotificationPreferenceEventType.JobCompleted,
    NotificationPreferenceEventType.JobFailed,
    NotificationPreferenceEventType.JobPaused,
    NotificationPreferenceEventType.PrinterFailure,
    NotificationPreferenceEventType.FilamentRunout,
    NotificationPreferenceEventType.HarvestReady,
    NotificationPreferenceEventType.MaintenanceDue,
    NotificationPreferenceEventType.PrinterOffline,
  ],
};

function renderPage() {
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: { retry: false },
      mutations: { retry: false },
    },
  });

  return {
    queryClient,
    ...render(
      <QueryClientProvider client={queryClient}>
        <MemoryRouter>
          <NotificationPreferencesPage />
        </MemoryRouter>
      </QueryClientProvider>,
    ),
  };
}

function getPreferencesMock() {
  return vi.mocked(apiClient.getNotificationPreferences);
}

function updatePreferencesMock() {
  return vi.mocked(apiClient.updateNotificationPreferences);
}

function getCapabilitiesMock() {
  return vi.mocked(apiClient.getNotificationCapabilities);
}

function expectFrequency(value: NotificationFrequency) {
  expect(screen.getByLabelText('Notification frequency')).toHaveValue(value);
}

async function waitForPreferences() {
  await screen.findByText('Event × Channel Matrix');
}

function setAuthoritativePreferences(overrides: Partial<NotificationPreferencesDto> = {}) {
  setPreferences({ ...createLegacyPreferences(), ...overrides });
}

function setPreferences(preferences: NotificationPreferencesDto) {
  getPreferencesMock().mockResolvedValue(preferences);
}

function setCapabilities(capabilities: NotificationCapabilitiesResponse | null = null) {
  getCapabilitiesMock().mockResolvedValue(capabilities);
}

function expectPreferencesBlocked() {
  expect(screen.getByRole('status', { name: 'Loading preferences' })).toBeInTheDocument();
  expect(screen.queryByText('Event × Channel Matrix')).not.toBeInTheDocument();
  expect(screen.queryByRole('button', { name: 'Save Preferences' })).not.toBeInTheDocument();
}

describe('NotificationPreferencesPage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    onlineManager.setOnline(true);
    setAuthoritativePreferences();
    setCapabilities();
    updatePreferencesMock().mockResolvedValue(createLegacyPreferences());
    mockUsePushSubscription.mockReturnValue({
      isSupported: true,
      isSubscribed: true,
      isLoading: false,
      error: null,
      subscribe: vi.fn(),
    });
  });

  afterEach(() => {
    onlineManager.setOnline(true);
  });

  it('renders the event-by-channel matrix headers and rows', async () => {
    renderPage();
    await waitForPreferences();

    expect(screen.getByText('Event × Channel Matrix')).toBeInTheDocument();
    // Header appears in both job matrix and operator matrix cards, so use getAllByText
    expect(screen.getAllByText('In-App').length).toBeGreaterThanOrEqual(1);
    expect(screen.getAllByText('Email').length).toBeGreaterThanOrEqual(1);
    expect(screen.getAllByText('Browser Push').length).toBeGreaterThanOrEqual(1);
    expect(screen.getAllByText('Telegram').length).toBeGreaterThanOrEqual(1);
    expect(screen.getByLabelText('Print Complete email')).toBeInTheDocument();
    expect(screen.getByLabelText('Print Complete Telegram')).toBeInTheDocument();
  });

  it('keeps print-failed in-app always on and disabled', async () => {
    renderPage();
    await waitForPreferences();

    const failedInApp = screen.getByLabelText('Print Failed in-app') as HTMLInputElement;
    expect(failedInApp.checked).toBe(true);
    expect(failedInApp.disabled).toBe(true);
    expect(screen.getByText('Always on')).toBeInTheDocument();
  });

  it('saves updated matrix when toggles change', async () => {
    renderPage();
    await waitForPreferences();

    fireEvent.click(screen.getByLabelText('Print Started email'));
    fireEvent.click(screen.getByRole('button', { name: 'Save Preferences' }));

    await waitFor(() => {
      expect(updatePreferencesMock()).toHaveBeenCalledTimes(1);
    });

    const payload = updatePreferencesMock().mock.calls[0][0] as UpdateNotificationPreferencesRequest;
    const started = payload.eventChannelPreferences?.find(x => x.eventType === NotificationPreferenceEventType.JobStarted);
    expect(started?.email).toBe(true);
    expect(payload.enableEmailNotifications).toBe(true);
  });

  it('saves Telegram opt-in when a Telegram toggle changes', async () => {
    renderPage();
    await waitForPreferences();

    fireEvent.click(screen.getByLabelText('Print Complete Telegram'));
    fireEvent.click(screen.getByRole('button', { name: 'Save Preferences' }));

    await waitFor(() => {
      expect(updatePreferencesMock()).toHaveBeenCalledTimes(1);
    });

    const payload = updatePreferencesMock().mock.calls[0][0] as UpdateNotificationPreferencesRequest;
    const completed = payload.eventChannelPreferences?.find(x => x.eventType === NotificationPreferenceEventType.JobCompleted);
    expect(completed?.telegram).toBe(true);
    expect(payload.enableTelegramNotifications).toBe(true);
  });

  describe('operator alerts (issue #716)', () => {
    it('renders operator alert rows with descriptions and accessible toggles', async () => {
      renderPage();
      await waitForPreferences();

      expect(screen.getByRole('heading', { name: 'Operator Alerts' })).toBeInTheDocument();
      expect(screen.getByText('Filament Runout Risk')).toBeInTheDocument();
      expect(screen.getByText('Harvest Ready')).toBeInTheDocument();
      expect(screen.getByText('Maintenance Due')).toBeInTheDocument();
      expect(screen.getByText('Printer Offline')).toBeInTheDocument();

      for (const label of [
        'Filament Runout Risk',
        'Harvest Ready',
        'Maintenance Due',
        'Printer Offline',
      ]) {
        expect(screen.getByLabelText(`${label} in-app`)).toBeInTheDocument();
        expect(screen.getByLabelText(`${label} email`)).toBeInTheDocument();
        expect(screen.getByLabelText(`${label} push`)).toBeInTheDocument();
        expect(screen.getByLabelText(`${label} Telegram`)).toBeInTheDocument();
      }
    });

    it('shows a legacy-server notice when the server does not expose operator categories', async () => {
      renderPage();
      await waitForPreferences();

      expect(
        screen.getByText(/server does not yet expose operator alert categories/i),
      ).toBeInTheDocument();
    });

    it('hides the legacy notice when the capabilities probe advertises operator tokens', async () => {
      setPreferences(createCapablePreferences());
      setCapabilities(CAPABLE_CAPABILITIES);

      renderPage();
      await waitForPreferences();

      expect(
        screen.queryByText(/server does not yet expose operator alert categories/i),
      ).not.toBeInTheDocument();
    });

    it('strips operator tokens from the save payload on legacy servers (capabilities probe 404)', async () => {
      renderPage();
      await waitForPreferences();

      // On a legacy server operator toggles are disabled (see below), so we
      // trigger the save via a visible job-row edit and rely on the hydrated
      // operator defaults still being carried in `formState`. Stripping
      // happens in `buildSavePayload` regardless of whether the operator
      // toggle was interactive — the whole point of the strip is that the
      // legacy server must never receive operator tokens.
      fireEvent.click(screen.getByLabelText('Print Started email'));
      fireEvent.click(screen.getByRole('button', { name: 'Save Preferences' }));

      await waitFor(() => {
        expect(updatePreferencesMock()).toHaveBeenCalledTimes(1);
      });

      const payload = updatePreferencesMock().mock.calls[0][0] as UpdateNotificationPreferencesRequest;
      const tokens = payload.eventChannelPreferences?.map(r => r.eventType) ?? [];
      expect(tokens).toEqual(
        expect.arrayContaining([
          NotificationPreferenceEventType.JobStarted,
          NotificationPreferenceEventType.JobCompleted,
          NotificationPreferenceEventType.JobFailed,
          NotificationPreferenceEventType.JobPaused,
        ]),
      );
      expect(tokens).not.toContain(NotificationPreferenceEventType.HarvestReady);
      expect(tokens).not.toContain(NotificationPreferenceEventType.FilamentRunout);
      expect(tokens).not.toContain(NotificationPreferenceEventType.MaintenanceDue);
      expect(tokens).not.toContain(NotificationPreferenceEventType.PrinterOffline);
    });

    it('keeps operator tokens in the save payload when the capabilities probe advertises them', async () => {
      setPreferences(createCapablePreferences());
      setCapabilities(CAPABLE_CAPABILITIES);

      renderPage();
      await waitForPreferences();

      fireEvent.click(screen.getByLabelText('Maintenance Due Telegram'));
      fireEvent.click(screen.getByRole('button', { name: 'Save Preferences' }));

      await waitFor(() => {
        expect(updatePreferencesMock()).toHaveBeenCalledTimes(1);
      });

      const payload = updatePreferencesMock().mock.calls[0][0] as UpdateNotificationPreferencesRequest;
      const maintenance = payload.eventChannelPreferences?.find(
        r => r.eventType === NotificationPreferenceEventType.MaintenanceDue,
      );
      expect(maintenance?.telegram).toBe(true);
      expect(payload.enableTelegramNotifications).toBe(true);
    });

    it('hydrates operator-row values from a capable server response', async () => {
      setPreferences(createCapablePreferences());
      setCapabilities(CAPABLE_CAPABILITIES);

      renderPage();
      await waitForPreferences();

      // PrinterOffline had all four channels on in the capable fixture.
      expect((screen.getByLabelText('Printer Offline in-app') as HTMLInputElement).checked).toBe(true);
      expect((screen.getByLabelText('Printer Offline email') as HTMLInputElement).checked).toBe(true);
      expect((screen.getByLabelText('Printer Offline push') as HTMLInputElement).checked).toBe(true);
      expect((screen.getByLabelText('Printer Offline Telegram') as HTMLInputElement).checked).toBe(true);
      // HarvestReady had only in-app + push
      expect((screen.getByLabelText('Harvest Ready in-app') as HTMLInputElement).checked).toBe(true);
      expect((screen.getByLabelText('Harvest Ready push') as HTMLInputElement).checked).toBe(true);
      expect((screen.getByLabelText('Harvest Ready email') as HTMLInputElement).checked).toBe(false);
    });

    it('does not render a printerFailure row (kept out of #716 UI scope) and OMITS it from the save payload for concurrent-write safety', async () => {
      const capable = createCapablePreferences();
      capable.eventChannelPreferences.push({
        eventType: NotificationPreferenceEventType.PrinterFailure,
        inApp: true,
        email: true,
        push: true,
        telegram: false,
      });
      setPreferences(capable);
      setCapabilities(CAPABLE_CAPABILITIES);

      renderPage();
      await waitForPreferences();

      // No visible label/toggle for printerFailure.
      expect(screen.queryByText(/printer failure/i)).not.toBeInTheDocument();
      expect(screen.queryByLabelText(/printer failure in-app/i)).not.toBeInTheDocument();

      // Toggle something visible to force the save through, then confirm the
      // hidden printerFailure row is OMITTED so the backend preserves the
      // persisted value (per #708 partial-PUT contract).
      fireEvent.click(screen.getByLabelText('Harvest Ready push'));
      fireEvent.click(screen.getByRole('button', { name: 'Save Preferences' }));

      await waitFor(() => expect(updatePreferencesMock()).toHaveBeenCalledTimes(1));

      const payload = updatePreferencesMock().mock.calls[0][0] as UpdateNotificationPreferencesRequest;
      const printerFailure = payload.eventChannelPreferences?.find(
        r => r.eventType === NotificationPreferenceEventType.PrinterFailure,
      );
      expect(printerFailure).toBeUndefined();
    });
  });

  describe('capability gating (trio remediation)', () => {
    it('shows the loading spinner while capabilities are still resolving, even if preferences have arrived', async () => {
      getCapabilitiesMock().mockImplementation(() => new Promise(() => {}));

      renderPage();
      await waitFor(() => expect(getPreferencesMock()).toHaveBeenCalledTimes(1));

      expect(screen.getByRole('status', { name: /loading preferences/i })).toBeInTheDocument();
      expect(screen.queryByRole('button', { name: /save preferences/i })).not.toBeInTheDocument();
    });

    it('disables the save button and surfaces a warning when the capabilities probe errors', async () => {
      getCapabilitiesMock().mockRejectedValue(new Error('network'));

      renderPage();

      // Warning banner in the operator card
      expect(
        await screen.findByText(/could not verify notification capabilities/i, {}, { timeout: 3_000 }),
      ).toBeInTheDocument();

      // Even after editing a job row (which makes the form dirty), save
      // stays disabled because the capability state is unresolved. Silent
      // strip on save would be data-loss for operator-row saves.
      fireEvent.click(screen.getByLabelText('Print Started email'));
      const saveButton = screen.getByRole('button', { name: /save preferences/i });
      expect((saveButton as HTMLButtonElement).disabled).toBe(true);
    });

    it('disables operator toggles on legacy servers so users cannot make changes that would be silently stripped', async () => {
      renderPage();
      await waitForPreferences();

      for (const label of [
        'Filament Runout Risk',
        'Harvest Ready',
        'Maintenance Due',
        'Printer Offline',
      ]) {
        expect((screen.getByLabelText(`${label} in-app`) as HTMLInputElement).disabled).toBe(true);
        expect((screen.getByLabelText(`${label} email`) as HTMLInputElement).disabled).toBe(true);
        expect((screen.getByLabelText(`${label} push`) as HTMLInputElement).disabled).toBe(true);
        expect((screen.getByLabelText(`${label} Telegram`) as HTMLInputElement).disabled).toBe(true);
      }
    });

    it('does NOT let the hidden PrinterFailure default-push flag keep the browser-push warning permanently on', async () => {
      // Capable server, but user has push=false on every VISIBLE row. The
      // hidden PrinterFailure default has push=true. Before the fix,
      // `isAnyPushEnabled` iterated the full matrix and the warning could
      // not be dismissed by unchecking visible toggles.
      const noVisiblePush = createCapablePreferences();
      noVisiblePush.eventChannelPreferences = noVisiblePush.eventChannelPreferences.map(r => ({
        ...r,
        push: false,
      }));
      noVisiblePush.eventChannelPreferences.push({
        eventType: NotificationPreferenceEventType.PrinterFailure,
        inApp: true,
        email: false,
        push: true,
        telegram: false,
      });
      setPreferences(noVisiblePush);
      setCapabilities(CAPABLE_CAPABILITIES);
      // Push subscription reports not-subscribed so the warning would render
      // if any push-enabled row was visible.
      mockUsePushSubscription.mockReturnValue({
        isSupported: true,
        isSubscribed: false,
        isLoading: false,
        error: null,
        subscribe: vi.fn(),
      });

      renderPage();
      await waitForPreferences();

      // Warning text: "Browser push is off. Enable it to receive push notifications"
      // (or equivalent). It must NOT be shown because no VISIBLE row has push=true.
      expect(screen.queryByText(/enable browser push/i)).not.toBeInTheDocument();
    });

    it('gates operator rows per advertised token — a partial rollout enables only the advertised rows', async () => {
      // Server advertises jobs + HarvestReady + FilamentRunout only;
      // MaintenanceDue and PrinterOffline are NOT in supportedEventTypes.
      // Before this fix the disabled state was all-or-nothing
      // (`!serverSupportsOperatorCategories`), so advertising ANY operator
      // token enabled ALL four operator rows — including ones the server
      // does not actually accept, which `buildSavePayload` would then
      // silently strip on save without any UI indication.
      setPreferences(createCapablePreferences());
      setCapabilities({
        supportedEventTypes: [
          NotificationPreferenceEventType.JobStarted,
          NotificationPreferenceEventType.JobCompleted,
          NotificationPreferenceEventType.JobFailed,
          NotificationPreferenceEventType.JobPaused,
          NotificationPreferenceEventType.HarvestReady,
          NotificationPreferenceEventType.FilamentRunout,
        ],
      });

      renderPage();
      await waitForPreferences();

      for (const label of ['Filament Runout Risk', 'Harvest Ready']) {
        expect((screen.getByLabelText(`${label} in-app`) as HTMLInputElement).disabled).toBe(false);
        expect((screen.getByLabelText(`${label} email`) as HTMLInputElement).disabled).toBe(false);
        expect((screen.getByLabelText(`${label} push`) as HTMLInputElement).disabled).toBe(false);
        expect((screen.getByLabelText(`${label} Telegram`) as HTMLInputElement).disabled).toBe(false);
      }
      for (const label of ['Maintenance Due', 'Printer Offline']) {
        expect((screen.getByLabelText(`${label} in-app`) as HTMLInputElement).disabled).toBe(true);
        expect((screen.getByLabelText(`${label} email`) as HTMLInputElement).disabled).toBe(true);
        expect((screen.getByLabelText(`${label} push`) as HTMLInputElement).disabled).toBe(true);
        expect((screen.getByLabelText(`${label} Telegram`) as HTMLInputElement).disabled).toBe(true);
      }
    });
  });

  describe('required query guards (issue #761)', () => {
    it('blocks synthetic defaults and writes during an offline paused first load, then uses authoritative data after reconnecting', async () => {
      onlineManager.setOnline(false);
      setAuthoritativePreferences({ frequency: NotificationFrequency.Weekly });

      renderPage();

      expectPreferencesBlocked();
      expect(getPreferencesMock()).not.toHaveBeenCalled();
      expect(updatePreferencesMock()).not.toHaveBeenCalled();

      onlineManager.setOnline(true);

      await waitForPreferences();
      expectFrequency(NotificationFrequency.Weekly);
      expect(screen.getByLabelText('Print Started email')).not.toBeChecked();
      expect(getPreferencesMock()).toHaveBeenCalledTimes(1);
      expect(updatePreferencesMock()).not.toHaveBeenCalled();

      fireEvent.click(screen.getByLabelText('Print Started email'));
      fireEvent.click(screen.getByRole('button', { name: 'Save Preferences' }));

      await waitFor(() => {
        expect(updatePreferencesMock()).toHaveBeenCalledTimes(1);
      });

      expect(updatePreferencesMock().mock.calls[0][0]).toEqual(
        expect.objectContaining({ frequency: NotificationFrequency.Weekly }),
      );
    });

    it('treats a resolved 404 as empty preferences and keeps defaults editable', async () => {
      getPreferencesMock().mockRejectedValue({ statusCode: 404, message: 'Not found' });

      renderPage();
      await waitForPreferences();

      expectFrequency(NotificationFrequency.RealTime);
      fireEvent.click(screen.getByLabelText('Print Started email'));
      fireEvent.click(screen.getByRole('button', { name: 'Save Preferences' }));

      await waitFor(() => {
        expect(updatePreferencesMock()).toHaveBeenCalledTimes(1);
      });
    });

    it('does not turn a failed preferences request into editable defaults', async () => {
      getPreferencesMock().mockRejectedValue({ statusCode: 503, message: 'Unavailable' });

      renderPage();

      expect(await screen.findByText('Failed to load notification preferences')).toBeInTheDocument();
      expect(screen.queryByRole('button', { name: 'Save Preferences' })).not.toBeInTheDocument();
      expect(updatePreferencesMock()).not.toHaveBeenCalled();
    });
  });
});

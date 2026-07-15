import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { NotificationPreferencesPage } from '@/features/notifications/pages/NotificationPreferencesPage';
import { NotificationFrequency, NotificationPreferenceEventType } from '@/types/api';
import type { NotificationPreferencesDto, UpdateNotificationPreferencesRequest } from '@/types/api';

const mockUseNotificationPreferences = vi.fn();
const mockUseNotificationCapabilities = vi.fn();
const mockMutateAsync = vi.fn();
const mockUseUpdateNotificationPreferences = vi.fn();
const mockUsePushSubscription = vi.fn();

vi.mock('sonner', () => ({
  toast: {
    success: vi.fn(),
    error: vi.fn(),
  },
}));

vi.mock('@/features/notifications/hooks/useNotificationPreferences', () => ({
  useNotificationPreferences: () => mockUseNotificationPreferences(),
  useNotificationCapabilities: () => mockUseNotificationCapabilities(),
  useUpdateNotificationPreferences: () => mockUseUpdateNotificationPreferences(),
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
  return render(
    <MemoryRouter>
      <NotificationPreferencesPage />
    </MemoryRouter>,
  );
}

describe('NotificationPreferencesPage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mockUseNotificationPreferences.mockReturnValue({
      data: createLegacyPreferences(),
      isLoading: false,
      error: null,
    });
    mockUseNotificationCapabilities.mockReturnValue({
      data: null, // legacy: capabilities endpoint 404
      isLoading: false,
      error: null,
    });
    mockUseUpdateNotificationPreferences.mockReturnValue({
      mutateAsync: mockMutateAsync,
      isPending: false,
    });
    mockUsePushSubscription.mockReturnValue({
      isSupported: true,
      isSubscribed: true,
      isLoading: false,
      error: null,
      subscribe: vi.fn(),
    });
    mockMutateAsync.mockResolvedValue({});
  });

  it('renders the event-by-channel matrix headers and rows', () => {
    renderPage();

    expect(screen.getByText('Event × Channel Matrix')).toBeInTheDocument();
    // Header appears in both job matrix and operator matrix cards, so use getAllByText
    expect(screen.getAllByText('In-App').length).toBeGreaterThanOrEqual(1);
    expect(screen.getAllByText('Email').length).toBeGreaterThanOrEqual(1);
    expect(screen.getAllByText('Browser Push').length).toBeGreaterThanOrEqual(1);
    expect(screen.getAllByText('Telegram').length).toBeGreaterThanOrEqual(1);
    expect(screen.getByLabelText('Print Complete email')).toBeInTheDocument();
    expect(screen.getByLabelText('Print Complete Telegram')).toBeInTheDocument();
  });

  it('keeps print-failed in-app always on and disabled', () => {
    renderPage();

    const failedInApp = screen.getByLabelText('Print Failed in-app') as HTMLInputElement;
    expect(failedInApp.checked).toBe(true);
    expect(failedInApp.disabled).toBe(true);
    expect(screen.getByText('Always on')).toBeInTheDocument();
  });

  it('saves updated matrix when toggles change', async () => {
    renderPage();

    fireEvent.click(screen.getByLabelText('Print Started email'));
    fireEvent.click(screen.getByRole('button', { name: 'Save Preferences' }));

    await waitFor(() => {
      expect(mockMutateAsync).toHaveBeenCalledTimes(1);
    });

    const payload = mockMutateAsync.mock.calls[0][0] as UpdateNotificationPreferencesRequest;
    const started = payload.eventChannelPreferences?.find(x => x.eventType === NotificationPreferenceEventType.JobStarted);
    expect(started?.email).toBe(true);
    expect(payload.enableEmailNotifications).toBe(true);
  });

  it('saves Telegram opt-in when a Telegram toggle changes', async () => {
    renderPage();

    fireEvent.click(screen.getByLabelText('Print Complete Telegram'));
    fireEvent.click(screen.getByRole('button', { name: 'Save Preferences' }));

    await waitFor(() => {
      expect(mockMutateAsync).toHaveBeenCalledTimes(1);
    });

    const payload = mockMutateAsync.mock.calls[0][0] as UpdateNotificationPreferencesRequest;
    const completed = payload.eventChannelPreferences?.find(x => x.eventType === NotificationPreferenceEventType.JobCompleted);
    expect(completed?.telegram).toBe(true);
    expect(payload.enableTelegramNotifications).toBe(true);
  });

  describe('operator alerts (issue #716)', () => {
    it('renders operator alert rows with descriptions and accessible toggles', () => {
      renderPage();

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

    it('shows a legacy-server notice when the server does not expose operator categories', () => {
      renderPage();

      expect(
        screen.getByText(/server does not yet expose operator alert categories/i),
      ).toBeInTheDocument();
    });

    it('hides the legacy notice when the capabilities probe advertises operator tokens', () => {
      mockUseNotificationPreferences.mockReturnValue({
        data: createCapablePreferences(),
        isLoading: false,
        error: null,
      });
      mockUseNotificationCapabilities.mockReturnValue({
        data: CAPABLE_CAPABILITIES,
        isLoading: false,
        error: null,
      });

      renderPage();

      expect(
        screen.queryByText(/server does not yet expose operator alert categories/i),
      ).not.toBeInTheDocument();
    });

    it('strips operator tokens from the save payload on legacy servers (capabilities probe 404)', async () => {
      renderPage();

      // On a legacy server operator toggles are disabled (see below), so we
      // trigger the save via a visible job-row edit and rely on the hydrated
      // operator defaults still being carried in `formState`. Stripping
      // happens in `buildSavePayload` regardless of whether the operator
      // toggle was interactive — the whole point of the strip is that the
      // legacy server must never receive operator tokens.
      fireEvent.click(screen.getByLabelText('Print Started email'));
      fireEvent.click(screen.getByRole('button', { name: 'Save Preferences' }));

      await waitFor(() => {
        expect(mockMutateAsync).toHaveBeenCalledTimes(1);
      });

      const payload = mockMutateAsync.mock.calls[0][0] as UpdateNotificationPreferencesRequest;
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
      mockUseNotificationPreferences.mockReturnValue({
        data: createCapablePreferences(),
        isLoading: false,
        error: null,
      });
      mockUseNotificationCapabilities.mockReturnValue({
        data: CAPABLE_CAPABILITIES,
        isLoading: false,
        error: null,
      });

      renderPage();

      fireEvent.click(screen.getByLabelText('Maintenance Due Telegram'));
      fireEvent.click(screen.getByRole('button', { name: 'Save Preferences' }));

      await waitFor(() => {
        expect(mockMutateAsync).toHaveBeenCalledTimes(1);
      });

      const payload = mockMutateAsync.mock.calls[0][0] as UpdateNotificationPreferencesRequest;
      const maintenance = payload.eventChannelPreferences?.find(
        r => r.eventType === NotificationPreferenceEventType.MaintenanceDue,
      );
      expect(maintenance?.telegram).toBe(true);
      expect(payload.enableTelegramNotifications).toBe(true);
    });

    it('hydrates operator-row values from a capable server response', () => {
      mockUseNotificationPreferences.mockReturnValue({
        data: createCapablePreferences(),
        isLoading: false,
        error: null,
      });

      renderPage();

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
      mockUseNotificationPreferences.mockReturnValue({
        data: capable,
        isLoading: false,
        error: null,
      });
      mockUseNotificationCapabilities.mockReturnValue({
        data: CAPABLE_CAPABILITIES,
        isLoading: false,
        error: null,
      });

      renderPage();

      // No visible label/toggle for printerFailure.
      expect(screen.queryByText(/printer failure/i)).not.toBeInTheDocument();
      expect(screen.queryByLabelText(/printer failure in-app/i)).not.toBeInTheDocument();

      // Toggle something visible to force the save through, then confirm the
      // hidden printerFailure row is OMITTED so the backend preserves the
      // persisted value (per #708 partial-PUT contract).
      fireEvent.click(screen.getByLabelText('Harvest Ready push'));
      fireEvent.click(screen.getByRole('button', { name: 'Save Preferences' }));

      await waitFor(() => expect(mockMutateAsync).toHaveBeenCalledTimes(1));

      const payload = mockMutateAsync.mock.calls[0][0] as UpdateNotificationPreferencesRequest;
      const printerFailure = payload.eventChannelPreferences?.find(
        r => r.eventType === NotificationPreferenceEventType.PrinterFailure,
      );
      expect(printerFailure).toBeUndefined();
    });
  });

  describe('capability gating (trio remediation)', () => {
    it('shows the loading spinner while capabilities are still resolving, even if preferences have arrived', () => {
      mockUseNotificationCapabilities.mockReturnValue({
        data: undefined,
        isLoading: true,
        error: null,
      });

      renderPage();

      expect(screen.getByRole('status', { name: /loading preferences/i })).toBeInTheDocument();
      expect(screen.queryByRole('button', { name: /save preferences/i })).not.toBeInTheDocument();
    });

    it('disables the save button and surfaces a warning when the capabilities probe errors', () => {
      mockUseNotificationCapabilities.mockReturnValue({
        data: undefined,
        isLoading: false,
        error: new Error('network'),
      });

      renderPage();

      // Warning banner in the operator card
      expect(
        screen.getByText(/could not verify notification capabilities/i),
      ).toBeInTheDocument();

      // Even after editing a job row (which makes the form dirty), save
      // stays disabled because the capability state is unresolved. Silent
      // strip on save would be data-loss for operator-row saves.
      fireEvent.click(screen.getByLabelText('Print Started email'));
      const saveButton = screen.getByRole('button', { name: /save preferences/i });
      expect((saveButton as HTMLButtonElement).disabled).toBe(true);
    });

    it('disables operator toggles on legacy servers so users cannot make changes that would be silently stripped', () => {
      renderPage();

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

    it('does NOT let the hidden PrinterFailure default-push flag keep the browser-push warning permanently on', () => {
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
      mockUseNotificationPreferences.mockReturnValue({
        data: noVisiblePush,
        isLoading: false,
        error: null,
      });
      mockUseNotificationCapabilities.mockReturnValue({
        data: CAPABLE_CAPABILITIES,
        isLoading: false,
        error: null,
      });
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

      // Warning text: "Browser push is off. Enable it to receive push notifications"
      // (or equivalent). It must NOT be shown because no VISIBLE row has push=true.
      expect(screen.queryByText(/enable browser push/i)).not.toBeInTheDocument();
    });
  });
});

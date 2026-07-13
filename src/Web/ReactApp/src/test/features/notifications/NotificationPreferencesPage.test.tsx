import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { NotificationPreferencesPage } from '@/features/notifications/pages/NotificationPreferencesPage';
import {
  AttentionKind,
  NotificationFrequency,
  NotificationPreferenceEventType,
} from '@/types/api';
import type {
  AttentionCategoriesResponse,
  AttentionPushPreferencesDto,
  NotificationPreferencesDto,
  UpdateAttentionPushPreferencesRequest,
  UpdateNotificationPreferencesRequest,
} from '@/types/api';

const mockUseNotificationPreferences = vi.fn();
const mockMutateAsync = vi.fn();
const mockUseUpdateNotificationPreferences = vi.fn();
const mockUsePushSubscription = vi.fn();

const mockUseAttentionCategories = vi.fn();
const mockUseAttentionPushPreferences = vi.fn();
const mockUpdateAttentionMutateAsync = vi.fn();
const mockUseUpdateAttentionPushPreferences = vi.fn();

vi.mock('sonner', () => ({
  toast: {
    success: vi.fn(),
    error: vi.fn(),
  },
}));

vi.mock('@/features/notifications/hooks/useNotificationPreferences', () => ({
  useNotificationPreferences: () => mockUseNotificationPreferences(),
  useUpdateNotificationPreferences: () => mockUseUpdateNotificationPreferences(),
}));

vi.mock('@/features/notifications/hooks/usePushSubscription', () => ({
  usePushSubscription: () => mockUsePushSubscription(),
}));

vi.mock('@/features/notifications/hooks/useAttentionPushPreferences', () => ({
  useAttentionCategories: () => mockUseAttentionCategories(),
  useAttentionPushPreferences: () => mockUseAttentionPushPreferences(),
  useUpdateAttentionPushPreferences: () => mockUseUpdateAttentionPushPreferences(),
}));

function createPreferences(): NotificationPreferencesDto {
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

function serverCategories(): AttentionCategoriesResponse {
  return {
    categories: [
      { id: 'PRINTER_FAILURE', kind: AttentionKind.Failure, actions: [], threadIdTemplate: '' },
      { id: 'PRINTER_OFFLINE', kind: AttentionKind.Offline, actions: [], threadIdTemplate: '' },
      { id: 'MAINTENANCE_DUE', kind: AttentionKind.Maintenance, actions: [], threadIdTemplate: '' },
      { id: 'HARVEST_READY', kind: AttentionKind.Harvest, actions: [], threadIdTemplate: '' },
      { id: 'FILAMENT_RUNOUT', kind: AttentionKind.Runout, actions: [], threadIdTemplate: '' },
    ],
  };
}

function attentionPreferences(overrides: Partial<AttentionPushPreferencesDto> = {}): AttentionPushPreferencesDto {
  return {
    enabled: true,
    categories: {
      PRINTER_FAILURE: true,
      PRINTER_OFFLINE: true,
      MAINTENANCE_DUE: false,
      HARVEST_READY: true,
      FILAMENT_RUNOUT: true,
    },
    ...overrides,
  };
}

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
      data: createPreferences(),
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
    // Default: legacy server — attention endpoints return null / feature unavailable.
    mockUseAttentionCategories.mockReturnValue({ data: null, isLoading: false, error: null });
    mockUseAttentionPushPreferences.mockReturnValue({
      data: { preferences: null, featureAvailable: false },
      isLoading: false,
      error: null,
    });
    mockUseUpdateAttentionPushPreferences.mockReturnValue({
      mutateAsync: mockUpdateAttentionMutateAsync,
      isPending: false,
    });
    mockMutateAsync.mockResolvedValue({});
    mockUpdateAttentionMutateAsync.mockResolvedValue(undefined);
  });

  describe('event × channel matrix (existing behaviour)', () => {
    it('renders the event-by-channel matrix headers and rows', () => {
      renderPage();

      expect(screen.getByText('Event × Channel Matrix')).toBeInTheDocument();
      expect(screen.getByText('In-App')).toBeInTheDocument();
      expect(screen.getByText('Email')).toBeInTheDocument();
      expect(screen.getByText('Browser Push')).toBeInTheDocument();
      expect(screen.getByText('Telegram')).toBeInTheDocument();
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
  });

  describe('operator alerts (issue #716 / #708)', () => {
    it('renders the operator card with all five APNs categories in canonical order', () => {
      renderPage();

      expect(screen.getByRole('heading', { name: 'Operator Alerts' })).toBeInTheDocument();
      expect(screen.getByText('Printer Failure')).toBeInTheDocument();
      expect(screen.getByText('Printer Offline')).toBeInTheDocument();
      expect(screen.getByText('Maintenance Due')).toBeInTheDocument();
      expect(screen.getByText('Harvest Ready')).toBeInTheDocument();
      expect(screen.getByText('Filament Runout Risk')).toBeInTheDocument();

      for (const label of [
        'Printer Failure',
        'Printer Offline',
        'Maintenance Due',
        'Harvest Ready',
        'Filament Runout Risk',
      ]) {
        expect(screen.getByLabelText(`${label} operator alert`)).toBeInTheDocument();
      }
    });

    it('shows the legacy-server notice and disables all operator toggles when the feature is unavailable', () => {
      renderPage();

      expect(
        screen.getByText(/Operator alert notifications are not available on this server/i),
      ).toBeInTheDocument();

      const master = screen.getByLabelText('Enable operator alert push notifications') as HTMLInputElement;
      expect(master.disabled).toBe(true);
      const failure = screen.getByLabelText('Printer Failure operator alert') as HTMLInputElement;
      expect(failure.disabled).toBe(true);
    });

    it('hides the legacy notice and enables the master toggle when the feature is available', () => {
      mockUseAttentionCategories.mockReturnValue({ data: serverCategories(), isLoading: false, error: null });
      mockUseAttentionPushPreferences.mockReturnValue({
        data: { preferences: attentionPreferences({ enabled: false }), featureAvailable: true },
        isLoading: false,
        error: null,
      });

      renderPage();

      expect(
        screen.queryByText(/Operator alert notifications are not available on this server/i),
      ).not.toBeInTheDocument();
      const master = screen.getByLabelText('Enable operator alert push notifications') as HTMLInputElement;
      expect(master.disabled).toBe(false);
      expect(master.checked).toBe(false);
    });

    it('disables per-category toggles until the master switch is on', () => {
      mockUseAttentionCategories.mockReturnValue({ data: serverCategories(), isLoading: false, error: null });
      mockUseAttentionPushPreferences.mockReturnValue({
        data: { preferences: attentionPreferences({ enabled: false }), featureAvailable: true },
        isLoading: false,
        error: null,
      });

      renderPage();

      const failure = screen.getByLabelText('Printer Failure operator alert') as HTMLInputElement;
      expect(failure.disabled).toBe(true);
    });

    it('hydrates per-category toggles from server preferences when the feature is available', () => {
      mockUseAttentionCategories.mockReturnValue({ data: serverCategories(), isLoading: false, error: null });
      mockUseAttentionPushPreferences.mockReturnValue({
        data: { preferences: attentionPreferences(), featureAvailable: true },
        isLoading: false,
        error: null,
      });

      renderPage();

      expect((screen.getByLabelText('Printer Failure operator alert') as HTMLInputElement).checked).toBe(true);
      expect((screen.getByLabelText('Printer Offline operator alert') as HTMLInputElement).checked).toBe(true);
      expect((screen.getByLabelText('Maintenance Due operator alert') as HTMLInputElement).checked).toBe(false);
      expect((screen.getByLabelText('Harvest Ready operator alert') as HTMLInputElement).checked).toBe(true);
      expect((screen.getByLabelText('Filament Runout Risk operator alert') as HTMLInputElement).checked).toBe(true);
    });

    it('saves attention push preferences with exhaustive category map when a category is toggled', async () => {
      mockUseAttentionCategories.mockReturnValue({ data: serverCategories(), isLoading: false, error: null });
      mockUseAttentionPushPreferences.mockReturnValue({
        data: { preferences: attentionPreferences(), featureAvailable: true },
        isLoading: false,
        error: null,
      });

      renderPage();

      fireEvent.click(screen.getByLabelText('Maintenance Due operator alert'));
      fireEvent.click(screen.getByRole('button', { name: 'Save Preferences' }));

      await waitFor(() => {
        expect(mockUpdateAttentionMutateAsync).toHaveBeenCalledTimes(1);
      });

      const payload = mockUpdateAttentionMutateAsync.mock.calls[0][0] as UpdateAttentionPushPreferencesRequest;
      expect(payload.enabled).toBe(true);
      expect(payload.categories.MAINTENANCE_DUE).toBe(true);
      expect(payload.categories.PRINTER_FAILURE).toBe(true);
      expect(payload.categories.PRINTER_OFFLINE).toBe(true);
      expect(payload.categories.HARVEST_READY).toBe(true);
      expect(payload.categories.FILAMENT_RUNOUT).toBe(true);
      // legacy /notifications/preferences must NOT be touched since only the operator card was dirtied
      expect(mockMutateAsync).not.toHaveBeenCalled();
    });

    it('does not call the attention PUT on legacy servers even when toggles are interacted with', async () => {
      renderPage();

      // Master toggle is disabled but sanity-check we cannot save operator changes at all.
      const master = screen.getByLabelText('Enable operator alert push notifications') as HTMLInputElement;
      // Even if we tried fireEvent.click, the toggle is disabled, but the save button should
      // still work for the (unchanged) legacy matrix without ever calling the attention PUT.
      expect(master.disabled).toBe(true);

      // Dirty only the legacy matrix and save
      fireEvent.click(screen.getByLabelText('Print Started email'));
      fireEvent.click(screen.getByRole('button', { name: 'Save Preferences' }));

      await waitFor(() => {
        expect(mockMutateAsync).toHaveBeenCalledTimes(1);
      });
      expect(mockUpdateAttentionMutateAsync).not.toHaveBeenCalled();
    });

    it('sends both PUTs when both matrices are dirtied on a capable server', async () => {
      mockUseAttentionCategories.mockReturnValue({ data: serverCategories(), isLoading: false, error: null });
      mockUseAttentionPushPreferences.mockReturnValue({
        data: { preferences: attentionPreferences(), featureAvailable: true },
        isLoading: false,
        error: null,
      });

      renderPage();

      fireEvent.click(screen.getByLabelText('Print Complete Telegram'));
      fireEvent.click(screen.getByLabelText('Harvest Ready operator alert'));
      fireEvent.click(screen.getByRole('button', { name: 'Save Preferences' }));

      await waitFor(() => {
        expect(mockMutateAsync).toHaveBeenCalledTimes(1);
        expect(mockUpdateAttentionMutateAsync).toHaveBeenCalledTimes(1);
      });

      const payload = mockUpdateAttentionMutateAsync.mock.calls[0][0] as UpdateAttentionPushPreferencesRequest;
      expect(payload.categories.HARVEST_READY).toBe(false); // toggled off from true
    });
  });
});

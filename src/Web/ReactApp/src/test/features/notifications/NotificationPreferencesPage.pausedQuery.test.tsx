import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider, onlineManager } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router';
import { NotificationPreferencesPage } from '@/features/notifications/pages/NotificationPreferencesPage';
import { NotificationFrequency, NotificationPreferenceEventType } from '@/types/api';
import type { NotificationPreferencesDto, UpdateNotificationPreferencesRequest } from '@/types/api';
import { apiClient } from '@/services/api';

// #766 paused-query defensive rendering: this suite drives the real
// TanStack Query hooks (with a mocked apiClient) so it can exercise
// paused first-load, resolved-404, and failed-request states that the
// mock-hook suite in NotificationPreferencesPage.test.tsx cannot.

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
    updateNotificationPreferences: vi.fn(),
    getNotificationCapabilities: vi.fn(),
  },
}));

vi.mock('@/features/notifications/hooks/usePushSubscription', () => ({
  usePushSubscription: () => mockUsePushSubscription(),
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
  getPreferencesMock().mockResolvedValue({
    ...createPreferences(),
    ...overrides,
  });
}

function expectPreferencesBlocked() {
  expect(screen.getByRole('status', { name: 'Loading preferences' })).toBeInTheDocument();
  expect(screen.queryByText('Event × Channel Matrix')).not.toBeInTheDocument();
  expect(screen.queryByRole('button', { name: 'Save Preferences' })).not.toBeInTheDocument();
}

describe('NotificationPreferencesPage paused-query behaviour (#766)', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    onlineManager.setOnline(true);
    setAuthoritativePreferences();
    updatePreferencesMock().mockResolvedValue(createPreferences());
    // Legacy-server capability probe — resolves to null so the page never
    // waits on a capabilities response that isn't the subject of this suite.
    getCapabilitiesMock().mockResolvedValue(null);
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

    const payload = updatePreferencesMock().mock.calls[0][0] as UpdateNotificationPreferencesRequest;
    const started = payload.eventChannelPreferences?.find(
      x => x.eventType === NotificationPreferenceEventType.JobStarted,
    );
    expect(started?.email).toBe(true);
  });

  it('does not turn a failed preferences request into editable defaults', async () => {
    getPreferencesMock().mockRejectedValue({ statusCode: 503, message: 'Unavailable' });

    renderPage();

    expect(await screen.findByText('Failed to load notification preferences')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Save Preferences' })).not.toBeInTheDocument();
    expect(updatePreferencesMock()).not.toHaveBeenCalled();
  });
});

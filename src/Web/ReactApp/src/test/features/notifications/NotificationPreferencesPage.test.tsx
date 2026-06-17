import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { NotificationPreferencesPage } from '@/features/notifications/pages/NotificationPreferencesPage';
import { NotificationFrequency, NotificationPreferenceEventType } from '@/types/api';
import type { NotificationPreferencesDto, UpdateNotificationPreferencesRequest } from '@/types/api';

const mockUseNotificationPreferences = vi.fn();
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
  useUpdateNotificationPreferences: () => mockUseUpdateNotificationPreferences(),
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
    notifyOnCompletion: true,
    notifyOnFailure: true,
    notifyOnStart: false,
    notifyOnPause: true,
    eventChannelPreferences: [
      { eventType: NotificationPreferenceEventType.JobStarted, inApp: false, email: false, push: false },
      { eventType: NotificationPreferenceEventType.JobCompleted, inApp: true, email: true, push: true },
      { eventType: NotificationPreferenceEventType.JobFailed, inApp: true, email: true, push: true },
      { eventType: NotificationPreferenceEventType.JobPaused, inApp: true, email: true, push: true },
    ],
    frequency: NotificationFrequency.RealTime,
    retentionDays: 30,
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
    mockMutateAsync.mockResolvedValue({});
  });

  it('renders the event-by-channel matrix headers and rows', () => {
    renderPage();

    expect(screen.getByText('Event × Channel Matrix')).toBeInTheDocument();
    expect(screen.getByText('In-App')).toBeInTheDocument();
    expect(screen.getByText('Email')).toBeInTheDocument();
    expect(screen.getByText('Browser Push')).toBeInTheDocument();
    expect(screen.getByLabelText('Print Complete email')).toBeInTheDocument();
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
});

import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { TelegramSettingsCard } from '@/features/settings/components/TelegramSettingsCard';
import { apiClient } from '@/services/api';

vi.mock('sonner', () => ({
  toast: {
    success: vi.fn(),
    error: vi.fn(),
  },
}));

vi.mock('@/services/api', () => ({
  apiClient: {
    getTelegramSettings: vi.fn(),
    updateTelegramSettings: vi.fn(),
    sendTelegramTestMessage: vi.fn(),
  },
}));

function renderCard() {
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: { retry: false },
      mutations: { retry: false },
    },
  });

  return render(
    <QueryClientProvider client={queryClient}>
      <TelegramSettingsCard />
    </QueryClientProvider>,
  );
}

describe('TelegramSettingsCard', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(apiClient.getTelegramSettings).mockResolvedValue({
      enabled: true,
      chatId: '987654',
      includeSnapshots: true,
      botTokenMasked: '***cdef',
    });
    vi.mocked(apiClient.updateTelegramSettings).mockResolvedValue({
      enabled: true,
      chatId: '987654',
      includeSnapshots: true,
      botTokenMasked: '***cdef',
    });
    vi.mocked(apiClient.sendTelegramTestMessage).mockResolvedValue({
      success: true,
      message: 'Test message sent.',
    });
  });

  it('renders masked bot token without plaintext', async () => {
    renderCard();

    expect(await screen.findByDisplayValue('***cdef')).toBeInTheDocument();
    expect(screen.queryByDisplayValue('123456:abcdef')).not.toBeInTheDocument();
  });

  it('saves Telegram settings with the masked token placeholder', async () => {
    renderCard();

    fireEvent.click(await screen.findByRole('button', { name: 'Save Telegram Settings' }));

    await waitFor(() => {
      expect(apiClient.updateTelegramSettings).toHaveBeenCalledWith({
        enabled: true,
        chatId: '987654',
        includeSnapshots: true,
        botToken: '***cdef',
      });
    });
  });

  it('sends a Telegram test message', async () => {
    renderCard();

    fireEvent.click(await screen.findByRole('button', { name: 'Send Test Message' }));

    await waitFor(() => {
      expect(apiClient.sendTelegramTestMessage).toHaveBeenCalledTimes(1);
    });
  });
});

import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { HomeAssistantSettingsCard, SpoolmanSettingsCard } from '@/features/settings/components/IntegrationSettingsCards';
import { client } from '@/services/api/httpClient';
import { fetchSpoolmanSettings } from '@/services/api/integrationSettingsApi';

vi.mock('@/services/api/httpClient', () => ({
  client: { get: vi.fn(), post: vi.fn(), put: vi.fn() },
}));
vi.mock('sonner', () => ({ toast: { success: vi.fn(), error: vi.fn() } }));

function renderCard(card: React.ReactNode) {
  return render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      {card}
    </QueryClientProvider>,
  );
}

beforeEach(() => vi.resetAllMocks());

describe('resource-specific integration configuration', () => {
  it('configures Spoolman through its existing resource endpoint, never the generic batch API', async () => {
    vi.mocked(client.get).mockResolvedValue({ status: 200, data: { baseUrl: 'http://spoolman.local:7912' } });
    vi.mocked(client.post).mockResolvedValue({ status: 204 });
    renderCard(<SpoolmanSettingsCard />);
    fireEvent.change(await screen.findByLabelText('Spoolman URL'), { target: { value: 'http://spoolman.example:7912' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save Spoolman Settings' }));
    await waitFor(() => expect(client.post).toHaveBeenCalledWith('/spoolman/config', { baseUrl: 'http://spoolman.example:7912' }));
    expect(client.get).toHaveBeenCalledWith('/spoolman/config');
    expect(client.post).not.toHaveBeenCalledWith('/settings', expect.anything());
  });

  it('represents an unconfigured Spoolman 204 explicitly', async () => {
    vi.mocked(client.get).mockResolvedValue({ status: 204 });
    await expect(fetchSpoolmanSettings()).resolves.toEqual({ baseUrl: '' });
  });

  it('surfaces service load errors rather than offering empty editable defaults', async () => {
    vi.mocked(client.get).mockRejectedValue(new Error('Service unavailable'));
    renderCard(<SpoolmanSettingsCard />);
    expect(await screen.findByRole('alert')).toHaveTextContent('Service unavailable');
    expect(screen.queryByRole('button', { name: 'Save Spoolman Settings' })).not.toBeInTheDocument();
  });

  it('preserves a stored Home Assistant token when saving other fields', async () => {
    const settings = { enabled: true, baseUrl: 'http://home-assistant.local:8123', tokenMasked: '***test' };
    vi.mocked(client.get).mockResolvedValue({ data: settings });
    vi.mocked(client.put).mockResolvedValue({ data: settings });
    renderCard(<HomeAssistantSettingsCard />);
    const token = await screen.findByLabelText('Access token');
    expect(token).toHaveValue('');
    expect(token).toHaveAttribute('type', 'password');
    fireEvent.click(screen.getByRole('button', { name: 'Save Home Assistant Settings' }));
    await waitFor(() => expect(client.put).toHaveBeenCalledWith('/admin/integrations/home-assistant/settings', {
      enabled: true,
      baseUrl: settings.baseUrl,
      token: '',
    }));
    expect(client.post).not.toHaveBeenCalled();
  });
});

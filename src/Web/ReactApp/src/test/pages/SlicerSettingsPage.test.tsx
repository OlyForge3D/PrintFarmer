import SlicerSettingsPage from '@/pages/SlicerSettingsPage';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

describe('SlicerSettingsPage', () => {
  beforeEach(() => {
    // Reset global fetch mock
    (global as any).fetch = vi.fn();
  });

  it('loads settings and validates jitter input', async () => {
    const mockGet = vi.fn().mockResolvedValue({ ok: true, json: async () => ({ enabled: true, perEngine: {}, jitterPercent: 15.0 }) });
    (global as any).fetch = mockGet;

    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={queryClient}>
        <SlicerSettingsPage />
      </QueryClientProvider>
    );

    // Wait for the jitter input to show current value
    const input = await screen.findByRole('spinbutton');
    expect((input as HTMLInputElement).value).toBe('15');

    // Enter invalid jitter
    fireEvent.change(input, { target: { value: '150' } });
    expect(await screen.findByText(/must be between 0 and 100/i)).toBeTruthy();

    // Enter valid jitter and simulate save
    fireEvent.change(input, { target: { value: '12.5' } });
    expect(screen.queryByText(/must be between 0 and 100/i)).toBeNull();

    const mockPost = vi.fn().mockResolvedValue({ ok: true, text: async () => '' });
    // next fetch should be POST called by save mutation
    (global as any).fetch = mockPost;

    const saveButton = screen.getByRole('button', { name: /save settings/i });
    fireEvent.click(saveButton);

    await waitFor(() => expect(mockPost).toHaveBeenCalled());

    const arg0 = mockPost.mock.calls[0][0];
    const arg1 = mockPost.mock.calls[0][1];
    expect(arg0).toBe('/api/slicer/settings');
    const body = JSON.parse(arg1.body);
    expect(body.jitterPercent).toBe(12.5);
  });
});

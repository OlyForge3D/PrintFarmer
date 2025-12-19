import React from 'react';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { vi, describe, it, expect, beforeEach, afterEach } from 'vitest';
import PrintersAdminPage from '../PrintersAdminPage';
import * as api from '@/services/api';
import * as hooks from '@/hooks/useApi';

vi.mock('@/services/api');
// Mock ProtectedRoute to avoid needing the AuthProvider in tests
vi.mock('@/components/auth/ProtectedRoute', () => ({
  ProtectedRoute: ({ children }: { children: React.ReactNode }) => <div>{children}</div>
}));

function renderWithProviders(ui: React.ReactNode) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={queryClient}>
      {ui}
    </QueryClientProvider>
  );
}

describe('PrintersAdminPage', () => {
  beforeEach(() => {
  // Provide empty printers list from hook
  vi.spyOn(hooks, 'usePrintersWithCameraUrls').mockReturnValue({ data: [], isLoading: false } as unknown as ReturnType<typeof hooks.usePrintersWithCameraUrls>);
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('parses uploaded JSON and shows preview items', async () => {
    renderWithProviders(<PrintersAdminPage />);

    const file = {
      name: 'printers.json',
      async text() { return JSON.stringify([{ name: 'P1', serverUrl: 'http://1' }, { name: 'P2', serverUrl: 'http://2' }]); }
    } as unknown as File;
    const input = screen.getByLabelText('Import printers JSON file') as HTMLInputElement;
    // trigger file selection
    fireEvent.change(input, { target: { files: [file] } });

    await waitFor(() => expect(screen.getByText('Import preview (2)')).toBeInTheDocument());
    expect(screen.getByText('P1')).toBeInTheDocument();
    expect(screen.getByText('P2')).toBeInTheDocument();
  });

  it('calls bulk endpoint on confirm import and shows results', async () => {
    const bulkMock = vi.spyOn(api.apiClient, 'bulkCreatePrinters').mockResolvedValue({ importedCount: 1, skippedCount: 0, results: [{ index: 0, name: 'P1', status: 'Imported', id: 'id-1' }] });

    renderWithProviders(<PrintersAdminPage />);
    const file = { name: 'printers.json', async text() { return JSON.stringify([{ name: 'P1', serverUrl: 'http://1' }]); } } as unknown as File;
    const input = screen.getByLabelText('Import printers JSON file') as HTMLInputElement;
    fireEvent.change(input, { target: { files: [file] } });

    await waitFor(() => screen.getByText('Import preview (1)'));

    const confirm = screen.getByText('Confirm Import');
    fireEvent.click(confirm);

    await waitFor(() => expect(bulkMock).toHaveBeenCalled());
    // If UI updated, Open link should be present
    await waitFor(() => expect(screen.getByText('Open')).toBeInTheDocument());
  });

  it('retries single failed row via Retry button', async () => {
    vi.spyOn(api.apiClient, 'bulkCreatePrinters')
      .mockResolvedValueOnce({ importedCount: 0, skippedCount: 0, results: [{ index: 0, name: 'P1', status: 'Failed', reason: 'bad' }] })
      .mockResolvedValueOnce({ importedCount: 1, skippedCount: 0, results: [{ index: 0, name: 'P1', status: 'Imported', id: 'id-1' }] });

    renderWithProviders(<PrintersAdminPage />);
  const file = { name: 'printers.json', async text() { return JSON.stringify([{ name: 'P1', serverUrl: 'http://1' }]); } } as unknown as File;
    const input = screen.getByLabelText('Import printers JSON file') as HTMLInputElement;
    fireEvent.change(input, { target: { files: [file] } });

    await waitFor(() => screen.getByText('Import preview (1)'));

    // First confirm: returns failed
    fireEvent.click(screen.getByText('Confirm Import'));
    await waitFor(() => expect(screen.getByText(/Failed:/)).toBeInTheDocument());

    // Click Retry
    fireEvent.click(screen.getByText('Retry'));
    await waitFor(() => expect(screen.getByText('Imported')).toBeInTheDocument());
  });

  it('retries all failed rows via Retry all failed', async () => {
    vi.spyOn(api.apiClient, 'bulkCreatePrinters')
      .mockResolvedValueOnce({ importedCount: 0, skippedCount: 0, results: [{ index: 0, name: 'P1', status: 'Failed', reason: 'bad' }] })
      .mockResolvedValueOnce({ importedCount: 1, skippedCount: 0, results: [{ index: 0, name: 'P1', status: 'Imported', id: 'id-1' }] });

    renderWithProviders(<PrintersAdminPage />);
  const file = { name: 'printers.json', async text() { return JSON.stringify([{ name: 'P1', serverUrl: 'http://1' }]); } } as unknown as File;
    const input = screen.getByLabelText('Import printers JSON file') as HTMLInputElement;
    fireEvent.change(input, { target: { files: [file] } });

    await waitFor(() => screen.getByText('Import preview (1)'));

    // First confirm: returns failed
    fireEvent.click(screen.getByText('Confirm Import'));
    await waitFor(() => expect(screen.getByText(/Failed:/)).toBeInTheDocument());

    // Retry all failed
    fireEvent.click(screen.getByText('Retry all failed'));
    await waitFor(() => expect(screen.getByText('Imported')).toBeInTheDocument());
  });
});

export {};

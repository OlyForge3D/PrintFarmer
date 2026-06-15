import '@testing-library/jest-dom';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { PrintablesImportModal } from '@/features/models3d/components/PrintablesImportModal';

const mockRequest = vi.fn();

vi.mock('@/services/api', () => ({
  apiClient: {
    request: (...args: unknown[]) => mockRequest(...args),
  },
}));

vi.mock('sonner', () => ({
  toast: {
    success: vi.fn(),
    error: vi.fn(),
  },
}));

function renderModal(initialUrl?: string | null) {
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: { retry: false },
      mutations: { retry: false },
    },
  });

  return render(
    <QueryClientProvider client={queryClient}>
      <PrintablesImportModal isOpen onClose={vi.fn()} initialUrl={initialUrl} />
    </QueryClientProvider>,
  );
}

describe('PrintablesImportModal', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('prefills and auto-previews initial URL from one-click import', async () => {
    mockRequest.mockResolvedValue({
      modelId: '12345',
      name: 'Voron Tool Holder',
      creator: 'maker',
      license: null,
      thumbnailUrl: null,
      sourceUrl: 'https://www.printables.com/model/12345-voron-tool-holder',
      files: [
        { id: 'file-1', name: 'holder.stl', fileSize: 1024 },
      ],
    });

    renderModal(' https://www.printables.com/model/12345-voron-tool-holder ');

    await waitFor(() => {
      expect(mockRequest).toHaveBeenCalledWith({
        method: 'GET',
        url: '/3d-models/printables/preview',
        params: { url: 'https://www.printables.com/model/12345-voron-tool-holder' },
      });
    });

    await screen.findByText('Select files to import:');

    fireEvent.click(screen.getByRole('button', { name: 'Back' }));
    expect(screen.getByRole('textbox')).toHaveValue('https://www.printables.com/model/12345-voron-tool-holder');
  });
});

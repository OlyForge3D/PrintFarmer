import { renderHook, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { usePrintablesCollections, usePrintablesUsername } from '../usePrintablesBrowser';
import { apiClient } from '@/services/api';
import { useUserSettings } from '@/features/settings/hooks/useUserSettings';

vi.mock('@/services/api', () => ({
  apiClient: {
    getPrintablesUserCollections: vi.fn(),
  },
}));

vi.mock('@/features/settings/hooks/useUserSettings', () => ({
  useUserSettings: vi.fn(),
}));

describe('usePrintablesBrowser', () => {
  let queryClient: QueryClient;

  const wrapper = ({ children }: { children: React.ReactNode }) => (
    <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
  );

  beforeEach(() => {
    queryClient = new QueryClient({
      defaultOptions: {
        queries: {
          retry: false,
        },
      },
    });
    vi.clearAllMocks();
  });

  it('preserves @handle when reading username from user settings', () => {
    vi.mocked(useUserSettings).mockReturnValue({
      data: { printablesUsername: '  @maker_jane  ' },
      isLoading: false,
      error: null,
    } as ReturnType<typeof useUserSettings>);

    const { result } = renderHook(() => usePrintablesUsername(), { wrapper });

    expect(result.current.username).toBe('@maker_jane');
  });

  it('passes @handle username as-is when requesting public collections', async () => {
    vi.mocked(apiClient.getPrintablesUserCollections).mockResolvedValue({
      items: [
        {
          id: 'col-1',
          name: 'Favorites',
          modelCount: 1,
          likesCount: 0,
          thumbnailUrls: [],
        },
      ],
      nextCursor: null,
    });

    renderHook(() => usePrintablesCollections('@maker_jane'), { wrapper });

    await waitFor(() => {
      expect(apiClient.getPrintablesUserCollections).toHaveBeenCalledWith('@maker_jane', {
        cursor: undefined,
        limit: 8,
      });
    });
  });
});

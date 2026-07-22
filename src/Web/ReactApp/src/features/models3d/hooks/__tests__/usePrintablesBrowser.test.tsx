import { renderHook, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import {
  usePrintablesCollectionModels,
  usePrintablesCollections,
  usePrintablesSearch,
  usePrintablesUsername,
} from '../usePrintablesBrowser';
import { apiClient } from '@/services/api';
import { useUserSettings } from '@/features/settings/hooks/useUserSettings';

vi.mock('@/services/api', () => ({
  apiClient: {
    getPrintablesUserCollections: vi.fn(),
    getPrintablesCollectionModels: vi.fn(),
    searchPrintablesModels: vi.fn(),
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

  it('normalizes @handle when reading username from user settings', () => {
    vi.mocked(useUserSettings).mockReturnValue({
      data: { printablesUsername: '  @maker_jane  ' },
      isLoading: false,
      error: null,
    } as ReturnType<typeof useUserSettings>);

    const { result } = renderHook(() => usePrintablesUsername(), { wrapper });

    expect(result.current.username).toBe('maker_jane');
  });

  it('passes @handle username through when requesting public collections', async () => {
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

  it('fetches collection models only after expand is enabled', async () => {
    vi.mocked(apiClient.getPrintablesCollectionModels).mockResolvedValue({
      items: [{ id: 'model-1', title: 'Clip', author: 'ripley' }],
      nextCursor: null,
      hasMore: false,
    });

    const { rerender } = renderHook(
      ({ enabled }) => usePrintablesCollectionModels('collection-1', enabled),
      { wrapper, initialProps: { enabled: false } },
    );

    expect(apiClient.getPrintablesCollectionModels).not.toHaveBeenCalled();

    rerender({ enabled: true });

    await waitFor(() => {
      expect(apiClient.getPrintablesCollectionModels).toHaveBeenCalledWith('collection-1', {
        cursor: undefined,
        limit: 24,
      });
    });
  });

  it('uses offset pagination for search and advances using offset + limit', async () => {
    vi.mocked(apiClient.searchPrintablesModels)
      .mockResolvedValueOnce({
        items: [{ id: 'result-1', title: 'Tool holder', author: 'maker' }],
        offset: 0,
        limit: 24,
        hasMore: true,
      })
      .mockResolvedValueOnce({
        items: [{ id: 'result-2', title: 'Tool caddy', author: 'maker' }],
        offset: 24,
        limit: 24,
        hasMore: false,
      });

    const { result } = renderHook(() => usePrintablesSearch('tool'), { wrapper });

    await waitFor(() => {
      expect(apiClient.searchPrintablesModels).toHaveBeenNthCalledWith(1, 'tool', {
        offset: 0,
        limit: 24,
      });
    });

    await result.current.fetchNextPage();

    await waitFor(() => {
      expect(apiClient.searchPrintablesModels).toHaveBeenNthCalledWith(2, 'tool', {
        offset: 24,
        limit: 24,
      });
    });
  });
});

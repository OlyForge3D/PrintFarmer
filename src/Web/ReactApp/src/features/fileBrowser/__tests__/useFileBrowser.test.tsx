import { describe, it, expect, vi } from 'vitest';
import { renderHook, act, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { useFileBrowser } from '../useFileBrowser';
import type { FileQueryState, UseFileBrowserConfig } from '../types';

const createWrapper = () => {
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: { retry: false },
    },
  });

  return ({ children }: { children: React.ReactNode }) => (
    <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
  );
};

describe('useFileBrowser', () => {
  const domainItem = { id: '1', path: '/file.gcode', name: 'file.gcode' };

  const mapQueryParams = (query: FileQueryState) => ({
    search: query.search,
    page: query.page,
  });

  const baseConfig: UseFileBrowserConfig<typeof domainItem> = {
    fetcher: vi.fn(async () => ({
      items: [domainItem],
      totalItems: 1,
      totalPages: 1,
      page: 1,
    })),
    mapQueryParams,
    mapDomainToFileItem: (item) => ({
      id: item.id,
      path: item.path,
      fileName: item.name,
      isDirectory: false,
    }),
    canDelete: true,
    canDownload: true,
    onDelete: vi.fn(),
    onDownload: vi.fn(),
  };

  it('loads files and maps domain items', async () => {
    const wrapper = createWrapper();
    const { result } = renderHook(() => useFileBrowser(baseConfig), { wrapper });

    await waitFor(() => {
      expect(result.current.files).toHaveLength(1);
    });

    expect(result.current.files[0].fileName).toBe('file.gcode');
  });

  it('supports selection', async () => {
    const wrapper = createWrapper();
    const { result } = renderHook(
      () => useFileBrowser(baseConfig),
      { wrapper }
    );

    await waitFor(() => {
      expect(result.current.files).toHaveLength(1);
    });

    act(() => {
      result.current.toggleSelect('1');
    });

    expect(result.current.selectedIds).toEqual(['1']);
  });
});

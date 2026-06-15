import { useInfiniteQuery, type InfiniteData } from '@tanstack/react-query';
import { apiClient } from '@/services/api';
import { useUserSettings } from '@/features/settings/hooks/useUserSettings';
import type {
  PrintablesCollectionSummary,
  PrintablesModelSummary,
  PrintablesPagedResponse,
} from '@/types/models';

const COLLECTIONS_PAGE_SIZE = 8;
const MODELS_PAGE_SIZE = 24;
const SEARCH_PAGE_SIZE = 24;
const SEARCH_DEBOUNCE_MS = 300;

function normalizePage<T>(
  page: Partial<PrintablesPagedResponse<T>> | null | undefined,
  listField: string,
): PrintablesPagedResponse<T> {
  if (!page) {
    return { items: [], nextCursor: null };
  }

  const directItems = Array.isArray(page.items) ? page.items : undefined;
  const fallbackItems = (page as Record<string, unknown>)[listField];
  const items = directItems ?? (Array.isArray(fallbackItems) ? (fallbackItems as T[]) : []);
  const nextCursor = typeof page.nextCursor === 'string' ? page.nextCursor : null;

  return { items, nextCursor };
}

export function usePrintablesUsername() {
  const { data, isLoading, error } = useUserSettings();
  const username = data?.printablesUsername?.trim() ?? '';
  return { username, isLoading, error };
}

export function usePrintablesCollections(username: string) {
  return useInfiniteQuery({
    queryKey: ['printables', 'collections', username],
    queryFn: async ({ pageParam }) => {
      const page = await apiClient.getPrintablesUserCollections(username, {
        cursor: pageParam as string | undefined,
        limit: COLLECTIONS_PAGE_SIZE,
      });
      return normalizePage<PrintablesCollectionSummary>(page, 'collections');
    },
    initialPageParam: undefined as string | undefined,
    enabled: username.length > 0,
    staleTime: 60_000,
    getNextPageParam: (lastPage) => lastPage.nextCursor ?? undefined,
  });
}

export function usePrintablesUserModels(username: string) {
  return useInfiniteQuery({
    queryKey: ['printables', 'user-models', username],
    queryFn: async ({ pageParam }) => {
      const page = await apiClient.getPrintablesUserModels(username, {
        cursor: pageParam as string | undefined,
        limit: MODELS_PAGE_SIZE,
      });
      return normalizePage<PrintablesModelSummary>(page, 'models');
    },
    initialPageParam: undefined as string | undefined,
    enabled: username.length > 0,
    staleTime: 60_000,
    getNextPageParam: (lastPage) => lastPage.nextCursor ?? undefined,
  });
}

export function usePrintablesSearch(searchQuery: string) {
  const normalizedQuery = searchQuery.trim();
  return useInfiniteQuery({
    queryKey: ['printables', 'search', normalizedQuery],
    queryFn: async ({ pageParam }) => {
      const page = await apiClient.searchPrintablesModels(normalizedQuery, {
        cursor: pageParam as string | undefined,
        limit: SEARCH_PAGE_SIZE,
      });
      return normalizePage<PrintablesModelSummary>(page, 'results');
    },
    initialPageParam: undefined as string | undefined,
    enabled: normalizedQuery.length > 0,
    staleTime: 30_000,
    getNextPageParam: (lastPage) => lastPage.nextCursor ?? undefined,
  });
}

export function flattenInfiniteItems<T>(data: InfiniteData<PrintablesPagedResponse<T>, unknown> | undefined): T[] {
  if (!data) {
    return [];
  }

  return data.pages.flatMap((page) => page.items);
}

export { SEARCH_DEBOUNCE_MS };

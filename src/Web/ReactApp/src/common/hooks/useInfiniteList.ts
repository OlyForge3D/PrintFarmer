/**
 * Hook for managing infinite scroll / pagination
 * Handles loading next pages, tracking hasMore state, etc.
 */
import { useInfiniteQuery, UseInfiniteQueryResult } from '@tanstack/react-query';

export interface PaginatedResponse<T> {
  items: T[];
  page: number;
  pageSize: number;
  totalCount: number;
  hasMore: boolean;
  nextCursor?: string | number;
}

export interface UseInfiniteListOptions {
  enabled?: boolean;
  staleTime?: number;
}

export function useInfiniteList<T>(
  queryKey: (string | number | object | undefined)[],
  fetchFn: (pageParam: number | undefined) => Promise<PaginatedResponse<T>>,
  options?: UseInfiniteListOptions
): UseInfiniteQueryResult<PaginatedResponse<T>, Error> & {
  allItems: T[];
  hasMore: boolean;
  isLoadingMore: boolean;
} {
  const { enabled = true, staleTime = 2 * 60 * 1000 } = options || {};

  const result = useInfiniteQuery({
    queryKey,
    queryFn: ({ pageParam = 1 }) => fetchFn(pageParam),
    getNextPageParam: (lastPage) => {
      if (!lastPage.hasMore) return undefined;
      return (lastPage.page || 1) + 1;
    },
    initialPageParam: 1,
    enabled,
    staleTime,
  });

  // Flatten all items from all pages
  const allItems = result.data?.pages.flatMap(page => page.items) || [];
  const hasMore = result.data?.pages[result.data.pages.length - 1]?.hasMore ?? false;
  const isLoadingMore = result.isFetchingNextPage;

  return {
    ...result,
    allItems,
    hasMore,
    isLoadingMore,
  };
}

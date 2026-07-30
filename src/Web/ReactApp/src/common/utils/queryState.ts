/**
 * Returns true once a required query has produced data.
 *
 * TanStack Query v5 reports `isLoading: false` for a paused initial fetch, so
 * `undefined` is the only reliable unresolved-data signal. Resolved `null` and
 * empty values remain valid server results.
 */
export function hasResolvedQueryData<T>(data: T | undefined): data is T {
  return data !== undefined;
}

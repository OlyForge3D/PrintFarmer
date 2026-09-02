import { useEffect, useRef, useState } from 'react';
import { apiClient } from '@/services/api';
import type { PrinterFileDto } from '@/types/api';

interface PrinterFileThumbnailsState {
  /** Maps a file's `thumbnailUrl` (proxy path) to a fetched, renderable object URL. */
  objectUrls: Record<string, string>;
  /** Maps a file's `thumbnailUrl` (proxy path) to true if the fetch failed. */
  failed: Record<string, boolean>;
}

/**
 * Maximum number of thumbnail fetches allowed in flight at once. An unbounded
 * `Promise.all` over every file in the list would fire one request per unique
 * thumbnail immediately - fine for a handful of files, but it saturates the
 * connection pool and floods the printer/API proxy for large libraries. A small
 * worker pool caps concurrency while still fetching everything passed in. See
 * issue #2393.
 */
const THUMBNAIL_FETCH_CONCURRENCY = 5;

interface ThumbnailFetchResult {
  thumbnailUrl: string;
  objectUrl: string | null;
  failed: boolean;
}

/**
 * Runs `fetchOne` over `items` with at most `concurrency` calls in flight at a time,
 * returning results in the same order as `items` (not completion order).
 */
async function runWithBoundedConcurrency<TItem, TResult>(
  items: TItem[],
  concurrency: number,
  fetchOne: (item: TItem) => Promise<TResult>
): Promise<TResult[]> {
  const results: TResult[] = new Array(items.length);
  let nextIndex = 0;

  async function worker() {
    while (true) {
      const index = nextIndex++;
      if (index >= items.length) {
        return;
      }
      results[index] = await fetchOne(items[index]);
    }
  }

  const workerCount = Math.min(concurrency, items.length);
  await Promise.all(Array.from({ length: workerCount }, () => worker()));

  return results;
}

/**
 * Fetches authenticated thumbnail blobs for a printer's file list and exposes them as
 * object URLs, keyed by each file's `thumbnailUrl` proxy path.
 *
 * A bare `<img src={file.thumbnailUrl}>` cannot work here: auth is JWT-bearer only (no
 * auth cookie), so the authenticated proxy endpoint
 * (`GET /api/printers/{id}/files/thumbnail`) must be fetched with the Authorization
 * header attached and rendered via `URL.createObjectURL`. Mirrors the same pattern
 * already used for printer history thumbnails (`useHistoryThumbnailPreview`), but
 * batches every file in the list in one effect instead of one hook call per row, since
 * this hook is driven from a single list-rendering component. See issue #1650.
 */
export function usePrinterFileThumbnails(files: PrinterFileDto[]) {
  const [state, setState] = useState<PrinterFileThumbnailsState>({
    objectUrls: {},
    failed: {},
  });

  // Persisted across effect re-runs (not reset every time `files` changes) so an incrementally
  // growing file list - e.g. PrinterFilesModal only passing in visible/near-visible rows as the
  // user scrolls, see #2393 - only fetches thumbnails that haven't been resolved yet instead of
  // re-fetching and re-revoking everything already loaded on every visibility change.
  const objectUrlsRef = useRef<Record<string, string>>({});
  const failedRef = useRef<Record<string, boolean>>({});
  // Urls with a fetch currently in flight from a not-yet-completed effect run. Consulted so an
  // overlapping later effect run (e.g. one more row scrolled into view before an earlier batch
  // finished) doesn't abort and restart requests that are still usefully in progress - it only
  // starts fetches for urls that are neither resolved, failed, nor already pending.
  const pendingRef = useRef<Set<string>>(new Set());
  // One AbortController for the whole hook lifetime rather than one per effect run: fetches
  // must survive an overlapping effect re-run (see pendingRef above) and only be aborted when
  // the component actually unmounts.
  const abortControllerRef = useRef<AbortController | null>(null);
  if (abortControllerRef.current === null) {
    abortControllerRef.current = new AbortController();
  }
  // Sidesteps the ambiguity of a per-effect-run cleanup flag (which fires both when deps change
  // *and* on true unmount): only a true unmount should discard in-flight results.
  const isMountedRef = useRef(true);

  useEffect(() => {
    const uniqueUrls = Array.from(
      new Set(
        files
          .map((file) => file.thumbnailUrl)
          .filter((url): url is string => !!url)
      )
    );
    const uniqueUrlSet = new Set(uniqueUrls);

    // Drop tracked thumbnails for urls no longer present in the current list (e.g. the file
    // list was replaced/refreshed for a different printer), revoking their object URLs -
    // matches the previous full-replace behavior for that case. Urls still pendingRef (fetch
    // in flight from an earlier run) are left alone rather than aborted - their eventual result
    // is simply pruned again on whichever later run is active once they resolve.
    let removedAny = false;
    for (const url of Object.keys(objectUrlsRef.current)) {
      if (!uniqueUrlSet.has(url)) {
        URL.revokeObjectURL(objectUrlsRef.current[url]);
        delete objectUrlsRef.current[url];
        removedAny = true;
      }
    }
    for (const url of Object.keys(failedRef.current)) {
      if (!uniqueUrlSet.has(url)) {
        delete failedRef.current[url];
        removedAny = true;
      }
    }

    // Only fetch urls that aren't already resolved, marked failed, or already in flight.
    const urlsToFetch = uniqueUrls.filter(
      (url) =>
        !(url in objectUrlsRef.current) &&
        !(url in failedRef.current) &&
        !pendingRef.current.has(url)
    );

    async function loadThumbnails() {
      if (removedAny) {
        // Still await a microtask so this setState isn't a synchronous call from the effect
        // body, matching react-hooks/set-state-in-effect.
        await Promise.resolve();
        if (!isMountedRef.current) {
          return;
        }

        setState({ objectUrls: { ...objectUrlsRef.current }, failed: { ...failedRef.current } });
      }

      if (urlsToFetch.length === 0) {
        return;
      }

      for (const url of urlsToFetch) {
        pendingRef.current.add(url);
      }

      await runWithBoundedConcurrency(
        urlsToFetch,
        THUMBNAIL_FETCH_CONCURRENCY,
        async (thumbnailUrl): Promise<ThumbnailFetchResult> => {
          let result: ThumbnailFetchResult;
          try {
            const blob = await apiClient.getPrinterFileThumbnail(
              thumbnailUrl,
              abortControllerRef.current!.signal
            );
            result = { thumbnailUrl, objectUrl: URL.createObjectURL(blob), failed: false };
          } catch {
            result = { thumbnailUrl, objectUrl: null, failed: true };
          }

          pendingRef.current.delete(thumbnailUrl);

          if (!isMountedRef.current) {
            // Unmounted while this fetch was in flight - discard, nothing left to update.
            if (result.objectUrl) {
              URL.revokeObjectURL(result.objectUrl);
            }
            return result;
          }

          if (result.objectUrl) {
            objectUrlsRef.current[thumbnailUrl] = result.objectUrl;
          } else {
            failedRef.current[thumbnailUrl] = true;
          }

          // Publish incrementally as each thumbnail resolves rather than waiting for the whole
          // worker-pool batch, so a thumbnail registered by an earlier run isn't stalled behind
          // stragglers a later, overlapping run added to the same pool.
          setState({ objectUrls: { ...objectUrlsRef.current }, failed: { ...failedRef.current } });

          return result;
        }
      );
    }

    void loadThumbnails();
  }, [files]);

  useEffect(() => {
    isMountedRef.current = true;
    return () => {
      isMountedRef.current = false;
      abortControllerRef.current?.abort();

      // Intentionally read `.current` at unmount time rather than copying it to a local
      // variable at effect-setup time: objectUrlsRef accumulates entries across every re-run of
      // the effect above, so a snapshot taken here at mount would only ever see the initial
      // (empty) value and leak every object URL created afterwards.
      // eslint-disable-next-line react-hooks/exhaustive-deps
      for (const url of Object.values(objectUrlsRef.current)) {
        URL.revokeObjectURL(url);
      }
    };
  }, []);

  return state;
}

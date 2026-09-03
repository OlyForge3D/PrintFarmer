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
 * Maximum number of thumbnail fetches allowed in flight at once, across every effect run for
 * the lifetime of one hook instance - not just within a single run. An unbounded `Promise.all`
 * over every file in the list would fire one request per unique thumbnail immediately - fine
 * for a handful of files, but it saturates the connection pool and floods the printer/API proxy
 * for large libraries. See issue #2393.
 */
const THUMBNAIL_FETCH_CONCURRENCY = 5;

/**
 * A persistent, globally-bounded task queue: `enqueue` never runs more than `concurrency` tasks
 * concurrently, no matter how many separate calls to `enqueue` contributed work, because all
 * calls share the same `active`/`queue` state. This is required (not just a fresh bounded-pool
 * per effect run) because the file list this hook is fed can grow incrementally across
 * multiple effect runs (e.g. `PrinterFilesModal` only passes visible/near-visible rows as the
 * user scrolls) - a fresh 5-worker pool per run would let concurrency drift past 5 whenever a
 * new run started before an earlier run's pool had drained.
 */
function createBoundedTaskQueue(concurrency: number) {
  const queue: Array<() => void> = [];
  let active = 0;

  function pump() {
    while (active < concurrency && queue.length > 0) {
      const runNext = queue.shift()!;
      active += 1;
      runNext();
    }
  }

  return {
    enqueue<T>(task: () => Promise<T>): Promise<T> {
      return new Promise<T>((resolve, reject) => {
        queue.push(() => {
          void task()
            .then(resolve, reject)
            .finally(() => {
              active -= 1;
              pump();
            });
        });
        pump();
      });
    },
  };
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
  // Urls with a fetch currently in flight, mapped to the AbortController that fetch is using.
  // Consulted so an overlapping later effect run (e.g. one more row scrolled into view before
  // an earlier batch finished) doesn't start a duplicate fetch for a url another run already
  // started - it only starts fetches for urls that are neither resolved, failed, nor pending.
  // Each fetch's completion handler only commits its result if it's still the entry recorded
  // here for that url (identity-checked by controller, not just by url) - this is what makes it
  // safe for a fetch to be superseded (pruned mid-flight, or torn down and reissued by a
  // React StrictMode mount replay - see the mount-tracking effect below) without a stale result
  // clobbering a newer one.
  const pendingRef = useRef<Map<string, AbortController>>(new Map());
  // One bounded task queue for the whole hook lifetime (not recreated per effect run), so
  // concurrency stays globally capped at THUMBNAIL_FETCH_CONCURRENCY even when multiple
  // overlapping effect runs each contribute urls to fetch.
  const queueRef = useRef<ReturnType<typeof createBoundedTaskQueue> | null>(null);
  if (queueRef.current === null) {
    queueRef.current = createBoundedTaskQueue(THUMBNAIL_FETCH_CONCURRENCY);
  }
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
    // matches the previous full-replace behavior for that case.
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
    // A url no longer wanted but still pending (in flight from an earlier run) is abandoned
    // here rather than left to resolve into state: abort it and drop its pendingRef entry so
    // its eventual result is recognized as superseded (see the ownership check below) instead
    // of being written in for a file that's no longer in the list.
    for (const [url, controller] of pendingRef.current) {
      if (!uniqueUrlSet.has(url)) {
        controller.abort();
        pendingRef.current.delete(url);
      }
    }

    // Only fetch urls that aren't already resolved, marked failed, or already in flight.
    const urlsToFetch = uniqueUrls.filter(
      (url) =>
        !(url in objectUrlsRef.current) &&
        !(url in failedRef.current) &&
        !pendingRef.current.has(url)
    );

    if (removedAny) {
      // Defer so this setState isn't a synchronous call from the effect body, matching
      // react-hooks/set-state-in-effect.
      void Promise.resolve().then(() => {
        if (!isMountedRef.current) {
          return;
        }
        setState({ objectUrls: { ...objectUrlsRef.current }, failed: { ...failedRef.current } });
      });
    }

    for (const thumbnailUrl of urlsToFetch) {
      const controller = new AbortController();
      pendingRef.current.set(thumbnailUrl, controller);

      void queueRef.current!.enqueue(async () => {
        let objectUrl: string | null = null;
        let failed = false;
        try {
          const blob = await apiClient.getPrinterFileThumbnail(thumbnailUrl, controller.signal);
          objectUrl = URL.createObjectURL(blob);
        } catch {
          failed = true;
        }

        // Only the run that registered this controller may commit the result - if a later
        // effect run superseded it (pruned it, or a StrictMode replay tore down and reissued
        // it), pendingRef no longer maps this url to this exact controller and the result is
        // discarded instead of clobbering whatever superseded it.
        const stillOwned = pendingRef.current.get(thumbnailUrl) === controller;
        if (stillOwned) {
          pendingRef.current.delete(thumbnailUrl);
        }

        if (!stillOwned || !isMountedRef.current) {
          if (objectUrl) {
            URL.revokeObjectURL(objectUrl);
          }
          return;
        }

        if (objectUrl) {
          objectUrlsRef.current[thumbnailUrl] = objectUrl;
        } else if (failed) {
          failedRef.current[thumbnailUrl] = true;
        }

        // Publish incrementally as each thumbnail resolves rather than waiting for every
        // enqueued fetch to finish, so one registered early isn't stalled behind stragglers a
        // later, overlapping run added to the same queue.
        setState({ objectUrls: { ...objectUrlsRef.current }, failed: { ...failedRef.current } });
      });
    }
  }, [files]);

  useEffect(() => {
    isMountedRef.current = true;
    return () => {
      isMountedRef.current = false;

      // Covers both a true unmount and a React StrictMode (development) mount replay, which
      // synchronously runs cleanup -> setup again for every effect on initial mount to prove
      // setup can undo whatever cleanup did. Aborting every still-pending fetch and clearing
      // pendingRef here means: on a true unmount there is nothing left to retry, and on a
      // replay the [files] effect's own immediately-following setup (also replayed) sees an
      // empty pendingRef and naturally re-issues fresh fetches (with fresh controllers) for
      // anything still wanted - so a fetch that was mid-flight during the replay is retried
      // instead of permanently marked failed. The stale, aborted fetch's eventual rejection is
      // still safely discarded by the ownership check above once pendingRef points at the new
      // controller (or has been cleared).
      // eslint-disable-next-line react-hooks/exhaustive-deps
      for (const controller of pendingRef.current.values()) {
        controller.abort();
      }
      pendingRef.current.clear();

      // Intentionally read `.current` at unmount/replay time rather than copying it to a local
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

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
  const objectUrlsRef = useRef<Record<string, string>>({});

  useEffect(() => {
    const uniqueUrls = Array.from(
      new Set(
        files
          .map((file) => file.thumbnailUrl)
          .filter((url): url is string => !!url)
      )
    );

    const revokeTrackedObjectUrls = () => {
      for (const url of Object.values(objectUrlsRef.current)) {
        URL.revokeObjectURL(url);
      }
      objectUrlsRef.current = {};
    };

    const abortController = new AbortController();
    let cancelled = false;

    async function loadThumbnails() {
      if (uniqueUrls.length === 0) {
        // Still await a microtask so the state reset below isn't a synchronous
        // setState call from the effect body, matching react-hooks/set-state-in-effect.
        await Promise.resolve();
        if (cancelled) {
          return;
        }

        revokeTrackedObjectUrls();
        setState({ objectUrls: {}, failed: {} });
        return;
      }

      const results = await runWithBoundedConcurrency(
        uniqueUrls,
        THUMBNAIL_FETCH_CONCURRENCY,
        async (thumbnailUrl): Promise<ThumbnailFetchResult> => {
          try {
            const blob = await apiClient.getPrinterFileThumbnail(
              thumbnailUrl,
              abortController.signal
            );
            return { thumbnailUrl, objectUrl: URL.createObjectURL(blob), failed: false };
          } catch {
            return { thumbnailUrl, objectUrl: null, failed: true };
          }
        }
      );

      if (cancelled) {
        for (const result of results) {
          if (result.objectUrl) {
            URL.revokeObjectURL(result.objectUrl);
          }
        }
        return;
      }

      revokeTrackedObjectUrls();

      const nextObjectUrls: Record<string, string> = {};
      const nextFailed: Record<string, boolean> = {};
      for (const result of results) {
        if (result.objectUrl) {
          nextObjectUrls[result.thumbnailUrl] = result.objectUrl;
        } else {
          nextFailed[result.thumbnailUrl] = true;
        }
      }

      objectUrlsRef.current = nextObjectUrls;
      setState({ objectUrls: nextObjectUrls, failed: nextFailed });
    }

    void loadThumbnails();

    return () => {
      cancelled = true;
      abortController.abort();
    };
  }, [files]);

  useEffect(() => {
    return () => {
      for (const url of Object.values(objectUrlsRef.current)) {
        URL.revokeObjectURL(url);
      }
    };
  }, []);

  return state;
}

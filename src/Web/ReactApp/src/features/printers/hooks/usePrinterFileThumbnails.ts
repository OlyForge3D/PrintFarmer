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

      const results = await Promise.all(
        uniqueUrls.map(async (thumbnailUrl) => {
          try {
            const blob = await apiClient.getPrinterFileThumbnail(
              thumbnailUrl,
              abortController.signal
            );
            return { thumbnailUrl, objectUrl: URL.createObjectURL(blob), failed: false };
          } catch {
            return { thumbnailUrl, objectUrl: null as string | null, failed: true };
          }
        })
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

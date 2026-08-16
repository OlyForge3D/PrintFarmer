import { useCallback, useEffect, useRef, useState } from 'react';
import { apiClient } from '@/services/api';

interface HistoryThumbnailState {
  key: string;
  src: string | null;
  failed: boolean;
}

export function useHistoryThumbnailPreview(
  printerId: string,
  jobId: string,
  enabled: boolean
) {
  const key = `${printerId}:${jobId}`;
  const objectUrlRef = useRef<string | null>(null);
  const [state, setState] = useState<HistoryThumbnailState>({
    key: '',
    src: null,
    failed: false,
  });

  useEffect(() => {
    const revokeCurrentObjectUrl = () => {
      if (!objectUrlRef.current) {
        return;
      }

      URL.revokeObjectURL(objectUrlRef.current);
      objectUrlRef.current = null;
    };

    if (!enabled) {
      revokeCurrentObjectUrl();
      return;
    }

    const abortController = new AbortController();
    let cancelled = false;

    async function loadThumbnail() {
      try {
        const blob = await apiClient.getPrinterHistoryThumbnail(
          printerId,
          jobId,
          abortController.signal
        );
        const nextObjectUrl = URL.createObjectURL(blob);
        if (cancelled) {
          URL.revokeObjectURL(nextObjectUrl);
          return;
        }

        revokeCurrentObjectUrl();
        objectUrlRef.current = nextObjectUrl;
        setState({ key, src: nextObjectUrl, failed: false });
      } catch {
        if (!cancelled) {
          revokeCurrentObjectUrl();
          setState({ key, src: null, failed: true });
        }
      }
    }

    void loadThumbnail();

    return () => {
      cancelled = true;
      abortController.abort();
    };
  }, [enabled, jobId, key, printerId]);

  useEffect(() => {
    return () => {
      if (objectUrlRef.current) {
        URL.revokeObjectURL(objectUrlRef.current);
      }
    };
  }, []);

  const handleThumbnailError = useCallback(() => {
    if (objectUrlRef.current) {
      URL.revokeObjectURL(objectUrlRef.current);
      objectUrlRef.current = null;
    }

    setState({ key, src: null, failed: true });
  }, [key]);

  return {
    thumbnailSrc: enabled && state.key === key ? state.src : null,
    thumbnailFailed: enabled && state.key === key ? state.failed : false,
    handleThumbnailError,
  };
}

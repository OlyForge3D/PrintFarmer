import { useState, useEffect, useRef, useCallback, useMemo } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { getApiBaseUrl, getAuthHeaders } from '@/common/utils/apiUrlHelpers';
import type { MonitoringMetricsSummaryDto } from '@/types/api';

const REFRESH_INTERVAL_MS = 30_000;

type PartialMetrics = Partial<MonitoringMetricsSummaryDto>;

interface StreamingMetricsResult {
  /** Progressively populated metrics object */
  metrics: PartialMetrics;
  /** Set of metric keys that have been received so far */
  loadedKeys: Set<string>;
  /** True once all metrics have been received for the current cycle */
  isComplete: boolean;
}

/**
 * Consumes the SSE `/api/monitoring/metrics/stream` endpoint and
 * progressively fills in metric values as each Prometheus query resolves.
 *
 * On completion, the full result is written to the TanStack Query cache
 * under `['monitoring-metrics-summary']` so other consumers (e.g. GrafanaEmbedPanels)
 * can read it without a separate fetch.
 */
export function useStreamingMetrics(): StreamingMetricsResult {
  const queryClient = useQueryClient();

  // Seed initial state from TanStack cache so there's no blank gap after remount
  const cachedSeed = useMemo(() => {
    const cached = queryClient.getQueryData<PartialMetrics>(['monitoring-metrics-summary']);
    return cached ?? {};
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const [metrics, setMetrics] = useState<PartialMetrics>(cachedSeed);
  const [loadedKeys, setLoadedKeys] = useState<Set<string>>(
    () => new Set(Object.keys(cachedSeed).filter(k => (cachedSeed as Record<string, unknown>)[k] != null)),
  );
  const [isComplete, setIsComplete] = useState(false);
  const abortRef = useRef<AbortController | null>(null);
  const accumulatedRef = useRef<Record<string, unknown>>({ ...cachedSeed });

  const fetchStream = useCallback(async () => {
    abortRef.current?.abort();
    const controller = new AbortController();
    abortRef.current = controller;

    setIsComplete(false);

    try {
      const baseUrl = getApiBaseUrl();
      const response = await fetch(`${baseUrl}/monitoring/metrics/stream`, {
        headers: getAuthHeaders() as Record<string, string>,
        signal: controller.signal,
      });

      if (!response.ok || !response.body) return;

      const reader = response.body.getReader();
      const decoder = new TextDecoder();
      let buffer = '';

      while (true) {
        const { done, value } = await reader.read();
        if (done) break;

        buffer += decoder.decode(value, { stream: true });
        const lines = buffer.split('\n');
        buffer = lines.pop() ?? '';

        const newEntries: Record<string, unknown> = {};
        const newKeys: string[] = [];

        for (const line of lines) {
          if (line.startsWith('data: ')) {
            try {
              const data = JSON.parse(line.slice(6)) as { key?: string; value?: unknown };
              if (data.key) {
                newEntries[data.key] = data.value;
                newKeys.push(data.key);
              }
            } catch {
              /* ignore malformed SSE data */
            }
          }
        }

        if (newKeys.length > 0) {
          Object.assign(accumulatedRef.current, newEntries);
          setMetrics({ ...accumulatedRef.current } as PartialMetrics);
          setLoadedKeys(prev => {
            const next = new Set(prev);
            for (const k of newKeys) next.add(k);
            return next;
          });
        }
      }

      setIsComplete(true);
      queryClient.setQueryData(['monitoring-metrics-summary'], {
        ...accumulatedRef.current,
        timestamp: new Date().toISOString(),
      });
    } catch (e) {
      if ((e as Error).name !== 'AbortError') {
        // Stream failed — GrafanaEmbedPanels' own useQuery acts as fallback
      }
    }
  }, [queryClient]);

  useEffect(() => {
    fetchStream();
    const id = window.setInterval(fetchStream, REFRESH_INTERVAL_MS);
    return () => {
      abortRef.current?.abort();
      clearInterval(id);
    };
  }, [fetchStream]);

  return { metrics, loadedKeys, isComplete };
}

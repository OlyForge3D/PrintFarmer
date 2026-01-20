import { useEffect, useRef, useState } from 'react';
import { DetailedHealthStatus } from '@/types/api';
import { apiClient } from '@/services/api';

interface HealthState {
  loading: boolean;
  error: string | null;
  data: DetailedHealthStatus | null;
  lastUpdated: number | null;
}

export function useDetailedHealth(pollMs: number = 15000, maxBackoffMs: number = 120000) {
  const [state, setState] = useState<HealthState>({ loading: true, error: null, data: null, lastUpdated: null });
  const backoffRef = useRef<number>(pollMs);
  const timerRef = useRef<number | null>(null);

  useEffect(() => {
    let aborted = false;

    const fetchHealth = async () => {
      try {
        const resp = await fetch('/health');
        if (!resp.ok) throw new Error(`HTTP ${resp.status}`);
        const json = await resp.json();
        const detailed: DetailedHealthStatus = {
          kind: 'detailed',
          status: json.status,
            totalChecksDuration: json.totalChecksDuration || json.totalChecksDuration?.toString?.() || '',
          results: json.results || {}
        };
        if (!aborted) {
          setState({ loading: false, error: null, data: detailed, lastUpdated: Date.now() });
          backoffRef.current = pollMs; // reset backoff on success
        }
      } catch (err) {
        if (!aborted) {
          setState(prev => ({ ...prev, loading: false, error: err instanceof Error ? err.message : 'Unknown error' }));
          backoffRef.current = Math.min(backoffRef.current * 2, maxBackoffMs);
        }
      } finally {
        if (!aborted) {
          timerRef.current = window.setTimeout(fetchHealth, backoffRef.current) as unknown as number;
        }
      }
    };

    fetchHealth();
    return () => {
      aborted = true;
      if (timerRef.current) window.clearTimeout(timerRef.current);
    };
  }, [pollMs, maxBackoffMs]);

  return state;
}

export interface DiagnosticsSummary {
  spoolman: { configured: boolean; baseUrl?: string | null };
  discovery: { ranges: string[]; ports: number[]; timeoutMs: number; maxConcurrentScans: number };
}

export function useDiagnosticsSummary(pollMs: number = 60000) {
  const [data, setData] = useState<DiagnosticsSummary | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let aborted = false;
    let timer: number | null = null;
    const run = async () => {
      try {
        const resp = await apiClient.getDiagnosticsSummary();
        const httpResp = (resp as unknown as Response);
        if (!httpResp.ok) throw new Error(`HTTP ${httpResp.status}`);
        const json = (await httpResp.json()) as unknown as Record<string, unknown>;
        if (!aborted) {
          setData((json as unknown as DiagnosticsSummary));
          setError(null);
        }
      } catch (e) {
        if (!aborted) setError(e instanceof Error ? e.message : 'Unknown error');
      } finally {
        if (!aborted) timer = window.setTimeout(run, pollMs) as unknown as number;
      }
    };
    run();
    return () => { aborted = true; if (timer) window.clearTimeout(timer); };
  }, [pollMs]);

  return { data, error };
}
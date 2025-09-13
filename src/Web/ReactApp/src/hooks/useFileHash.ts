import { apiClient } from '@/services/api';
import { useQuery, useQueryClient } from '@tanstack/react-query';

/**
 * React Query hook to fetch & cache a file hash for a virtual G-code path.
 * Cached by (path, algorithm). Default algorithm sha256.
 */
export function useFileHash(path: string | undefined, algorithm: 'sha256' | 'sha1' = 'sha256') {
  const enabled = !!path && (path.endsWith('.gcode') || path.endsWith('.bgcode'));
  const storageKey = 'pf.hashCache';
  let initialData: { fileName: string; size: number; algorithm: string; hash: string } | undefined;
  let initialDataUpdatedAt: number | undefined;
  if (enabled) {
    try {
      const raw = localStorage.getItem(storageKey);
      if (raw) {
        const map = JSON.parse(raw) as Record<string, { d: unknown; t: number }>;
        const k = `${algorithm}:${path}`;
        const entry = map[k];
        if (entry && entry.d && typeof (entry.d as any).hash === 'string') {
          initialData = entry.d as { fileName: string; size: number; algorithm: string; hash: string };
          initialDataUpdatedAt = entry.t;
        }
      }
    } catch { /* ignore */ }
  }
  return useQuery<{ fileName: string; size: number; algorithm: string; hash: string; }>({
    queryKey: ['gcode-file-hash', path, algorithm],
    queryFn: async () => {
      const resp = await apiClient.getGcodeFileHash(path!, algorithm);
      // Persist
      try {
        const raw = localStorage.getItem(storageKey);
        const map = raw ? (JSON.parse(raw) as Record<string, { d: any; t: number }>) : {};
        map[`${algorithm}:${path}`] = { d: resp, t: Date.now() };
        // Optional: cap size to 500 entries
        const entries = Object.entries(map);
        if (entries.length > 500) {
          entries.sort((a, b) => a[1].t - b[1].t); // oldest first
          const trimmed = entries.slice(entries.length - 500);
          const newMap: Record<string, { d: any; t: number }> = {};
          for (const [k, v] of trimmed) newMap[k] = v;
          localStorage.setItem(storageKey, JSON.stringify(newMap));
        } else {
          localStorage.setItem(storageKey, JSON.stringify(map));
        }
      } catch { /* ignore persistence errors */ }
      return resp;
    },
    enabled,
    staleTime: 1000 * 60 * 60, // 1 hour
    retry: 1,
    initialData,
    initialDataUpdatedAt
  });
}

/** Prefetch helper for bulk duplicate detection */
export async function prefetchFileHash(queryClient: ReturnType<typeof useQueryClient>, path: string, algorithm: 'sha256' | 'sha1' = 'sha256') {
  if (!path) return;
  const key = ['gcode-file-hash', path, algorithm];
  if (queryClient.getQueryData(key)) return;
  await queryClient.prefetchQuery({ queryKey: key, queryFn: () => apiClient.getGcodeFileHash(path, algorithm) });
}

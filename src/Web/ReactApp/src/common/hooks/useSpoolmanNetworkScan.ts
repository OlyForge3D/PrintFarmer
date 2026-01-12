import { useState } from 'react';
import { apiClient } from '@/services/api';
import type { SpoolmanDiscoveryResult } from '@/types/api';

export function useSpoolmanNetworkScan() {
  const [isScanning, setIsScanning] = useState(false);
  const [results, setResults] = useState<SpoolmanDiscoveryResult[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [lastMessage, setLastMessage] = useState<string | null>(null);

  const scanNetwork = async () => {
    if (isScanning) return;
    
    setIsScanning(true);
    setError(null);
    setResults([]);

    try {
      if (window.PrintFarmerDebug?.spoolman) { console.debug('[useSpoolmanNetworkScan] starting scanNetwork'); }
      const discoveredInstances = await apiClient.scanNetworkForSpoolman();
      if (window.PrintFarmerDebug?.spoolman) { console.debug('[useSpoolmanNetworkScan] scanNetwork result:', discoveredInstances); }
      setResults(discoveredInstances);
      if (!discoveredInstances || discoveredInstances.length === 0) {
        const msg = 'No Spoolman instances found on the configured network ranges';
        setError(msg);
        setLastMessage(msg);
      } else {
        const msg = `Found ${discoveredInstances.length} address(es), ${discoveredInstances.filter(d => d.isAvailable).length} available`;
        setLastMessage(msg);
      }
    } catch (err) {
      const errorMessage = err instanceof Error ? err.message : 'Network scan failed';
      // Try to extract structured details from API error objects (ApiClient transforms axios errors)
  try {
        const unknownErr = err as unknown;
        if (typeof unknownErr === 'object' && unknownErr !== null && 'details' in unknownErr) {
          // eslint-disable-next-line @typescript-eslint/no-explicit-any
          const details = (unknownErr as any).details;
          const detailsStr = typeof details === 'string' ? details : JSON.stringify(details);
          const composed = `${errorMessage}: ${detailsStr}`;
          setError(composed);
          setLastMessage(composed);
          console.error('[useSpoolmanNetworkScan] scan error details:', details);
        } else {
          setError(errorMessage);
          setLastMessage(errorMessage);
          console.error('[useSpoolmanNetworkScan] scan error:', unknownErr);
        }
      } catch (ex) {
        setError(errorMessage);
        setLastMessage(errorMessage);
        console.error('[useSpoolmanNetworkScan] error while processing caught error:', ex);
      }
      setResults([]);
    } finally {
      setIsScanning(false);
    }
  };

  const reset = () => {
    setResults([]);
    setError(null);
    setIsScanning(false);
  };

  return {
    isScanning,
    results,
    error,
    scanNetwork,
    reset,
    hasResults: results.length > 0,
    availableInstances: results.filter(r => r.isAvailable),
    lastMessage
  };
}
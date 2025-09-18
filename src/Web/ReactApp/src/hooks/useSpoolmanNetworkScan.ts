import { useState } from 'react';
import { apiClient } from '@/services/api';
import type { SpoolmanDiscoveryResult } from '@/types/api';

export function useSpoolmanNetworkScan() {
  const [isScanning, setIsScanning] = useState(false);
  const [results, setResults] = useState<SpoolmanDiscoveryResult[]>([]);
  const [error, setError] = useState<string | null>(null);

  const scanNetwork = async () => {
    if (isScanning) return;
    
    setIsScanning(true);
    setError(null);
    setResults([]);

    try {
      const discoveredInstances = await apiClient.scanNetworkForSpoolman();
      setResults(discoveredInstances);
      
      if (discoveredInstances.length === 0) {
        setError('No Spoolman instances found on the configured network ranges');
      }
    } catch (err) {
      const errorMessage = err instanceof Error ? err.message : 'Network scan failed';
      setError(errorMessage);
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
    availableInstances: results.filter(r => r.isAvailable)
  };
}
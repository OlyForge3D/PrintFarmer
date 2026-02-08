import { useEffect, useRef, useState, useCallback } from 'react';
import { flushSync } from 'react-dom';
import { HubConnectionState } from '@microsoft/signalr';
import { signalRService as harvestSignalRService } from '@/services/harvest-signalr';
import { printerSignalRService } from '@/services/printer-signalr';
import { PrinterStatusUpdate, HarvestUpdateDto, JobQueueUpdateDto } from '@/types/api';

// ============ Connection Hook ============

export function useSignalRConnection(service: 'harvest' | 'printer' = 'harvest') {
  // Choose which SignalR service to observe. Default preserves existing behavior (harvest).
  const svc = service === 'printer' ? printerSignalRService : harvestSignalRService;

  const [connectionState, setConnectionState] = useState<HubConnectionState>(
    svc.connectionState
  );
  const [connectionId, setConnectionId] = useState<string | null>(
    svc.connectionId
  );

  useEffect(() => {
    // Connect on mount for the selected service
    svc.connect();

    // Subscribe to connection state changes - callback is async so it's allowed
    const unsubscribe = svc.onConnectionStateChange(() => {
      setConnectionState(svc.connectionState);
      setConnectionId(svc.connectionId);
    });

    return () => {
      unsubscribe();
    };
  }, [service, svc]);

  return {
    connectionState,
    connectionId,
    isConnected: connectionState === HubConnectionState.Connected,
    isConnecting: connectionState === HubConnectionState.Connecting,
    isDisconnected: connectionState === HubConnectionState.Disconnected,
    isReconnecting: connectionState === HubConnectionState.Reconnecting,
  };
}

// ============ Printer Status Hook ============

export function usePrinterStatusUpdates(
  onUpdate?: (status: PrinterStatusUpdate) => void,
  printerIds?: string[]
) {
  const [latestUpdate, setLatestUpdate] = useState<PrinterStatusUpdate | null>(null);
  const [printerStatuses, setPrinterStatuses] = useState<Map<string, PrinterStatusUpdate>>(() => {
    try {
      const cached = printerSignalRService.getLastStatuses();
      if (!cached?.size) return new Map();
      if (!printerIds?.length) return cached;
      return new Map(Array.from(cached.entries()).filter(([id]) => printerIds!.includes(id)));
    } catch (e) {
      console.error('[usePrinterStatusUpdates] Failed to initialize from cache:', e);
      return new Map();
    }
  });
  const onUpdateRef = useRef(onUpdate);
  const printerIdsRef = useRef(printerIds);

  // Update refs when props change
  useEffect(() => {
    onUpdateRef.current = onUpdate;
  }, [onUpdate]);
  useEffect(() => {
    printerIdsRef.current = printerIds;
  }, [printerIds]);

  useEffect(() => {
    printerSignalRService.connect();

    // Seed from any cached statuses (helps components that mount later).
    try {
      const cached = printerSignalRService.getLastStatuses();
      if (cached.size > 0) {
        setPrinterStatuses(() => {
          if (printerIdsRef.current && printerIdsRef.current.length > 0) {
            return new Map(Array.from(cached.entries()).filter(([id]) => printerIdsRef.current!.includes(id)));
          }
          return cached;
        });
      }
    } catch {
      // ignore cache seed failures
    }

    const handleStatusUpdate = (status: PrinterStatusUpdate) => {
      try {
        // Expose debug info for live inspection in browser console (guarded)
          const win = window as unknown as { PrintFarmerDebug?: Record<string, unknown> };
          if (!win.PrintFarmerDebug) win.PrintFarmerDebug = {};
          (win.PrintFarmerDebug as Record<string, unknown>).lastPrinterUpdate = status as unknown as Record<string, unknown>;
        if (win.PrintFarmerDebug?.usePrinterStatusUpdates) {
          console.debug('[usePrinterStatusUpdates] received status update', status.id, status.state, status.isOnline, status.hotendTemp, status.bedTemp);
        }
      } catch {
        // Swallow debug failures
      }
      if (printerIdsRef.current && !printerIdsRef.current.includes(status.id)) return;
      
      // Update state normally - React will batch these appropriately
      // Note: Printer status updates are less frequent than discovery progress,
      // so batching is acceptable here
      setLatestUpdate(status);
      setPrinterStatuses(prev => new Map(prev.set(status.id, status)));
      onUpdateRef.current?.(status);
    };
    const unsubscribe = printerSignalRService.onPrinterStatusUpdate(handleStatusUpdate);
    return unsubscribe;
  }, []);

  const getPrinterStatus = useCallback((printerId: string): PrinterStatusUpdate | undefined => {
    return printerStatuses.get(printerId);
  }, [printerStatuses]);
  const clearPrinterStatus = useCallback((printerId: string) => {
    setPrinterStatuses(prev => {
      const newMap = new Map(prev);
      newMap.delete(printerId);
      return newMap;
    });
  }, []);
  const clearAllStatuses = useCallback(() => {
    setPrinterStatuses(new Map());
    setLatestUpdate(null);
  }, []);
  return {
    latestUpdate,
    printerStatuses,
    getPrinterStatus,
    clearPrinterStatus,
    clearAllStatuses,
  };
}

// ============ Harvest Updates Hook ============

export function useHarvestUpdates(
  onUpdate?: (operationId: string, status: HarvestUpdateDto) => void,
  operationIds?: string[]
) {
  const [latestUpdate, setLatestUpdate] = useState<{ operationId: string; status: HarvestUpdateDto } | null>(null);
  const [harvestStatuses, setHarvestStatuses] = useState<Map<string, HarvestUpdateDto>>(new Map());

  const onUpdateRef = useRef(onUpdate);
  const operationIdsRef = useRef(operationIds);

  useEffect(() => {
    onUpdateRef.current = onUpdate;
  }, [onUpdate]);

  useEffect(() => {
    operationIdsRef.current = operationIds;
  }, [operationIds]);

  useEffect(() => {
  const handleHarvestUpdate = (operationId: string, status: HarvestUpdateDto) => {
      if (operationIdsRef.current && !operationIdsRef.current.includes(operationId)) {
        return;
      }

      setLatestUpdate({ operationId, status });
      setHarvestStatuses(prev => new Map(prev.set(operationId, status)));
      onUpdateRef.current?.(operationId, status);
    };

  const unsubscribe = harvestSignalRService.onHarvestUpdate(handleHarvestUpdate);

    return unsubscribe;
  }, []);

  const getHarvestStatus = useCallback((operationId: string) => {
    return harvestStatuses.get(operationId);
  }, [harvestStatuses]);

  return {
    latestUpdate,
    harvestStatuses,
    getHarvestStatus,
  };
}

// ============ Job Queue Updates Hook ============

export function useJobQueueUpdates(onUpdate?: (update: JobQueueUpdateDto) => void) {
  const [latestUpdate, setLatestUpdate] = useState<JobQueueUpdateDto | null>(null);

  const onUpdateRef = useRef(onUpdate);

  useEffect(() => {
    onUpdateRef.current = onUpdate;
  }, [onUpdate]);

  useEffect(() => {
  const handleJobQueueUpdate = (update: JobQueueUpdateDto) => {
      setLatestUpdate(update);
      onUpdateRef.current?.(update);
    };

  const unsubscribe = harvestSignalRService.onJobQueueUpdate(handleJobQueueUpdate);

    return unsubscribe;
  }, []);

  return {
    latestUpdate,
  };
}

// ============ Printer Group Management Hook ============

export function usePrinterGroup(printerId: string | null, autoJoin = true) {
  const [isInGroup, setIsInGroup] = useState(false);
  const printerIdRef = useRef(printerId);

  useEffect(() => {
    printerIdRef.current = printerId;
  }, [printerId]);

  const joinGroup = useCallback(async (id?: string) => {
    const targetId = id || printerIdRef.current;
    if (!targetId) return;

    try {
  await harvestSignalRService.joinPrinterGroup(targetId);
      setIsInGroup(true);
    } catch (error) {
      console.error('Failed to join printer group:', error);
    }
  }, []);

  const leaveGroup = useCallback(async (id?: string) => {
    const targetId = id || printerIdRef.current;
    if (!targetId) return;

    try {
  await harvestSignalRService.leavePrinterGroup(targetId);
      setIsInGroup(false);
    } catch (error) {
      console.error('Failed to leave printer group:', error);
    }
  }, []);

  const requestStatus = useCallback(async (id?: string) => {
    const targetId = id || printerIdRef.current;
    if (!targetId) return;

    try {
  await harvestSignalRService.requestPrinterStatus(targetId);
    } catch (error) {
      console.error('Failed to request printer status:', error);
    }
  }, []);

  // Auto-join/leave when printerId changes
  useEffect(() => {
    if (!autoJoin || !printerId) return;

    const handleJoin = async () => {
      await joinGroup(printerId);
    };

    // Only join when connected
  if (harvestSignalRService.isConnected) {
      handleJoin();
    }

    return () => {
      if (printerId) {
        leaveGroup(printerId);
      }
    };
  }, [printerId, autoJoin, joinGroup, leaveGroup]);

  return {
    isInGroup,
    joinGroup,
    leaveGroup,
    requestStatus,
  };
}

// ============ Comprehensive SignalR Hook ============

export function useSignalR(options: {
  printerIds?: string[];
  harvestOperationIds?: string[];
  autoConnect?: boolean;
  onPrinterUpdate?: (status: PrinterStatusUpdate) => void;
  onHarvestUpdate?: (operationId: string, status: HarvestUpdateDto) => void;
  onJobQueueUpdate?: (update: JobQueueUpdateDto) => void;
} = {}) {
  const {
    printerIds,
    harvestOperationIds,
    // autoConnect intentionally unused currently; reserved for future manual connect toggle
    onPrinterUpdate,
    onHarvestUpdate,
    onJobQueueUpdate,
  } = options;

  const connection = useSignalRConnection();
  const printerUpdates = usePrinterStatusUpdates(onPrinterUpdate, printerIds);
  const harvestUpdates = useHarvestUpdates(onHarvestUpdate, harvestOperationIds);
  const jobQueueUpdates = useJobQueueUpdates(onJobQueueUpdate);

  return {
    connection,
    printerUpdates,
    harvestUpdates,
    jobQueueUpdates,
  };
}

// ============ Discovery Hooks ============

export function useDiscoveryProgress(
  sessionId?: string,
  onProgress?: (progress: import('@/types/api').DiscoveryProgressDto) => void
) {
  // State is reset by parent remounting with key prop when sessionId changes
  const [progress, setProgress] = useState<import('@/types/api').DiscoveryProgressDto | null>(null);

  useEffect(() => {
    if (!sessionId) return;

    const unsubscribe = printerSignalRService.onDiscoveryProgress?.((progressUpdate) => {
      if (progressUpdate.sessionId === sessionId) {
        // Force synchronous render to bypass React batching for smooth progress updates
        flushSync(() => {
          setProgress(progressUpdate);
        });
        onProgress?.(progressUpdate);
      }
    });
    return unsubscribe;
  }, [sessionId, onProgress]);

  return { progress };
}

export function useDiscoveryPrinterFound(
  sessionId?: string,
  onPrinterFound?: (found: import('@/types/api').DiscoveryPrinterFoundDto) => void
) {
  // State is reset by parent remounting with key prop when sessionId changes
  const [foundPrinters, setFoundPrinters] = useState<import('@/types/api').DiscoveredPrinterDto[]>([]);

  useEffect(() => {
    if (!sessionId) return;

    const unsubscribe = printerSignalRService.onDiscoveryPrinterFound?.((found) => {
      if (found.sessionId === sessionId) {
        setFoundPrinters(prev => [...prev, found.printer]);
        onPrinterFound?.(found);
      }
    });
    return unsubscribe;
  }, [sessionId, onPrinterFound]);

  return { foundPrinters, setFoundPrinters };
}

export function useDiscoveryCompleted(
  sessionId?: string,
  onCompleted?: (completed: import('@/types/api').DiscoveryCompletedDto) => void
) {
  // State is reset by parent remounting with key prop when sessionId changes
  const [completed, setCompleted] = useState<import('@/types/api').DiscoveryCompletedDto | null>(null);

  useEffect(() => {
    if (!sessionId) return;

    const unsubscribe = printerSignalRService.onDiscoveryCompleted?.((completedUpdate) => {
      if (completedUpdate.sessionId === sessionId) {
        setCompleted(completedUpdate);
        onCompleted?.(completedUpdate);
      }
    });
    return unsubscribe;
  }, [sessionId, onCompleted]);

  return { completed, setCompleted };
}

export function useDiscoveryStream(sessionId?: string) {
  // Ensure we have a connection state to react to (will trigger connect on mount)

  // Use printerSignalRService connection state for discovery
  const [connectionState, setConnectionState] = useState(printerSignalRService.connectionState);
  const [isConnected, setIsConnected] = useState(printerSignalRService.isConnected);
  useEffect(() => {
    printerSignalRService.connect();
    const unsubscribe = printerSignalRService.onConnectionStateChange((connected) => {
      setConnectionState(printerSignalRService.connectionState);
      setIsConnected(connected);
    });
    return unsubscribe;
  }, []);

  const { progress } = useDiscoveryProgress(sessionId);
  const { foundPrinters, setFoundPrinters } = useDiscoveryPrinterFound(sessionId);
  const { completed, setCompleted } = useDiscoveryCompleted(sessionId);

  useEffect(() => {
    if (!sessionId) return;
    if (!isConnected) {
      if (window.PrintFarmerDebug?.discovery) { console.log('[Discovery] Waiting for SignalR connection before joining group', { sessionId, isConnected }); }
      return;
    }
    let cancelled = false;
    if (window.PrintFarmerDebug?.discovery) { console.log('[Discovery] Attempting to join SignalR discovery group', { sessionId, isConnected }); }
    (async () => {
      try {
        await printerSignalRService.joinDiscoveryGroup?.(sessionId);
        if (window.PrintFarmerDebug?.discovery) { console.log('[Discovery] Successfully joined SignalR discovery group', { sessionId }); }
      } catch (err) {
        if (!cancelled) {
          console.warn('[Discovery] Failed to join discovery group, will retry on next connection state change', err);
        }
      }
    })();
    return () => {
      cancelled = true;
      if (isConnected) {
        if (window.PrintFarmerDebug?.discovery) { console.log('[Discovery] Leaving SignalR discovery group', { sessionId }); }
        printerSignalRService.leaveDiscoveryGroup?.(sessionId);
      }
    };
  }, [sessionId, isConnected, connectionState]);

  // Reset all discovery state when starting a new session
  const resetDiscovery = useCallback(() => {
    setFoundPrinters([]);
    setCompleted(null);
  }, [setFoundPrinters, setCompleted]);

  return {
    progress,
    foundPrinters,
    completed,
    resetDiscovery,
    isActive: progress && !completed,
    isCompleted: !!completed,
  };
}
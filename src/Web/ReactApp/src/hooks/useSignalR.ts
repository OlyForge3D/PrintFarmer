import { useEffect, useRef, useState, useCallback } from 'react';
import { HubConnectionState } from '@microsoft/signalr';
import { signalRService } from '@/services/signalr';
import { PrinterStatusUpdate, HarvestUpdateDto, JobQueueUpdateDto } from '@/types/api';

// ============ Connection Hook ============

export function useSignalRConnection() {
  const [connectionState, setConnectionState] = useState<HubConnectionState>(
    signalRService.connectionState
  );
  const [connectionId, setConnectionId] = useState<string | null>(
    signalRService.connectionId
  );

  useEffect(() => {
    // Connect on mount
    signalRService.connect();

    // Subscribe to connection state changes
    const unsubscribe = signalRService.onConnectionStateChange(() => {
      setConnectionState(signalRService.connectionState);
      setConnectionId(signalRService.connectionId);
    });

    // Update initial state
    setConnectionState(signalRService.connectionState);
    setConnectionId(signalRService.connectionId);

    return () => {
      unsubscribe();
    };
  }, []);

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
  printerIds?: string[] // Optional: only listen to specific printers
) {
  const [latestUpdate, setLatestUpdate] = useState<PrinterStatusUpdate | null>(null);
  const [printerStatuses, setPrinterStatuses] = useState<Map<string, PrinterStatusUpdate>>(
    new Map()
  );

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
    const handleStatusUpdate = (status: PrinterStatusUpdate) => {
      // Filter by printer IDs if specified
      if (printerIdsRef.current && !printerIdsRef.current.includes(status.id)) {
        return;
      }

      setLatestUpdate(status);
      setPrinterStatuses(prev => new Map(prev.set(status.id, status)));
      onUpdateRef.current?.(status);
    };

    const unsubscribe = signalRService.onPrinterStatusUpdate(handleStatusUpdate);

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

    const unsubscribe = signalRService.onHarvestUpdate(handleHarvestUpdate);

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

    const unsubscribe = signalRService.onJobQueueUpdate(handleJobQueueUpdate);

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
      await signalRService.joinPrinterGroup(targetId);
      setIsInGroup(true);
    } catch (error) {
      console.error('Failed to join printer group:', error);
    }
  }, []);

  const leaveGroup = useCallback(async (id?: string) => {
    const targetId = id || printerIdRef.current;
    if (!targetId) return;

    try {
      await signalRService.leavePrinterGroup(targetId);
      setIsInGroup(false);
    } catch (error) {
      console.error('Failed to leave printer group:', error);
    }
  }, []);

  const requestStatus = useCallback(async (id?: string) => {
    const targetId = id || printerIdRef.current;
    if (!targetId) return;

    try {
      await signalRService.requestPrinterStatus(targetId);
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
    if (signalRService.isConnected) {
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
  const [progress, setProgress] = useState<import('@/types/api').DiscoveryProgressDto | null>(null);

  useEffect(() => {
    if (!sessionId) {
      // Clear stale progress when ending a session
      setProgress(null);
      return;
    }

    const unsubscribe = signalRService.onDiscoveryProgress((progressUpdate) => {
      if (progressUpdate.sessionId === sessionId) {
        setProgress(progressUpdate);
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
  const [foundPrinters, setFoundPrinters] = useState<import('@/types/api').DiscoveredPrinterDto[]>([]);

  useEffect(() => {
    if (!sessionId) {
      // Reset previously found printers when session ends
      setFoundPrinters([]);
      return;
    }

    const unsubscribe = signalRService.onDiscoveryPrinterFound((found) => {
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
  const [completed, setCompleted] = useState<import('@/types/api').DiscoveryCompletedDto | null>(null);

  useEffect(() => {
    if (!sessionId) {
      // Clear completion marker when session resets
      setCompleted(null);
      return;
    }

    const unsubscribe = signalRService.onDiscoveryCompleted((completedUpdate) => {
      if (completedUpdate.sessionId === sessionId) {
        setCompleted(completedUpdate);
        onCompleted?.(completedUpdate);
      }
    });

    return unsubscribe;
  }, [sessionId, onCompleted]);

  return { completed };
}

export function useDiscoveryStream(sessionId?: string) {
  // Ensure we have a connection state to react to (will trigger connect on mount)
  const { isConnected, connectionState } = useSignalRConnection();
  const { progress } = useDiscoveryProgress(sessionId);
  const { foundPrinters, setFoundPrinters } = useDiscoveryPrinterFound(sessionId);
  const { completed } = useDiscoveryCompleted(sessionId);

  // Attempt to join discovery group when we have both a sessionId and an active connection.
  useEffect(() => {
    if (!sessionId) return;
    if (!isConnected) {
      // Wait until connection is established
      return;
    }

    let cancelled = false;
    (async () => {
      try {
        console.debug('[Discovery] Joining SignalR discovery group', { sessionId });
        await signalRService.joinDiscoveryGroup(sessionId);
      } catch (err) {
        if (!cancelled) {
          console.warn('[Discovery] Failed to join discovery group, will retry on next connection state change', err);
        }
      }
    })();

    return () => {
      cancelled = true;
      if (isConnected) {
        console.debug('[Discovery] Leaving SignalR discovery group', { sessionId });
        signalRService.leaveDiscoveryGroup(sessionId);
      }
    };
  }, [sessionId, isConnected, connectionState]);

  // Reset found printers when starting a new session
  const resetDiscovery = useCallback(() => {
    setFoundPrinters([]);
  }, [setFoundPrinters]);

  return {
    progress,
    foundPrinters,
    completed,
    resetDiscovery,
    isActive: progress && !completed,
    isCompleted: !!completed,
  };
}
import React, { useState, useEffect, useMemo } from 'react';
import { Route, Routes } from 'react-router-dom';
import { useMutation, useQuery } from '@tanstack/react-query';
import { toast } from 'sonner';
import { 
  Printer, 
  HarvestOptions, 
  GcodeHarvestStatus,
  GcodeHarvestOperation
} from '@/types/api';
import { useAuth } from '@/contexts/AuthContext';
import { usePrinters, useCancelHarvestOperation } from '@/hooks/useApi';
import { usePrinterStatusUpdates } from '@/hooks/useSignalR';
import { signalRService } from '@/services/harvest-signalr';
import { apiClient } from '@/services/api';
import { HarvestOperationCard } from '@/components/harvest/HarvestOperationCard';
import { HarvestProgressCard } from '@/components/harvest/HarvestProgressCard';
import { HarvestOperationDetails } from '@/components/harvest/HarvestOperationDetails';
import { PrinterCard } from '@/components/harvest/PrinterCard';
import { VirtualizedPrinterGrid } from '@/components/harvest/VirtualizedPrinterGrid';
import { AccessDenied } from '@/components/common/AccessDenied';
// import { IndexedFilesList } from '@/components/harvest/IndexedFilesList';

export const HarvestPage: React.FC = () => {
  // All hooks must be above any early return!
  const [selectedOperation, setSelectedOperation] = useState<GcodeHarvestOperation | null>(null);
  const [perFileProgressMap, setPerFileProgressMap] = useState<
    Record<string, Record<string, { fileName: string; percent: number; status: 'processing' | 'completed' | 'skipped' | 'errored' }>>
  >({});
  const [compact, setCompact] = useState(false);
  const [searchTerm, setSearchTerm] = useState('');
  const [backendFilter, setBackendFilter] = useState<string>('');
  const [groupBy, setGroupBy] = useState<string>('');
  const cancelHarvestMutation = useCancelHarvestOperation();
  const { hasPermission } = useAuth();
  const [optimisticOps, setOptimisticOps] = useState<GcodeHarvestOperation[]>([]);
  const { data: printers } = usePrinters();
  const { getPrinterStatus } = usePrinterStatusUpdates();
  const { data: harvestOperations, refetch: refetchOperations } = useQuery({
    queryKey: ['harvest-operations'],
    queryFn: () => apiClient.getHarvestOperations(),
    refetchInterval: 2000,
  });
  type StartHarvestPayload = { printerIds: string[]; options: { includeSubfolders?: boolean; maxFileAge?: number; fileTypes?: string[]; minFileSize?: number; duplicateHandling?: string } };
  const startHarvestMutation = useMutation({
    mutationFn: ({ printerIds, options }: StartHarvestPayload) =>
      apiClient.startBulkHarvest(printerIds, options),
    onSuccess: () => {
      refetchOperations();
      toast.success('Harvest operations started successfully');
      setTimeout(() => setOptimisticOps([]), 2000);
    },
    onError: (error) => {
      setOptimisticOps([]);
      toast.error('Failed to start harvest operations');
      console.error('Harvest error:', error);
    }
  });

  // Handler to update per-file progress for a specific operation
  const handleFileProgressUpdate = (
    operationId: string,
    fileName: string,
    percent: number,
    status: 'processing' | 'completed' | 'skipped' | 'errored'
  ) => {
    setPerFileProgressMap(prev => ({
      ...prev,
      [operationId]: {
        ...(prev[operationId] || {}),
        [fileName]: { fileName, percent, status }
      }
    }));
  };

  // Early return must come after all hooks
  if (!hasPermission('gcode_harvest', 'execute')) {
    return <AccessDenied />;
  }


  // Set up real-time updates for harvest progress and per-file progress
  useEffect(() => {
    signalRService.connect();

    // Join SignalR group for each running operation
    const joinedOps = new Set<string>();
    if (harvestOperations) {
      for (const op of harvestOperations) {
        if (op.status === GcodeHarvestStatus.Running && op.id) {
          signalRService.joinHarvestGroup(op.id);
          joinedOps.add(op.id);
        }
      }
    }

    // Subscribe to per-file progress events
    const unsubscribeFileProgress = signalRService.onHarvestFileProgress(progress => {
      // progress: { operationId, fileName, percent, ... }
      handleFileProgressUpdate(
        progress.operationId,
        progress.fileName,
        progress.percent,
        progress.percent >= 100 ? 'completed' : 'processing'
      );
    });

    // Also subscribe to harvest update for total progress (existing logic)
    const unsubscribe = signalRService.onHarvestUpdate((operationId, status) => {
      refetchOperations();
      if (status.currentFile && typeof status.progressPercent === 'number') {
        let fileStatus: 'processing' | 'completed' | 'skipped' | 'errored' = 'processing';
        if (status.phase === 'completing' || status.progressPercent >= 100) fileStatus = 'completed';
        if (status.filesSkipped > 0 && status.progressPercent >= 100) fileStatus = 'skipped';
        if (status.filesErrored > 0 && status.progressPercent >= 100) fileStatus = 'errored';
        handleFileProgressUpdate(operationId, status.currentFile, status.progressPercent, fileStatus);
      }
    });

    return () => {
      unsubscribe();
      unsubscribeFileProgress();
      // Leave SignalR group for each joined operation
      joinedOps.forEach(opId => signalRService.leaveHarvestGroup(opId));
    };
  }, [refetchOperations, harvestOperations]);

  // Clean up optimistic operations when real operations appear
  useEffect(() => {
    if (harvestOperations && optimisticOps.length > 0) {
      const realRunningOps = harvestOperations.filter(op => op.status === GcodeHarvestStatus.Running);
      const realPrinterIds = new Set(realRunningOps.map(op => op.printerId));
      
      // If we have real operations for any of our optimistic operations, clean up optimistic ones
      if (optimisticOps.some(op => realPrinterIds.has(op.printerId))) {
        setOptimisticOps(prev => prev.filter(op => !realPrinterIds.has(op.printerId)));
      }
    }
  }, [harvestOperations, optimisticOps]);


  const activeOperations = (() => {
    const realRunningOps = harvestOperations?.filter(op => 
      op.status === GcodeHarvestStatus.Running
    ) || [];
    
    // If we have real operations, filter out optimistic ones for the same printers
    const realPrinterIds = new Set(realRunningOps.map(op => op.printerId));
    const filteredOptimisticOps = optimisticOps.filter(op => !realPrinterIds.has(op.printerId));
    
    // Combine filtered optimistic ops with real operations
    return [...filteredOptimisticOps, ...realRunningOps];
  })();



  // Merge live status into base printer data
  const printersWithLive = (printers || []).map(p => {
    const status = getPrinterStatus(p.id);
    if (!status) return p;
    return {
      ...p,
      isOnline: status.isOnline,
      isReachable: status.isOnline || p.isReachable, // preserve reachable if previously true
      state: status.state ?? p.state,
      progress: status.progress ?? p.progress,
      jobName: status.jobName ?? p.jobName,
      hotendTemp: status.hotendTemp ?? p.hotendTemp,
      bedTemp: status.bedTemp ?? p.bedTemp,
      hotendTarget: status.hotendTarget ?? p.hotendTarget,
      bedTarget: status.bedTarget ?? p.bedTarget,
      x: status.x ?? p.x,
      y: status.y ?? p.y,
      z: status.z ?? p.z,
    } as Printer;
  });

  // Compute filtered and grouped printers (must be after printersWithLive)
  const filteredPrinters = useMemo(() => {
    let result = printersWithLive;
    if (searchTerm.trim()) {
      const term = searchTerm.trim().toLowerCase();
      result = result.filter((p: Printer) =>
        p.name.toLowerCase().includes(term) ||
        (p.modelName?.toLowerCase().includes(term) ?? false) ||
        (p.manufacturerName?.toLowerCase().includes(term) ?? false)
      );
    }
    if (backendFilter) {
      // Debug output
      // eslint-disable-next-line no-console
      console.log('Backend filter value:', backendFilter, 'Type:', typeof backendFilter);
      // eslint-disable-next-line no-console
      console.log('Printer backend values:', result.map(p => ({ id: p.id, backend: p.backend, name: p.name })));
      result = result.filter((p: Printer) => p.backend === Number(backendFilter));
    }
    // Grouping logic can be added here if needed
    return result;
  }, [printersWithLive, searchTerm, backendFilter]);

  // Recompute selected printers if a printer went offline (keep selection but disable start button logic uses isReachable)
  // const reachableSelectedCount = selectedPrinters.filter(id => printersWithLive.find(p => p.id === id && (p.isReachable || p.isOnline))).length;

  return (
    <Routes>
      <Route path="*" element={
        <div className="p-6 space-y-6">
          {/* Details panel overlay */}
          {selectedOperation && (
            <HarvestOperationDetails
              operation={selectedOperation}
              onClose={() => setSelectedOperation(null)}
            />
          )}
          <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4">
            <h1 className="text-2xl font-bold text-pf-text-primary">G-code Harvest</h1>
            <div className="flex flex-wrap gap-2 items-center">
              {activeOperations.length > 0 && (
                <button
                  onClick={async () => {
                    let successCount = 0;
                    let errorCount = 0;
                    for (const op of activeOperations) {
                      try {
                        await cancelHarvestMutation.mutateAsync(op.id);
                        toast.success(`Cancelled: ${op.printerName}`);
                        successCount++;
                      } catch (err) {
                        toast.error(`Failed to cancel: ${op.printerName}`);
                        errorCount++;
                      }
                    }
                    if (successCount && !errorCount) {
                      toast.success('All harvest operations cancelled');
                    } else if (successCount && errorCount) {
                      toast.warning(`${successCount} cancelled, ${errorCount} failed`);
                    } else if (errorCount) {
                      toast.error('Failed to cancel any operations');
                    }
                  }}
                  className="pf-btn pf-btn-danger"
                  disabled={cancelHarvestMutation.isPending}
                >
                  {cancelHarvestMutation.isPending ? 'Cancelling...' : 'Cancel All'}
                </button>
              )}
            </div>
          </div>
          {/* Virtualized printer grid for scalable fleets */}
          <div className="mt-6">
            {/* Search, filter, group controls */}
            <div className="flex flex-wrap gap-3 mb-4 items-end">
              <div>
                <label className="block text-xs font-medium text-pf-text-secondary mb-1">Search</label>
                <input
                  type="text"
                  value={searchTerm}
                  onChange={e => setSearchTerm(e.target.value)}
                  placeholder="Search printers..."
                  className="pf-input w-48"
                />
              </div>
              <div>
                <label className="block text-xs font-medium text-pf-text-secondary mb-1">Backend</label>
                <select
                  aria-label="Backend filter"
                  value={backendFilter}
                  onChange={e => setBackendFilter(e.target.value)}
                  className="pf-input w-36"
                >
                  <option value="">All</option>
                  <option value="0">Moonraker</option>
                  <option value="1">PrusaLink</option>
                  <option value="2">SDCP</option>
                  <option value="3">OctoPrint</option>
                </select>
              </div>
              {/* Grouping control placeholder */}
              {/* <div>
                <label className="block text-xs font-medium text-pf-text-secondary mb-1">Group by</label>
                <select
                  value={groupBy}
                  onChange={e => setGroupBy(e.target.value)}
                  className="pf-input w-36"
                >
                  <option value="">None</option>
                  <option value="backend">Backend</option>
                  <option value="manufacturer">Manufacturer</option>
                </select>
              </div> */}
            </div>
            <div className="flex items-center gap-2 mb-2">
              <label className="text-xs font-medium text-pf-text-secondary">Compact cards</label>
              <input
                type="checkbox"
                checked={compact}
                onChange={e => setCompact(e.target.checked)}
                className="pf-input"
                aria-label="Toggle compact card layout"
              />
            </div>
            <VirtualizedPrinterGrid
              printers={filteredPrinters}
              operations={Object.fromEntries(activeOperations.map(op => [op.printerId, op]))}
              onStartHarvest={(printerId, options) => {
                // Start harvest for this printer with its options
                setOptimisticOps(prev => [
                  ...prev,
                  {
                    id: `optimistic-${printerId}-${Date.now()}`,
                    printerId,
                    printerName: printersWithLive.find(p => p.id === printerId)?.name || 'Printer',
                    status: GcodeHarvestStatus.Running,
                    filesFound: 0,
                    filesProcessed: 0,
                    filesAdded: 0,
                    filesSkipped: 0,
                    filesErrored: 0,
                    duplicatesSkipped: 0,
                    totalSizeBytes: 0,
                    startedAt: new Date().toISOString(),
                    options
                  }
                ]);
                startHarvestMutation.mutate({ printerIds: [printerId], options });
              }}
              onCancelHarvest={opId => {
                cancelHarvestMutation.mutateAsync(opId);
              }}
              onSettings={() => {}}
              onViewDetails={(op: GcodeHarvestOperation) => setSelectedOperation(op)}
              columnCount={compact ? 6 : 4}
              cardHeight={compact ? 120 : 240}
              cardWidth={compact ? 180 : 320}
              compact={compact}
            />
          </div>
        </div>
      } />
    </Routes>
  );
};
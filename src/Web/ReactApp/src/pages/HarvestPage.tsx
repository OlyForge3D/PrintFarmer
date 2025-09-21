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

// Compact/expanded card toggle
const [compact, setCompact] = useState(false);
// Search/filter/group state (must be before filteredPrinters)
const [searchTerm, setSearchTerm] = useState('');
const [backendFilter, setBackendFilter] = useState<string>('');
const [groupBy, setGroupBy] = useState<string>('');
// Cancel mutation for harvest operations
const cancelHarvestMutation = useCancelHarvestOperation();

export const HarvestPage: React.FC = () => {
  // Track selected operation for details panel
  const [selectedOperation, setSelectedOperation] = useState<GcodeHarvestOperation | null>(null);
  // Per-operation per-file progress state
  const [perFileProgressMap, setPerFileProgressMap] = useState<
    Record<string, Record<string, { fileName: string; percent: number; status: 'processing' | 'completed' | 'skipped' | 'errored' }>>
  >({});

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
  const { hasPermission } = useAuth();
  // const queryClient = useQueryClient(); // not used here currently
  const [selectedPrinters, setSelectedPrinters] = useState<string[]>([]);
  const [harvestOptions, setHarvestOptions] = useState<HarvestOptions>({
    includeSubfolders: true,
    fileTypes: ['gcode', 'gco', 'g'],
    minFileSize: 1024, // 1KB minimum
    maxFileAge: undefined, // No age limit
    duplicateHandling: 'skip'
  });
  // Optimistic operations started on client before server push/update arrives
  const [optimisticOps, setOptimisticOps] = useState<GcodeHarvestOperation[]>([]);
  // const [selectedOperationForModal, setSelectedOperationForModal] = useState<GcodeHarvestOperation | null>(null);

  const { data: printers } = usePrinters();
  // Real-time printer status (SignalR)
  const { getPrinterStatus } = usePrinterStatusUpdates();
  const { data: harvestOperations, refetch: refetchOperations } = useQuery({
    queryKey: ['harvest-operations'],
    queryFn: () => apiClient.getHarvestOperations(),
    refetchInterval: 2000, // Frequent updates during active operations
  });

  type StartHarvestPayload = { printerIds: string[]; options: { includeSubfolders?: boolean; maxFileAge?: number; fileTypes?: string[]; minFileSize?: number; duplicateHandling?: string } };
  const startHarvestMutation = useMutation({
    mutationFn: ({ printerIds, options }: StartHarvestPayload) =>
      apiClient.startBulkHarvest(printerIds, options),
    onSuccess: () => {
      refetchOperations();
      toast.success('Harvest operations started successfully');
      setSelectedPrinters([]);
      // Clear optimistic placeholders once real data expected shortly
      setTimeout(() => setOptimisticOps([]), 2000); // Reduced from 4000 to 2000ms
    },
    onError: (error) => {
      // Roll back optimistic placeholders immediately on error
      setOptimisticOps([]);
      toast.error('Failed to start harvest operations');
      console.error('Harvest error:', error);
    }
  });

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

  const handleStartHarvest = () => {
    if (selectedPrinters.length === 0) {
      toast.error('Please select at least one printer');
      return;
    }
    // Add optimistic operation entries for each selected printer
    const now = new Date();
    const optimistic: GcodeHarvestOperation[] = selectedPrinters.map(pid => {
      const printer = printersWithLive.find(p => p.id === pid)!;
      return {
        id: `optimistic-${pid}-${now.getTime()}`,
        printerId: pid,
        printerName: printer?.name || 'Printer',
        status: GcodeHarvestStatus.Running,
        filesFound: 0,
        filesProcessed: 0,
        filesAdded: 0,
        filesSkipped: 0,
        filesErrored: 0,
        duplicatesSkipped: 0,
        totalSizeBytes: 0,
        startedAt: now.toISOString(),
        options: harvestOptions
      };
    });
    setOptimisticOps(optimistic);

    startHarvestMutation.mutate({ printerIds: selectedPrinters, options: {
      includeSubfolders: harvestOptions.includeSubfolders,
      maxFileAge: harvestOptions.maxFileAge,
      fileTypes: harvestOptions.fileTypes,
      minFileSize: harvestOptions.minFileSize,
      duplicateHandling: harvestOptions.duplicateHandling
    }});
  };

  if (!hasPermission('gcode_harvest', 'execute')) {
    return <AccessDenied />;
  }

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
      result = result.filter((p: Printer) => String(p.backend) === backendFilter);
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
              {hasPermission('gcode_harvest', 'create') && (
                <button
                  onClick={handleStartHarvest}
                  disabled={startHarvestMutation.isPending || selectedPrinters.length === 0}
                  title={selectedPrinters.length === 0 ? 'Select at least one reachable printer first' : undefined}
                  className={`pf-btn pf-btn-primary ${startHarvestMutation.isPending ? 'opacity-60 cursor-not-allowed' : ''}`}
                >
                  {startHarvestMutation.isPending ? 'Starting...' : 'Start Harvest'}
                </button>
              )}
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
          <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">
            {/* Printer Selection */}
            <div className="lg:col-span-1">
              <div className="bg-pf-bg-1 rounded-lg shadow-lg border border-pf-border">
                <div className="p-4 border-b border-pf-border">
                  <h3 className="font-medium text-pf-text-primary">Select Printers</h3>
                  <p className="text-sm text-pf-text-secondary mt-1">
                    Choose printers to harvest G-code files from
                  </p>
                </div>
                <div className="p-4 space-y-3">
                  <div className="flex items-center justify-between">
                    <button
                      onClick={() => {
                        const reachablePrinters = printers?.filter((p: Printer) => p.isReachable).map((p: Printer) => p.id) || [];
                        if (selectedPrinters.length === reachablePrinters.length) {
                          setSelectedPrinters([]);
                        } else {
                          setSelectedPrinters(reachablePrinters);
                        }
                      }}
                      className="text-sm text-pf-link hover:text-pf-accent"
                    >
                      {selectedPrinters.length === printers?.filter((p: Printer) => p.isReachable).length ? 'Deselect All' : 'Select All'}
                    </button>
                    <span className="text-sm text-pf-text-tertiary">
                      {selectedPrinters.length} selected
                    </span>
                  </div>
                  <div className="space-y-2 max-h-96 overflow-y-auto">
                    {printersWithLive.map((printer: Printer) => (
                      <label
                        key={printer.id}
                        className={`flex items-center p-3 border rounded-lg cursor-pointer transition-colors ${
                          selectedPrinters.includes(printer.id)
                            ? 'border-pf-accent bg-pf-accent-bg bg-opacity-20'
                            : 'border-pf-border hover:border-pf-border-light'
                        } ${!printer.isReachable ? 'opacity-50 cursor-not-allowed' : ''}`}
                      >
                        <input
                          type="checkbox"
                          checked={selectedPrinters.includes(printer.id)}
                          onChange={(e) => {
                            if (!printer.isReachable) return;
                            if (e.target.checked) {
                              setSelectedPrinters(prev => [...prev, printer.id]);
                            } else {
                              setSelectedPrinters(prev => prev.filter(id => id !== printer.id));
                            }
                          }}
                          disabled={!printer.isReachable}
                          className="mr-3"
                        />
                        <div className="flex-1">
                          <div className="font-medium text-pf-text-primary">{printer.name}</div>
                          <div className="text-sm text-pf-text-secondary">
                            {printer.backend} • {printer.isReachable ? 'Online' : 'Offline'}
                          </div>
                        </div>
                        <div className={`w-3 h-3 rounded-full ${
                          printer.isReachable ? 'bg-pf-success' : 'bg-pf-error'
                        }`} />
                      </label>
                    ))}
                  </div>
                </div>
              </div>
              {/* Harvest Options */}
              <div className="mt-6 bg-pf-bg-1 rounded-lg shadow-lg border border-pf-border">
                <div className="p-4 border-b border-pf-border">
                  <h3 className="font-medium text-pf-text-primary">Harvest Options</h3>
                </div>
                <div className="p-4 space-y-4">
                  <label className="flex items-center">
                    <input
                      type="checkbox"
                      checked={harvestOptions.includeSubfolders}
                      onChange={(e) => setHarvestOptions(prev => ({
                        ...prev,
                        includeSubfolders: e.target.checked
                      }))}
                      className="mr-2"
                    />
                    <span className="text-sm">Include subfolders</span>
                  </label>
                  <div>
                    <label className="block text-sm font-medium text-pf-text-primary mb-2">
                      File Types
                    </label>
                    <div className="space-y-2">
                      {['gcode', 'gco', 'g'].map(ext => (
                        <label key={ext} className="flex items-center">
                          <input
                            type="checkbox"
                            checked={harvestOptions.fileTypes.includes(ext)}
                            onChange={(e) => {
                              if (e.target.checked) {
                                setHarvestOptions(prev => ({
                                  ...prev,
                                  fileTypes: [...prev.fileTypes, ext]
                                }));
                              } else {
                                setHarvestOptions(prev => ({
                                  ...prev,
                                  fileTypes: prev.fileTypes.filter(t => t !== ext)
                                }));
                              }
                            }}
                            className="mr-2"
                          />
                          <span className="text-sm">.{ext}</span>
                        </label>
                      ))}
                    </div>
                  </div>
                  <div>
                    <label className="block text-sm font-medium text-pf-text-primary mb-2">
                      Minimum File Size
                    </label>
                    <select
                      aria-label="Minimum file size"
                      value={harvestOptions.minFileSize}
                      onChange={(e) => setHarvestOptions(prev => ({
                        ...prev,
                        minFileSize: parseInt(e.target.value)
                      }))}
                      className="w-full px-3 py-2 border border-pf-border rounded-md bg-pf-bg-0 text-pf-text-primary focus:outline-none focus:ring-2 focus:ring-pf-accent"
                    >
                      <option value={0}>No minimum</option>
                      <option value={1024}>1 KB</option>
                      <option value={10240}>10 KB</option>
                      <option value={102400}>100 KB</option>
                      <option value={1048576}>1 MB</option>
                    </select>
                  </div>
                  <div>
                    <label className="block text-sm font-medium text-pf-text-primary mb-2">
                      Duplicate Handling
                    </label>
                    <select
                      aria-label="Duplicate handling"
                      value={harvestOptions.duplicateHandling}
                      onChange={(e) => setHarvestOptions(prev => ({
                        ...prev,
                        duplicateHandling: e.target.value as 'skip' | 'overwrite' | 'rename'
                      }))}
                      className="w-full px-3 py-2 border border-pf-border rounded-md bg-pf-bg-0 text-pf-text-primary focus:outline-none focus:ring-2 focus:ring-pf-accent"
                    >
                      <option value="skip">Skip duplicates</option>
                      <option value="overwrite">Overwrite existing</option>
                      <option value="rename">Rename duplicates</option>
                    </select>
                  </div>
                </div>
              </div>
            </div>
            {/* Virtualized printer grid for scalable fleets */}
            <div className="mt-6 lg:col-span-2">
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
                onStartHarvest={(id: string) => setSelectedPrinters([id])}
                onCancelHarvest={(opId: string) => {
                  // TODO: Wire up cancel logic
                }}
                onSettings={(id: string) => {
                  // TODO: Open printer settings modal or navigate
                }}
                onViewDetails={(op: GcodeHarvestOperation) => setSelectedOperation(op)}
                columnCount={compact ? 6 : 4}
                cardHeight={compact ? 120 : 240}
                cardWidth={compact ? 180 : 320}
                compact={compact}
              />
            </div>
          </div>
        </div>
      } />
    </Routes>
  );
};
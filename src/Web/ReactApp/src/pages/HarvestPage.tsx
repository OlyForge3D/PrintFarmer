import React, { useState, useEffect } from 'react';
import { Link } from 'react-router-dom';
import { useMutation, useQuery } from '@tanstack/react-query';
import { toast } from 'sonner';
// Icons removed (unused after refactor)

import { 
  Printer, 
  HarvestOptions, 
  GcodeHarvestStatus,
  GcodeHarvestOperation
} from '@/types/api';
import { useAuth } from '@/contexts/AuthContext';
import { usePrinters } from '@/hooks/useApi';
import { usePrinterStatusUpdates } from '@/hooks/useSignalR';
import { signalRService } from '@/services/signalr';
import { apiClient } from '@/services/api';
import { HarvestOperationCard } from '@/components/harvest/HarvestOperationCard';
import { HarvestProgressModal } from '@/components/harvest/HarvestProgressModal';
import { AccessDenied } from '@/components/common/AccessDenied';

export const HarvestPage: React.FC = () => {
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
  const [selectedOperationForModal, setSelectedOperationForModal] = useState<GcodeHarvestOperation | null>(null);

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

  // Set up real-time updates for harvest progress
  useEffect(() => {
    signalRService.connect();
    
    const unsubscribe = signalRService.onHarvestUpdate(() => {
      refetchOperations();
    });

    return () => {
      unsubscribe();
    };
  }, [refetchOperations]);

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
        startedAt: now,
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

  const completedOperations = harvestOperations?.filter(op =>
    op.status === GcodeHarvestStatus.Completed || op.status === GcodeHarvestStatus.Failed
  )?.slice(0, 10) || [];

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

  // Recompute selected printers if a printer went offline (keep selection but disable start button logic uses isReachable)
  // const reachableSelectedCount = selectedPrinters.filter(id => printersWithLive.find(p => p.id === id && (p.isReachable || p.isOnline))).length;

  return (
    <div className="p-6 space-y-6">
      <div className="flex items-center justify-between">
        <h1 className="text-2xl font-bold text-gray-900">G-code Harvest</h1>
        
        {hasPermission('gcode_harvest', 'create') && (
          <div className="flex flex-col items-end">
            <button
              onClick={handleStartHarvest}
              disabled={startHarvestMutation.isPending}
              title={selectedPrinters.length === 0 ? 'Select at least one reachable printer first' : undefined}
              className={`btn btn-primary transition-opacity ${startHarvestMutation.isPending ? 'opacity-60 cursor-not-allowed' : ''}`}
            >
              {startHarvestMutation.isPending ? 'Starting...' : 'Start Harvest'}
            </button>
            {selectedPrinters.length === 0 && !startHarvestMutation.isPending && (
              <span className="mt-1 text-xs text-gray-500">Select one or more printers below</span>
            )}
          </div>
        )}
      </div>

      <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">
        {/* Printer Selection */}
        <div className="lg:col-span-1">
          <div className="bg-white rounded-lg shadow">
            <div className="p-4 border-b border-gray-200">
              <h3 className="font-medium text-gray-900">Select Printers</h3>
              <p className="text-sm text-gray-500 mt-1">
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
                  className="text-sm text-blue-600 hover:text-blue-800"
                >
                  {selectedPrinters.length === printers?.filter((p: Printer) => p.isReachable).length ? 'Deselect All' : 'Select All'}
                </button>
                <span className="text-sm text-gray-500">
                  {selectedPrinters.length} selected
                </span>
              </div>

              <div className="space-y-2 max-h-96 overflow-y-auto">
                {printersWithLive.map((printer: Printer) => (
                  <label
                    key={printer.id}
                    className={`flex items-center p-3 border rounded-lg cursor-pointer transition-colors ${
                      selectedPrinters.includes(printer.id)
                        ? 'border-blue-500 bg-blue-50'
                        : 'border-gray-200 hover:border-gray-300'
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
                      <div className="font-medium text-gray-900">{printer.name}</div>
                      <div className="text-sm text-gray-500">
                        {printer.backend} • {printer.isReachable ? 'Online' : 'Offline'}
                      </div>
                    </div>
                    <div className={`w-3 h-3 rounded-full ${
                      printer.isReachable ? 'bg-green-500' : 'bg-red-500'
                    }`} />
                  </label>
                ))}
              </div>
            </div>
          </div>

          {/* Harvest Options */}
          <div className="mt-6 bg-white rounded-lg shadow">
            <div className="p-4 border-b border-gray-200">
              <h3 className="font-medium text-gray-900">Harvest Options</h3>
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
                <label className="block text-sm font-medium text-gray-700 mb-2">
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
                <label className="block text-sm font-medium text-gray-700 mb-2">
                  Minimum File Size
                </label>
                <select
                  aria-label="Minimum file size"
                  value={harvestOptions.minFileSize}
                  onChange={(e) => setHarvestOptions(prev => ({
                    ...prev,
                    minFileSize: parseInt(e.target.value)
                  }))}
                  className="w-full px-3 py-2 border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500"
                >
                  <option value={0}>No minimum</option>
                  <option value={1024}>1 KB</option>
                  <option value={10240}>10 KB</option>
                  <option value={102400}>100 KB</option>
                  <option value={1048576}>1 MB</option>
                </select>
              </div>

              <div>
                <label className="block text-sm font-medium text-gray-700 mb-2">
                  Duplicate Handling
                </label>
                <select
                  aria-label="Duplicate handling"
                  value={harvestOptions.duplicateHandling}
                  onChange={(e) => setHarvestOptions(prev => ({
                    ...prev,
                    duplicateHandling: e.target.value as 'skip' | 'overwrite' | 'rename'
                  }))}
                  className="w-full px-3 py-2 border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500"
                >
                  <option value="skip">Skip duplicates</option>
                  <option value="overwrite">Overwrite existing</option>
                  <option value="rename">Rename duplicates</option>
                </select>
              </div>
            </div>
          </div>
        </div>

        {/* Operations Status */}
        <div className="lg:col-span-2 space-y-6">
          {/* Active Operations */}
          {activeOperations.length > 0 && (
            <div className="bg-white rounded-lg shadow">
              <div className="p-4 border-b border-gray-200">
                <h3 className="font-medium text-gray-900">Active Operations</h3>
              </div>
              
              <div className="p-4 space-y-4">
                {activeOperations.map(operation => (
                  <HarvestOperationCard
                    key={operation.id}
                    operation={operation}
                    showProgress={true}
                    onViewDetails={setSelectedOperationForModal}
                  />
                ))}
              </div>
            </div>
          )}

          {/* Recent Operations */}
          <div className="bg-white rounded-lg shadow">
            <div className="p-4 border-b border-gray-200 flex items-center justify-between">
              <h3 className="font-medium text-gray-900">Recent Operations</h3>
              
              {hasPermission('gcode_harvest', 'read') && (
                <Link to="/harvest/history" className="text-sm text-blue-600 hover:text-blue-800">
                  View All History
                </Link>
              )}
            </div>
            
            <div className="divide-y divide-gray-200">
              {completedOperations.length > 0 ? (
                completedOperations.map(operation => (
                  <HarvestOperationCard
                    key={operation.id}
                    operation={operation}
                    showProgress={false}
                  />
                ))
              ) : (
                <div className="p-8 text-center text-gray-500">
                  No harvest operations yet
                </div>
              )}
            </div>
          </div>
        </div>
      </div>
      
      {/* Harvest Progress Modal */}
      {selectedOperationForModal && (
        <HarvestProgressModal
          isOpen={true}
          onClose={() => setSelectedOperationForModal(null)}
          operation={selectedOperationForModal}
          onOperationUpdate={() => {
            refetchOperations();
            // Update the selected operation with latest data
            const updated = harvestOperations?.find(op => op.id === selectedOperationForModal.id);
            if (updated) {
              setSelectedOperationForModal(updated);
            }
          }}
        />
      )}
    </div>
  );
};
import React, { useState, useEffect } from 'react';
import { Link } from 'react-router-dom';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import { formatDistanceToNow } from 'date-fns';
import { 
  ClockIcon, 
  PlayIcon, 
  CheckCircleIcon, 
  ExclamationCircleIcon, 
  XCircleIcon,
  ChevronDownIcon 
} from '@heroicons/react/24/outline';

import { 
  Printer, 
  HarvestOptions, 
  GcodeHarvestOperation, 
  StartBulkHarvestRequest,
  HarvestProgress,
  GcodeHarvestStatus
} from '@/types/api';
import { useAuth } from '@/contexts/AuthContext';
import { usePrinters } from '@/hooks/useApi';
import { useHarvestUpdates } from '@/hooks/useSignalR';
import { signalRService } from '@/services/signalr';
import { HarvestOperationCard } from '@/components/harvest/HarvestOperationCard';
import { AccessDenied } from '@/components/common/AccessDenied';

// Mock API client - in a real app this would be imported from services
const apiClient = {
  getHarvestOperations: async (): Promise<GcodeHarvestOperation[]> => {
    // Mock implementation - replace with actual API call
    return [];
  },
  startBulkHarvest: async (printerIds: string[], options: HarvestOptions): Promise<{ operationIds: string[] }> => {
    // Mock implementation - replace with actual API call
    return { operationIds: printerIds.map(() => crypto.randomUUID()) };
  }
};

export const HarvestPage: React.FC = () => {
  const { hasPermission } = useAuth();
  const queryClient = useQueryClient();
  const [selectedPrinters, setSelectedPrinters] = useState<string[]>([]);
  const [harvestOptions, setHarvestOptions] = useState<HarvestOptions>({
    includeSubfolders: true,
    fileTypes: ['gcode', 'gco', 'g'],
    minFileSize: 1024, // 1KB minimum
    maxFileAge: undefined, // No age limit
    duplicateHandling: 'skip'
  });

  const { data: printers } = usePrinters();
  const { data: harvestOperations, refetch: refetchOperations } = useQuery({
    queryKey: ['harvest-operations'],
    queryFn: () => apiClient.getHarvestOperations(),
    refetchInterval: 2000, // Frequent updates during active operations
  });

  const startHarvestMutation = useMutation({
    mutationFn: ({ printerIds, options }: { printerIds: string[], options: HarvestOptions }) =>
      apiClient.startBulkHarvest(printerIds, options),
    onSuccess: () => {
      refetchOperations();
      toast.success('Harvest operations started successfully');
      setSelectedPrinters([]);
    },
    onError: (error) => {
      toast.error('Failed to start harvest operations');
      console.error('Harvest error:', error);
    }
  });

  // Set up real-time updates for harvest progress
  useEffect(() => {
    signalRService.connect();
    
    const unsubscribe = signalRService.onHarvestUpdate((operationId: string, progress: HarvestProgress) => {
      // Update UI with progress information
      refetchOperations();
    });

    return () => {
      unsubscribe();
    };
  }, [refetchOperations]);

  const handleStartHarvest = () => {
    if (selectedPrinters.length === 0) {
      toast.error('Please select at least one printer');
      return;
    }

    startHarvestMutation.mutate({
      printerIds: selectedPrinters,
      options: harvestOptions
    });
  };

  if (!hasPermission('gcode_harvest', 'execute')) {
    return <AccessDenied />;
  }

  const activeOperations = harvestOperations?.filter(op => 
    op.status === GcodeHarvestStatus.Running || op.status === GcodeHarvestStatus.Starting
  ) || [];

  const completedOperations = harvestOperations?.filter(op =>
    op.status === GcodeHarvestStatus.Completed || op.status === GcodeHarvestStatus.Failed
  )?.slice(0, 10) || [];

  return (
    <div className="p-6 space-y-6">
      <div className="flex items-center justify-between">
        <h1 className="text-2xl font-bold text-gray-900">G-code Harvest</h1>
        
        {hasPermission('gcode_harvest', 'create') && (
          <button
            onClick={handleStartHarvest}
            disabled={selectedPrinters.length === 0 || startHarvestMutation.isPending}
            className="btn btn-primary"
          >
            {startHarvestMutation.isPending ? 'Starting...' : 'Start Harvest'}
          </button>
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
                {printers?.map((printer: Printer) => (
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
    </div>
  );
};
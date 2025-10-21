import { useEffect, useState } from 'react';
import { workerService, WorkerResponse, WorkerStatus } from '@/services/workerService';
import { slicerHubService, SlicerRegisteredEvent, SlicerHeartbeatEvent, SlicerDeregisteredEvent } from '@/services/slicerHubService';

export default function WorkerManagementPage() {
  const [workers, setWorkers] = useState<WorkerResponse[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [selectedWorker, setSelectedWorker] = useState<WorkerResponse | null>(null);
  const [showDisableDialog, setShowDisableDialog] = useState(false);
  const [disableReason, setDisableReason] = useState('');
  const [filter, setFilter] = useState<'all' | WorkerStatus>('all');
  const [isConnected, setIsConnected] = useState(false);

  useEffect(() => {
    loadWorkers();
    
    // Start SignalR connection
    startSignalRConnection();

    // Refresh every 30 seconds as fallback (SignalR should provide real-time updates)
    const interval = setInterval(loadWorkers, 30000);
    
    return () => {
      clearInterval(interval);
      // Clean up SignalR connection
      slicerHubService.stop();
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Reload workers when filter changes
  useEffect(() => {
    loadWorkers();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [filter]);

  const startSignalRConnection = async () => {
    try {
      await slicerHubService.start();
      setIsConnected(true);

      // Subscribe to worker events
      slicerHubService.onSlicerRegistered(handleWorkerRegistered);
      slicerHubService.onSlicerHeartbeat(handleWorkerHeartbeat);
      slicerHubService.onSlicerDeregistered(handleWorkerDeregistered);
    } catch (err) {
      console.error('Failed to connect to SlicerHub:', err);
      setIsConnected(false);
    }
  };

  const handleWorkerRegistered = (event: SlicerRegisteredEvent) => {
    console.log('Worker registered:', event);
    // Reload workers to get the new worker
    loadWorkers();
  };

  const handleWorkerHeartbeat = (event: SlicerHeartbeatEvent) => {
    console.log('Worker heartbeat:', event);
    // Update worker status in real-time
    setWorkers(prev => prev.map(worker => 
      worker.id === event.id 
        ? { 
            ...worker, 
            status: event.status, 
            freeSlots: event.freeSlots, 
            lastHeartbeat: event.lastSeen,
            activeJobs: worker.totalSlots - event.freeSlots
          }
        : worker
    ));
  };

  const handleWorkerDeregistered = (event: SlicerDeregisteredEvent) => {
    console.log('Worker deregistered:', event);
    // Remove worker from list
    setWorkers(prev => prev.filter(worker => worker.id !== event.id));
  };

  const loadWorkers = async () => {
    try {
      setError(null);
      let data: WorkerResponse[];
      
      if (filter === 'all') {
        data = await workerService.getAllWorkers();
      } else {
        data = await workerService.getWorkersByStatus(filter);
      }
      
      setWorkers(data);
      setLoading(false);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to load workers');
      setLoading(false);
    }
  };

  const handleDisableWorker = async () => {
    if (!selectedWorker || !disableReason.trim()) return;
    
    try {
      await workerService.disableWorker(selectedWorker.id, disableReason);
      setShowDisableDialog(false);
      setDisableReason('');
      setSelectedWorker(null);
      loadWorkers();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to disable worker');
    }
  };

  const handleEnableWorker = async (worker: WorkerResponse) => {
    try {
      await workerService.enableWorker(worker.id);
      loadWorkers();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to enable worker');
    }
  };

  const handleDeleteWorker = async (worker: WorkerResponse) => {
    if (!confirm(`Are you sure you want to delete worker "${worker.name}"?`)) return;
    
    try {
      await workerService.deleteWorker(worker.id);
      loadWorkers();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to delete worker');
    }
  };

  const getStatusBadgeClass = (status: string) => {
    switch (status) {
      case WorkerStatus.Online:
        return 'bg-green-100 text-green-800';
      case WorkerStatus.Busy:
        return 'bg-yellow-100 text-yellow-800';
      case WorkerStatus.Offline:
        return 'bg-gray-100 text-gray-800';
      case WorkerStatus.Error:
        return 'bg-red-100 text-red-800';
      case WorkerStatus.Draining:
        return 'bg-blue-100 text-blue-800';
      default:
        return 'bg-gray-100 text-gray-800';
    }
  };

  if (loading) {
    return (
      <div className="container mx-auto p-6">
        <div className="flex items-center justify-center h-64">
          <div className="text-lg">Loading workers...</div>
        </div>
      </div>
    );
  }

  return (
    <div className="container mx-auto p-6">
      <div className="flex justify-between items-center mb-6">
          <div>
            <h1 className="text-3xl font-bold">Worker Management</h1>
            <div className="flex items-center gap-2 mt-2">
              <div className={`w-2 h-2 rounded-full ${isConnected ? 'bg-green-500' : 'bg-gray-400'}`}></div>
              <span className="text-sm text-gray-600">
                {isConnected ? 'Real-time updates active' : 'Polling mode (SignalR disconnected)'}
              </span>
            </div>
          </div>
        <button
          onClick={loadWorkers}
          className="px-4 py-2 bg-blue-600 text-white rounded hover:bg-blue-700"
        >
          Refresh
        </button>
      </div>

      {error && (
        <div className="mb-4 p-4 bg-red-100 border border-red-400 text-red-700 rounded">
          {error}
        </div>
      )}

      {/* Filter tabs */}
      <div className="mb-4 flex space-x-2">
        <button
          onClick={() => setFilter('all')}
          className={`px-4 py-2 rounded ${filter === 'all' ? 'bg-blue-600 text-white' : 'bg-gray-200'}`}
        >
          All ({workers.length})
        </button>
        {Object.values(WorkerStatus).map(status => (
          <button
            key={status}
            onClick={() => setFilter(status)}
            className={`px-4 py-2 rounded ${filter === status ? 'bg-blue-600 text-white' : 'bg-gray-200'}`}
          >
            {status}
          </button>
        ))}
      </div>

      {/* Workers table */}
      <div className="bg-white rounded-lg shadow overflow-hidden">
        <table className="min-w-full divide-y divide-gray-200">
          <thead className="bg-gray-50">
            <tr>
              <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">
                Worker
              </th>
              <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">
                Status
              </th>
              <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">
                Capacity
              </th>
              <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">
                Statistics
              </th>
              <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">
                Performance
              </th>
              <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">
                Actions
              </th>
            </tr>
          </thead>
          <tbody className="bg-white divide-y divide-gray-200">
            {workers.map(worker => (
              <tr key={worker.id} className="hover:bg-gray-50">
                <td className="px-6 py-4 whitespace-nowrap">
                  <div className="flex flex-col">
                    <div className="text-sm font-medium text-gray-900">{worker.name}</div>
                    <div className="text-xs text-gray-500">{worker.endpointUrl}</div>
                    <div className="text-xs text-gray-400 mt-1">
                      {worker.capabilities.join(', ')}
                    </div>
                  </div>
                </td>
                <td className="px-6 py-4 whitespace-nowrap">
                  <span className={`px-2 inline-flex text-xs leading-5 font-semibold rounded-full ${getStatusBadgeClass(worker.status)}`}>
                    {worker.status}
                  </span>
                  {worker.isDisabled && (
                    <div className="text-xs text-red-600 mt-1">
                      Disabled: {worker.disabledReason}
                    </div>
                  )}
                  {workerService.isHeartbeatStale(worker) && (
                    <div className="text-xs text-orange-600 mt-1">
                      ⚠️ Stale heartbeat
                    </div>
                  )}
                </td>
                <td className="px-6 py-4 whitespace-nowrap">
                  <div className="text-sm text-gray-900">
                    {worker.totalSlots - worker.freeSlots} / {worker.totalSlots} slots used
                  </div>
                  <div className="text-xs text-gray-500">
                    {workerService.calculateUtilization(worker).toFixed(0)}% utilization
                  </div>
                  <div className="w-full bg-gray-200 rounded-full h-2 mt-1">
                    <div
                      className="bg-blue-600 h-2 rounded-full"
                      style={{ width: `${workerService.calculateUtilization(worker)}%` }}
                    />
                  </div>
                </td>
                <td className="px-6 py-4 whitespace-nowrap">
                  <div className="text-sm text-gray-900">
                    Active: {worker.activeJobs}
                  </div>
                  <div className="text-xs text-green-600">
                    ✓ Completed: {worker.completedJobs}
                  </div>
                  <div className="text-xs text-red-600">
                    ✗ Failed: {worker.failedJobs}
                  </div>
                  <div className="text-xs text-gray-500">
                    Success: {workerService.calculateSuccessRate(worker).toFixed(1)}%
                  </div>
                </td>
                <td className="px-6 py-4 whitespace-nowrap">
                  <div className="text-sm text-gray-900">
                    Avg: {worker.averageProcessingTimeSeconds.toFixed(0)}s
                  </div>
                  <div className="text-xs text-gray-500">
                    Uptime: {workerService.getUptime(worker)}
                  </div>
                  <div className="text-xs text-gray-400">
                    v{worker.version}
                  </div>
                </td>
                <td className="px-6 py-4 whitespace-nowrap text-sm">
                  <div className="flex flex-col space-y-1">
                    {worker.isDisabled ? (
                      <button
                        onClick={() => handleEnableWorker(worker)}
                        className="text-green-600 hover:text-green-900"
                      >
                        Enable
                      </button>
                    ) : (
                      <button
                        onClick={() => {
                          setSelectedWorker(worker);
                          setShowDisableDialog(true);
                        }}
                        className="text-yellow-600 hover:text-yellow-900"
                      >
                        Disable
                      </button>
                    )}
                    <button
                      onClick={() => handleDeleteWorker(worker)}
                      className="text-red-600 hover:text-red-900"
                    >
                      Delete
                    </button>
                  </div>
                </td>
              </tr>
            ))}
          </tbody>
        </table>

        {workers.length === 0 && (
          <div className="text-center py-12 text-gray-500">
            No workers found
          </div>
        )}
      </div>

      {/* Disable Worker Dialog */}
      {showDisableDialog && selectedWorker && (
        <div className="fixed inset-0 bg-black bg-opacity-50 flex items-center justify-center z-50">
          <div className="bg-white rounded-lg p-6 max-w-md w-full">
            <h2 className="text-xl font-bold mb-4">Disable Worker</h2>
            <p className="mb-4 text-gray-600">
              Disable worker: <strong>{selectedWorker.name}</strong>
            </p>
            <div className="mb-4">
              <label className="block text-sm font-medium text-gray-700 mb-2">
                Reason (required)
              </label>
              <textarea
                value={disableReason}
                onChange={(e) => setDisableReason(e.target.value)}
                className="w-full px-3 py-2 border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500"
                rows={3}
                placeholder="Enter reason for disabling this worker..."
              />
            </div>
            <div className="flex justify-end space-x-2">
              <button
                onClick={() => {
                  setShowDisableDialog(false);
                  setDisableReason('');
                  setSelectedWorker(null);
                }}
                className="px-4 py-2 border border-gray-300 rounded hover:bg-gray-50"
              >
                Cancel
              </button>
              <button
                onClick={handleDisableWorker}
                disabled={!disableReason.trim()}
                className="px-4 py-2 bg-yellow-600 text-white rounded hover:bg-yellow-700 disabled:opacity-50 disabled:cursor-not-allowed"
              >
                Disable Worker
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}

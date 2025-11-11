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
    let isMounted = true;

    const initialize = async () => {
      // Load initial data
      if (isMounted) await loadWorkers();

      // Start SignalR connection
      if (isMounted) await startSignalRConnection();
    };

    initialize();

    // Refresh every 30 seconds as fallback (SignalR should provide real-time updates)
    const interval = setInterval(() => {
      if (isMounted) loadWorkers();
    }, 30000);

    return () => {
      isMounted = false;
      clearInterval(interval);
      // Clean up SignalR connection - properly stop before unmounting
      slicerHubService.stop().catch(err => console.warn('Error stopping SlicerHub:', err));
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
      // Ensure we're not double-starting
      if (isConnected) return;

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
          className="btn-base btn-md btn-primary"
        >
          Refresh
        </button>
      </div>

      {error && (
        <div className="alert-base alert-error mb-4" role="alert">
          {error}
        </div>
      )}

      {/* Filter tabs */}
      <div className="mb-4 flex gap-sm">
        <button
          onClick={() => setFilter('all')}
          className={filter === 'all' ? 'btn-base btn-md btn-primary' : 'btn-base btn-md btn-secondary'}
        >
          All ({workers.length})
        </button>
        {Object.values(WorkerStatus).map(status => (
          <button
            key={status}
            onClick={() => setFilter(status)}
            className={filter === status ? 'btn-base btn-md btn-primary' : 'btn-base btn-md btn-secondary'}
          >
            {status}
          </button>
        ))}
      </div>

      {/* Workers table */}
      <div className="card">
        <table>
          <thead>
            <tr>
              <th>Worker</th>
              <th>Status</th>
              <th>Capacity</th>
              <th>Statistics</th>
              <th>Performance</th>
              <th>Actions</th>
            </tr>
          </thead>
          <tbody>
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
                  <div className="w-full bg-gray-200 rounded-full h-2 mt-1 overflow-hidden">
                    <div
                      className="bg-blue-600 h-2 rounded-full"
                      style={{
                        width: `${Math.min(100, Math.max(0, workerService.calculateUtilization(worker)))}%`,
                        transition: 'width 0.3s ease-in-out'
                      }}
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
                    Success: {(workerService.calculateSuccessRate(worker) ?? 0).toFixed(1)}%
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
                  <div className="gap-sm flex-col">
                    {worker.isDisabled ? (
                      <button
                        onClick={() => handleEnableWorker(worker)}
                        className="btn-base btn-sm btn-success"
                      >
                        Enable
                      </button>
                    ) : (
                      <button
                        onClick={() => {
                          setSelectedWorker(worker);
                          setShowDisableDialog(true);
                        }}
                        className="btn-base btn-sm btn-subtle"
                      >
                        Disable
                      </button>
                    )}
                    <button
                      onClick={() => handleDeleteWorker(worker)}
                      className="btn-base btn-sm btn-danger"
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
        <div className="modal-overlay">
          <div className="modal modal-sm">
            <div className="modal-header">
              <h2 className="modal-header-title">Disable Worker</h2>
            </div>
            <div className="modal-body">
              <p className="mb-4">
                Disable worker: <strong>{selectedWorker.name}</strong>
              </p>
              <div className="form-group">
                <label className="form-label">Reason (required)</label>
                <textarea
                  value={disableReason}
                  onChange={(e) => setDisableReason(e.target.value)}
                  className="input-base w-full"
                  rows={3}
                  placeholder="Enter reason for disabling this worker..."
                />
              </div>
            </div>
            <div className="modal-footer">
              <button
                onClick={() => {
                  setShowDisableDialog(false);
                  setDisableReason('');
                  setSelectedWorker(null);
                }}
                className="btn-base btn-md btn-secondary"
              >
                Cancel
              </button>
              <button
                onClick={handleDisableWorker}
                disabled={!disableReason.trim()}
                className="btn-base btn-md btn-danger"
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

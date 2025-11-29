import { useEffect, useState } from 'react';
import { workerService, WorkerResponse, WorkerStatus, WorkerJobResponse } from '@/services/workerService';
import { slicerHubService, SlicerRegisteredEvent, SlicerHeartbeatEvent, SlicerDeregisteredEvent } from '@/services/slicerHubService';
import { PageTemplate } from '@/components/PageTemplate';
import { Button } from '@/components/ui/Button';
import { Alert } from '@/components/ui/Alert';
import { Badge } from '@/components/ui/Badge';
import { Modal } from '@/components/ui/Modal';
import { Label } from '@/components/ui/Label';
import { Textarea } from '@/components/ui/Textarea';
import { Input } from '@/components/ui/Input';
import { ProgressBar } from '@/components/ui/ProgressBar';
import { RefreshCw, Wrench, ChevronDown, ChevronRight } from 'lucide-react';

export default function WorkerManagementPage() {
  const [workers, setWorkers] = useState<WorkerResponse[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [selectedWorker, setSelectedWorker] = useState<WorkerResponse | null>(null);
  const [showDisableDialog, setShowDisableDialog] = useState(false);
  const [disableReason, setDisableReason] = useState('');
  const [showEditSlotsDialog, setShowEditSlotsDialog] = useState(false);
  const [editSlotsValue, setEditSlotsValue] = useState('');
  const [filter, setFilter] = useState<'all' | WorkerStatus>('all');
  const [isConnected, setIsConnected] = useState(false);
  const [expandedWorker, setExpandedWorker] = useState<string | null>(null);
  const [workerJobs, setWorkerJobs] = useState<Map<string, WorkerJobResponse[]>>(new Map());
  const [loadingJobs, setLoadingJobs] = useState<Set<string>>(new Set());

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
    if (window.PrintFarmerDebug?.slicing) {
      console.log('Worker registered:', event);
    }
    // Reload workers to get the new worker
    loadWorkers();
  };

  const handleWorkerHeartbeat = (event: SlicerHeartbeatEvent) => {
    if (window.PrintFarmerDebug?.slicing) {
      console.log('Worker heartbeat:', event);
    }
    // Update worker status in real-time
    // NOTE: Don't recalculate activeJobs from totalSlots - freeSlots because:
    // - totalSlots might have been recently updated in the UI
    // - freeSlots from heartbeat is based on old totalSlots value
    // This causes incorrect calculations. Just update status and freeSlots,
    // and let the next full worker reload sync activeJobs correctly.
    setWorkers(prev => prev.map(worker =>
      worker.id === event.id
        ? {
          ...worker,
          status: event.status,
          freeSlots: event.freeSlots,
          lastHeartbeat: event.lastSeen
        }
        : worker
    ));
  };

  const handleWorkerDeregistered = (event: SlicerDeregisteredEvent) => {
    if (window.PrintFarmerDebug?.slicing) {
      console.log('Worker deregistered:', event);
    }
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

  const handleEditSlots = (worker: WorkerResponse) => {
    setSelectedWorker(worker);
    setEditSlotsValue(worker.totalSlots.toString());
    setShowEditSlotsDialog(true);
  };

  const handleUpdateSlots = async () => {
    if (!selectedWorker) return;
    const newSlots = parseInt(editSlotsValue, 10);
    if (isNaN(newSlots) || newSlots < 1) {
      setError('Total slots must be a number greater than 0');
      return;
    }

    try {
      await workerService.updateWorkerSlots(selectedWorker.id, newSlots);
      setShowEditSlotsDialog(false);
      setEditSlotsValue('');
      setSelectedWorker(null);
      loadWorkers();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to update worker slots');
    }
  };

  const handleToggleExpanded = async (workerId: string) => {
    if (expandedWorker === workerId) {
      setExpandedWorker(null);
    } else {
      setExpandedWorker(workerId);
      if (!workerJobs.has(workerId)) {
        const loading = new Set(loadingJobs);
        loading.add(workerId);
        setLoadingJobs(loading);
        try {
          const jobs = await workerService.getWorkerJobs(workerId);
          const map = new Map(workerJobs);
          map.set(workerId, jobs);
          setWorkerJobs(map);
        } catch (err) {
          console.error('Failed to load worker jobs:', err);
        } finally {
          const loading = new Set(loadingJobs);
          loading.delete(workerId);
          setLoadingJobs(loading);
        }
      }
    }
  };

  const getStatusBadgeVariant = (status: string) => {
    switch (status) {
      case WorkerStatus.Online:
        return 'success';
      case WorkerStatus.Busy:
        return 'warning';
      case WorkerStatus.Offline:
        return 'default';
      case WorkerStatus.Error:
        return 'error';
      case WorkerStatus.Draining:
        return 'info';
      default:
        return 'default';
    }
  };

  if (loading) {
    return (
      <PageTemplate
        title="Worker Management"
        subtitle="Monitor and manage your Slicer workers"
        icon={Wrench}
        maxWidth="max-w-7xl"
      >
        <div className="flex items-center justify-center py-12">
          <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-pf-accent"></div>
        </div>
      </PageTemplate>
    );
  }

  return (
    <PageTemplate
      title="Worker Management"
      subtitle="Monitor and manage your Slicer workers"
      icon={Wrench}
      maxWidth="max-w-7xl"
      actions={
        <Button
          variant="secondary"
          size="md"
          onClick={loadWorkers}
          iconLeft={<RefreshCw className="h-4 w-4" />}
        >
          Refresh
        </Button>
      }
    >
      {/* Connection status */}
      <div className="mb-4 flex items-center gap-2">
        <div className={`w-2 h-2 rounded-full ${isConnected ? 'bg-pf-success' : 'bg-pf-text-muted'}`}></div>
        <span className="text-sm text-pf-text-secondary">
          {isConnected ? 'Real-time updates active' : 'Polling mode (SignalR disconnected)'}
        </span>
      </div>

      {error && (
        <Alert type="error" className="mb-4" onClose={() => setError(null)}>
          {error}
        </Alert>
      )}

      {/* Filter tabs */}
      <div className="mb-4 flex gap-2 flex-wrap">
        <Button
          variant={filter === 'all' ? 'primary' : 'secondary'}
          size="sm"
          onClick={() => setFilter('all')}
        >
          All ({workers.length})
        </Button>
        {Object.values(WorkerStatus).map(status => (
          <Button
            key={status}
            variant={filter === status ? 'primary' : 'secondary'}
            size="sm"
            onClick={() => setFilter(status as WorkerStatus)}
          >
            {status} ({workers.filter(w => w.status === status).length})
          </Button>
        ))}
      </div>

      {/* Workers table */}
      <div className="bg-pf-bg-1 rounded border border-pf-border overflow-hidden">
        <table className="w-full">
          <thead className="bg-pf-bg-2 border-b border-pf-border">
            <tr>
              <th className="px-6 py-3 text-left text-xs font-medium text-pf-text-primary uppercase tracking-wider">Worker</th>
              <th className="px-6 py-3 text-left text-xs font-medium text-pf-text-primary uppercase tracking-wider">Status</th>
              <th className="px-6 py-3 text-left text-xs font-medium text-pf-text-primary uppercase tracking-wider">Capacity</th>
              <th className="px-6 py-3 text-left text-xs font-medium text-pf-text-primary uppercase tracking-wider">Statistics</th>
              <th className="px-6 py-3 text-left text-xs font-medium text-pf-text-primary uppercase tracking-wider">Performance</th>
              <th className="px-6 py-3 text-left text-xs font-medium text-pf-text-primary uppercase tracking-wider">Actions</th>
            </tr>
          </thead>
          <tbody className="divide-y divide-pf-border">
            {workers.map(worker => (
              <>
                <tr key={worker.id} className="hover:bg-pf-bg-2 transition-colors">
                  <td className="px-6 py-4 whitespace-nowrap">
                    <div className="flex items-start gap-2">
                      <Button
                        onClick={() => handleToggleExpanded(worker.id)}
                        variant="subtle"
                      >
                        {expandedWorker === worker.id ? (
                          <ChevronDown size={18} />
                        ) : (
                          <ChevronRight size={18} />
                        )}
                      </Button>
                      <div className="flex flex-col">
                        <div className="text-sm font-medium text-pf-text-primary">{worker.name}</div>
                        <div className="text-xs text-pf-text-secondary">{worker.endpointUrl}</div>
                        <div className="text-xs text-pf-text-tertiary mt-1">
                          {worker.capabilities.join(', ')}
                        </div>
                      </div>
                    </div>
                  </td>
                <td className="px-6 py-4 whitespace-nowrap">
                  <Badge variant={getStatusBadgeVariant(worker.status)}>
                    {worker.status}
                  </Badge>
                  {worker.isDisabled && (
                    <div className="text-xs text-pf-error mt-1">
                      Disabled: {worker.disabledReason}
                    </div>
                  )}
                  {workerService.isHeartbeatStale(worker) && (
                    <div className="text-xs text-pf-warning mt-1">
                      ⚠️ Stale heartbeat
                    </div>
                  )}
                </td>
                <td className="px-6 py-4 whitespace-nowrap">
                  <div className="text-sm text-pf-text-primary">
                    {worker.totalSlots - worker.freeSlots} / {worker.totalSlots} slots used
                  </div>
                  <div className="text-xs text-pf-text-secondary">
                    {workerService.calculateUtilization(worker).toFixed(0)}% utilization
                  </div>
                  <ProgressBar
                    value={workerService.calculateUtilization(worker)}
                    size="xs"
                    showPercent={false}
                    className="mt-1"
                  />
                </td>
                <td className="px-6 py-4 whitespace-nowrap">
                  <div className="text-sm text-pf-text-primary">
                    Active: {worker.activeJobs}
                  </div>
                  <div className="text-xs text-pf-success">
                    ✓ Completed: {worker.completedJobs}
                  </div>
                  <div className="text-xs text-pf-error">
                    ✗ Failed: {worker.failedJobs}
                  </div>
                  <div className="text-xs text-pf-text-secondary">
                    Success: {(workerService.calculateSuccessRate(worker) ?? 0).toFixed(1)}%
                  </div>
                </td>
                <td className="px-6 py-4 whitespace-nowrap">
                  <div className="text-sm text-pf-text-primary">
                    Avg: {(worker.averageProcessingTimeSeconds ?? 0).toFixed(0)}s
                  </div>
                  <div className="text-xs text-pf-text-secondary">
                    Uptime: {workerService.getUptime(worker)}
                  </div>
                  <div className="text-xs text-pf-text-tertiary">
                    v{worker.version}
                  </div>
                </td>
                <td className="px-6 py-4 whitespace-nowrap text-sm">
                  <div className="flex gap-2">
                    {worker.isDisabled ? (
                      <Button
                        variant="success"
                        size="sm"
                        onClick={() => handleEnableWorker(worker)}
                      >
                        Enable
                      </Button>
                    ) : (
                      <Button
                        variant="subtle"
                        size="sm"
                        onClick={() => {
                          setSelectedWorker(worker);
                          setShowDisableDialog(true);
                        }}
                      >
                        Disable
                      </Button>
                    )}
                    <Button
                      variant="danger"
                      size="sm"
                      onClick={() => handleDeleteWorker(worker)}
                    >
                      Delete
                    </Button>
                    <Button
                      variant="primary"
                      size="sm"
                      onClick={() => handleEditSlots(worker)}
                    >
                      Edit Slots
                    </Button>
                  </div>
                </td>
              </tr>

              {/* Jobs expansion row */}
              {expandedWorker === worker.id && (
                <tr className="bg-pf-bg-2">
                  <td colSpan={6} className="px-6 py-4">
                    <div className="ml-8">
                      {loadingJobs.has(worker.id) ? (
                        <div className="text-sm text-pf-text-secondary">Loading jobs...</div>
                      ) : workerJobs.get(worker.id)?.length === 0 ? (
                        <div className="text-sm text-pf-text-secondary">No active jobs</div>
                      ) : (
                        <div className="space-y-2">
                          <h4 className="text-sm font-medium text-pf-text-primary mb-3">Active Slicing Jobs:</h4>
                          <div className="space-y-2">
                            {workerJobs.get(worker.id)?.map(job => (
                              <div key={job.jobId} className="bg-pf-bg-1 rounded p-3 border border-pf-border">
                                <div className="flex justify-between items-start mb-2">
                                  <div className="flex-1">
                                    <div className="text-sm font-medium text-pf-text-primary">{job.modelFileName}</div>
                                    <div className="text-xs text-pf-text-secondary">Job ID: {job.jobId}</div>
                                  </div>
                                  <Badge variant={job.status === 'completed' ? 'success' : job.status === 'failed' ? 'error' : 'default'}>
                                    {job.status}
                                  </Badge>
                                </div>
                                <div className="mb-2">
                                  <div className="text-xs text-pf-text-secondary mb-1">
                                    Progress: {job.progressPercent}%
                                  </div>
                                  <ProgressBar value={job.progressPercent} size="sm" showPercent={false} />
                                </div>
                                {job.progressMessage && (
                                  <div className="text-xs text-pf-text-secondary">{job.progressMessage}</div>
                                )}
                                {job.startedAt && (
                                  <div className="text-xs text-pf-text-tertiary mt-1">
                                    Started: {new Date(job.startedAt).toLocaleString()}
                                  </div>
                                )}
                              </div>
                            ))}
                          </div>
                        </div>
                      )}
                    </div>
                  </td>
                </tr>
              )}
            </>
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
        <Modal
          isOpen={true}
          onClose={() => {
            setShowDisableDialog(false);
            setDisableReason('');
            setSelectedWorker(null);
          }}
          title="Disable Worker"
        >
          <div>
            <p className="mb-4">
              Disable worker: <strong>{selectedWorker.name}</strong>
            </p>
            <div className="mb-4">
              <Label htmlFor="disable-reason">Reason (required)</Label>
              <Textarea
                id="disable-reason"
                value={disableReason}
                onChange={(e) => setDisableReason(e.target.value)}
                rows={3}
                placeholder="Enter reason for disabling this worker..."
              />
            </div>
          </div>
          <div className="flex gap-2 justify-end mt-6">
            <Button
              variant="secondary"
              onClick={() => {
                setShowDisableDialog(false);
                setDisableReason('');
                setSelectedWorker(null);
              }}
            >
              Cancel
            </Button>
            <Button
              variant="danger"
              disabled={!disableReason.trim()}
              onClick={handleDisableWorker}
            >
              Disable Worker
            </Button>
          </div>
        </Modal>
      )}

      {/* Edit Slots Dialog */}
      {showEditSlotsDialog && selectedWorker && (
        <Modal
          isOpen={true}
          onClose={() => {
            setShowEditSlotsDialog(false);
            setEditSlotsValue('');
            setSelectedWorker(null);
          }}
          title="Edit Worker Slots"
        >
          <div>
            <p className="mb-4">
              Update total slots for worker: <strong>{selectedWorker.name}</strong>
            </p>
            <div className="mb-4">
              <Label htmlFor="edit-slots-value">Total Slots</Label>
              <Input
                id="edit-slots-value"
                type="number"
                min="1"
                value={editSlotsValue}
                onChange={(e) => setEditSlotsValue(e.target.value)}
                placeholder="Enter total slots..."
              />
              <p className="text-xs text-pf-text-tertiary mt-2">
                Current: {selectedWorker.totalSlots} slots | Active: {selectedWorker.activeJobs}
              </p>
            </div>
          </div>
          <div className="flex gap-2 justify-end mt-6">
            <Button
              variant="secondary"
              onClick={() => {
                setShowEditSlotsDialog(false);
                setEditSlotsValue('');
                setSelectedWorker(null);
              }}
            >
              Cancel
            </Button>
            <Button
              variant="primary"
              disabled={!editSlotsValue || parseInt(editSlotsValue, 10) < 1}
              onClick={handleUpdateSlots}
            >
              Update Slots
            </Button>
          </div>
        </Modal>
      )}
    </PageTemplate>
  );
}

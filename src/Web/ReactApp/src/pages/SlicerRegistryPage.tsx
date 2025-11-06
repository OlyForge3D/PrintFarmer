import { useEffect, useState } from 'react';
import { HubConnection, HubConnectionBuilder } from '@microsoft/signalr';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import workersService, { WorkerJobResponse } from '@/services/workersService';
import { WorkerResponse } from '@/types/worker';
import { getWorkerStatusColor, formatWorkerCapacity } from '@/types/worker';
import { getHubUrl } from '@/utils/apiUrlHelpers';

export default function SlicerRegistryPage() {
  const qc = useQueryClient();
  const [connection, setConnection] = useState<HubConnection | null>(null);
  const [selectedWorkerId, setSelectedWorkerId] = useState<string | null>(null);

  // Fetch workers using React Query
  const { data: workers = [], isLoading: loading, error, refetch: loadServices } = useQuery<WorkerResponse[], Error>({
    queryKey: ['workers-all'],
    queryFn: () => workersService.getAllWorkers(100),
    staleTime: 10_000,
    refetchInterval: 30_000, // Auto-refresh every 30 seconds
  });

  // Fetch jobs for selected worker
  const { data: workerJobs = [] } = useQuery<WorkerJobResponse[], Error>({
    queryKey: ['worker-jobs', selectedWorkerId],
    queryFn: () => (selectedWorkerId ? workersService.getWorkerJobs(selectedWorkerId) : Promise.resolve([])),
    enabled: !!selectedWorkerId,
    staleTime: 5_000,
    refetchInterval: 10_000, // Auto-refresh job list every 10 seconds
  });

  useEffect(() => {
    setupSignalR();
    
    // Cleanup on unmount
    return () => {
      if (connection) {
        connection.stop();
      }
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const setupSignalR = async () => {
    const hubConnection = new HubConnectionBuilder()
      .withUrl(getHubUrl('/hubs/slicer-registry'))
      .withAutomaticReconnect()
      .build();

    // Handle slicer registered event
    hubConnection.on('SlicerRegistered', () => {
      qc.invalidateQueries({ queryKey: ['workers-all'] });
    });

    // Handle slicer heartbeat event
    hubConnection.on('SlicerHeartbeat', () => {
      qc.invalidateQueries({ queryKey: ['workers-all'] });
      if (selectedWorkerId) {
        qc.invalidateQueries({ queryKey: ['worker-jobs', selectedWorkerId] });
      }
    });

    // Handle slicer deregistered event
    hubConnection.on('SlicerDeregistered', () => {
      qc.invalidateQueries({ queryKey: ['workers-all'] });
    });

    try {
      await hubConnection.start();
      console.log('Connected to SlicerHub');
      setConnection(hubConnection);
    } catch (err) {
      console.error('SignalR connection error:', err);
    }
  };

  const formatLastSeen = (lastHeartbeat?: string): string => {
    if (!lastHeartbeat) return 'Never';
    const date = new Date(lastHeartbeat);
    const now = new Date();
    const diffMs = now.getTime() - date.getTime();
    const diffSecs = Math.floor(diffMs / 1000);
    
    if (diffSecs < 60) return `${diffSecs}s ago`;
    if (diffSecs < 3600) return `${Math.floor(diffSecs / 60)}m ago`;
    if (diffSecs < 86400) return `${Math.floor(diffSecs / 3600)}h ago`;
    return `${Math.floor(diffSecs / 86400)}d ago`;
  };

  if (loading) {
    return (
      <div className="loading-overlay">
        <div className="spinner lg" />
      </div>
    );
  }

  if (error) {
    return (
      <div className="p-6">
        <div className="alert-base alert-error">
          <div>
            <div className="alert-title">Error Loading Workers</div>
            <p>{error.message}</p>
            <button
              onClick={() => loadServices()}
              className="btn-base btn-md btn-danger mt-4"
            >
              Retry
            </button>
          </div>
        </div>
      </div>
    );
  }

  return (
    <div className="p-6">
      <div className="mb-6">
        <h1 className="text-3xl font-bold text-gray-900 mb-2">Slicer Worker Registry</h1>
        <p className="text-gray-600">
          Manage and monitor registered slicer workers. Workers auto-register on startup and send periodic heartbeats.
        </p>
        {connection?.state === 'Connected' && (
          <div className="mt-2 flex items-center text-sm text-green-600">
            <span className="inline-block w-2 h-2 bg-green-500 rounded-full mr-2 animate-pulse"></span>
            Real-time updates active
          </div>
        )}
      </div>

      {workers.length === 0 ? (
        <div className="card text-center">
          <svg className="mx-auto h-12 w-12 text-pf-text-tertiary mb-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 12h6m-6 4h6m2 5H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z" />
          </svg>
          <h3 className="text-lg font-medium text-pf-text-primary mb-2">No Registered Workers</h3>
          <p className="text-pf-text-secondary">
            Workers will appear here when they register with the API. Check worker configuration and network connectivity.
          </p>
        </div>
      ) : (
        <div className="grid gap-6 md:grid-cols-2 lg:grid-cols-3">
          {workers.map((worker) => (
            <div 
              key={worker.id} 
              className="card clickable"
              onClick={() => setSelectedWorkerId(worker.id === selectedWorkerId ? null : worker.id)}
            >
              {/* Header */}
              <div className="flex items-start justify-between mb-4">
                <div className="flex-1">
                  <h3 className="text-lg font-semibold text-gray-900 mb-1">{worker.name}</h3>
                  <p className="text-sm text-gray-600">{formatWorkerCapacity(worker)}</p>
                </div>
                <div className="flex items-center gap-2">
                  <span className={`inline-block w-3 h-3 rounded-full ${getWorkerStatusColor(worker.status)}`}></span>
                  <span className="text-sm font-medium text-gray-700">{worker.status}</span>
                </div>
              </div>

              {/* Details */}
              <div className="space-y-2 mb-4">
                <div className="flex justify-between text-sm">
                  <span className="text-gray-600">Version:</span>
                  <span className="font-medium text-gray-900">{worker.version}</span>
                </div>
                <div className="flex justify-between text-sm">
                  <span className="text-gray-600">Active Jobs:</span>
                  <span className="font-medium text-gray-900">{worker.activeJobs} / {worker.totalSlots}</span>
                </div>
                <div className="flex justify-between text-sm">
                  <span className="text-gray-600">Completed:</span>
                  <span className="font-medium text-gray-900">{worker.completedJobs}</span>
                </div>
                <div className="flex justify-between text-sm">
                  <span className="text-gray-600">Last Seen:</span>
                  <span className="font-medium text-gray-900">{formatLastSeen(worker.lastHeartbeat)}</span>
                </div>
              </div>

              {/* Capabilities */}
              {worker.capabilities.length > 0 && (
                <div className="mb-4">
                  <h4 className="text-xs font-semibold text-gray-700 uppercase mb-2">Capabilities</h4>
                  <div className="gap-sm flex-row flex-wrap">
                    {worker.capabilities.map((cap, idx) => (
                      <span
                        key={idx}
                        className="status-badge success"
                      >
                        {cap}
                      </span>
                    ))}
                  </div>
                </div>
              )}

              {/* Active Jobs Section */}
              {selectedWorkerId === worker.id && (
                <div className="mt-4 pt-4 border-t border-gray-200">
                  <h4 className="text-xs font-semibold text-gray-700 uppercase mb-2">
                    Active Jobs ({workerJobs.length})
                  </h4>
                  {workerJobs.length === 0 ? (
                    <p className="text-sm text-gray-500 italic">No active jobs</p>
                  ) : (
                    <div className="space-y-2">
                      {workerJobs.map((job) => (
                        <div key={job.jobId} className="bg-gray-50 rounded p-2 text-xs">
                          <div className="flex justify-between items-start mb-1">
                            <span className="font-medium text-gray-900 truncate flex-1">{job.modelFileName}</span>
                            <span className="text-gray-600 ml-2">{job.progressPercent}%</span>
                          </div>
                          {job.progressMessage && (
                            <p className="text-gray-600 truncate">{job.progressMessage}</p>
                          )}
                          <div className="mt-1 w-full bg-gray-200 rounded-full h-1">
                            <div 
                              className="bg-blue-600 h-1 rounded-full transition-all progress-width"
                              style={{ '--progress-width': `${job.progressPercent}%` } as React.CSSProperties}
                            ></div>
                          </div>
                        </div>
                      ))}
                    </div>
                  )}
                </div>
              )}

              {/* Endpoint */}
              <div className="text-xs text-gray-500 truncate mt-2" title={worker.endpointUrl}>
                {worker.endpointUrl}
              </div>
            </div>
          ))}
        </div>
      )}

      {/* Stats Footer */}
      <div className="card mt-8">
        <div className="grid grid-cols-2 md:grid-cols-4 gap-4 text-center">
          <div>
            <div className="text-2xl font-bold text-gray-900">{workers.length}</div>
            <div className="text-sm text-gray-600">Total Workers</div>
          </div>
          <div>
            <div className="text-2xl font-bold text-green-600">{workers.filter(w => w.status === 'Online').length}</div>
            <div className="text-sm text-gray-600">Online</div>
          </div>
          <div>
            <div className="text-2xl font-bold text-gray-600">{workers.filter(w => w.status === 'Offline').length}</div>
            <div className="text-sm text-gray-600">Offline</div>
          </div>
          <div>
            <div className="text-2xl font-bold text-blue-600">
              {workers.reduce((sum, w) => sum + w.totalSlots, 0)}
            </div>
            <div className="text-sm text-gray-600">Total Capacity</div>
          </div>
        </div>
      </div>
    </div>
  );
}

import { useEffect, useState } from 'react';
import { sliceJobService, SliceJobStatusResponse, SliceJobStatus } from '@/services/sliceJobService';

export default function JobQueueDashboardPage() {
  const [jobs, setJobs] = useState<SliceJobStatusResponse[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [filter, setFilter] = useState<'all' | 'my' | SliceJobStatus>('all');
  const [showQueue, setShowQueue] = useState(false);

  useEffect(() => {
    loadJobs();
    // Refresh every 5 seconds
    const interval = setInterval(loadJobs, 5000);
    return () => clearInterval(interval);
  }, [filter]);

  const loadJobs = async () => {
    try {
      setError(null);
      let data: SliceJobStatusResponse[];
      
      if (filter === 'my') {
        data = await sliceJobService.getMyJobs();
      } else if (filter === 'all' && showQueue) {
        data = await sliceJobService.getQueue();
      } else if (filter === 'all') {
        data = await sliceJobService.getMyJobs();
      } else {
        // Filter by status client-side (backend doesn't have status filter endpoint yet)
        const allJobs = await sliceJobService.getMyJobs();
        data = allJobs.filter(job => job.status === filter);
      }
      
      setJobs(data);
      setLoading(false);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to load jobs');
      setLoading(false);
    }
  };

  const handleCancelJob = async (jobId: string) => {
    if (!confirm('Are you sure you want to cancel this job?')) return;
    
    try {
      await sliceJobService.cancelJob(jobId);
      loadJobs();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to cancel job');
    }
  };

  const handleViewQueue = async () => {
    setShowQueue(true);
    setFilter('all');
  };

  if (loading) {
    return (
      <div className="container mx-auto p-6">
        <div className="flex items-center justify-center h-64">
          <div className="text-lg">Loading jobs...</div>
        </div>
      </div>
    );
  }

  return (
    <div className="container mx-auto p-6">
      <div className="flex justify-between items-center mb-6">
        <h1 className="text-3xl font-bold">Slice Job Queue</h1>
        <div className="flex space-x-2">
          <button
            onClick={handleViewQueue}
            className="px-4 py-2 bg-purple-600 text-white rounded hover:bg-purple-700"
          >
            View Full Queue
          </button>
          <button
            onClick={loadJobs}
            className="px-4 py-2 bg-blue-600 text-white rounded hover:bg-blue-700"
          >
            Refresh
          </button>
        </div>
      </div>

      {error && (
        <div className="mb-4 p-4 bg-red-100 border border-red-400 text-red-700 rounded">
          {error}
        </div>
      )}

      {/* Filter tabs */}
      <div className="mb-4 flex space-x-2 overflow-x-auto">
        <button
          onClick={() => {
            setFilter('all');
            setShowQueue(false);
          }}
          className={`px-4 py-2 rounded whitespace-nowrap ${filter === 'all' && !showQueue ? 'bg-blue-600 text-white' : 'bg-gray-200'}`}
        >
          My Jobs
        </button>
        {Object.values(SliceJobStatus).map(status => (
          <button
            key={status}
            onClick={() => {
              setFilter(status);
              setShowQueue(false);
            }}
            className={`px-4 py-2 rounded whitespace-nowrap ${filter === status ? 'bg-blue-600 text-white' : 'bg-gray-200'}`}
          >
            {status}
          </button>
        ))}
      </div>

      {/* Jobs list */}
      <div className="space-y-4">
        {jobs.map(job => (
          <div key={job.id} className="bg-white rounded-lg shadow p-6">
            <div className="flex justify-between items-start mb-4">
              <div className="flex-1">
                <div className="flex items-center space-x-3 mb-2">
                  <h3 className="text-lg font-semibold">Job {job.id.substring(0, 8)}</h3>
                  <span className={`px-3 py-1 rounded-full text-sm font-medium ${sliceJobService.getStatusColor(job.status as SliceJobStatus)}`}>
                    {sliceJobService.getStatusText(job.status as SliceJobStatus)}
                  </span>
                </div>
                {job.progressMessage && (
                  <p className="text-gray-600 mb-2">{job.progressMessage}</p>
                )}
                <div className="text-sm text-gray-500">
                  Queued: {new Date(job.queuedAt).toLocaleString()}
                </div>
                {job.startedAt && (
                  <div className="text-sm text-gray-500">
                    Started: {new Date(job.startedAt).toLocaleString()}
                  </div>
                )}
                {job.completedAt && (
                  <div className="text-sm text-gray-500">
                    Completed: {new Date(job.completedAt).toLocaleString()}
                  </div>
                )}
              </div>
              
              <div className="text-right">
                {(job.status === SliceJobStatus.Queued || job.status === SliceJobStatus.Processing) && (
                  <button
                    onClick={() => handleCancelJob(job.id)}
                    className="px-4 py-2 bg-red-600 text-white rounded hover:bg-red-700"
                  >
                    Cancel
                  </button>
                )}
              </div>
            </div>

            {/* Progress bar for processing jobs */}
            {job.status === SliceJobStatus.Processing && (
              <div className="mb-4">
                <div className="flex justify-between text-sm text-gray-600 mb-1">
                  <span>Progress: {job.progressPercent}%</span>
                  {job.startedAt && (
                    <span>
                      ETA: {sliceJobService.getEstimatedTimeRemaining(job) || 'Calculating...'}
                    </span>
                  )}
                </div>
                <div className="w-full bg-gray-200 rounded-full h-2">
                  <div
                    className="bg-blue-600 h-2 rounded-full transition-all duration-300"
                    style={{ width: `${job.progressPercent}%` }}
                  />
                </div>
              </div>
            )}

            {/* Job details */}
            <div className="grid grid-cols-2 md:grid-cols-4 gap-4 mt-4 pt-4 border-t">
              {job.workerId && (
                <div>
                  <div className="text-xs text-gray-500">Worker</div>
                  <div className="text-sm font-medium">{job.workerId.substring(0, 8)}</div>
                </div>
              )}
              {job.estimatedPrintTimeSeconds !== undefined && (
                <div>
                  <div className="text-xs text-gray-500">Est. Print Time</div>
                  <div className="text-sm font-medium">
                    {sliceJobService.formatPrintTime(job.estimatedPrintTimeSeconds)}
                  </div>
                </div>
              )}
              {job.filamentUsedGrams !== undefined && (
                <div>
                  <div className="text-xs text-gray-500">Filament</div>
                  <div className="text-sm font-medium">
                    {sliceJobService.formatFilamentUsed(job.filamentUsedGrams)}
                  </div>
                </div>
              )}
              {job.resultFileUrl && (
                <div>
                  <div className="text-xs text-gray-500">Result</div>
                  <a
                    href={job.resultFileUrl}
                    target="_blank"
                    rel="noopener noreferrer"
                    className="text-sm font-medium text-blue-600 hover:text-blue-800"
                  >
                    Download G-code
                  </a>
                </div>
              )}
            </div>

            {/* Error message */}
            {job.status === SliceJobStatus.Failed && job.errorMessage && (
              <div className="mt-4 p-3 bg-red-50 border border-red-200 rounded">
                <div className="text-xs text-gray-500 mb-1">Error</div>
                <div className="text-sm text-red-700">{job.errorMessage}</div>
              </div>
            )}
          </div>
        ))}

        {jobs.length === 0 && (
          <div className="bg-white rounded-lg shadow p-12 text-center text-gray-500">
            No jobs found
          </div>
        )}
      </div>
    </div>
  );
}

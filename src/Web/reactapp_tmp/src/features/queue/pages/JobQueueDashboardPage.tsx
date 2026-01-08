import { useEffect, useState } from 'react';
import { sliceJobService, SliceJobStatusResponse, SliceJobStatus } from '@/services/sliceJobService';
import { ConfirmationModal } from '@/common/components/modals/ConfirmationModal';
import { PageTemplate } from '@/common/components/PageTemplate';
import { Button } from '@/common/components/ui/Button';
import { Alert } from '@/common/components/ui/Alert';
import { ProgressBar } from '@/common/components/ui/ProgressBar';

export function JobQueueDashboardPage() {
  const [jobs, setJobs] = useState<SliceJobStatusResponse[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [filter, setFilter] = useState<'all' | 'my' | SliceJobStatus>('all');
  const [showQueue, setShowQueue] = useState(false);
  const [showCancelConfirmation, setShowCancelConfirmation] = useState(false);
  const [jobToCancel, setJobToCancel] = useState<string | null>(null);

  useEffect(() => {
    loadJobs();
    const interval = setInterval(loadJobs, 5000);
    return () => clearInterval(interval);
    // eslint-disable-next-line react-hooks/exhaustive-deps
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

  const handleCancelJob = (jobId: string) => {
    setJobToCancel(jobId);
    setShowCancelConfirmation(true);
  };

  const handleConfirmCancel = async () => {
    if (!jobToCancel) return;
    
    try {
      await sliceJobService.cancelJob(jobToCancel);
      loadJobs();
      setShowCancelConfirmation(false);
      setJobToCancel(null);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to cancel job');
      setShowCancelConfirmation(false);
      setJobToCancel(null);
    }
  };

  const handleViewQueue = async () => {
    setShowQueue(true);
    setFilter('all');
  };

  return (
    <PageTemplate
      title="Slice Job Queue"
      subtitle={showQueue ? 'Viewing full queue (all jobs).' : 'Your slice jobs and progress.'}
    >
      {loading && <div className="h-32 flex items-center justify-center text-sm">Loading jobs…</div>}
      {!loading && error && <Alert type="error">{error}</Alert>}
      <div className="flex flex-wrap gap-2 mb-4">
        <Button variant="success" onClick={() => { window.location.href = '/jobs/new'; }}>New Job</Button>
        <Button variant="secondary" onClick={handleViewQueue}>View Full Queue</Button>
        <Button variant="primary" onClick={loadJobs}>Refresh</Button>
      </div>

      {/* Filter tabs */}
      <div className="mb-4 flex flex-wrap gap-2 overflow-x-auto">
        <Button
          variant={filter === 'all' && !showQueue ? 'primary' : 'secondary'}
          onClick={() => {
            setFilter('all');
            setShowQueue(false);
          }}
          className="whitespace-nowrap"
        >
          My Jobs
        </Button>
        {Object.values(SliceJobStatus).map(status => (
          <Button
            key={status}
            variant={filter === status ? 'primary' : 'secondary'}
            onClick={() => {
              setFilter(status);
              setShowQueue(false);
            }}
            className="whitespace-nowrap"
          >
            {status}
          </Button>
        ))}
      </div>

      {/* Jobs list */}
      <div className="space-y-4">
        {!loading && jobs.map(job => (
          <div key={job.id} className="bg-pf-panel rounded-lg shadow p-6 border border-pf-border">
            <div className="flex justify-between items-start mb-4">
              <div className="flex-1">
                <div className="flex items-center space-x-3 mb-2">
                  <h3 className="text-lg font-semibold">Job {job.id.substring(0, 8)}</h3>
                  <span className={`px-3 py-1 rounded-full text-sm font-medium ${sliceJobService.getStatusColor(job.status as SliceJobStatus)}`}>
                    {sliceJobService.getStatusText(job.status as SliceJobStatus)}
                  </span>
                </div>
                {job.progressMessage && (
                  <p className="text-pf-text-secondary mb-2">{job.progressMessage}</p>
                )}
                <div className="text-sm text-pf-text-muted">
                  Queued: {new Date(job.queuedAt).toLocaleString()}
                </div>
                {job.startedAt && (
                  <div className="text-sm text-pf-text-muted">
                    Started: {new Date(job.startedAt).toLocaleString()}
                  </div>
                )}
                {job.completedAt && (
                  <div className="text-sm text-pf-text-muted">
                    Completed: {new Date(job.completedAt).toLocaleString()}
                  </div>
                )}
              </div>
              
              <div className="text-right">
                {(job.status === SliceJobStatus.Queued || job.status === SliceJobStatus.Processing) && (
                  <Button variant="danger" onClick={() => handleCancelJob(job.id)}>Cancel</Button>
                )}
              </div>
            </div>

            {/* Progress bar for processing jobs */}
            {job.status === SliceJobStatus.Processing && (
              <div className="mb-4">
                <ProgressBar
                  value={job.progressPercent}
                  label={job.startedAt ? `ETA: ${sliceJobService.getEstimatedTimeRemaining(job) || 'Calculating…'}` : 'Processing'}
                  size="sm"
                  color="blue"
                />
              </div>
            )}

            {/* Completed job - prominent download section */}
            {job.status === SliceJobStatus.Completed && job.resultFileUrl && (
              <div className="mt-4 p-4 bg-green-50 dark:bg-green-900/20 border border-green-200 dark:border-green-800 rounded-lg">
                <div className="flex items-center justify-between">
                  <div className="flex-1">
                    <h4 className="text-sm font-semibold text-green-900 dark:text-green-100 mb-1">✓ Slicing Complete</h4>
                    <p className="text-xs text-green-700 dark:text-green-300">Your G-code is ready to download</p>
                  </div>
                  <a
                    href={job.resultFileUrl}
                    download
                    className="px-6 py-3 bg-green-600 hover:bg-green-700 text-white font-medium rounded-lg transition-colors inline-flex items-center gap-2"
                  >
                    <svg className="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M4 16v1a3 3 0 003 3h10a3 3 0 003-3v-1m-4-4l-4 4m0 0l-4-4m4 4V4" />
                    </svg>
                    Download G-code
                  </a>
                </div>
              </div>
            )}

            {/* Job details */}
            <div className="grid grid-cols-2 md:grid-cols-4 gap-4 mt-4 pt-4 border-t border-pf-border">
              {job.workerId && (
                <div>
                  <div className="text-xs text-pf-text-muted">Worker</div>
                  <div className="text-sm font-medium">{job.workerId.substring(0, 8)}</div>
                </div>
              )}
              {job.estimatedPrintTimeSeconds !== undefined && (
                <div>
                  <div className="text-xs text-pf-text-muted">Est. Print Time</div>
                  <div className="text-sm font-medium">
                    {sliceJobService.formatPrintTime(job.estimatedPrintTimeSeconds)}
                  </div>
                </div>
              )}
              {job.filamentUsedGrams !== undefined && (
                <div>
                  <div className="text-xs text-pf-text-muted">Filament</div>
                  <div className="text-sm font-medium">
                    {sliceJobService.formatFilamentUsed(job.filamentUsedGrams)}
                  </div>
                </div>
              )}
              {job.artifactsCount !== undefined && job.artifactsCount > 0 && (
                <div>
                  <div className="text-xs text-pf-text-muted">Artifacts</div>
                  <div className="text-sm font-medium">
                    {job.artifactsCount} file{job.artifactsCount !== 1 ? 's' : ''}
                    {job.artifactsTotalBytes && ` (${sliceJobService.formatFileSize(job.artifactsTotalBytes)})`}
                  </div>
                </div>
              )}
            </div>

            {/* Error message */}
            {job.status === SliceJobStatus.Failed && job.errorMessage && (
              <div className="mt-4 p-3 bg-pf-error-bg border border-pf-error-border rounded">
                <div className="text-xs text-pf-text-muted mb-1">Error</div>
                <div className="text-sm text-pf-error-text">{job.errorMessage}</div>
              </div>
            )}
          </div>
        ))}

        {!loading && jobs.length === 0 && (
          <div className="bg-pf-panel rounded-lg shadow p-12 text-center text-pf-text-muted border border-pf-border">
            No jobs found
          </div>
        )}
      </div>

      <ConfirmationModal
        isOpen={showCancelConfirmation}
        title="Cancel Job"
        message="Are you sure you want to cancel this job?"
        confirmButtonText="Cancel Job"
        cancelButtonText="Keep Job"
        isDangerous={true}
        onConfirm={handleConfirmCancel}
        onCancel={() => {
          setShowCancelConfirmation(false);
          setJobToCancel(null);
        }}
      />
    </PageTemplate>
  );
}

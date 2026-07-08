import { useState, useMemo } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from 'react-router';
import { toast } from 'sonner';
import clsx from 'clsx';
import { Button } from '@/common/components/ui/Button';
import { Badge } from '@/common/components/ui/Badge';
import { Card } from '@/common/components/ui/Card';
import { Spinner } from '@/common/components/ui/Spinner';
import { Select } from '@/common/components/ui/Select';
import { ProgressBar } from '@/common/components/ui/ProgressBar';
import {
  GridIcon,
  TableIcon,
  DownloadIcon,
  CloseIcon,
  LayersIcon,
  PrinterIcon,
  RefreshIcon,
  EyeIcon,
  ExternalLinkIcon,
} from '@/common/components/icons/MdiIcons';
import { getApiBaseUrl } from '@/common/utils/apiUrlHelpers';
import { useViewModePreference } from '@/common/hooks/useViewModePreference';
import {
  sliceJobService,
  SliceJobStatus,
  SliceJobStatusResponse,
} from '@/services/sliceJobService';
import { useSliceJobsRealtime } from '@/features/slicer/hooks/useSliceJobsRealtime';
import { SendToPrinterModal } from '@/features/slicer/components/SendToPrinterModal';
import { GcodePreviewModal } from '@/features/slicer/components/GcodePreviewModal';
import type { BadgeVariant } from '@/common/components/ui/Badge';

type StatusFilter = 'all' | SliceJobStatus;

const STATUS_FILTERS: { value: StatusFilter; label: string }[] = [
  { value: 'all', label: 'All Jobs' },
  { value: SliceJobStatus.Queued, label: 'Queued' },
  { value: SliceJobStatus.Processing, label: 'Processing' },
  { value: SliceJobStatus.Completed, label: 'Completed' },
  { value: SliceJobStatus.Failed, label: 'Failed' },
  { value: SliceJobStatus.Cancelled, label: 'Cancelled' },
];

function getStatusBadgeVariant(status: string): BadgeVariant {
  switch (status) {
    case SliceJobStatus.Queued: return 'default';
    case SliceJobStatus.Processing: return 'primary';
    case SliceJobStatus.Completed: return 'success';
    case SliceJobStatus.Failed: return 'error';
    case SliceJobStatus.Cancelled: return 'warning';
    default: return 'default';
  }
}

function formatRelativeTime(dateStr: string): string {
  const date = new Date(dateStr);
  const now = new Date();
  const diffMs = now.getTime() - date.getTime();
  const diffSec = Math.floor(diffMs / 1000);

  if (diffSec < 60) return 'just now';
  if (diffSec < 3600) return `${Math.floor(diffSec / 60)}m ago`;
  if (diffSec < 86400) return `${Math.floor(diffSec / 3600)}h ago`;
  return `${Math.floor(diffSec / 86400)}d ago`;
}

function formatDuration(startStr?: string, endStr?: string): string {
  if (!startStr) return '—';
  const start = new Date(startStr).getTime();
  const end = endStr ? new Date(endStr).getTime() : Date.now();
  const sec = Math.floor((end - start) / 1000);

  if (sec < 60) return `${sec}s`;
  if (sec < 3600) return `${Math.floor(sec / 60)}m ${sec % 60}s`;
  const h = Math.floor(sec / 3600);
  const m = Math.floor((sec % 3600) / 60);
  return `${h}h ${m}m`;
}

function truncateId(id: string): string {
  return id.length > 8 ? `${id.substring(0, 8)}…` : id;
}

function hasActiveJobs(jobs: SliceJobStatusResponse[]): boolean {
  return jobs.some(j => j.status === SliceJobStatus.Queued || j.status === SliceJobStatus.Processing);
}

/**
 * Embeddable slice jobs panel with real-time updates, filtering, and job actions.
 * Used both standalone (SliceJobsPage) and embedded (WorkerManagementPage Jobs tab).
 */
export function SliceJobsPanel() {
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const { viewMode, setViewMode } = useViewModePreference('printfarmer-slice-jobs-viewmode');
  const [statusFilter, setStatusFilter] = useState<StatusFilter>('all');
  const [expandedJobId, setExpandedJobId] = useState<string | null>(null);
  const [sendToJobId, setSendToJobId] = useState<string | null>(null);
  const [previewJobId, setPreviewJobId] = useState<string | null>(null);

  const { isConnected: isRealtimeConnected } = useSliceJobsRealtime();

  const {
    data: jobs = [],
    isLoading,
    error,
    isFetching,
  } = useQuery({
    queryKey: ['slice-jobs'],
    queryFn: () => sliceJobService.getMyJobs(100),
    staleTime: 10_000,
    refetchInterval: (query) => {
      const data = query.state.data as SliceJobStatusResponse[] | undefined;
      if (isRealtimeConnected) {
        return data && hasActiveJobs(data) ? 30_000 : 60_000;
      }
      return data && hasActiveJobs(data) ? 5_000 : 30_000;
    },
  });

  const cancelMutation = useMutation({
    mutationFn: (jobId: string) => sliceJobService.cancelJob(jobId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['slice-jobs'] });
      toast.success('Job cancelled');
    },
    onError: (err: Error) => {
      toast.error(`Failed to cancel job: ${err.message}`);
    },
  });

  const retryMutation = useMutation({
    mutationFn: (jobId: string) => sliceJobService.retryJob(jobId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['slice-jobs'] });
      toast.success('Job requeued');
    },
    onError: (err: Error) => {
      toast.error(`Failed to retry job: ${err.message}`);
    },
  });

  const filteredJobs = useMemo(() => {
    if (statusFilter === 'all') return jobs;
    return jobs.filter(j => j.status === statusFilter);
  }, [jobs, statusFilter]);

  const statusCounts = useMemo(() => {
    const counts: Record<string, number> = { all: jobs.length };
    for (const job of jobs) {
      counts[job.status] = (counts[job.status] ?? 0) + 1;
    }
    return counts;
  }, [jobs]);

  const handleDownloadArtifact = (jobId: string) => {
    window.open(`${getApiBaseUrl()}/artifacts/job/${jobId}`, '_blank');
  };

  const toggleExpand = (jobId: string) => {
    setExpandedJobId(prev => prev === jobId ? null : jobId);
  };

  if (isLoading) {
    return (
      <div className="flex items-center justify-center py-16">
        <Spinner size="lg" />
      </div>
    );
  }

  if (error) {
    return (
      <div className="p-4 text-pf-error">Failed to load slice jobs: {String(error)}</div>
    );
  }

  return (
    <div>
      {/* Header bar */}
      <div className="flex flex-wrap items-center gap-3 mb-4">
        <Select
          value={statusFilter}
          onChange={(e) => setStatusFilter(e.target.value as StatusFilter)}
          containerClassName="w-44"
          aria-label="Filter by status"
        >
          {STATUS_FILTERS.map(f => (
            <option key={f.value} value={f.value}>
              {f.label} ({statusCounts[f.value] ?? 0})
            </option>
          ))}
        </Select>

        {isFetching && <Spinner size="sm" />}

        <div className="ml-auto flex items-center gap-1">
          <Button variant="primary" size="sm" onClick={() => navigate('/slicer')}>
            New Slice Job
          </Button>
          <Button
            variant={viewMode === 'explorer' ? 'primary' : 'ghost'}
            size="sm"
            onClick={() => setViewMode('explorer')}
            aria-label="Table view"
          >
            <TableIcon className="w-4 h-4" />
          </Button>
          <Button
            variant={viewMode === 'grid' ? 'primary' : 'ghost'}
            size="sm"
            onClick={() => setViewMode('grid')}
            aria-label="Card view"
          >
            <GridIcon className="w-4 h-4" />
          </Button>
        </div>
      </div>

      {filteredJobs.length === 0 ? (
        <EmptyState
          hasFilter={statusFilter !== 'all'}
          onClearFilter={() => setStatusFilter('all')}
          onCreateJob={() => navigate('/slicer')}
        />
      ) : viewMode === 'explorer' ? (
        <JobTable
          jobs={filteredJobs}
          expandedJobId={expandedJobId}
          onToggleExpand={toggleExpand}
          onCancel={(id) => cancelMutation.mutate(id)}
          onRetry={(id) => retryMutation.mutate(id)}
          onDownload={handleDownloadArtifact}
          onSendToPrinter={(id) => setSendToJobId(id)}
          onPreview={(id) => setPreviewJobId(id)}
          cancellingId={cancelMutation.isPending ? (cancelMutation.variables ?? null) : null}
        />
      ) : (
        <JobCardGrid
          jobs={filteredJobs}
          onCancel={(id) => cancelMutation.mutate(id)}
          onRetry={(id) => retryMutation.mutate(id)}
          onDownload={handleDownloadArtifact}
          onSendToPrinter={(id) => setSendToJobId(id)}
          onPreview={(id) => setPreviewJobId(id)}
          cancellingId={cancelMutation.isPending ? (cancelMutation.variables ?? null) : null}
        />
      )}

      <SendToPrinterModal
        isOpen={sendToJobId !== null}
        onClose={() => setSendToJobId(null)}
        jobId={sendToJobId ?? ''}
      />

      <GcodePreviewModal
        isOpen={previewJobId !== null}
        onClose={() => setPreviewJobId(null)}
        jobId={previewJobId ?? ''}
      />
    </div>
  );
}

/* ─── Table View ─── */

function JobTable({
  jobs,
  expandedJobId,
  onToggleExpand,
  onCancel,
  onRetry,
  onDownload,
  onSendToPrinter,
  onPreview,
  cancellingId,
}: {
  jobs: SliceJobStatusResponse[];
  expandedJobId: string | null;
  onToggleExpand: (id: string) => void;
  onCancel: (id: string) => void;
  onRetry: (id: string) => void;
  onDownload: (id: string) => void;
  onSendToPrinter: (id: string) => void;
  onPreview: (id: string) => void;
  cancellingId: string | null;
}) {
  return (
    <div className="overflow-x-auto rounded-lg border border-pf-border">
      <table className="w-full text-sm" role="grid">
        <thead>
          <tr className="bg-pf-bg-1 text-pf-text-secondary text-left">
            <th className="px-4 py-3 font-medium">Job ID</th>
            <th className="px-4 py-3 font-medium">Status</th>
            <th className="px-4 py-3 font-medium">Progress</th>
            <th className="px-4 py-3 font-medium">Created</th>
            <th className="px-4 py-3 font-medium">Duration</th>
            <th className="px-4 py-3 font-medium text-right">Actions</th>
          </tr>
        </thead>
        <tbody className="divide-y divide-pf-border">
          {jobs.map(job => (
            <JobTableRow
              key={job.id}
              job={job}
              isExpanded={expandedJobId === job.id}
              onToggleExpand={() => onToggleExpand(job.id)}
              onCancel={() => onCancel(job.id)}
              onRetry={() => onRetry(job.id)}
              onDownload={() => onDownload(job.id)}
              onSendToPrinter={() => onSendToPrinter(job.id)}
              onPreview={() => onPreview(job.id)}
              isCancelling={cancellingId === job.id}
            />
          ))}
        </tbody>
      </table>
    </div>
  );
}

function JobTableRow({
  job,
  isExpanded,
  onToggleExpand,
  onCancel,
  onRetry,
  onDownload,
  onSendToPrinter,
  onPreview,
  isCancelling,
}: {
  job: SliceJobStatusResponse;
  isExpanded: boolean;
  onToggleExpand: () => void;
  onCancel: () => void;
  onRetry: () => void;
  onDownload: () => void;
  onSendToPrinter: () => void;
  onPreview: () => void;
  isCancelling: boolean;
}) {
  const canCancel = job.status === SliceJobStatus.Queued || job.status === SliceJobStatus.Processing;
  const canDownload = job.status === SliceJobStatus.Completed;
  const canRetry = job.status === SliceJobStatus.Failed || job.status === SliceJobStatus.Cancelled;

  return (
    <>
      <tr
        className="bg-pf-bg-0 hover:bg-pf-bg-1/50 cursor-pointer transition-colors"
        onClick={onToggleExpand}
        role="row"
      >
        <td className="px-4 py-3 font-mono text-xs text-pf-text-secondary" title={job.id}>
          {truncateId(job.id)}
        </td>
        <td className="px-4 py-3">
          <Badge variant={getStatusBadgeVariant(job.status)}>{job.status}</Badge>
        </td>
        <td className="px-4 py-3">
          <ProgressCell job={job} />
        </td>
        <td className="px-4 py-3 text-pf-text-secondary" title={new Date(job.queuedAt).toLocaleString()}>
          {formatRelativeTime(job.queuedAt)}
        </td>
        <td className="px-4 py-3 text-pf-text-secondary">
          {formatDuration(job.startedAt, job.completedAt)}
        </td>
        <td className="px-4 py-3 text-right">
          <div className="flex items-center justify-end gap-1" onClick={(e) => e.stopPropagation()}>
            {canRetry && (
              <Button
                variant="secondary"
                size="sm"
                onClick={onRetry}
                aria-label="Retry job"
                title="Retry job"
              >
                <RefreshIcon className="w-3.5 h-3.5" />
              </Button>
            )}
            {canCancel && (
              <Button
                variant="danger"
                size="sm"
                onClick={onCancel}
                loading={isCancelling}
                disabled={isCancelling}
                aria-label="Cancel job"
              >
                <CloseIcon className="w-3.5 h-3.5" />
              </Button>
            )}
            {canDownload && (
              <Button
                variant="success"
                size="sm"
                onClick={onDownload}
                aria-label="Download gcode"
              >
                <DownloadIcon className="w-3.5 h-3.5" />
              </Button>
            )}
            {canDownload && (
              <Button
                variant="secondary"
                size="sm"
                onClick={onPreview}
                aria-label="Preview gcode"
                title="Preview G-code"
              >
                <EyeIcon className="w-3.5 h-3.5" />
              </Button>
            )}
            {canDownload && (
              <Button
                variant="primary"
                size="sm"
                onClick={onSendToPrinter}
                aria-label="Send to printer"
              >
                <PrinterIcon className="w-3.5 h-3.5" />
              </Button>
            )}
          </div>
        </td>
      </tr>
      {isExpanded && (
        <tr>
          <td colSpan={6} className="px-4 py-3 bg-pf-bg-1/30">
            <JobDetailPanel job={job} />
          </td>
        </tr>
      )}
    </>
  );
}

/* ─── Card View ─── */

function JobCardGrid({
  jobs,
  onCancel,
  onRetry,
  onDownload,
  onSendToPrinter,
  onPreview,
  cancellingId,
}: {
  jobs: SliceJobStatusResponse[];
  onCancel: (id: string) => void;
  onRetry: (id: string) => void;
  onDownload: (id: string) => void;
  onSendToPrinter: (id: string) => void;
  onPreview: (id: string) => void;
  cancellingId: string | null;
}) {
  return (
    <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
      {jobs.map(job => (
        <JobCard
          key={job.id}
          job={job}
          onCancel={() => onCancel(job.id)}
          onRetry={() => onRetry(job.id)}
          onDownload={() => onDownload(job.id)}
          onSendToPrinter={() => onSendToPrinter(job.id)}
          onPreview={() => onPreview(job.id)}
          isCancelling={cancellingId === job.id}
        />
      ))}
    </div>
  );
}

function JobCard({
  job,
  onCancel,
  onRetry,
  onDownload,
  onSendToPrinter,
  onPreview,
  isCancelling,
}: {
  job: SliceJobStatusResponse;
  onCancel: () => void;
  onRetry: () => void;
  onDownload: () => void;
  onSendToPrinter: () => void;
  onPreview: () => void;
  isCancelling: boolean;
}) {
  const canCancel = job.status === SliceJobStatus.Queued || job.status === SliceJobStatus.Processing;
  const canDownload = job.status === SliceJobStatus.Completed;
  const canRetry = job.status === SliceJobStatus.Failed || job.status === SliceJobStatus.Cancelled;

  return (
    <Card>
      <Card.Body className="space-y-3">
        <div className="flex items-start justify-between">
          <div>
            <p className="font-mono text-xs text-pf-text-secondary" title={job.id}>
              {truncateId(job.id)}
            </p>
            <p className="text-sm text-pf-text-secondary mt-1">
              {formatRelativeTime(job.queuedAt)}
            </p>
          </div>
          <Badge variant={getStatusBadgeVariant(job.status)}>{job.status}</Badge>
        </div>

        <ProgressCell job={job} />

        <div className="grid grid-cols-2 gap-2 text-xs text-pf-text-secondary">
          <div>
            <span className="text-pf-text-tertiary">Duration</span>
            <p>{formatDuration(job.startedAt, job.completedAt)}</p>
          </div>
          {job.filamentUsedGrams !== undefined && job.filamentUsedGrams > 0 && (
            <div>
              <span className="text-pf-text-tertiary">Filament</span>
              <p>{sliceJobService.formatFilamentUsed(job.filamentUsedGrams)}</p>
            </div>
          )}
          {job.estimatedPrintTimeSeconds !== undefined && job.estimatedPrintTimeSeconds > 0 && (
            <div>
              <span className="text-pf-text-tertiary">Print Time</span>
              <p>{sliceJobService.formatPrintTime(job.estimatedPrintTimeSeconds)}</p>
            </div>
          )}
          {job.workerId && (
            <div>
              <span className="text-pf-text-tertiary">Worker</span>
              <p className="truncate" title={job.workerId}>{job.workerId}</p>
            </div>
          )}
        </div>

        {job.errorMessage && (
          <p className="text-xs text-pf-error bg-pf-error/10 rounded p-2 wrap-break-word">
            {job.errorMessage}
          </p>
        )}

        {job.sourceUrl && (
          <a
            href={job.sourceUrl}
            target="_blank"
            rel="noopener noreferrer"
            className="inline-flex items-center gap-1 text-xs text-pf-text-tertiary hover:text-pf-accent"
          >
            from Printables{job.sourceCreator ? ` by ${job.sourceCreator}` : ''}
            <ExternalLinkIcon className="w-3 h-3" />
          </a>
        )}

        <div className="flex items-center gap-2 pt-1">
          {canRetry && (
            <Button
              variant="secondary"
              size="sm"
              onClick={onRetry}
              iconLeft={<RefreshIcon className="w-3.5 h-3.5" />}
            >
              Retry
            </Button>
          )}
          {canCancel && (
            <Button
              variant="danger"
              size="sm"
              onClick={onCancel}
              loading={isCancelling}
              disabled={isCancelling}
              iconLeft={<CloseIcon className="w-3.5 h-3.5" />}
            >
              Cancel
            </Button>
          )}
          {canDownload && (
            <Button
              variant="success"
              size="sm"
              onClick={onDownload}
              iconLeft={<DownloadIcon className="w-3.5 h-3.5" />}
            >
              Download
            </Button>
          )}
          {canDownload && (
            <Button
              variant="secondary"
              size="sm"
              onClick={onPreview}
              iconLeft={<EyeIcon className="w-3.5 h-3.5" />}
            >
              Preview
            </Button>
          )}
          {canDownload && (
            <Button
              variant="primary"
              size="sm"
              onClick={onSendToPrinter}
              iconLeft={<PrinterIcon className="w-3.5 h-3.5" />}
            >
              Send to Printer
            </Button>
          )}
        </div>
      </Card.Body>
    </Card>
  );
}

/* ─── Shared Pieces ─── */

function ProgressCell({ job }: { job: SliceJobStatusResponse }) {
  if (job.status === SliceJobStatus.Processing) {
    const timeRemaining = sliceJobService.getEstimatedTimeRemaining(job);
    return (
      <div className="space-y-1">
        <div className="flex items-center gap-2">
          <ProgressBar
            value={job.progressPercent}
            ariaLabel={`Slice progress: ${job.progressPercent}%`}
            showPercent={false}
            size="xs"
            className="flex-1"
          />
          <span className="text-xs text-pf-text-secondary font-mono whitespace-nowrap">
            {job.progressPercent}%
          </span>
        </div>
        {(job.progressMessage ?? timeRemaining) && (
          <p className="text-xs text-pf-text-tertiary truncate">
            {job.progressMessage ?? (timeRemaining ? `~${timeRemaining} remaining` : '')}
          </p>
        )}
      </div>
    );
  }
  return <span className="text-xs text-pf-text-tertiary">—</span>;
}

function JobDetailPanel({ job }: { job: SliceJobStatusResponse }) {
  return (
    <div className="grid grid-cols-2 md:grid-cols-4 gap-4 text-sm">
      <DetailField label="Full ID" value={job.id} mono />
      <DetailField label="Status" value={job.status} />
      <DetailField label="Queued" value={new Date(job.queuedAt).toLocaleString()} />
      {job.startedAt && (
        <DetailField label="Started" value={new Date(job.startedAt).toLocaleString()} />
      )}
      {job.completedAt && (
        <DetailField label="Completed" value={new Date(job.completedAt).toLocaleString()} />
      )}
      {job.workerId && <DetailField label="Worker" value={job.workerId} />}
      {job.estimatedPrintTimeSeconds !== undefined && job.estimatedPrintTimeSeconds > 0 && (
        <DetailField label="Est. Print Time" value={sliceJobService.formatPrintTime(job.estimatedPrintTimeSeconds)} />
      )}
      {job.filamentUsedGrams !== undefined && job.filamentUsedGrams > 0 && (
        <DetailField label="Filament Used" value={sliceJobService.formatFilamentUsed(job.filamentUsedGrams)} />
      )}
      {job.artifactsCount !== undefined && job.artifactsCount > 0 && (
        <DetailField
          label="Artifacts"
          value={`${job.artifactsCount} file${job.artifactsCount > 1 ? 's' : ''} (${sliceJobService.formatFileSize(job.artifactsTotalBytes ?? 0)})`}
        />
      )}
      {job.errorMessage && (
        <div className="col-span-full">
          <DetailField label="Error" value={job.errorMessage} error />
        </div>
      )}
      {job.sourceUrl && (
        <div className="col-span-full">
          <span className="text-xs text-pf-text-tertiary">Source</span>
          <p className="text-sm">
            <a
              href={job.sourceUrl}
              target="_blank"
              rel="noopener noreferrer"
              className="inline-flex items-center gap-1 text-pf-accent hover:underline"
            >
              from Printables{job.sourceCreator ? ` by ${job.sourceCreator}` : ''}
              <ExternalLinkIcon className="w-3 h-3" />
            </a>
          </p>
        </div>
      )}
      {job.resultFileUrl && (
        <div className="col-span-full">
          <DetailField label="Result" value={job.resultFileUrl} />
        </div>
      )}
    </div>
  );
}

function DetailField({
  label,
  value,
  mono = false,
  error = false,
}: {
  label: string;
  value: string;
  mono?: boolean;
  error?: boolean;
}) {
  return (
    <div>
      <span className="text-xs text-pf-text-tertiary">{label}</span>
      <p
        className={clsx(
          'text-sm wrap-break-word',
          mono && 'font-mono text-xs',
          error ? 'text-pf-error' : 'text-pf-text-primary',
        )}
      >
        {value}
      </p>
    </div>
  );
}

function EmptyState({
  hasFilter,
  onClearFilter,
  onCreateJob,
}: {
  hasFilter: boolean;
  onClearFilter: () => void;
  onCreateJob: () => void;
}) {
  return (
    <div className="flex flex-col items-center justify-center py-16 text-center">
      <LayersIcon className="w-12 h-12 text-pf-text-tertiary mb-4" />
      {hasFilter ? (
        <>
          <h3 className="text-lg text-pf-text-primary mb-1">No matching jobs</h3>
          <p className="text-sm text-pf-text-secondary mb-4">
            No jobs match the current filter. Try a different status.
          </p>
          <Button variant="secondary" onClick={onClearFilter}>
            Clear Filter
          </Button>
        </>
      ) : (
        <>
          <h3 className="text-lg text-pf-text-primary mb-1">No slice jobs yet</h3>
          <p className="text-sm text-pf-text-secondary mb-4">
            Submit your first slicing job to get started.
          </p>
          <Button variant="primary" onClick={onCreateJob}>
            Create Slice Job
          </Button>
        </>
      )}
    </div>
  );
}

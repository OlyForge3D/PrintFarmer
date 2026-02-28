/**
 * ActiveJobsWidget Component
 * 
 * Dashboard widget showing active and queued print jobs with thumbnails.
 * Uses the common DashboardWidget for consistent styling.
 */

import { useState } from 'react';
import { DashboardWidget } from '@/common/components/DashboardWidget';
import { PlayIcon, ClockIcon } from '@/common/components/icons/MdiIcons';
import { Button } from '@/common/components/ui/Button';
import { ImagePreviewModal } from '@/common/components/modals/ImagePreviewModal';
import { useJobQueue } from '@/common/hooks/useApi';

export interface ActiveJobsWidgetProps {
  /** Maximum jobs to display */
  maxJobs?: number;
  /** Additional CSS classes */
  className?: string;
}

/**
 * Dashboard widget showing active and queued jobs
 */
export function ActiveJobsWidget({ maxJobs = 5, className = '' }: ActiveJobsWidgetProps) {
  const { data: globalQueue } = useJobQueue(undefined);
  const [previewImage, setPreviewImage] = useState<{ src: string; alt: string } | null>(null);

  const hasJobs = globalQueue && globalQueue.length > 0;
  const jobCount = globalQueue?.length ?? 0;

  return (
    <>
      <DashboardWidget
        title="Active & Queued Jobs"
        icon={PlayIcon}
        iconColorClass={hasJobs ? 'text-blue-400' : 'text-pf-text-tertiary'}
        iconBgClass={hasJobs ? 'bg-blue-500/20' : 'bg-pf-bg-2'}
        subtitle={
          hasJobs
            ? `${jobCount} job${jobCount !== 1 ? 's' : ''} in queue`
            : 'No active jobs'
        }
        hasContent={hasJobs}
        collapsible
        storageKey="active-jobs"
        moreInfoLink="/printQueue?tab=all-jobs"
        moreInfoText="View Queue"
        className={className}
        emptyState={
          <div className="text-center py-6">
            <ClockIcon className="h-10 w-10 text-pf-text-tertiary mx-auto mb-2" />
            <p className="text-sm text-pf-text-primary font-medium">No Jobs in Queue</p>
            <p className="text-xs text-pf-text-tertiary mt-1">Queue a print from the G-code library</p>
          </div>
        }
      >
        <div className="space-y-2 max-h-64 overflow-y-auto">
          {globalQueue?.slice(0, maxJobs).map((item) => {
            const thumbUrl = item.gcodeFile?.thumbnailUrl;
            const jobName = item.gcodeFile?.name ?? item.job.fileName ?? item.job.name ?? 'Unknown Job';

            return (
              <div key={item.job.id} className="flex items-start gap-3 p-3 bg-pf-bg-1 rounded-lg border border-pf-border">
                {/* Thumbnail */}
                {thumbUrl ? (
                  <Button
                    variant="unstyled"
                    onClick={() => setPreviewImage({ src: thumbUrl, alt: jobName })}
                    className="shrink-0 w-10 h-10 rounded overflow-hidden border border-pf-border hover:border-pf-accent transition-colors cursor-pointer !p-0"
                    aria-label={jobName ? `Preview thumbnail for ${jobName}` : 'Preview thumbnail'}
                  >
                    <img
                      src={thumbUrl}
                      alt=""
                      className="w-full h-full object-cover"
                      loading="lazy"
                    />
                  </Button>
                ) : (
                  <div className="shrink-0 w-10 h-10 rounded bg-pf-bg-2 flex items-center justify-center border border-pf-border">
                    <PlayIcon className="h-4 w-4 text-pf-text-tertiary" />
                  </div>
                )}

                {/* Job info */}
                <div className="flex-1 min-w-0">
                  <p className="text-sm font-medium text-pf-text-primary truncate">
                    {jobName}
                  </p>
                  <p className="text-xs text-pf-text-tertiary">
                    Queue Position: {item.job.queuePosition}
                    {item.assignedPrinter && ` • ${item.assignedPrinter.name}`}
                  </p>
                </div>

                {/* Status badge */}
                <span className={`ml-2 inline-flex items-center px-2 py-1 rounded-full text-xs font-medium whitespace-nowrap ${
                  item.job.status === 'Printing' 
                    ? 'bg-green-500/20 text-green-400'
                    : 'bg-blue-500/20 text-blue-400'
                }`}>
                  {item.job.status}
                </span>
              </div>
            );
          })}
          {globalQueue && globalQueue.length > maxJobs && (
            <p className="text-xs text-pf-text-tertiary text-center py-2">
              +{globalQueue.length - maxJobs} more in queue
            </p>
          )}
        </div>
      </DashboardWidget>

      {/* Image Preview Modal */}
      {previewImage && (
        <ImagePreviewModal
          isOpen
          onClose={() => setPreviewImage(null)}
          src={previewImage.src}
          alt={previewImage.alt}
          title={previewImage.alt}
        />
      )}
    </>
  );
}

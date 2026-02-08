import { JobStateHistoryDto } from '@/services/printQueueService';
import { useState } from 'react';
import { Button } from '@/common/components/ui/Button';

interface JobStateHistoryViewProps {
  history: JobStateHistoryDto;
}

export function JobStateHistoryView({ history }: JobStateHistoryViewProps) {
  const [expandedIndex, setExpandedIndex] = useState<number | null>(null);

  const formatDate = (dateString: string) => {
    try {
      const date = new Date(dateString);
      return date.toLocaleString();
    } catch {
      return dateString;
    }
  };

  const formatDuration = (seconds?: number) => {
    if (!seconds) return 'N/A';
    const hours = Math.floor(seconds / 3600);
    const minutes = Math.floor((seconds % 3600) / 60);
    const secs = seconds % 60;

    if (hours > 0) {
      return `${hours}h ${minutes}m ${secs}s`;
    } else if (minutes > 0) {
      return `${minutes}m ${secs}s`;
    } else {
      return `${secs}s`;
    }
  };

  const getStateColor = (state: string) => {
    switch (state?.toLowerCase()) {
      case 'queued':
        return 'bg-pf-info';
      case 'printing':
        return 'bg-pf-success';
      case 'completed':
        return 'bg-pf-success';
      case 'failed':
        return 'bg-pf-danger';
      case 'cancelled':
        return 'bg-pf-warning';
      case 'paused':
        return 'bg-pf-warning';
      default:
        return 'bg-pf-border';
    }
  };

  return (
    <article className="bg-pf-bg-1 border border-pf-border rounded-lg p-4 sm:p-6 lg:p-8" role="region" aria-label="Job state history and timeline">
      <div className="mb-6 lg:mb-8 border-b border-pf-border pb-4 sm:pb-6">
        <h2 className="text-xl font-semibold text-pf-text-primary mb-2 wrap-break-word">{history.jobName}</h2>
        <p className="text-xs sm:text-sm text-pf-text-secondary">Job ID: <code className="font-mono text-pf-text-primary bg-pf-bg-2 px-2 py-1 rounded-sm">{history.jobId}</code></p>
      </div>

      {/* Summary Stats */}
      <div className="grid grid-cols-1 sm:grid-cols-3 gap-3 sm:gap-4 mb-6 lg:mb-8">
        <div className="bg-pf-bg-2 border border-pf-border rounded-lg p-3 sm:p-4 hover:border-pf-text-secondary transition-colors">
          <p className="text-xs text-pf-text-secondary mb-2 font-medium">Total Duration</p>
          <p className="text-lg sm:text-xl font-bold text-pf-text-primary">{formatDuration(history.totalDurationSeconds)}</p>
        </div>

        <div className="bg-pf-bg-2 border border-pf-border rounded-lg p-3 sm:p-4 hover:border-pf-text-secondary transition-colors">
          <p className="text-xs text-pf-text-secondary mb-2 font-medium">Estimated Duration</p>
          <p className="text-lg sm:text-xl font-bold text-pf-text-primary">{formatDuration(history.estimatedDurationSeconds)}</p>
        </div>

        {history.variancePercent !== undefined && (
          <div className="bg-pf-bg-2 border border-pf-border rounded-lg p-3 sm:p-4 hover:border-pf-text-secondary transition-colors">
            <p className="text-xs text-pf-text-secondary mb-1">Variance</p>
            <p className={`text-lg font-bold ${history.variancePercent > 0 ? 'text-pf-warning' : 'text-pf-success'}`}>
              {history.variancePercent > 0 ? '+' : ''}{history.variancePercent.toFixed(1)}%
            </p>
          </div>
        )}
      </div>

      {/* State Transitions */}
      <section>
        <h3 className="font-semibold text-pf-text-primary mb-4 text-lg">State Transitions</h3>

        <div className="space-y-2 sm:space-y-3" role="list">
          {history.transitions.map((transition, index) => (
            <div key={index} role="listitem">
              <Button
                onClick={() => setExpandedIndex(expandedIndex === index ? null : index)}
                className="w-full text-left"
                aria-expanded={expandedIndex === index}
                aria-controls={`transition-${index}`}
                variant="subtle"
              >
                <div className="flex items-center justify-between gap-3 sm:gap-4">
                  <div className="flex items-center gap-2 sm:gap-3 flex-1 min-w-0">
                    <span className={`inline-block px-2.5 sm:px-3 py-1 rounded-sm text-xs font-medium text-white whitespace-nowrap ${getStateColor(transition.fromState)}`}>
                      {transition.fromState}
                    </span>
                    <span className="text-pf-text-secondary shrink-0" aria-hidden="true">→</span>
                    <span className={`inline-block px-2.5 sm:px-3 py-1 rounded-sm text-xs font-medium text-white whitespace-nowrap ${getStateColor(transition.toState)}`}>
                      {transition.toState}
                    </span>
                  </div>
                  <div className="flex items-center gap-2 sm:gap-3 shrink-0">
                    <span className="text-xs sm:text-sm text-pf-text-secondary whitespace-nowrap">{formatDuration(transition.durationInStateSeconds)}</span>
                    <span className={`transition-transform shrink-0 text-pf-text-secondary ${expandedIndex === index ? 'rotate-180' : ''}`} aria-hidden="true">▼</span>
                  </div>
                </div>
              </Button>

              {expandedIndex === index && (
                <div id={`transition-${index}`} className="bg-pf-bg-2 border border-t-0 border-pf-border rounded-b-lg p-3 sm:p-4 text-xs sm:text-sm space-y-2 sm:space-y-3">
                  <div>
                    <p className="text-pf-text-secondary">Transitioned</p>
                    <p className="text-pf-text-primary font-mono text-xs">{formatDate(transition.transitionedAtUtc)}</p>
                  </div>

                  {transition.durationInStateSeconds !== undefined && (
                    <div>
                      <p className="text-pf-text-secondary">Duration in State</p>
                      <p className="text-pf-text-primary">{formatDuration(transition.durationInStateSeconds)}</p>
                    </div>
                  )}

                  {transition.notes && (
                    <div>
                      <p className="text-pf-text-secondary">Notes</p>
                      <p className="text-pf-text-primary bg-pf-bg-1 rounded-sm p-2 font-mono text-xs">{transition.notes}</p>
                    </div>
                  )}
                </div>
              )}
            </div>
          ))}
        </div>
      </section>
    </article>
  );
}

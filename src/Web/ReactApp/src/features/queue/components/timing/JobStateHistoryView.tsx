import { JobStateHistoryDto } from '@/services/printQueueService';
import { useState } from 'react';

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
    <div className="bg-pf-bg-1 border border-pf-border rounded-lg p-6">
      <div className="mb-6">
        <h3 className="text-lg font-semibold text-pf-text-primary mb-2">{history.jobName}</h3>
        <p className="text-sm text-pf-text-secondary">Job ID: <code className="text-xs">{history.jobId}</code></p>
      </div>

      {/* Summary Stats */}
      <div className="grid grid-cols-1 md:grid-cols-3 gap-4 mb-6">
        <div className="bg-pf-bg-2 border border-pf-border rounded p-3">
          <p className="text-xs text-pf-text-secondary mb-1">Total Duration</p>
          <p className="text-lg font-bold text-pf-text-primary">{formatDuration(history.totalDurationSeconds)}</p>
        </div>

        <div className="bg-pf-bg-2 border border-pf-border rounded p-3">
          <p className="text-xs text-pf-text-secondary mb-1">Estimated Duration</p>
          <p className="text-lg font-bold text-pf-text-primary">{formatDuration(history.estimatedDurationSeconds)}</p>
        </div>

        {history.variancePercent !== undefined && (
          <div className="bg-pf-bg-2 border border-pf-border rounded p-3">
            <p className="text-xs text-pf-text-secondary mb-1">Variance</p>
            <p className={`text-lg font-bold ${history.variancePercent > 0 ? 'text-pf-warning' : 'text-pf-success'}`}>
              {history.variancePercent > 0 ? '+' : ''}{history.variancePercent.toFixed(1)}%
            </p>
          </div>
        )}
      </div>

      {/* State Transitions */}
      <div>
        <h4 className="font-semibold text-pf-text-primary mb-4">State Transitions</h4>

        <div className="space-y-2">
          {history.transitions.map((transition, index) => (
            <div key={index}>
              <button
                onClick={() => setExpandedIndex(expandedIndex === index ? null : index)}
                className="w-full text-left p-3 bg-pf-bg-2 border border-pf-border rounded hover:border-pf-text-secondary transition-colors"
              >
                <div className="flex items-center justify-between">
                  <div className="flex items-center gap-3 flex-1">
                    <span className={`inline-block px-3 py-1 rounded text-xs font-medium text-white ${getStateColor(transition.fromState)}`}>
                      {transition.fromState}
                    </span>
                    <span className="text-pf-text-secondary">→</span>
                    <span className={`inline-block px-3 py-1 rounded text-xs font-medium text-white ${getStateColor(transition.toState)}`}>
                      {transition.toState}
                    </span>
                  </div>
                  <div className="flex items-center gap-3">
                    <span className="text-sm text-pf-text-secondary">{formatDuration(transition.durationInStateSeconds)}</span>
                    <span className={`transition-transform ${expandedIndex === index ? 'rotate-180' : ''}`}>▼</span>
                  </div>
                </div>
              </button>

              {expandedIndex === index && (
                <div className="bg-pf-bg-2 border border-t-0 border-pf-border rounded-b p-3 text-sm space-y-2">
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
                      <p className="text-pf-text-primary bg-pf-bg-1 rounded p-2 font-mono text-xs">{transition.notes}</p>
                    </div>
                  )}
                </div>
              )}
            </div>
          ))}
        </div>
      </div>
    </div>
  );
}

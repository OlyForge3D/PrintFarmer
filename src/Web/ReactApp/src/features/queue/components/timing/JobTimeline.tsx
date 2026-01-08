import { TimelineEventDto } from '@/services/printQueueService';
import { formatDistanceToNow, formatISO } from 'date-fns';

interface JobTimelineProps {
  events: TimelineEventDto[];
}

export function JobTimeline({ events }: JobTimelineProps) {
  const formatDate = (dateString: string) => {
    try {
      const date = new Date(dateString);
      return formatISO(date, { representation: 'date' });
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
      return `${hours}h ${minutes}m`;
    } else if (minutes > 0) {
      return `${minutes}m ${secs}s`;
    } else {
      return `${secs}s`;
    }
  };

  const getStateColor = (state: string) => {
    switch (state?.toLowerCase()) {
      case 'queued':
        return 'bg-pf-info text-white';
      case 'printing':
        return 'bg-pf-success text-white';
      case 'completed':
        return 'bg-pf-success text-white';
      case 'failed':
        return 'bg-pf-danger text-white';
      case 'cancelled':
        return 'bg-pf-warning text-white';
      case 'paused':
        return 'bg-pf-warning text-white';
      default:
        return 'bg-pf-border text-pf-text-secondary';
    }
  };

  if (events.length === 0) {
    return (
      <div className="bg-pf-bg-1 border border-pf-border rounded-lg p-6 text-center">
        <p className="text-pf-text-secondary">No timeline events found for the selected date range</p>
      </div>
    );
  }

  return (
    <div className="bg-pf-bg-1 border border-pf-border rounded-lg p-6">
      <h3 className="text-lg font-semibold text-pf-text-primary mb-6">Job Timeline</h3>

      <div className="space-y-4">
        {events.map((event, index) => (
          <div key={`${event.jobId}-${index}`} className="flex gap-4">
            {/* Timeline bar */}
            <div className="flex flex-col items-center">
              <div className={`w-12 h-12 rounded-full flex items-center justify-center font-semibold text-sm ${getStateColor(event.state)}`}>
                {event.state.substring(0, 1).toUpperCase()}
              </div>
              {index < events.length - 1 && (
                <div className="w-0.5 h-12 bg-pf-border my-2"></div>
              )}
            </div>

            {/* Event details */}
            <div className="flex-1 py-2">
              <div className="flex items-baseline gap-2 mb-1">
                <h4 className="font-semibold text-pf-text-primary">{event.jobName}</h4>
                <span className={`px-2 py-1 rounded text-xs font-medium ${getStateColor(event.state)}`}>
                  {event.state}
                </span>
              </div>

              <p className="text-sm text-pf-text-secondary mb-2">
                Printer: <span className="text-pf-text-primary font-medium">{event.printerName}</span>
              </p>

              <div className="grid grid-cols-2 md:grid-cols-4 gap-3 text-sm">
                <div>
                  <p className="text-pf-text-secondary">Started</p>
                  <p className="text-pf-text-primary font-medium">{formatDate(event.enteredAtUtc)}</p>
                </div>

                {event.exitedAtUtc && (
                  <div>
                    <p className="text-pf-text-secondary">Ended</p>
                    <p className="text-pf-text-primary font-medium">{formatDate(event.exitedAtUtc)}</p>
                  </div>
                )}

                {event.durationSeconds !== undefined && (
                  <div>
                    <p className="text-pf-text-secondary">Actual Duration</p>
                    <p className="text-pf-text-primary font-medium">{formatDuration(event.durationSeconds)}</p>
                  </div>
                )}

                {event.estimatedDurationSeconds !== undefined && (
                  <div>
                    <p className="text-pf-text-secondary">Estimated Duration</p>
                    <p className="text-pf-text-primary font-medium">{formatDuration(event.estimatedDurationSeconds)}</p>
                  </div>
                )}

                {event.variancePercent !== undefined && (
                  <div>
                    <p className="text-pf-text-secondary">Variance</p>
                    <p className={`font-medium ${event.variancePercent > 0 ? 'text-pf-warning' : 'text-pf-success'}`}>
                      {event.variancePercent > 0 ? '+' : ''}{event.variancePercent.toFixed(1)}%
                    </p>
                  </div>
                )}
              </div>
            </div>
          </div>
        ))}
      </div>
    </div>
  );
}

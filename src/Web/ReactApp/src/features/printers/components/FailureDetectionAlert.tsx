import { Alert } from '@/common/components/ui/Alert';
import type { FailureDetectionEvent } from '@/types/api';

interface FailureDetectionAlertProps {
  event: FailureDetectionEvent;
  onDismiss?: () => void;
}

function formatDetectedAt(detectedAt: string): string {
  const parsed = new Date(detectedAt);
  if (Number.isNaN(parsed.getTime())) {
    return detectedAt;
  }

  return parsed.toLocaleTimeString([], {
    hour: 'numeric',
    minute: '2-digit',
  });
}

export function FailureDetectionAlert({ event, onDismiss }: FailureDetectionAlertProps) {
  const confidencePercent = Math.round(event.confidence * 100);
  const type = event.autoPaused || event.confidence >= 0.8 ? 'error' : 'warning';
  const title = event.autoPaused
    ? 'Print auto-paused after failure detection'
    : 'Possible print failure detected';

  return (
    <Alert type={type} title={title} onClose={onDismiss}>
      <div className="flex flex-wrap items-center gap-x-3 gap-y-1">
        <span>
          Confidence <strong>{confidencePercent}%</strong>
        </span>
        <span>Detected {formatDetectedAt(event.detectedAt)}</span>
        {event.snapshotUrl && (
          <a
            href={event.snapshotUrl}
            target="_blank"
            rel="noopener noreferrer"
            className="font-medium underline underline-offset-2"
          >
            View snapshot
          </a>
        )}
      </div>
    </Alert>
  );
}

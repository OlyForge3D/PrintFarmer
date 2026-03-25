import clsx from 'clsx';
import { AlertCircleIcon } from '@/common/components/icons/MdiIcons';
import { Badge } from '@/common/components/ui';
import type { FailureDetectionEvent } from '@/types/api';

interface FailureDetectionBadgeProps {
  event: FailureDetectionEvent;
  className?: string;
}

export function FailureDetectionBadge({ event, className }: FailureDetectionBadgeProps) {
  const confidencePercent = Math.round(event.confidence * 100);
  const variant = event.autoPaused || event.confidence >= 0.8 ? 'error' : 'warning';

  return (
    <Badge variant={variant} size="sm" className={clsx('gap-1 shrink-0', className)}>
      <AlertCircleIcon className="h-3 w-3" ariaLabel="Failure detected" />
      <span>Failure: {confidencePercent}%</span>
    </Badge>
  );
}

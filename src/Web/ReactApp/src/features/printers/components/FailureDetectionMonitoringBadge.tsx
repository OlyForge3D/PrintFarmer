import clsx from 'clsx';
import { ShieldIcon } from '@/common/components/icons/MdiIcons';
import { Badge } from '@/common/components/ui';
import type { FailureDetectionPrinterStatusDto } from '@/types/api';
import {
  getFailureDetectionStateLabel,
  getFailureDetectionStateVariant,
} from '@/features/printers/utils/failureDetectionStatus';

interface FailureDetectionMonitoringBadgeProps {
  enabled: boolean;
  status?: FailureDetectionPrinterStatusDto;
  className?: string;
}

export function FailureDetectionMonitoringBadge({
  enabled,
  status,
  className,
}: FailureDetectionMonitoringBadgeProps) {
  if (!enabled && !status) {
    return null;
  }

  const label = getFailureDetectionStateLabel(status?.state, enabled);
  const variant = getFailureDetectionStateVariant(status?.state, enabled);

  return (
    <Badge
      variant={variant}
      size="sm"
      className={clsx(
        'gap-1.5 shrink-0 shadow-sm shadow-black/20 backdrop-blur-sm',
        className
      )}
    >
      <span
        className={clsx(
          'inline-flex h-4 w-4 items-center justify-center rounded-full',
          status?.state === 'monitoring' && 'bg-pf-success/15 text-pf-success'
        )}
      >
        <ShieldIcon
          className="h-3 w-3"
          ariaLabel={`Failure detection ${label.toLowerCase()}`}
        />
      </span>
      <span>{label}</span>
    </Badge>
  );
}

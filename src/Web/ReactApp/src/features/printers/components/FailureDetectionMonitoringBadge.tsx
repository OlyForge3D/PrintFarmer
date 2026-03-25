import { useState } from 'react';
import clsx from 'clsx';
import { ShieldIcon } from '@/common/components/icons/MdiIcons';
import { Badge, Button } from '@/common/components/ui';
import type { FailureDetectionPrinterStatusDto } from '@/types/api';
import {
  getFailureDetectionDisplayState,
  getFailureDetectionStateLabel,
  getFailureDetectionStateVariant,
} from '@/features/printers/utils/failureDetectionStatus';
import { FailureDetectionStatusModal } from '@/features/printers/components/FailureDetectionStatusModal';

interface FailureDetectionMonitoringBadgeProps {
  enabled: boolean;
  status?: FailureDetectionPrinterStatusDto;
  className?: string;
  printerName?: string;
}

export function FailureDetectionMonitoringBadge({
  enabled,
  status,
  className,
  printerName,
}: FailureDetectionMonitoringBadgeProps) {
  const [isDetailsOpen, setIsDetailsOpen] = useState(false);

  if (!enabled && !status) {
    return null;
  }

  const displayState = getFailureDetectionDisplayState(status);
  const label = getFailureDetectionStateLabel(displayState, enabled);
  const variant = getFailureDetectionStateVariant(displayState, enabled);
  const resolvedPrinterName = status?.printerName ?? printerName;

  return (
    <>
      <Button
        type="button"
        variant="unstyled"
        onClick={() => setIsDetailsOpen(true)}
        className="rounded-full focus-visible:outline-hidden focus-visible:ring-2 focus-visible:ring-pf-accent focus-visible:ring-offset-2"
        aria-label={`Open spaghetti detection details${resolvedPrinterName ? ` for ${resolvedPrinterName}` : ''}`}
        aria-haspopup="dialog"
        aria-expanded={isDetailsOpen}
        title={`${label} • open spaghetti detection details`}
      >
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
              displayState === 'monitoring' && 'bg-pf-success/15 text-pf-success'
            )}
          >
            <ShieldIcon
              className="h-3 w-3"
              ariaLabel={`Failure detection ${label.toLowerCase()}`}
            />
          </span>
          <span>{label}</span>
        </Badge>
      </Button>
      <FailureDetectionStatusModal
        isOpen={isDetailsOpen}
        onClose={() => setIsDetailsOpen(false)}
        enabled={enabled}
        status={status}
        printerName={resolvedPrinterName}
      />
    </>
  );
}

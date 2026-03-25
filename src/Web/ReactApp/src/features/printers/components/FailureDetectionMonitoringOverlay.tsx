import clsx from 'clsx';
import { ShieldIcon } from '@/common/components/icons/MdiIcons';
import type { FailureDetectionPrinterStatusDto } from '@/types/api';
import {
  getFailureDetectionDetail,
  getFailureDetectionSourceLabel,
  getFailureDetectionStateLabel,
} from '@/features/printers/utils/failureDetectionStatus';

interface FailureDetectionMonitoringOverlayProps {
  enabled: boolean;
  status?: FailureDetectionPrinterStatusDto;
  className?: string;
}

function accentClasses(state?: string): string {
  switch (state) {
    case 'monitoring':
      return 'border-pf-success/30 bg-slate-950/74 shadow-[0_14px_30px_-20px_rgba(34,197,94,0.75)]';
    case 'idle':
      return 'border-pf-accent/25 bg-slate-950/72 shadow-[0_14px_30px_-20px_rgba(59,130,246,0.65)]';
    case 'misconfigured':
      return 'border-pf-warning/35 bg-slate-950/76 shadow-[0_14px_30px_-20px_rgba(245,158,11,0.75)]';
    case 'error':
      return 'border-pf-error/35 bg-slate-950/78 shadow-[0_14px_30px_-20px_rgba(239,68,68,0.75)]';
    default:
      return 'border-white/15 bg-slate-950/72 shadow-[0_14px_30px_-20px_rgba(15,23,42,0.9)]';
  }
}

export function FailureDetectionMonitoringOverlay({
  enabled,
  status,
  className,
}: FailureDetectionMonitoringOverlayProps) {
  if (!enabled && !status) {
    return null;
  }

  const label = getFailureDetectionStateLabel(status?.state, enabled);
  const detail = getFailureDetectionDetail(status, enabled);
  const sourceLabel = getFailureDetectionSourceLabel(status?.detectionSource);

  return (
    <div
      className={clsx(
        'pointer-events-none max-w-[15rem] rounded-2xl border px-3 py-2 text-white backdrop-blur-md',
        accentClasses(status?.state),
        className
      )}
    >
      <div className="flex items-start gap-2">
        <span className="mt-0.5 inline-flex h-7 w-7 items-center justify-center rounded-full border border-white/10 bg-white/8 text-white">
          <ShieldIcon className="h-4 w-4" ariaLabel="Failure detection status" />
        </span>
        <div className="min-w-0 flex-1">
          <div className="text-[10px] font-semibold uppercase tracking-[0.24em] text-white/55">
            Spaghetti watch
          </div>
          <div className="truncate text-sm font-semibold text-white">
            {label}
          </div>
        </div>
        {sourceLabel && (
          <span className="rounded-full border border-white/10 bg-white/7 px-2 py-0.5 text-[10px] font-semibold uppercase tracking-[0.18em] text-white/65">
            {sourceLabel}
          </span>
        )}
      </div>
      <div className="mt-2 text-[11px] leading-snug text-white/74">
        {detail}
      </div>
    </div>
  );
}

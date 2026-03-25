import clsx from 'clsx';
import { ShieldIcon } from '@/common/components/icons/MdiIcons';
import type { FailureDetectionPrinterStatusDto } from '@/types/api';
import { getFailureDetectionStateLabel } from '@/features/printers/utils/failureDetectionStatus';

interface FailureDetectionMonitoringOverlayProps {
  enabled: boolean;
  status?: FailureDetectionPrinterStatusDto;
  className?: string;
}

function getChipStyles(state?: string): { border: string; glow: string; icon: string } {
  switch (state) {
    case 'monitoring':
      return {
        border: 'border-pf-success/40',
        glow: 'shadow-[0_2px_8px_rgba(34,197,94,0.4)]',
        icon: 'text-pf-success',
      };
    case 'idle':
      return {
        border: 'border-pf-accent/30',
        glow: 'shadow-[0_2px_8px_rgba(59,130,246,0.35)]',
        icon: 'text-pf-accent',
      };
    case 'misconfigured':
      return {
        border: 'border-pf-warning/40',
        glow: 'shadow-[0_2px_8px_rgba(245,158,11,0.4)]',
        icon: 'text-pf-warning',
      };
    case 'error':
      return {
        border: 'border-pf-error/40',
        glow: 'shadow-[0_2px_8px_rgba(239,68,68,0.4)]',
        icon: 'text-pf-error',
      };
    default:
      return {
        border: 'border-white/20',
        glow: '',
        icon: 'text-white/60',
      };
  }
}

function getCompactHint(state?: string, enabled = false): string | null {
  if (state === 'misconfigured') return 'Check settings';
  if (state === 'error') return 'Needs attention';
  if (!state && enabled) return 'Connecting…';
  return null;
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
  const hint = getCompactHint(status?.state, enabled);
  const styles = getChipStyles(status?.state);

  return (
    <div
      className={clsx(
        'pointer-events-none inline-flex items-center gap-1.5 rounded-full border bg-slate-950/80 px-2.5 py-1 backdrop-blur-sm',
        styles.border,
        styles.glow,
        className
      )}
    >
      <ShieldIcon className={clsx('h-3.5 w-3.5', styles.icon)} ariaLabel="Spaghetti detection" />
      <span className="text-[11px] font-medium text-white">{label}</span>
      {hint && (
        <span className="text-[10px] text-white/50">· {hint}</span>
      )}
    </div>
  );
}

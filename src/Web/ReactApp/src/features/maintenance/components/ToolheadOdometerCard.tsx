import React from 'react';
import clsx from 'clsx';
import {
  ClockIcon,
  ExclamationTriangleIcon,
  CheckCircleIcon,
  QuestionMarkCircleIcon,
} from '@heroicons/react/24/outline';
import type { PrinterToolheadOdometer, ToolheadDueState } from '@/types/maintenance';

export interface ToolheadOdometerCardProps {
  odometer: PrinterToolheadOdometer;
  /** Optional callback fired when the card is activated (e.g. to filter). */
  onActivate?: (toolheadId: string) => void;
  /** True when the parent view is currently filtered to this toolhead. */
  isActive?: boolean;
  className?: string;
}

/**
 * Compact per-toolhead odometer card used on `PrinterMaintenancePage`. Renders
 * the toolhead's cumulative print hours (from
 * `PrinterDetailsDto.toolheads[].cumulativePrintHours` on the #711 backend
 * contract at feature head `1b696b954`) plus a non-color due-state chip
 * (icon + label) so state is perceivable without relying on color alone
 * (WCAG 1.4.1).
 *
 * The card does NOT infer due-state from cumulative hours; that would let a
 * loading/failed upcoming-maintenance feed render as a false "OK". The
 * schedule engine's verdict is pre-computed by the page and passed in via
 * `odometer.dueState`.
 */
export function ToolheadOdometerCard({
  odometer,
  onActivate,
  isActive,
  className,
}: ToolheadOdometerCardProps) {
  const dueState = odometer.dueState;
  const hours = formatHours(odometer.cumulativePrintHours);
  const label = describeToolhead(odometer);

  const interactive = typeof onActivate === 'function';
  const Container: React.ElementType = interactive ? 'button' : 'div';

  return (
    <Container
      type={interactive ? 'button' : undefined}
      onClick={interactive ? () => onActivate!(odometer.toolheadId) : undefined}
      aria-pressed={interactive ? Boolean(isActive) : undefined}
      className={clsx(
        'w-full text-left p-4 rounded-lg border bg-pf-bg-card',
        isActive ? 'border-pf-accent ring-1 ring-pf-accent/50' : 'border-pf-border',
        interactive &&
          'transition-colors hover:border-pf-accent focus:outline-hidden focus-visible:ring-2 focus-visible:ring-pf-accent focus-visible:ring-offset-1 focus-visible:ring-offset-pf-bg-0',
        className
      )}
      data-testid={`toolhead-odometer-${odometer.toolheadId}`}
    >
      <div className="flex items-start justify-between gap-3">
        <div className="flex-1 min-w-0">
          <div className="text-sm font-medium text-pf-text-primary truncate">{label}</div>
          <div className="mt-2 flex items-center gap-2 text-xs">
            <ClockIcon className="h-4 w-4 shrink-0 text-pf-primary" aria-hidden />
            <div className="min-w-0">
              <div className="text-pf-text-tertiary">Cumulative print hours</div>
              <div
                className="text-pf-text-primary font-medium truncate"
                aria-label={`Cumulative print hours: ${hours}`}
              >
                {hours}
              </div>
            </div>
          </div>
          {odometer.nextDueTaskName && dueState !== 'unknown' && (
            <p className="mt-2 text-xs text-pf-text-secondary truncate">
              Next: <span className="text-pf-text-primary">{odometer.nextDueTaskName}</span>
            </p>
          )}
        </div>
        <DueStateChip state={dueState} />
      </div>
    </Container>
  );
}

function DueStateChip({ state }: { state: ToolheadDueState }) {
  const meta = DUE_STATE_META[state];
  const Icon = meta.icon;
  return (
    <span
      className={clsx(
        'inline-flex shrink-0 items-center gap-1 rounded-full border px-2 py-0.5 text-xs font-medium',
        meta.className
      )}
      data-testid={`due-state-${state}`}
      // The chip is the accessible name for the tooth-scoped due state; it
      // stands alone alongside the toolhead label so screen readers announce
      // the state without needing to read the whole card.
      aria-label={`Maintenance due state: ${meta.label}`}
      role="status"
    >
      <Icon className="h-3.5 w-3.5" aria-hidden />
      <span>{meta.label}</span>
    </span>
  );
}

const DUE_STATE_META: Record<
  ToolheadDueState,
  { label: string; className: string; icon: React.ComponentType<{ className?: string; 'aria-hidden'?: boolean }> }
> = {
  overdue: {
    label: 'Overdue',
    icon: ExclamationTriangleIcon,
    className: 'bg-pf-error/10 border-pf-error/40 text-pf-error',
  },
  'due-today': {
    label: 'Due today',
    icon: ExclamationTriangleIcon,
    className: 'bg-pf-warning/10 border-pf-warning/40 text-pf-warning',
  },
  ok: {
    label: 'OK',
    icon: CheckCircleIcon,
    className: 'bg-pf-success/10 border-pf-success/40 text-pf-success',
  },
  // The "unknown" chip is intentionally distinct from "OK": it uses a
  // question-mark icon and neutral color so operators can distinguish
  // "schedule engine says everything's clear" (green ✓) from "we don't know
  // yet — the schedule feed is loading or failed" (neutral ?).
  unknown: {
    label: 'No data',
    icon: QuestionMarkCircleIcon,
    className: 'bg-pf-bg-dark/50 border-pf-border text-pf-text-tertiary',
  },
};

function describeToolhead(o: PrinterToolheadOdometer): string {
  const parts: string[] = [];
  if (typeof o.toolheadIndex === 'number') {
    parts.push(`T${o.toolheadIndex}`);
  }
  if (o.toolheadName && o.toolheadName.trim()) {
    parts.push(o.toolheadName.trim());
  }
  if (parts.length === 0) {
    parts.push(o.toolheadId);
  }
  return parts.join(' · ');
}

function formatHours(value: number | null | undefined): string {
  if (typeof value !== 'number' || !Number.isFinite(value)) {
    return '—';
  }
  return `${value.toFixed(1)} h`;
}

export default ToolheadOdometerCard;

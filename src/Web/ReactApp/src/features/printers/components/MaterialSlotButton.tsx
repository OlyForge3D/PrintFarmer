import clsx from 'clsx';
import { Button } from '@/common/components/ui';
import { SpoolIcon } from '@/common/components/icons/SpoolIcon';
import { PlusIcon } from '@/common/components/icons/MdiIcons';
import type { LoadoutSlot } from '@/features/printers/utils/materialLoadout';
import { materialSlotState, type FilamentFeedState } from '@/features/printers/utils/materialSlotState';
import type { SpoolmanSpool } from '@/types/api';

interface MaterialSlotButtonProps {
  slot: LoadoutSlot;
  spool?: SpoolmanSpool;
  label?: string;
  accessibleLabel?: string;
  selected: boolean;
  inUse?: boolean;
  activeState?: 'loaded' | 'selected';
  feed?: FilamentFeedState;
  atRisk?: boolean;
  compact?: boolean;
  testId?: string;
  status?: string;
  onSelect: () => void;
}

/** Shared spool grammar for card/sidebar gates, racks and physical toolheads. */
export function MaterialSlotButton({
  slot, spool, label = slot.label, accessibleLabel = label, selected, inUse = false,
  activeState, feed = 'unknown', atRisk = false, compact = false, testId, status = 'unknown', onSelect,
}: MaterialSlotButtonProps) {
  const state = materialSlotState(slot, spool, feed);
  const color = slot.color ?? (spool?.colorHex ? `#${spool.colorHex.replace(/^#/, '')}` : undefined);
  const material = slot.material ?? spool?.material;
  return (
    <Button
      type="button"
      variant="unstyled"
      onClick={onSelect}
      aria-pressed={selected}
      aria-current={inUse ? 'true' : undefined}
      aria-label={`${accessibleLabel}: ${material ?? 'Unknown material'}, ${state.assignment}, ${state.quantity ?? ''}, ${state.filament}${inUse ? ', In use' : ''}${slot.disabled ? ', Disabled' : ''}${atRisk ? ', runout risk' : ''}`}
      data-testid={testId}
      data-source={slot.source}
      data-disabled={slot.disabled ? 'true' : undefined}
      data-active={activeState}
      data-status={status}
      data-assignment={!state.assigned ? 'unassigned' : state.depleted ? 'depleted' : 'assigned'}
      className={clsx(
        'flex w-full min-h-11 min-w-0 flex-col items-center gap-1 rounded-lg border p-2 text-center transition-colors',
        'focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-pf-accent',
        selected ? 'border-pf-accent bg-pf-bg-2' : 'border-transparent hover:bg-pf-bg-2',
        slot.disabled && 'opacity-60',
      )}
    >
      <span className="flex w-full min-w-0 flex-col items-center gap-1">
        <span className="text-xs font-semibold text-pf-text-primary">{label}</span>
        <span className="relative" aria-hidden="true">
          <SpoolIcon decorative size={compact ? 44 : 52} frameColor="var(--pf-text-secondary)"
            fillColor={state.depleted ? undefined : color} />
          {!state.assigned && (
            <PlusIcon className="absolute bottom-0 right-0 h-4 w-4 rounded-full bg-pf-bg-1 text-pf-text-secondary" ariaLabel="" />
          )}
        </span>
        <span className="flex max-w-full items-center gap-1 text-xs text-pf-text-primary">
          {color && <span className="h-2.5 w-2.5 shrink-0 rounded-full border border-pf-border" style={{ backgroundColor: color }} aria-hidden="true" />}
          <span className="truncate">{material ?? 'Unknown material'}</span>
        </span>
        <span className={clsx('text-[11px] leading-snug', state.depleted ? 'text-pf-warning' : 'text-pf-text-secondary')}>
          {state.assignment}
        </span>
        {state.quantity && <span className="text-[11px] leading-snug text-pf-text-secondary">{state.quantity}</span>}
        <span className="text-[11px] leading-snug text-pf-text-secondary">{state.filament}</span>
        {slot.disabled && <span className="text-[11px] text-pf-warning">Disabled</span>}
        {atRisk && <span className="text-[11px] text-pf-warning">Runout risk</span>}
        {inUse && <span className="rounded bg-pf-bg-2 px-1.5 py-0.5 text-[11px] font-semibold text-pf-text-primary">In use</span>}
      </span>
    </Button>
  );
}

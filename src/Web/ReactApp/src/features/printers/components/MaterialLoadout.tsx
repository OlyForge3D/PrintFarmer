import { useMemo, useState } from 'react';
import clsx from 'clsx';
import { toast } from 'sonner';
import { Badge, Button, Tooltip } from '@/common/components/ui';
import { SpoolPickerModal } from '@/features/printers/components/SpoolPickerModal';
import { useSetToolheadSpool, useClearToolheadSpool } from '@/common/hooks/useApi';
import { usePrinterCoverageFromFleet } from '@/features/filament-coverage/hooks';
import {
  FilamentCoverageBadge,
  RunoutRiskChip,
} from '@/features/filament-coverage/components/FilamentCoverageBadge';
import type { ToolheadCoverage } from '@/features/filament-coverage/types';
import type { MmuStatus, ToolheadDto } from '@/types/api';
import {
  isLightColor,
  resolveMaterialLoadout,
  type LoadoutKind,
  type LoadoutSlot,
} from '@/features/printers/utils/materialLoadout';

export interface MaterialLoadoutProps {
  printerId: string;
  /** Live MMU/AMS status; authoritative for how many slots the hardware has. */
  mmuStatus?: MmuStatus;
  /** Persisted toolhead topology; used to translate slot indices for the API. */
  toolheads?: ToolheadDto[];
  /** Printer revision required by the optimistic-concurrency spool endpoints. */
  reviewedRowVersion?: string | null;
  /** Denser rendering for the printer card. */
  compact?: boolean;
  onSpoolChange?: () => void;
  className?: string;
}

const RING_RADIUS = 19;
const RING_CIRCUMFERENCE = 2 * Math.PI * RING_RADIUS;

function slotNoun(kind: LoadoutKind): string {
  return kind === 'gate' ? 'gate' : 'toolhead';
}

/**
 * How much of the ring to fill, and in which colour.
 *
 * The API reports grams remaining and grams demanded but never spool capacity, so
 * a "percent full" ring would need an invented denominator. Instead the ring shows
 * what is actually known: a complete ring when the slot covers its committed
 * demand, a partial arc when it does not (the fraction of demand it can satisfy),
 * and a muted dashed ring when there is nothing to judge.
 */
function ringFor(coverage: ToolheadCoverage | undefined): {
  fraction: number;
  className: string;
  dashed: boolean;
} {
  if (!coverage || coverage.status === 'unknown') {
    return { fraction: 1, className: 'text-pf-border-light', dashed: true };
  }
  if (coverage.status === 'covers') {
    return { fraction: 1, className: 'text-pf-success', dashed: false };
  }
  const remaining = coverage.remainingGrams ?? 0;
  const demand = coverage.totalDemandGrams ?? 0;
  const fraction = demand > 0 ? Math.min(1, Math.max(0.04, remaining / demand)) : 0.04;
  return { fraction, className: 'text-pf-warning', dashed: false };
}

function describeSlot(
  slot: LoadoutSlot,
  kind: LoadoutKind,
  coverage: ToolheadCoverage | undefined,
): string {
  const noun = slot.external ? 'external spool' : slotNoun(kind);
  const material = slot.material ? `loaded with ${slot.material}` : 'empty';
  const risk = coverage?.status === 'runout' ? ', runout risk' : '';
  return `${slot.label} ${noun}, ${material}${risk}`;
}

function SlotButton({
  slot,
  kind,
  coverage,
  compact,
  selected,
  onSelect,
}: {
  slot: LoadoutSlot;
  kind: LoadoutKind;
  coverage: ToolheadCoverage | undefined;
  compact: boolean;
  selected: boolean;
  onSelect: () => void;
}) {
  const ring = ringFor(coverage);
  const size = compact ? 44 : 52;
  const atRisk = coverage?.status === 'runout';
  const swatch = slot.color;

  return (
    // eslint-disable-next-line local/pf-no-raw-html-controls -- Composite slot control: an SVG coverage ring around a filament swatch, which <Button> cannot express
    <button
      type="button"
      onClick={onSelect}
      aria-pressed={selected}
      aria-label={describeSlot(slot, kind, coverage)}
      data-testid={`loadout-slot-${slot.gcodeIndex}`}
      data-status={coverage?.status ?? 'unknown'}
      className={clsx(
        'group relative flex shrink-0 flex-col items-center gap-1 rounded-lg px-1.5 py-1.5',
        'transition-transform duration-150 ease-out will-change-transform',
        'hover:-translate-y-0.5 motion-reduce:transform-none motion-reduce:transition-none',
        'focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-pf-accent',
        selected ? 'bg-pf-bg-2' : 'hover:bg-pf-bg-1',
      )}
    >
      <span className="relative block" style={{ width: size, height: size }}>
        <svg
          width={size}
          height={size}
          viewBox="0 0 44 44"
          className={clsx('block -rotate-90', ring.className)}
          aria-hidden="true"
        >
          {!ring.dashed && ring.fraction < 1 && (
            <circle
              cx="22"
              cy="22"
              r={RING_RADIUS}
              fill="none"
              stroke="currentColor"
              strokeWidth="2.5"
              opacity={0.2}
            />
          )}
          <circle
            cx="22"
            cy="22"
            r={RING_RADIUS}
            fill="none"
            stroke="currentColor"
            strokeWidth="2.5"
            strokeLinecap="round"
            opacity={ring.dashed ? 0.55 : 1}
            strokeDasharray={
              ring.dashed
                ? '2 4'
                : `${RING_CIRCUMFERENCE * ring.fraction} ${RING_CIRCUMFERENCE}`
            }
          />
        </svg>
        <span
          className={clsx(
            'absolute left-1/2 top-1/2 -translate-x-1/2 -translate-y-1/2 rounded-full',
            compact ? 'h-6 w-6' : 'h-7 w-7',
            swatch
              ? isLightColor(swatch)
                ? 'ring-1 ring-pf-border'
                : 'ring-1 ring-white/15'
              : 'border border-dashed border-pf-border-light',
          )}
          style={swatch ? { backgroundColor: swatch } : undefined}
        />
        {atRisk && (
          <span
            className="absolute -right-0.5 -top-0.5 h-2.5 w-2.5 rounded-full bg-pf-warning ring-2 ring-pf-bg-1"
            aria-hidden="true"
          />
        )}
      </span>

      <span className="font-mono text-[11px] font-semibold leading-none text-pf-text-primary">
        {slot.label}
      </span>
      <span
        className={clsx(
          'max-w-[58px] truncate text-[10px] leading-none',
          slot.material ? 'text-pf-text-secondary' : 'text-pf-text-tertiary',
        )}
      >
        {slot.material ?? 'Empty'}
      </span>
    </button>
  );
}

/**
 * Single consolidated materials module for a multi-slot printer.
 *
 * Replaces the previous "Material Slots" strip plus "Spools" assignment list, which
 * derived their slots from different sources and so could disagree about how many
 * slots existed and what was loaded in them. Here one resolved slot list drives the
 * rail, the coverage rings and the assignment drawer, which makes that class of
 * contradiction structurally impossible.
 */
export function MaterialLoadout({
  printerId,
  mmuStatus,
  toolheads,
  reviewedRowVersion,
  compact = false,
  onSpoolChange,
  className,
}: MaterialLoadoutProps) {
  const [selectedKey, setSelectedKey] = useState<string | null>(null);
  const [pickerOpen, setPickerOpen] = useState(false);
  const setSpoolMutation = useSetToolheadSpool();
  const clearSpoolMutation = useClearToolheadSpool();
  const { data: coverage } = usePrinterCoverageFromFleet(printerId);

  const loadout = useMemo(
    () => resolveMaterialLoadout(mmuStatus, toolheads),
    [mmuStatus, toolheads],
  );

  const coverageByIndex = useMemo(() => {
    const map = new Map<number, ToolheadCoverage>();
    coverage?.toolheads.forEach((th) => map.set(th.toolheadIndex, th));
    return map;
  }, [coverage]);

  if (!loadout || loadout.slots.length === 0) return null;

  const { kind, unitLabel, slots } = loadout;
  const selected = slots.find((s) => s.key === selectedKey) ?? null;
  const selectedCoverage = selected ? coverageByIndex.get(selected.gcodeIndex) : undefined;
  const loadedCount = slots.filter((s) => s.material != null || s.spoolId != null).length;
  const busy = setSpoolMutation.isPending || clearSpoolMutation.isPending;
  // The spool endpoints are optimistically concurrent, so without a revision to
  // review against no assignment can succeed. Say so before the user picks a
  // spool rather than failing them afterwards.
  const canMutate = !!reviewedRowVersion;
  const blockedReason = canMutate ? undefined : 'Printer revision unavailable — refresh to assign spools';

  const requireRevision = (): string | null => {
    if (!reviewedRowVersion) {
      toast.error('Printer revision unavailable. Refresh and review again.');
      return null;
    }
    return reviewedRowVersion;
  };

  const handleAssign = async (spoolId: number) => {
    if (!selected) return;
    const revision = requireRevision();
    if (!revision) return;
    await setSpoolMutation.mutateAsync({
      printerId,
      toolheadIndex: selected.apiIndex,
      spoolId,
      reviewedRowVersion: revision,
    });
    setPickerOpen(false);
    onSpoolChange?.();
  };

  const handleClear = () => {
    if (!selected) return;
    const revision = requireRevision();
    if (!revision) return;
    clearSpoolMutation.mutate({
      printerId,
      toolheadIndex: selected.apiIndex,
      reviewedRowVersion: revision,
    });
    onSpoolChange?.();
  };

  return (
    <section
      className={clsx(
        'rounded-lg border border-pf-border bg-pf-bg-1/60',
        compact ? 'p-2' : 'p-2.5',
        className,
      )}
      aria-label="Materials"
      data-testid="material-loadout"
    >
      <header className="mb-1.5 flex items-center gap-2">
        <span className="text-xs font-bold uppercase tracking-wide text-pf-text-secondary">
          Materials
        </span>
        <Badge variant="default" size="sm">{unitLabel}</Badge>
        <span className="text-[10px] text-pf-text-tertiary">
          {loadedCount}/{slots.length} loaded
        </span>
        <div className="ml-auto flex items-center gap-1.5">
          {coverage && (
            <FilamentCoverageBadge
              status={coverage.status}
              ariaContext={coverage.printerName || undefined}
              compact={compact}
            />
          )}
          {coverage?.status === 'runout' && (
            <RunoutRiskChip
              predictedRunoutAt={coverage.earliestPredictedRunoutAt}
              predictedRunoutLayer={null}
            />
          )}
        </div>
      </header>

      <div className="flex flex-wrap items-start gap-0.5" role="group" aria-label={`${unitLabel} slots`}>
        {slots.map((slot) => {
          const slotCoverage = coverageByIndex.get(slot.gcodeIndex);
          const button = (
            <SlotButton
              key={slot.key}
              slot={slot}
              kind={kind}
              coverage={slotCoverage}
              compact={compact}
              selected={selected?.key === slot.key}
              onSelect={() => setSelectedKey(selected?.key === slot.key ? null : slot.key)}
            />
          );
          return slot.name && slot.name !== slot.label ? (
            <Tooltip key={slot.key} content={slot.name} position="top">
              {button}
            </Tooltip>
          ) : (
            button
          );
        })}
      </div>

      {selected && (
        <div
          className="pf-animate-rise mt-2 rounded-lg border border-pf-border bg-pf-bg-0/60 p-2"
          data-testid="loadout-drawer"
        >
          <div className="flex flex-wrap items-center gap-x-2 gap-y-1 text-xs">
            <span className="font-mono font-semibold text-pf-text-primary">{selected.label}</span>
            <Badge variant={selected.external ? 'default' : 'primary'} size="sm">
              {selected.external ? 'External' : kind === 'gate' ? 'Gate' : 'Tool'}
            </Badge>
            {selected.spoolId != null ? (
              <>
                <span className="text-pf-text-primary">{selected.material ?? 'Unknown material'}</span>
                <span className="text-pf-text-tertiary">Spool #{selected.spoolId}</span>
              </>
            ) : (
              <span className="italic text-pf-text-tertiary">No spool assigned</span>
            )}
            {selectedCoverage?.remainingGrams != null && (
              <span className="text-pf-text-tertiary">
                {Math.round(selectedCoverage.remainingGrams)}g left
              </span>
            )}
            {selectedCoverage?.totalDemandGrams != null && selectedCoverage.totalDemandGrams > 0 && (
              <span className="text-pf-text-tertiary">
                {Math.round(selectedCoverage.totalDemandGrams)}g needed
              </span>
            )}
            {selectedCoverage && selectedCoverage.status !== 'covers' && (
              <FilamentCoverageBadge
                status={selectedCoverage.status}
                reason={selectedCoverage.statusReason}
                ariaContext={`${selected.label} ${slotNoun(kind)}`}
              />
            )}
            <div className="ml-auto flex items-center gap-1.5">
              <Button
                variant="secondary"
                size="sm"
                disabled={busy || !canMutate}
                title={blockedReason}
                onClick={() => setPickerOpen(true)}
              >
                {selected.spoolId != null ? 'Change' : 'Assign'}
              </Button>
              {selected.spoolId != null && (
                <Button
                  variant="danger"
                  size="sm"
                  disabled={busy || !canMutate}
                  title={blockedReason}
                  onClick={handleClear}
                >
                  Clear
                </Button>
              )}
            </div>
          </div>
          {blockedReason && (
            <p className="mt-1 text-[10px] text-pf-text-tertiary">{blockedReason}</p>
          )}
        </div>
      )}

      {pickerOpen && selected && (
        <SpoolPickerModal
          isOpen
          onClose={() => setPickerOpen(false)}
          onSelect={handleAssign}
          printerId={printerId}
          activeSpoolId={selected.spoolId}
        />
      )}
    </section>
  );
}

import { useEffect, useMemo, useRef, useState } from 'react';
import clsx from 'clsx';
import { toast } from 'sonner';
import { Badge, Button, Tooltip } from '@/common/components/ui';
import { SpoolPickerModal } from '@/features/printers/components/SpoolPickerModal';
import {
  useSetToolheadSpool,
  useClearToolheadSpool,
  usePrinterDetails,
} from '@/common/hooks/useApi';
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
  resolveActiveSlot,
  type ActiveSlotInfo,
  type LoadoutKind,
  type LoadoutSlot,
} from '@/features/printers/utils/materialLoadout';

export interface MaterialLoadoutProps {
  printerId: string;
  /** Live MMU/AMS status; authoritative for how many slots the hardware has. */
  mmuStatus?: MmuStatus;
  /** Persisted toolhead topology; used to translate slot indices for the API. */
  toolheads?: ToolheadDto[];
  /** Current spool loaded on a single-toolhead printer without AMS/MMU topology. */
  currentSpoolId?: number | null;
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
  activeInfo?: ActiveSlotInfo | null,
): string {
  const noun = slot.external ? 'external spool' : slotNoun(kind);
  const material = slot.material ? `loaded with ${slot.material}` : 'empty';
  const risk = coverage?.status === 'runout' ? ', runout risk' : '';
  const disabled = slot.disabled ? ', disabled' : '';
  // Hazard 2: external slots never match activeGate.
  const isActive = activeInfo && slot.source !== 'external' && slot.gcodeIndex === activeInfo.gcodeIndex;
  const activeLabel = isActive
    ? activeInfo.state === 'loaded' ? ', active and loaded' : ', active'
    : '';
  return `${slot.label} ${noun}, ${material}${disabled}${activeLabel}${risk}`;
}

function SlotButton({
  slot,
  kind,
  coverage,
  compact,
  selected,
  active,
  onSelect,
}: {
  slot: LoadoutSlot;
  kind: LoadoutKind;
  coverage: ToolheadCoverage | undefined;
  compact: boolean;
  selected: boolean;
  active?: ActiveSlotInfo | null;
  onSelect: () => void;
}) {
  const ring = ringFor(coverage);
  const size = compact ? 44 : 52;
  const atRisk = coverage?.status === 'runout';
  const swatch = slot.color;
  // Hazard 2: External slots must never match activeGate.
  const isActive = active && slot.source !== 'external' && slot.gcodeIndex === active.gcodeIndex;
  const slotTestId = slot.source === 'external'
    ? `loadout-slot-external-${slot.apiIndex}`
    : `loadout-slot-${slot.gcodeIndex}`;

  return (
    // eslint-disable-next-line local/pf-no-raw-html-controls -- Composite slot control: an SVG coverage ring around a filament swatch, which <Button> cannot express
    <button
      type="button"
      onClick={onSelect}
      aria-pressed={selected}
      aria-label={describeSlot(slot, kind, coverage, active)}
      aria-current={isActive ? 'true' : undefined}
      data-testid={slotTestId}
      data-source={slot.source}
      data-disabled={slot.disabled ? 'true' : undefined}
      data-active={isActive ? (active!.state === 'loaded' ? 'loaded' : 'selected') : undefined}
      data-status={coverage?.status ?? 'unknown'}
      className={clsx(
        'group relative flex shrink-0 flex-col items-center gap-1 rounded-lg px-1.5 py-1.5',
        'transition-transform duration-150 ease-out will-change-transform',
        'hover:-translate-y-0.5 motion-reduce:transform-none motion-reduce:transition-none',
        'focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-pf-accent',
        selected ? 'bg-pf-bg-2' : 'hover:bg-pf-bg-1',
        // Kept legible rather than hidden: removing the slot would renumber
        // every gate after it and break the mapping to the device's own labels.
        slot.disabled && 'opacity-55 saturate-50',
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
        {isActive && (
          <span
            className={clsx(
              'absolute -bottom-0.5 left-1/2 h-2 w-2 -translate-x-1/2 rotate-45 rounded-[1px]',
              active!.state === 'loaded' ? 'bg-pf-success' : 'bg-pf-accent',
            )}
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
  currentSpoolId,
  reviewedRowVersion,
  compact = false,
  onSpoolChange,
  className,
}: MaterialLoadoutProps) {
  const [selectedKey, setSelectedKey] = useState<string | null>(null);
  const [pickerOpen, setPickerOpen] = useState(false);
  // Optimistic-concurrency anchor, captured when the user opens a slot rather
  // than read at dispatch time. If a SignalR `printerupdated` lands while the
  // drawer is open, the user's decision was made against the *older* state, so
  // the write must still be validated against that revision — otherwise it
  // silently overwrites whatever changed underneath instead of returning 412.
  // Re-anchored to the response revision after each successful mutation (see
  // handleAssign/handleClear) so a second action in the same open drawer (e.g.
  // Assign then Clear) posts the just-written revision instead of the stale
  // one it opened with, and does not spuriously 412 against its own write.
  const [lockedRevision, setLockedRevision] = useState<string | null>(null);
  const [capturedFallbackRevision, setCapturedFallbackRevision] = useState<string | null>(null);
  // The detail query can resolve after a slot opens without a card revision.
  // The reactive snapshot enables the drawer; this ref carries that exact
  // immutable token into handlers after cache or live updates arrive.
  const initialFallbackRevisionRef = useRef<string | null>(null);
  const setSpoolMutation = useSetToolheadSpool();
  const clearSpoolMutation = useClearToolheadSpool();
  // The compact printer DTO can arrive before its concurrency token. Fetch the
  // detail DTO only in that case so spool actions remain available once the
  // authoritative revision has loaded.
  const { data: revisionSource } = usePrinterDetails(printerId, {
    enabled: !reviewedRowVersion,
  });
  const { data: coverage } = usePrinterCoverageFromFleet(printerId);
  const fallbackRevision = revisionSource?.rowVersion ?? null;
  const effectiveRowVersion = reviewedRowVersion ?? fallbackRevision;
  if (selectedKey && !lockedRevision && !capturedFallbackRevision && fallbackRevision) {
    setCapturedFallbackRevision(fallbackRevision);
  }
  const activeRevision = lockedRevision ?? capturedFallbackRevision ?? effectiveRowVersion;

  useEffect(() => {
    if (
      selectedKey &&
      !lockedRevision &&
      !initialFallbackRevisionRef.current &&
      capturedFallbackRevision
    ) {
      initialFallbackRevisionRef.current = capturedFallbackRevision;
    }
  }, [capturedFallbackRevision, lockedRevision, selectedKey]);

  const loadout = useMemo(
    () => resolveMaterialLoadout(mmuStatus, toolheads, currentSpoolId),
    [mmuStatus, toolheads, currentSpoolId],
  );

  const activeSlot = useMemo(
    () => loadout ? resolveActiveSlot(mmuStatus, loadout.kind) : null,
    [mmuStatus, loadout],
  );

  const coverageByIndex = useMemo(() => {
    const map = new Map<number, ToolheadCoverage>();
    coverage?.toolheads?.forEach((th) => map.set(th.toolheadIndex, th));
    return map;
  }, [coverage]);

  if (!loadout || loadout.slots.length === 0) return null;

  const { kind, unitLabel, slots, hasResolvedTopology } = loadout;
  const selected = slots.find((s) => s.key === selectedKey) ?? null;
  // Mirrors the non-contiguous defence in persistedGateIndicesByLiveIndex()
  // (materialLoadout.ts): coverage is keyed by a 0-based g-code index, and
  // joining it to `gcodeIndex` only reflects the right hardware when every
  // gate-derived index is present and contiguous from 0. The backend only
  // ever creates contiguous `1..N` gates today, so this never trips in
  // production — but if it ever didn't, a gappy set of indices could
  // silently join a slot to the wrong toolhead's coverage figures instead of
  // falling back to "unknown".
  const gcodeIndices = slots
    .map((s) => s.gcodeIndex)
    .filter((i): i is number => i != null)
    .sort((a, b) => a - b);
  const hasContiguousGcodeIndices = gcodeIndices.every((index, position) => index === position);
  // Externals do not join the shared coverage-by-gcode-index space (see
  // materialLoadout.ts). Explicitly skip coverage lookup for external slots so
  // an external hotend never inherits the first gate's remaining-material figure.
  const coverageForSlot = (slot: LoadoutSlot): ToolheadCoverage | undefined =>
    slot.gcodeIndex != null && hasContiguousGcodeIndices
      ? coverageByIndex.get(slot.gcodeIndex)
      : undefined;
  const selectedCoverage = selected ? coverageForSlot(selected) : undefined;
  const loadedCount = slots.filter((s) => s.material != null || s.spoolId != null).length;
  const busy = setSpoolMutation.isPending || clearSpoolMutation.isPending;
  // The spool endpoints are optimistically concurrent, so without a revision to
  // review against no assignment can succeed. Say so before the user picks a
  // spool rather than failing them afterwards.
  //
  // For live-MMU printers we additionally require persisted toolhead topology
  // to be resolved: without it the API-index mapping from live gate 0 to
  // persisted `Toolhead.Index` is a guess and could write a G1 assignment to
  // the physical hotend at index 0 (#1585 blocker 2).
  //
  // Preserve the revision present when the drawer opens. If the card omitted
  // one, use the just-fetched detail revision once it becomes available.
  const canMutate = !!activeRevision && hasResolvedTopology;
  const blockedReason = !activeRevision
    ? 'Printer revision unavailable — refresh to assign spools'
    : !hasResolvedTopology
      ? 'Materials topology not yet loaded — refresh to assign spools'
      : undefined;
  // Clearing stays available on a disabled gate: if the device disabled a gate
  // that still carries a stale binding, the user needs a way to release it.
  const disabledSlotReason = 'Disabled on the device — cannot take a spool';

  const selectSlot = (slot: LoadoutSlot) => {
    const next = selectedKey === slot.key ? null : slot.key;
    initialFallbackRevisionRef.current = null;
    setCapturedFallbackRevision(null);
    setSelectedKey(next);
    // Anchor the revision to the state the user is actually looking at.
    setLockedRevision(next ? effectiveRowVersion : null);
  };

  const closeDrawer = () => {
    setPickerOpen(false);
    setSelectedKey(null);
    setLockedRevision(null);
    setCapturedFallbackRevision(null);
    initialFallbackRevisionRef.current = null;
  };

  const requireRevision = (): string | null => {
    const revision = lockedRevision ?? initialFallbackRevisionRef.current ?? activeRevision;
    if (!revision) {
      toast.error('Printer revision unavailable. Refresh and review again.');
      return null;
    }
    if (!hasResolvedTopology) {
      toast.error('Materials topology not yet loaded. Refresh and review again.');
      return null;
    }
    return revision;
  };

  const handleAssign = async (spoolId: number) => {
    if (!selected) return;
    // A disabled gate cannot feed filament, so binding a spool to it would
    // record material the printer can never draw.
    if (selected.disabled) {
      toast.error(`${selected.label} is disabled on the device and cannot take a spool.`);
      return;
    }
    const revision = requireRevision();
    if (!revision) return;
    try {
      const newRevision = await setSpoolMutation.mutateAsync({
        printerId,
        toolheadIndex: selected.apiIndex,
        spoolId,
        reviewedRowVersion: revision,
      });
      // Re-anchor to the revision this write just produced so a second action
      // in the same open drawer (e.g. Change then Clear) validates against
      // what is now persisted, not the stale revision the drawer opened with.
      setLockedRevision(newRevision);
      setPickerOpen(false);
      onSpoolChange?.();
    } catch {
      // Feedback is emitted from the mutation's onError toast. Await so the
      // caller (spool picker) can react to the completed cycle, and swallow so
      // React Query doesn't report an unhandled rejection while the picker
      // stays open for the user to retry.
    }
  };

  const handleClear = async (): Promise<boolean> => {
    if (!selected) return false;
    const revision = requireRevision();
    if (!revision) return false;
    try {
      const newRevision = await clearSpoolMutation.mutateAsync({
        printerId,
        toolheadIndex: selected.apiIndex,
        reviewedRowVersion: revision,
      });
      // Same reasoning as handleAssign — re-anchor so a subsequent action in
      // this open drawer sees the just-cleared state's revision.
      setLockedRevision(newRevision);
      onSpoolChange?.();
      return true;
    } catch {
      // Same reasoning as handleAssign — the mutation's onError toast already
      // told the user what happened; suppress the unhandled rejection.
      return false;
    }
  };

  // The picker's Eject action reports spool id 0, which means "release this
  // slot" — not "bind spool 0". Route it to the clear endpoint so an eject can
  // never persist a bogus zero binding. This is checked ahead of handleAssign's
  // disabled-gate guard because releasing a disabled gate stays deliberately
  // allowed; only binding to one is blocked.
  const handlePickerSelect = async (spoolId: number) => {
    if (spoolId > 0) {
      await handleAssign(spoolId);
      return;
    }
    if (await handleClear()) {
      setPickerOpen(false);
    }
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
          const slotCoverage = coverageForSlot(slot);
          const button = (
            <SlotButton
              key={slot.key}
              slot={slot}
              kind={kind}
              coverage={slotCoverage}
              compact={compact}
              selected={selected?.key === slot.key}
              active={activeSlot}
              onSelect={() => selectSlot(slot)}
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
            {selected.disabled && (
              <Badge variant="warning" size="sm">
                Disabled
              </Badge>
            )}
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
                variant="ghost"
                size="sm"
                onClick={closeDrawer}
              >
                Cancel
              </Button>
              <Button
                variant="secondary"
                size="sm"
                disabled={busy || !canMutate || selected.disabled}
                explainedDisabled={!canMutate || selected.disabled}
                title={selected.disabled ? disabledSlotReason : blockedReason}
                aria-describedby={(!canMutate || selected.disabled) ? `loadout-action-desc-${printerId}` : undefined}
                onClick={() => setPickerOpen(true)}
              >
                {selected.spoolId != null ? 'Change' : 'Assign'}
              </Button>
              {selected.spoolId != null && (
                <Button
                  variant="danger"
                  size="sm"
                  disabled={busy || !canMutate}
                  explainedDisabled={!canMutate}
                  title={blockedReason}
                  aria-describedby={!canMutate ? `loadout-clear-desc-${printerId}` : undefined}
                  onClick={() => void handleClear()}
                >
                  Clear
                </Button>
              )}
            </div>
          </div>
          {(blockedReason || (selected?.disabled && disabledSlotReason)) && (
            <p id={`loadout-action-desc-${printerId}`} className="mt-1 text-[10px] text-pf-text-tertiary">
              {selected?.disabled ? disabledSlotReason : blockedReason}
            </p>
          )}
          {/* Clear is deliberately kept available on a disabled gate (see handleAssign
              comment above), so its only real blocker is a missing/unresolved revision —
              never reuse the disabled-gate text here, or a screen-reader user hearing
              Clear's description would be told the wrong reason it's blocked. */}
          {selected.spoolId != null && blockedReason && (
            <p id={`loadout-clear-desc-${printerId}`} className="mt-1 text-[10px] text-pf-text-tertiary">
              {blockedReason}
            </p>
          )}
        </div>
      )}

      {pickerOpen && selected && (
        <SpoolPickerModal
          isOpen
          onClose={closeDrawer}
          onSelect={handlePickerSelect}
          printerId={printerId}
          activeSpoolId={selected.spoolId}
        />
      )}
    </section>
  );
}


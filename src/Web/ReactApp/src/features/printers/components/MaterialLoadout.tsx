import { useMemo, useState } from 'react';
import clsx from 'clsx';
import { MaterialSlotButton } from '@/features/printers/components/MaterialSlotButton';
import { usePrinterSpoolInventory } from '@/features/printers/hooks/usePrinterSpoolInventory';
import { Badge, Button, Tooltip } from '@/common/components/ui';
import { SpoolPickerModal } from '@/features/printers/components/SpoolPickerModal';
import {
  useSlotSpoolAssignment,
  DISABLED_SLOT_REASON,
} from '@/features/printers/hooks/useSlotSpoolAssignment';
import { usePrinterCoverageFromFleet } from '@/features/filament-coverage/hooks';
import {
  FilamentCoverageBadge,
  RunoutRiskChip,
} from '@/features/filament-coverage/components/FilamentCoverageBadge';
import type { ToolheadCoverage } from '@/features/filament-coverage/types';
import { withOfflineOverride } from '@/features/filament-coverage/utils';
import type { MmuStatus, ToolheadDto } from '@/types/api';
import {
  resolveMaterialLoadout,
  resolveActiveSlot,
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
  /** Catalog model name used only for model-specific persisted gate recovery. */
  modelName?: string;
  /** Printer revision required by the optimistic-concurrency spool endpoints. */
  reviewedRowVersion?: string | null;
  /** Denser rendering for the printer card. */
  compact?: boolean;
  /**
   * Whether the printer is currently reachable. Defaults to `true` so
   * existing callers that don't yet track connectivity keep their prior
   * behavior. When explicitly `false`, the last-known coverage snapshot is
   * treated as stale/unverifiable: every status is downgraded to
   * "unknown" so an offline printer never shows a "Filament OK" success
   * indicator or a "runout" warning it can no longer verify (issue #1684).
   */
  isOnline?: boolean;
  onSpoolChange?: () => void;
  className?: string;
}

function slotNoun(kind: 'gate' | 'tool'): string {
  return kind === 'gate' ? 'gate' : 'toolhead';
}

/**
 * Single consolidated materials module for a multi-slot printer.
 *
 * Replaces the previous "Material Slots" strip plus "Spools" assignment list, which
 * derived their slots from different sources and so could disagree about how many
 * slots existed and what was loaded in them. Here one resolved slot list drives the
 * rail and the assignment drawer, which makes that class of
 * contradiction structurally impossible.
 */
export function MaterialLoadout({
  printerId,
  mmuStatus,
  toolheads,
  currentSpoolId,
  modelName,
  reviewedRowVersion,
  compact = false,
  isOnline = true,
  onSpoolChange,
  className,
}: MaterialLoadoutProps) {
  const [pickerOpen, setPickerOpen] = useState(false);
  const { data: rawCoverage } = usePrinterCoverageFromFleet(printerId);
  const coverage = useMemo(
    () => withOfflineOverride(rawCoverage, isOnline),
    [rawCoverage, isOnline],
  );

  const loadout = useMemo(
    () => resolveMaterialLoadout(mmuStatus, toolheads, currentSpoolId, modelName),
    [mmuStatus, toolheads, currentSpoolId, modelName],
  );

  const inventory = usePrinterSpoolInventory(printerId, !!loadout?.slots.some((slot) => slot.spoolId != null));

  const {
    selectedKey,
    selectSlot: selectSlotKey,
    canMutate,
    blockedReason,
    busy,
    assign,
    clear,
  } = useSlotSpoolAssignment({
    printerId,
    reviewedRowVersion,
    hasResolvedTopology: loadout?.hasResolvedTopology ?? false,
    topologyPending: loadout?.topologyPending ?? false,
    onSpoolChange,
  });

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

  const { kind, unitLabel, slots } = loadout;
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
  const assignedCount = slots.filter((s) => s.spoolId != null).length;
  // Clearing stays available on a disabled gate: if the device disabled a gate
  // that still carries a stale binding, the user needs a way to release it.
  const disabledSlotReason = DISABLED_SLOT_REASON;

  const selectSlot = (slot: LoadoutSlot) => {
    selectSlotKey(selectedKey === slot.key ? null : slot.key);
  };

  const closeDrawer = () => {
    setPickerOpen(false);
    selectSlotKey(null);
  };

  const handleAssign = async (spoolId: number) => {
    if (selected && await assign(selected, spoolId)) setPickerOpen(false);
  };

  const handleClear = async (): Promise<boolean> =>
    selected ? clear(selected) : false;

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
      <header className="mb-1.5 flex flex-wrap items-center gap-2">
        <span className="text-xs font-bold uppercase tracking-wide text-pf-text-secondary">
          Materials
        </span>
        <Badge variant="default" size="sm">{unitLabel}</Badge>
        <span className="text-[10px] text-pf-text-tertiary">
          {assignedCount}/{slots.length} assigned
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

      <div className="grid grid-cols-[repeat(auto-fit,minmax(5.5rem,1fr))] items-stretch gap-1" role="group" aria-label={`${unitLabel} slots`}>
        {slots.map((slot) => {
          const slotCoverage = coverageForSlot(slot);
          const isActive = slot.source !== 'external' && slot.gcodeIndex === activeSlot?.gcodeIndex;
          const feed = isOnline && isActive && mmuStatus?.filamentState?.toLowerCase() === 'loaded' ? 'loaded'
            : isOnline && ((isActive && mmuStatus?.filamentState?.toLowerCase() === 'unloaded') || slot.filamentPresence === 'absent') ? 'not-loaded'
            : isOnline && slot.filamentPresence === 'available' ? 'available' : 'unknown';
          const button = (
            <MaterialSlotButton
              key={slot.key}
              slot={slot}
              spool={inventory.isError ? undefined : inventory.data?.find((spool) => spool.id === slot.spoolId)}
              accessibleLabel={`${slot.label} ${slot.external ? 'external spool' : slotNoun(kind)}`}
              feed={feed}
              inUse={isOnline && isActive && activeSlot?.state === 'loaded'}
              activeState={isActive ? activeSlot?.state === 'loaded' ? 'loaded' : 'selected' : undefined}
              atRisk={slotCoverage?.status === 'runout'}
              status={slotCoverage?.status}
              testId={slot.source === 'external' ? `loadout-slot-external-${slot.apiIndex}` : `loadout-slot-${slot.gcodeIndex}`}
              compact={compact}
              selected={selected?.key === slot.key}
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

      {inventory.isError && (
        <p className="mt-2 text-xs text-pf-text-secondary" role="status">
          Spoolman unavailable — remaining amount unknown.
          <Button variant="ghost" size="sm" onClick={() => void inventory.refetch()}>Retry</Button>
        </p>
      )}
      {selected && (
        <div
          className="pf-animate-rise mt-2 rounded-lg border border-pf-border bg-pf-bg-0/60 p-2"
          data-testid="loadout-drawer"
        >
          <p className="mb-2 text-xs text-pf-text-secondary">Spool assignment tracks inventory; it does not load or unload filament.</p>
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
                {Math.round(selectedCoverage.remainingGrams)}g coverage estimate
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
                  Unassign
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
          assignmentOnly
          pending={busy}
        />
      )}
    </section>
  );
}


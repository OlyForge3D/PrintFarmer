import { useMemo, useState } from 'react';
import clsx from 'clsx';
import { usePrinterSpoolInventory } from '@/features/printers/hooks/usePrinterSpoolInventory';
import { SpoolPickerModal } from '@/features/printers/components/SpoolPickerModal';
import {
  useSlotSpoolAssignment,
  DISABLED_SLOT_REASON,
} from '@/features/printers/hooks/useSlotSpoolAssignment';
import { useAuth } from '@/features/auth/hooks/useAuth';
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
  type LoadoutSpoolInfo,
} from '@/features/printers/utils/materialLoadout';
import { FilamentSlots } from '@/features/printers/components/filament/FilamentSlots';
import {
  buildSlotView,
  slotActionLabel,
  slotPanelTitle,
  type FilamentSlotView,
  type SlotAction,
  type SlotPanelContent,
  type SlotTargetKind,
} from '@/features/printers/components/filament/filamentSlotModel';

/** The printer's generic LOAD_FILAMENT / UNLOAD_FILAMENT macros. */
export interface FilamentMacroControl {
  /** Why the macros cannot run right now (offline, disabled, printing); `undefined` when they can. */
  blockedReason?: string;
  pending?: boolean;
  supportsLoad?: boolean;
  supportsUnload?: boolean;
  onAction: (action: 'load' | 'unload') => void | Promise<void>;
}

export interface MaterialLoadoutProps {
  printerId: string;
  /** Live MMU/AMS status; authoritative for how many slots the hardware has. */
  mmuStatus?: MmuStatus;
  /** Persisted toolhead topology; used to translate slot indices for the API. */
  toolheads?: ToolheadDto[];
  /** Current spool loaded on a single-toolhead printer without AMS/MMU topology. */
  currentSpoolId?: number | null;
  /** Printer-level spool snapshot; fills a single-feed slot that has no persisted spool data. */
  spoolInfo?: LoadoutSpoolInfo | null;
  /** Catalog model name used only for model-specific persisted gate recovery. */
  modelName?: string;
  /** Printer revision required by the optimistic-concurrency spool endpoints. */
  reviewedRowVersion?: string | null;
  /** Denser rendering for the printer card. */
  compact?: boolean;
  /**
   * Whether the printer is currently reachable. Defaults to `true`. When
   * `false` the last-known coverage snapshot is treated as unverifiable, so an
   * offline printer never shows a runout warning it can no longer verify (#1684).
   */
  isOnline?: boolean;
  /**
   * Generic load/unload macros. Omit when the backend does not support them;
   * the slot panel then offers inventory actions only.
   */
  filamentControl?: FilamentMacroControl;
  onSpoolChange?: () => void;
  className?: string;
}

const NO_PERMISSION_REASON = 'You do not have permission to operate filament controls';

/**
 * Materials module for single-toolhead, toolchanger (U1) and gate (AD5X)
 * printers, rendered through the shared Option 4 filament slots: one resting
 * line, a disclosable spool rail and one shared slot action panel.
 */
export function MaterialLoadout({
  printerId,
  mmuStatus,
  toolheads,
  currentSpoolId,
  spoolInfo,
  modelName,
  reviewedRowVersion,
  compact = false,
  isOnline = true,
  filamentControl,
  onSpoolChange,
  className,
}: MaterialLoadoutProps) {
  const [pickerOpen, setPickerOpen] = useState(false);
  const { hasPermission } = useAuth();
  const canOperate = hasPermission('queue', 'start');
  const { data: rawCoverage } = usePrinterCoverageFromFleet(printerId);
  const coverage = useMemo(
    () => withOfflineOverride(rawCoverage, isOnline),
    [rawCoverage, isOnline],
  );

  const loadout = useMemo(
    () => resolveMaterialLoadout(mmuStatus, toolheads, currentSpoolId, modelName, spoolInfo),
    [mmuStatus, toolheads, currentSpoolId, modelName, spoolInfo],
  );

  const inventory = usePrinterSpoolInventory(printerId, !!loadout?.slots.some((slot) => slot.spoolId != null));

  const {
    selectedKey,
    selectSlot,
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

  const { kind, slots } = loadout;
  const single = slots.length === 1 && kind === 'tool' && !(mmuStatus?.gates?.length);
  const selected = slots.find((s) => s.key === selectedKey) ?? null;
  // Coverage is keyed by a 0-based g-code index; joining it to `gcodeIndex`
  // only reflects the right hardware when every index is contiguous from 0.
  // Externals never join that index space (see materialLoadout.ts).
  const gcodeIndices = slots
    .map((s) => s.gcodeIndex)
    .filter((i): i is number => i != null)
    .sort((a, b) => a - b);
  const hasContiguousGcodeIndices = gcodeIndices.every((index, position) => index === position);
  const coverageForSlot = (slot: LoadoutSlot): ToolheadCoverage | undefined =>
    slot.gcodeIndex != null && hasContiguousGcodeIndices
      ? coverageByIndex.get(slot.gcodeIndex)
      : undefined;
  const spoolFor = (slot: LoadoutSlot) =>
    inventory.isError ? undefined : inventory.data?.find((spool) => spool.id === slot.spoolId);
  const isActive = (slot: LoadoutSlot) =>
    isOnline && slot.source !== 'external' && activeSlot != null && slot.gcodeIndex === activeSlot.gcodeIndex;

  const views: FilamentSlotView[] = slots.map((slot) => {
    const slotCoverage = coverageForSlot(slot);
    const view = buildSlotView(slot, spoolFor(slot), {
      id: single ? '' : slot.label,
      active: !single && isActive(slot),
      atRisk: slotCoverage?.status === 'runout',
      testId: slot.source === 'external' ? `loadout-slot-external-${slot.apiIndex}` : `loadout-slot-${slot.gcodeIndex}`,
    });
    const selectedFeed = !single && isActive(slot) && activeSlot?.state !== 'loaded';
    return {
      ...view,
      // Only a loaded feed is "in use"; a selected-but-unloaded feed is marked separately.
      active: view.active && !selectedFeed,
      selectedFeed,
      coverageStatus: slotCoverage?.status ?? 'unknown',
    };
  });
  const summarySlot = views.find((view) => view.active) ?? null;
  const slotByKey = new Map(slots.map((slot) => [slot.key, slot]));

  const target: SlotTargetKind = single ? 'single' : kind === 'gate' ? 'gate' : 'tool';

  const openPicker = (slot: LoadoutSlot) => {
    // The panel already anchored the reviewed revision when the slot opened;
    // re-selecting here would silently adopt a newer live revision.
    if (selectedKey !== slot.key) selectSlot(slot.key);
    setPickerOpen(true);
  };

  const closePicker = () => setPickerOpen(false);

  const handlePickerSelect = async (spoolId: number) => {
    if (!selected) return;
    // The picker's Eject reports spool id 0, meaning "release this slot" — route
    // it to the clear endpoint so an eject can never persist a bogus zero binding.
    const ok = spoolId > 0 ? await assign(selected, spoolId) : await clear(selected);
    if (ok) setPickerOpen(false);
  };

  const macroReason = (slot: LoadoutSlot): string | undefined => {
    if (!canOperate) return NO_PERMISSION_REASON;
    if (filamentControl?.blockedReason) return filamentControl.blockedReason;
    if (filamentControl?.pending) return 'Another filament command is running';
    // The generic macros act on whatever feed the printer has selected, so a
    // multi-feed printer may only run them from that feed's own slot.
    if (!single && !isActive(slot)) {
      const noun = kind === 'gate' ? 'slot' : 'tool';
      return activeSlot == null || !isOnline
        ? `The printer has not reported an active ${noun}`
        : `Acts on the active ${noun} only`;
    }
    return undefined;
  };

  const panelFor = (view: FilamentSlotView): SlotPanelContent => {
    const slot = slotByKey.get(view.key)!;
    const t = target;
    const actions: SlotAction[] = [];
    const assignReason = slot.disabled ? DISABLED_SLOT_REASON : canMutate ? undefined : blockedReason;
    if (slot.spoolId != null) {
      actions.push({
        kind: 'change',
        label: slotActionLabel('change', t, view.id),
        onClick: () => openPicker(slot),
        disabledReason: assignReason,
        busy,
      });
      // Releasing stays available on a disabled gate so a stale binding can be cleared.
      actions.push({
        kind: 'unassign',
        label: slotActionLabel('unassign', t, view.id),
        onClick: () => void clear(slot),
        disabledReason: canMutate ? undefined : blockedReason,
        busy,
      });
    } else {
      actions.push({
        kind: 'assign',
        label: slotActionLabel('assign', t, view.id),
        onClick: () => openPicker(slot),
        disabledReason: assignReason,
        busy,
      });
    }
    if (filamentControl && slot.source !== 'external') {
      const reason = macroReason(slot);
      actions.push(
        {
          kind: 'load',
          label: slotActionLabel('load', t, view.id),
          onClick: () => void filamentControl.onAction('load'),
          disabledReason: filamentControl.supportsLoad === false ? 'Filament loading is not supported' : reason,
        },
        {
          kind: 'unload',
          label: slotActionLabel('unload', t, view.id),
          onClick: () => void filamentControl.onAction('unload'),
          disabledReason: filamentControl.supportsUnload === false ? 'Filament unloading is not supported' : reason,
        },
      );
    }
    const slotCoverage = coverageForSlot(slot);
    const extra = slotCoverage?.status === 'runout' ? (
      <div className="mt-1 flex flex-wrap items-center gap-1.5 text-xs text-pf-text-tertiary">
        <FilamentCoverageBadge status="runout" reason={slotCoverage.statusReason} ariaContext={view.id || undefined} />
        {slotCoverage.totalDemandGrams != null && slotCoverage.totalDemandGrams > 0 && (
          <span className="tabular-nums">Needs {Math.round(slotCoverage.totalDemandGrams)} g</span>
        )}
      </div>
    ) : undefined;
    return { title: slotPanelTitle(t, view.id), actions, extra };
  };

  return (
    <section
      className={clsx(compact ? 'py-1' : undefined, className)}
      aria-label="Materials"
      data-testid="material-loadout"
    >
      {coverage?.status === 'runout' && (
        <div className="mb-1 flex flex-wrap items-center gap-1.5">
          <FilamentCoverageBadge status="runout" ariaContext={coverage.printerName || undefined} />
          <RunoutRiskChip predictedRunoutAt={coverage.earliestPredictedRunoutAt} predictedRunoutLayer={null} />
        </div>
      )}

      <FilamentSlots
        slots={views}
        summarySlot={summarySlot}
        mode={single ? 'single' : 'multi'}
        unitWord={kind === 'tool' ? 'tools' : 'slots'}
        selectedKey={selected?.key ?? null}
        onSelect={selectSlot}
        panelFor={panelFor}
        testId="material-loadout-slots"
      />

      {pickerOpen && selected && (
        <SpoolPickerModal
          isOpen
          onClose={closePicker}
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

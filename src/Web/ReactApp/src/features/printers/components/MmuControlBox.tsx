import { useState, useCallback, useMemo, type ReactNode } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import type { MmuStatus, ToolheadDto } from '@/types/api';
import { usePrinterSpoolInventory } from '@/features/printers/hooks/usePrinterSpoolInventory';
import { useAuth } from '@/features/auth/hooks/useAuth';
import { SpoolPickerModal } from '@/features/printers/components/SpoolPickerModal';
import {
  useSlotSpoolAssignment,
  DISABLED_SLOT_REASON,
} from '@/features/printers/hooks/useSlotSpoolAssignment';
import {
  resolveMaterialLoadout,
  resolveQidiRackSlot,
  type LoadoutSlot,
} from '@/features/printers/utils/materialLoadout';
import { queryKeys } from '@/common/hooks/useApi';
import { getErrorMessage, isApiError } from '@/common/utils/apiErrors';
import { usePrinterCoverageFromFleet } from '@/features/filament-coverage/hooks';
import {
  FilamentCoverageBadge,
  RunoutRiskChip,
} from '@/features/filament-coverage/components/FilamentCoverageBadge';
import type { ToolheadCoverage } from '@/features/filament-coverage/types';
import { withOfflineOverride } from '@/features/filament-coverage/utils';
import { apiClient } from '@/services/api';
import { toast } from 'sonner';
import { MmuProtocol } from '../constants/mmuProtocol';
import { Button, Tooltip } from '@/common/components/ui';
import { GearIcon, HomeIcon } from '@/common/components/icons/MdiIcons';
import { FilamentSlots } from '@/features/printers/components/filament/FilamentSlots';
import {
  buildSlotView,
  slotActionLabel,
  slotPanelTitle,
  type FilamentSlotView,
  type SlotAction,
  type SlotActionKind,
  type SlotPanelContent,
} from '@/features/printers/components/filament/filamentSlotModel';
import type { FilamentMacroControl } from '@/features/printers/components/MaterialLoadout';

interface MmuControlBoxProps {
  /** Printer ID for API commands */
  printerId: string;
  /** MMU status from real-time updates */
  mmuStatus: MmuStatus;
  /** Whether printer is online (enables/disables commands) */
  isOnline: boolean;
  /** Persisted toolhead topology; maps live gates to the spool-assignment API index. */
  toolheads?: ToolheadDto[];
  /** Catalog model identity for model-specific persisted gate recovery. */
  modelName?: string;
  /** Printer revision required by the optimistic-concurrency spool endpoints. */
  reviewedRowVersion?: string | null;
  /**
   * Show the QidiBox external spool holder ("Rack") as a slot. Hidden by
   * default; when hidden but feeding, the resting line still names it.
   */
  showExternalSpool?: boolean;
  /**
   * Printer-level LOAD/UNLOAD_FILAMENT macros. Offered from the slot panel only
   * while the unit itself is disabled, when gate commands cannot run.
   */
  filamentControl?: FilamentMacroControl;
  /** Called after a spool is assigned or cleared. */
  onSpoolChange?: () => void;
  className?: string;
}

/** QidiBox / Happy Hare gate id: four gates per unit, "1A".."1D", "2A".. */
function mmuGateId(index: number): string {
  return `${Math.floor(index / 4) + 1}${String.fromCharCode(65 + (index % 4))}`;
}

const NO_PERMISSION_REASON = 'You do not have permission to operate filament controls';

/**
 * Materials module for MMU / AFC / QidiBox units, rendered through the shared
 * Option 4 filament slots. Each gate's panel carries both the inventory
 * actions (assign, change, unassign) and the protocol's physical commands.
 */
export function MmuControlBox({
  printerId,
  mmuStatus,
  isOnline,
  toolheads,
  modelName,
  reviewedRowVersion,
  showExternalSpool = false,
  filamentControl,
  onSpoolChange,
  className,
}: MmuControlBoxProps) {
  const [pendingAction, setPendingAction] = useState<string | null>(null);
  const [pickerOpen, setPickerOpen] = useState(false);
  const queryClient = useQueryClient();
  const { hasPermission } = useAuth();
  const canOperate = hasPermission('queue', 'start');

  const loadout = useMemo(
    () => resolveMaterialLoadout(mmuStatus, toolheads, undefined, modelName),
    [mmuStatus, toolheads, modelName],
  );
  const spoolAssignment = useSlotSpoolAssignment({
    printerId,
    reviewedRowVersion,
    hasResolvedTopology: loadout?.hasResolvedTopology ?? false,
    topologyPending: loadout?.topologyPending ?? false,
    onSpoolChange,
  });

  const { data: rawCoverage } = usePrinterCoverageFromFleet(printerId);
  const coverage = useMemo(
    () => withOfflineOverride(rawCoverage, isOnline),
    [rawCoverage, isOnline],
  );
  // Coverage is keyed by 0-based g-code tool, which is the live gate index only
  // when the reported gates are contiguous from 0 and the saved layout resolves
  // to the same gates via the canonical `Toolhead.Index = gate + 1` mapping;
  // otherwise show "unknown" rather than join a gate to another gate's figures.
  const coverageByGate = useMemo(() => {
    const map = new Map<number, ToolheadCoverage>();
    if (!loadout?.hasResolvedTopology) return map;
    const indices = mmuStatus.gates.map((gate) => gate.index).sort((a, b) => a - b);
    if (!indices.every((index, position) => index === position)) return map;
    const byTool = new Map(coverage?.toolheads?.map((th) => [th.toolheadIndex, th]) ?? []);
    for (const index of indices) {
      const slot = loadout.slots.find((s) => s.key === `gate-${index}`);
      const th = byTool.get(index);
      if (slot?.apiIndex === index + 1 && th) map.set(index, th);
    }
    return map;
  }, [coverage, mmuStatus.gates, loadout]);

  const isQidibox = mmuStatus.mmuType === MmuProtocol.Qidibox;
  const isAfc = mmuStatus.mmuType === MmuProtocol.Afc;
  const isHappyHare = !isQidibox && !isAfc;
  const activeGate = mmuStatus.activeGate >= 0 ? mmuStatus.activeGate : null;
  const filamentUnloaded = mmuStatus.filamentState?.toLowerCase() === 'unloaded';

  // QidiBox external spool holder ("Rack"). It is bound through its persisted
  // physical toolhead, never through a box slot, and box commands cannot reach it.
  const rackSlot = useMemo(
    () => resolveQidiRackSlot(mmuStatus, toolheads),
    [mmuStatus, toolheads],
  );
  const inventory = usePrinterSpoolInventory(printerId, !!rackSlot?.spoolId || !!loadout?.slots.some((slot) => slot.spoolId != null));
  // QidiBox reuses the Happy Hare bypass sentinel (-2) for the Rack (filament_slot16);
  // box slots are never reported active at the same time.
  const rackActive = isOnline && isQidibox && mmuStatus.hasBypass && mmuStatus.activeGate === -2 && rackSlot !== null;

  const spoolFor = (slot: LoadoutSlot) =>
    inventory.isError ? undefined : inventory.data?.find((spool) => spool.id === slot.spoolId);

  // Telemetry arrays are not guaranteed to be ordered by gate index, so every
  // lookup goes through the explicit gate identity rather than array position.
  const gateSlots = [...mmuStatus.gates]
    .sort((a, b) => a.index - b.index)
    .map((gate) => ({ gate, slot: loadout?.slots.find((s) => s.key === `gate-${gate.index}`) }))
    .filter((entry): entry is { gate: MmuStatus['gates'][number]; slot: LoadoutSlot } => entry.slot != null);

  const gateViews: FilamentSlotView[] = gateSlots.map(({ gate, slot }) => ({
    ...buildSlotView(slot, spoolFor(slot), {
      id: mmuGateId(gate.index),
      active: isOnline && gate.index === activeGate && !filamentUnloaded,
      atRisk: coverageByGate.get(gate.index)?.status === 'runout',
      testId: `mmu-gate-${gate.index}`,
    }),
    coverageStatus: coverageByGate.get(gate.index)?.status ?? 'unknown',
  }));
  // The rack's backing toolhead name ("Toolhead 0") is an internal detail.
  const rackView = rackSlot
    ? { ...buildSlotView(rackSlot, spoolFor(rackSlot), { id: 'Rack', active: rackActive, testId: 'mmu-rack' }), deviceName: undefined }
    : null;
  const railViews = rackView && showExternalSpool ? [rackView, ...gateViews] : gateViews;
  const summarySlot = gateViews.find((view) => view.active) ?? (rackView?.active ? rackView : null);

  const slotForKey = (key: string | null): LoadoutSlot | null => {
    if (!key) return null;
    if (rackSlot?.key === key) return rackSlot;
    return loadout?.slots.find((s) => s.key === key) ?? null;
  };
  const selectedSlot = slotForKey(spoolAssignment.selectedKey);
  const selectedKey = selectedSlot && railViews.some((view) => view.key === selectedSlot.key)
    ? selectedSlot.key
    : null;
  const topologyMismatch = !!loadout && !loadout.hasResolvedTopology && !loadout.topologyPending;

  const openAssignPicker = (slot: LoadoutSlot) => {
    // The revision is anchored when the slot panel opens; re-selecting here
    // would silently adopt a newer live revision.
    if (spoolAssignment.selectedKey !== slot.key) spoolAssignment.selectSlot(slot.key);
    setPickerOpen(true);
  };

  // The picker is pinned to the slot that was open when it launched, so a live
  // active-gate change cannot silently retarget the confirmation.
  const pinnedSlot = pickerOpen ? selectedSlot : null;

  // Spool id 0 from the picker's Eject action means "release this slot".
  const handlePickerSelect = async (spoolId: number) => {
    if (!pinnedSlot) {
      toast.error('That slot is no longer reported by the device. Review the slots and try again.');
      setPickerOpen(false);
      spoolAssignment.selectSlot(null);
      return;
    }
    const ok = spoolId > 0
      ? await spoolAssignment.assign(pinnedSlot, spoolId)
      : await spoolAssignment.clear(pinnedSlot);
    if (ok) setPickerOpen(false);
  };

  const handleRetryTopology = () => {
    void queryClient.invalidateQueries({ queryKey: queryKeys.printerDetails(printerId) });
  };

  const executeCommand = useCallback(async (label: string, fn: () => Promise<unknown>) => {
    setPendingAction(label);
    try {
      await fn();
    } catch (err) {
      console.error(`MMU ${label} failed:`, err);
      const problem = isApiError(err) ? err.data : undefined;
      const title = problem && typeof problem === 'object' && 'title' in problem
        && typeof problem.title === 'string' ? problem.title.trim() : '';
      toast.error(title || getErrorMessage(err, `MMU ${label} failed`));
    } finally {
      setPendingAction(null);
    }
  }, []);

  const laneName = (index: number) =>
    mmuStatus.gates.find((gate) => gate.index === index)?.name ?? `lane${index + 1}`;

  const runGateCommand = (kind: 'load' | 'unload' | 'eject', index: number) => {
    const label = kind === 'load' ? 'Load' : kind === 'unload' ? 'Unload' : 'Eject';
    if (isQidibox) {
      void executeCommand(label, () => apiClient.mmuGateAction(printerId, { protocol: 'Qidibox', action: label, gateIndex: index }));
    } else if (isAfc) {
      if (kind === 'eject') return;
      void executeCommand(label, () => apiClient.mmuGateAction(printerId, { protocol: 'Afc', action: label, laneName: laneName(index) }));
    } else if (kind === 'load') {
      void executeCommand(label, () => apiClient.mmuChangeTool(printerId, index));
    } else {
      void executeCommand(label, () => apiClient.mmuEject(printerId));
    }
  };

  const commandReason = !isOnline ? 'Printer is offline'
    : !canOperate ? NO_PERMISSION_REASON
      : !mmuStatus.enabled ? 'The filament unit is disabled'
        : pendingAction ? `${pendingAction} in progress`
          : undefined;

  // Load, Unload and Eject per protocol. QidiBox and AFC address a gate
  // directly; Happy Hare loads by tool change and unloads whatever is loaded,
  // so its Unload only acts from the loaded gate and it has no separate Eject.
  const physicalKinds: SlotActionKind[] = isQidibox ? ['load', 'unload', 'eject'] : ['load', 'unload'];

  const panelFor = (view: FilamentSlotView): SlotPanelContent => {
    const slot = slotForKey(view.key)!;
    const isRack = rackSlot?.key === slot.key;
    const target = isRack ? 'rack' : 'gate';
    const busy = spoolAssignment.busy;
    const mutateReason = spoolAssignment.canMutate ? undefined : spoolAssignment.blockedReason;
    const assignReason = slot.disabled ? DISABLED_SLOT_REASON : mutateReason;
    const actions: SlotAction[] = slot.spoolId != null
      ? [
        { kind: 'change', label: slotActionLabel('change', target, view.id), onClick: () => openAssignPicker(slot), disabledReason: assignReason, busy },
        // Releasing is allowed on a device-disabled gate: it only removes a stale
        // binding, it never asks the gate to feed filament.
        { kind: 'unassign', label: slotActionLabel('unassign', target, view.id), onClick: () => void spoolAssignment.clear(slot), disabledReason: mutateReason, busy },
      ]
      : [{ kind: 'assign', label: slotActionLabel('assign', target, view.id), onClick: () => openAssignPicker(slot), disabledReason: assignReason, busy }];

    if (!mmuStatus.enabled && filamentControl) {
      const macroReason = !canOperate ? NO_PERMISSION_REASON
        : filamentControl.blockedReason
          ?? (filamentControl.pending ? 'Another filament command is running' : undefined);
      for (const kind of ['load', 'unload'] as const) {
        actions.push({
          kind,
          label: slotActionLabel(kind, 'single', view.id),
          onClick: () => filamentControl.onAction(kind),
          disabledReason: macroReason,
        });
      }
    } else if (!isRack) {
      const index = slot.gcodeIndex ?? 0;
      for (const kind of physicalKinds) {
        const reason = commandReason
          ?? (kind === 'load' && slot.disabled ? DISABLED_SLOT_REASON : undefined)
          ?? (isHappyHare && kind === 'unload' && index !== activeGate ? 'Unloads the loaded gate only' : undefined);
        actions.push({
          kind,
          label: slotActionLabel(kind, target, view.id),
          onClick: () => runGateCommand(kind as 'load' | 'unload' | 'eject', index),
          disabledReason: reason,
        });
      }
    }

    const gateCoverage = isRack ? undefined : coverageByGate.get(slot.gcodeIndex ?? -1);
    const extra = gateCoverage && gateCoverage.status !== 'covers' && gateCoverage.status !== 'unknown' ? (
      <div className="mt-1 flex flex-wrap items-center gap-1.5 text-xs text-pf-text-tertiary">
        <FilamentCoverageBadge status={gateCoverage.status} reason={gateCoverage.statusReason} ariaContext={`Slot ${view.id}`} />
        {gateCoverage.totalDemandGrams != null && gateCoverage.totalDemandGrams > 0 && (
          <span className="tabular-nums">Needs {Math.round(gateCoverage.totalDemandGrams)} g</span>
        )}
      </div>
    ) : undefined;
    return { title: isRack ? 'Rack' : slotPanelTitle('gate', view.id), actions, extra };
  };

  const statusBadges: ReactNode[] = [];
  if (coverage?.status === 'runout') {
    statusBadges.push(
      <FilamentCoverageBadge key="cov" status="runout" ariaContext={coverage.printerName || undefined} compact />,
      <RunoutRiskChip key="risk" predictedRunoutAt={coverage.earliestPredictedRunoutAt} predictedRunoutLayer={null} />,
    );
  }
  if (mmuStatus.action && mmuStatus.action !== 'Idle' && mmuStatus.action !== 'Initialized') {
    statusBadges.push(
      <span key="action" className="inline-flex items-center gap-1 rounded-xs bg-pf-accent-bg/15 px-1.5 py-0.5 text-[11px] font-semibold text-pf-accent">
        <span aria-hidden="true" className="size-1.5 rounded-full bg-pf-accent motion-safe:animate-pulse" />
        {mmuStatus.action}
      </span>,
    );
  } else if (pendingAction) {
    statusBadges.push(
      <span key="pending" role="status" className="text-[11px] font-medium text-pf-accent">{pendingAction}…</span>,
    );
  }
  if (isHappyHare && mmuStatus.endlessSpool) {
    statusBadges.push(<span key="endless" className="rounded-xs bg-pf-success/10 px-1.5 py-0.5 text-[11px] font-medium text-pf-success">Endless spool</span>);
  }
  if (isHappyHare && mmuStatus.clogDetection) {
    statusBadges.push(<span key="clog" className="rounded-xs bg-pf-warning/10 px-1.5 py-0.5 text-[11px] font-medium text-pf-warning">Clog detection</span>);
  }
  if (isHappyHare && !mmuStatus.isHomed) {
    statusBadges.push(<span key="homed" className="rounded-xs bg-pf-error/10 px-1.5 py-0.5 text-[11px] font-medium text-pf-error">Not homed</span>);
  }

  const homeReason = commandReason;
  const recoverReason = !isOnline ? 'Printer is offline'
    : !canOperate ? NO_PERMISSION_REASON
      : !mmuStatus.enabled ? 'The filament unit is disabled' : undefined;

  return (
    <section className={className} aria-label="Materials" data-testid="mmu-control-box">
      {(statusBadges.length > 0 || isHappyHare) && (
        <div className="mb-1 flex flex-wrap items-center gap-1.5">
          {statusBadges}
          {isHappyHare && (
            <span className="ml-auto inline-flex items-center">
              <UnitCommandButton label="Home the MMU" reason={homeReason} onClick={() => void executeCommand('Home', () => apiClient.mmuHome(printerId))}>
                <HomeIcon className="size-4" ariaLabel="" />
              </UnitCommandButton>
              {mmuStatus.action === 'Error' && (
                // Recover is for error states — available even while another action seems pending.
                <UnitCommandButton label="Recover the MMU from error" reason={recoverReason} danger onClick={() => void executeCommand('Recover', () => apiClient.mmuRecover(printerId))}>
                  <GearIcon className="size-4" ariaLabel="" />
                </UnitCommandButton>
              )}
            </span>
          )}
        </div>
      )}

      <FilamentSlots
        slots={railViews}
        summarySlot={summarySlot}
        mode="multi"
        unitWord="slots"
        selectedKey={selectedKey}
        onSelect={spoolAssignment.selectSlot}
        panelFor={panelFor}
        testId="mmu-slots"
      />

      {inventory.isError && (
        <p className="mt-2 text-xs text-pf-text-secondary" role="status">
          Spoolman unavailable — remaining amount unknown.
          <Button variant="ghost" size="sm" onClick={() => void inventory.refetch()}>Retry</Button>
        </p>
      )}
      {spoolAssignment.blockedReason && (loadout?.topologyPending || topologyMismatch) && (
        <div className="mt-2 flex items-center gap-2 text-xs text-pf-text-secondary" role="status">
          <span>{spoolAssignment.blockedReason}</span>
          {topologyMismatch && (
            <Button type="button" variant="ghost" size="sm" onClick={handleRetryTopology}>
              Retry
            </Button>
          )}
        </div>
      )}
      {pickerOpen && (
        <SpoolPickerModal
          isOpen
          onClose={() => setPickerOpen(false)}
          onSelect={handlePickerSelect}
          printerId={printerId}
          activeSpoolId={pinnedSlot?.spoolId}
          assignmentOnly
          pending={spoolAssignment.busy}
        />
      )}
    </section>
  );
}

function UnitCommandButton({
  label,
  reason,
  danger = false,
  onClick,
  children,
}: {
  label: string;
  reason?: string;
  danger?: boolean;
  onClick: () => void;
  children: ReactNode;
}) {
  const disabled = reason != null;
  return (
    <Tooltip content={disabled ? `${label} — ${reason}` : label} ariaMode={disabled ? 'describe' : 'hidden'} position="top" delay={400} className="w-max max-w-56 whitespace-normal text-center">
      <Button
        type="button"
        variant="unstyled"
        aria-label={label}
        disabled={disabled}
        explainedDisabled={disabled}
        onClick={onClick}
        className={`inline-flex size-11 items-center justify-center rounded-md transition-colors focus-visible:outline-2 focus-visible:-outline-offset-2 focus-visible:outline-pf-accent data-[pf-disabled-explained]:cursor-not-allowed data-[pf-disabled-explained]:opacity-40 ${danger ? 'text-pf-error hover:bg-pf-error/10' : 'text-pf-text-secondary hover:bg-pf-bg-2 hover:text-pf-text-primary'}`}
      >
        {children}
      </Button>
    </Tooltip>
  );
}

import { useState, useCallback, useMemo, type ReactNode } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { MmuGateStatus, type MmuStatus, type MmuGate, type ToolheadDto } from '@/types/api';
import { SpoolPickerModal } from '@/features/printers/components/SpoolPickerModal';
import {
  useSlotSpoolAssignment,
  DISABLED_SLOT_REASON,
} from '@/features/printers/hooks/useSlotSpoolAssignment';
import { resolveMaterialLoadout, resolveQidiRackSlot } from '@/features/printers/utils/materialLoadout';
import { queryKeys } from '@/common/hooks/useApi';
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
import { Button, CollapsibleSection } from '@/common/components/ui';
import {
  GearIcon,
  EjectIcon,
  HomeIcon,
  RefreshIcon,
} from '@/common/components/icons/MdiIcons';

// ── Spool SVG visualization ──

interface SpoolProps {
  /** CSS color for the filament winding */
  color?: string;
  /** Whether this slot is currently selected/active */
  active?: boolean;
  /** Whether filament is present */
  available?: boolean;
  /** Size in pixels */
  size?: number;
}

/** SVG spool icon that shows filament color and presence. */
function SpoolIcon({ color, active, available, size = 56 }: SpoolProps) {
  // Default gray for empty/unknown, use filament color when available
  const windingColor = available && color ? color : 'var(--pf-text-tertiary, #555)';
  const rimColor = 'var(--pf-border, #888)';
  const hubColor = 'var(--pf-bg-2)';

  return (
    <svg
      width={size}
      height={size * 1.15}
      viewBox="0 0 56 64"
      fill="none"
      xmlns="http://www.w3.org/2000/svg"
      aria-hidden="true"
      className={active ? 'drop-shadow-[0_0_6px_rgba(59,130,246,0.6)]' : ''}
    >
      {/* Side flange (left) */}
      <ellipse cx="28" cy="8" rx="22" ry="8" fill={rimColor} opacity="0.7" />
      {/* Filament winding */}
      <rect x="8" y="8" width="40" height="44" rx="4" fill={windingColor} opacity={available ? 0.85 : 0.25} />
      {/* Front flange */}
      <ellipse cx="28" cy="52" rx="22" ry="8" fill={rimColor} opacity="0.8" />
      {/* Hub hole */}
      <ellipse cx="28" cy="30" rx="8" ry="6" fill={hubColor} opacity="0.6" />
      {/* Highlight */}
      <rect x="12" y="12" width="6" height="36" rx="3" fill="#fff" opacity="0.12" />
      {/* Active selection ring */}
      {active && (
        <rect
          x="4"
          y="4"
          width="48"
          height="56"
          rx="6"
          stroke="#3b82f6"
          strokeWidth="2.5"
          fill="none"
        />
      )}
    </svg>
  );
}

// ── Gate status helpers ──

function gateStatusLabel(status: MmuGateStatus): string {
  switch (status) {
    case MmuGateStatus.Disabled: return 'Disabled';
    case MmuGateStatus.Empty: return 'Empty';
    case MmuGateStatus.Available: return 'Ready';
    default: return '?';
  }
}

function gateStatusColor(status: MmuGateStatus): string {
  switch (status) {
    case MmuGateStatus.Disabled: return 'text-pf-text-tertiary';
    case MmuGateStatus.Empty: return 'text-pf-error';
    case MmuGateStatus.Available: return 'text-pf-success';
    default: return 'text-pf-warning';
  }
}

// ── Color swatch ──

function ColorSwatch({ color }: { color?: string }) {
  if (!color) {
    return (
      <span
        role="img"
        className="inline-block w-5 h-5 rounded-full bg-pf-bg-2 border border-pf-border"
        title="Unknown color"
        aria-label="Unknown color"
      />
    );
  }

  return (
    <span
      role="img"
      className="inline-block w-5 h-5 rounded-full border border-pf-border"
      style={{ backgroundColor: color }}
      title={color}
      aria-label={`Filament color: ${color}`}
    />
  );
}

// ── Gate slot card ──

interface GateSlotProps {
  gate: MmuGate;
  isActive: boolean;
  coverage?: ToolheadCoverage;
  onSelect: (gateIndex: number) => void;
}

function GateSlot({ gate, isActive, coverage, onSelect }: GateSlotProps) {
  const unit = Math.floor(gate.index / 4);
  const slot = gate.index % 4;
  const label = `${unit + 1}${String.fromCharCode(65 + slot)}`;
  const available = gate.status === MmuGateStatus.Available;
  const atRisk = coverage?.status === 'runout';

  return (
    <Button
      type="button"
      variant="unstyled"
      className={`
        flex flex-col items-center gap-1 p-2 rounded-lg border transition-colors cursor-pointer min-w-[70px]
        ${isActive
          ? 'border-pf-accent bg-pf-accent-bg/15'
          : atRisk
            ? 'border-pf-error bg-pf-bg-1 hover:bg-pf-bg-2'
            : 'border-pf-border bg-pf-bg-1 hover:bg-pf-bg-2'}
      `}
      onClick={() => onSelect(gate.index)}
      aria-pressed={isActive}
      aria-label={`Gate ${label}: ${gate.material ?? 'Unknown'} - ${gateStatusLabel(gate.status)}${atRisk ? ', runout risk' : ''}`}
      data-status={coverage?.status ?? 'unknown'}
    >
      {/* Gate label with refresh icon */}
      <div className="flex items-center gap-1 text-xs text-pf-text-secondary">
        <RefreshIcon className="w-3 h-3 opacity-50" ariaLabel="" />
        <span className="font-medium">{label}</span>
      </div>

      {/* Spool visualization */}
      <SpoolIcon
        color={gate.color}
        active={isActive}
        available={available}
        size={48}
      />

      {/* Material label */}
      <span className={`text-xs font-medium ${gateStatusColor(gate.status)}`}>
        {available ? (gate.material || '?') : gateStatusLabel(gate.status)}
      </span>
    </Button>
  );
}

// ── Main ControlBox component ──

interface MmuControlBoxProps {
  /** Printer ID for API commands */
  printerId: string;
  /** MMU status from real-time updates */
  mmuStatus: MmuStatus;
  /** Whether printer is online (enables/disables commands) */
  isOnline: boolean;
  /** Persisted toolhead topology; maps live gates to the spool-assignment API index. */
  toolheads?: ToolheadDto[];
  /** Printer revision required by the optimistic-concurrency spool endpoints. */
  reviewedRowVersion?: string | null;
  /** Called after a spool is assigned or cleared. */
  onSpoolChange?: () => void;
}

/**
 * Control Box panel for MMU/ERCF/AMS multi-material units.
 * Displays gate status with spool visualizations and provides
 * load/unload/select and spool-assignment commands.
 */
export function MmuControlBox({
  printerId,
  mmuStatus,
  isOnline,
  toolheads,
  reviewedRowVersion,
  onSpoolChange,
}: MmuControlBoxProps) {
  const [isExpanded, setIsExpanded] = useState(true);
  const [selectedGate, setSelectedGate] = useState<number | null>(null);
  const [rackSelected, setRackSelected] = useState(false);
  const [pendingAction, setPendingAction] = useState<string | null>(null);
  const [pickerOpen, setPickerOpen] = useState(false);
  const queryClient = useQueryClient();

  const loadout = useMemo(
    () => resolveMaterialLoadout(mmuStatus, toolheads),
    [mmuStatus, toolheads],
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
  // when the reported gates are contiguous from 0; otherwise show "unknown"
  // rather than join a gate to another gate's figures.
  const coverageByGate = useMemo(() => {
    const map = new Map<number, ToolheadCoverage>();
    const indices = mmuStatus.gates.map((gate) => gate.index).sort((a, b) => a - b);
    if (!indices.every((index, position) => index === position)) return map;
    coverage?.toolheads?.forEach((th) => map.set(th.toolheadIndex, th));
    return map;
  }, [coverage, mmuStatus.gates]);

  const isQidibox = mmuStatus.mmuType === MmuProtocol.Qidibox;
  const isAfc = mmuStatus.mmuType === MmuProtocol.Afc;

  // Telemetry arrays are not guaranteed to be ordered by gate index, so every
  // lookup goes through the explicit gate identity rather than array position.
  const findGate = (index: number | null): MmuGate | null =>
    index === null ? null : mmuStatus.gates.find((gate) => gate.index === index) ?? null;

  // Determine which gate is actually active (from MMU state)
  const activeGate = mmuStatus.activeGate >= 0 ? mmuStatus.activeGate : null;
  const activeGateData = findGate(activeGate);

  // QidiBox external spool holder ("Rack"). It is bound through its persisted
  // physical toolhead, never through a box slot, and box commands cannot reach it.
  const rackSlot = useMemo(
    () => resolveQidiRackSlot(mmuStatus, toolheads),
    [mmuStatus, toolheads],
  );
  const showRack = rackSelected && rackSlot !== null;

  // Use selected gate or fall back to active gate for detail display
  const displayGate = showRack ? null : selectedGate ?? activeGate;
  const displayGateData = findGate(displayGate);
  const displaySlot = showRack
    ? rackSlot
    : displayGateData
      ? loadout?.slots.find((s) => s.key === `gate-${displayGateData.index}`) ?? null
      : null;
  const displaySpoolId = displaySlot?.spoolId
    ?? (displayGateData && displayGateData.spoolId > 0 ? displayGateData.spoolId : undefined);
  // Box slot that Eject/Unload act on; never falls back to the loaded gate while
  // the Rack is selected, so a Rack click cannot unload a box slot.
  const commandGate = showRack ? null : displayGate ?? activeGate;
  const rackCommandReason = 'The Rack spool is fed manually — select a box slot to load, unload or eject';
  const displayCoverage = displayGateData ? coverageByGate.get(displayGateData.index) : undefined;
  const assignDisabled = !displaySlot
    || spoolAssignment.busy
    || !spoolAssignment.canMutate
    || displaySlot.disabled;
  const assignTitle = !displaySlot
    ? 'Select a slot first'
    : displaySlot.disabled
      ? DISABLED_SLOT_REASON
      : spoolAssignment.blockedReason
        ?? `${displaySpoolId != null ? 'Change' : 'Assign'} the spool in ${displaySlot.label}`;
  // Releasing is allowed on a device-disabled gate: it only removes a stale
  // binding, it never asks the gate to feed filament.
  const releaseDisabled = spoolAssignment.busy || !spoolAssignment.canMutate;
  const topologyMismatch = !!loadout && !loadout.hasResolvedTopology && !loadout.topologyPending;

  // The picker is pinned to the slot that was open when it launched, so a live
  // active-gate change cannot silently retarget the confirmation.
  const pinnedSlot = pickerOpen && spoolAssignment.selectedKey
    ? (rackSlot?.key === spoolAssignment.selectedKey
      ? rackSlot
      : loadout?.slots.find((s) => s.key === spoolAssignment.selectedKey) ?? null)
    : null;

  const openAssignPicker = () => {
    if (!displaySlot) return;
    // Anchor the reviewed revision to the moment the user opened the picker.
    spoolAssignment.selectSlot(displaySlot.key);
    setPickerOpen(true);
  };

  const closeAssignPicker = () => {
    setPickerOpen(false);
    spoolAssignment.selectSlot(null);
  };

  // Spool id 0 from the picker's Eject action means "release this slot".
  const handlePickerSelect = async (spoolId: number) => {
    if (!pinnedSlot) {
      toast.error('That slot is no longer reported by the device. Review the slots and try again.');
      closeAssignPicker();
      return;
    }
    const ok = spoolId > 0
      ? await spoolAssignment.assign(pinnedSlot, spoolId)
      : await spoolAssignment.clear(pinnedSlot);
    if (ok) closeAssignPicker();
  };

  const handleRelease = async () => {
    if (!displaySlot) return;
    await spoolAssignment.clear(displaySlot);
    // Drop the post-write revision anchor so later actions read the refreshed one.
    spoolAssignment.selectSlot(null);
  };

  const handleRetryTopology = () => {
    void queryClient.invalidateQueries({ queryKey: queryKeys.printerDetails(printerId) });
  };

  const canSendCommand = isOnline && mmuStatus.enabled && !pendingAction;

  const executeCommand = useCallback(async (label: string, fn: () => Promise<unknown>) => {
    setPendingAction(label);
    try {
      await fn();
    } catch (err) {
      console.error(`MMU ${label} failed:`, err);
      toast.error(`MMU ${label} failed`);
    } finally {
      setPendingAction(null);
    }
  }, []);

  const handleSelectGate = useCallback((gateIndex: number) => {
    setSelectedGate(gateIndex);
    setRackSelected(false);
  }, []);

  const handleSelectRack = useCallback(() => {
    setSelectedGate(null);
    setRackSelected(true);
  }, []);

  const handleLoad = useCallback(() => {
    if (!canSendCommand || displayGate === null) return;
    if (isQidibox) {
      void executeCommand('Load', () => apiClient.mmuGateAction(printerId, {
        protocol: 'Qidibox',
        action: 'Load',
        gateIndex: displayGate,
      }));
    } else if (isAfc) {
      const laneName = mmuStatus.gates.find((gate) => gate.index === displayGate)?.name ?? `lane${displayGate + 1}`;
      void executeCommand('Load', () => apiClient.mmuGateAction(printerId, {
        protocol: 'Afc',
        action: 'Load',
        laneName,
      }));
    } else {
      void executeCommand('Load', () => apiClient.mmuChangeTool(printerId, displayGate));
    }
  }, [canSendCommand, displayGate, printerId, executeCommand, isQidibox, isAfc, mmuStatus.gates]);

  const handleUnload = useCallback(() => {
    if (!canSendCommand || showRack) return;
    const unloadGate = commandGate;
    if (isQidibox) {
      if (unloadGate === null) return;
      void executeCommand('Unload', () => apiClient.mmuGateAction(printerId, {
        protocol: 'Qidibox',
        action: 'Unload',
        gateIndex: unloadGate,
      }));
    } else if (isAfc) {
      if (unloadGate === null) return;
      const laneName = mmuStatus.gates.find((gate) => gate.index === unloadGate)?.name ?? `lane${unloadGate + 1}`;
      void executeCommand('Unload', () => apiClient.mmuGateAction(printerId, {
        protocol: 'Afc',
        action: 'Unload',
        laneName,
      }));
    } else {
      void executeCommand('Unload', () => apiClient.mmuEject(printerId));
    }
  }, [canSendCommand, printerId, executeCommand, isQidibox, isAfc, showRack, commandGate, mmuStatus.gates]);

  const handleEject = useCallback(() => {
    if (!canSendCommand || showRack) return;
    const ejectGate = commandGate;
    if (isQidibox) {
      if (ejectGate === null) return;
      void executeCommand('Eject', () => apiClient.mmuGateAction(printerId, {
        protocol: 'Qidibox',
        action: 'Eject',
        gateIndex: ejectGate,
      }));
    } else {
      void executeCommand('Eject', () => apiClient.mmuEject(printerId));
    }
  }, [canSendCommand, printerId, executeCommand, isQidibox, showRack, commandGate]);

  const handleHome = useCallback(() => {
    if (!canSendCommand) return;
    void executeCommand('Home', () => apiClient.mmuHome(printerId));
  }, [canSendCommand, printerId, executeCommand]);

  const handleRecover = useCallback(() => {
    if (!isOnline || !mmuStatus.enabled) return;
    // Recover is for error states — available even when another action seems pending
    void executeCommand('Recover', () => apiClient.mmuRecover(printerId));
  }, [isOnline, mmuStatus.enabled, printerId, executeCommand]);

  // Action status badge
  const actionBadge: ReactNode = mmuStatus.action && mmuStatus.action !== 'Idle' ? (
    <span className="inline-flex items-center gap-1 px-2 py-0.5 rounded-xs text-[10px] font-bold bg-pf-accent-bg/15 text-pf-accent">
      <span className="w-1.5 h-1.5 rounded-full bg-pf-accent animate-pulse" />
      {mmuStatus.action}
    </span>
  ) : null;

  // Filament state indicator
  const filamentBadge: ReactNode = mmuStatus.filamentState ? (
    <span className={`text-[10px] font-bold uppercase tracking-wide ${
      mmuStatus.filamentState === 'Loaded' ? 'text-pf-success' :
      mmuStatus.filamentState === 'Unloaded' ? 'text-pf-text-tertiary' :
      'text-pf-warning'
    }`}>
      {mmuStatus.filamentState}
    </span>
  ) : null;

  return (
    <CollapsibleSection
      title="AMS"
      collapsedTitle="AMS"
      expanded={isExpanded}
      onToggle={setIsExpanded}
      headerActions={
        <div className="flex items-center gap-2">
          {coverage && (
            <FilamentCoverageBadge
              status={coverage.status}
              ariaContext={coverage.printerName || undefined}
              compact
            />
          )}
          {coverage?.status === 'runout' && (
            <RunoutRiskChip
              predictedRunoutAt={coverage.earliestPredictedRunoutAt}
              predictedRunoutLayer={null}
            />
          )}
          {actionBadge}
          {!isExpanded && filamentBadge}
        </div>
      }
    >
      <div className="space-y-3">
        {/* Unit tab bar */}
        <div className="flex items-center gap-2">
          <div className="flex items-center gap-1 px-3 py-1.5 rounded-t-lg bg-pf-bg-1 border border-b-0 border-pf-border">
            <svg width="16" height="16" viewBox="0 0 24 24" fill="currentColor" className="text-pf-text-secondary" aria-hidden="true">
              <path d="M2 6h20v12H2V6zm2 2v8h16V8H4zm2 2h4v2H6v-2zm6 0h4v2h-4v-2z" />
            </svg>
            <span className="text-sm font-bold text-pf-text-primary">1</span>
          </div>
          <div className="flex-1 border-b border-pf-border" />
          {/* MMU info badges */}
          <div className="flex items-center gap-2 text-[10px]">
            {isQidibox && (
              <span className="px-1.5 py-0.5 rounded bg-pf-accent-bg/15 text-pf-accent font-medium">
                QIDIBOX
              </span>
            )}
            {isAfc && (
              <span className="px-1.5 py-0.5 rounded bg-pf-success/10 text-pf-success font-medium">
                AFC
              </span>
            )}
            {isAfc && mmuStatus.action && mmuStatus.action !== 'Idle' && mmuStatus.action !== 'Initialized' && (
              <span className="px-1.5 py-0.5 rounded bg-pf-accent-bg/15 text-pf-accent font-medium">
                {mmuStatus.action.toUpperCase()}
              </span>
            )}
            {!isQidibox && !isAfc && mmuStatus.endlessSpool && (
              <span className="px-1.5 py-0.5 rounded bg-pf-success/10 text-pf-success font-medium">
                ENDLESS
              </span>
            )}
            {!isQidibox && !isAfc && mmuStatus.clogDetection && (
              <span className="px-1.5 py-0.5 rounded bg-pf-warning/10 text-pf-warning font-medium">
                CLOG DET
              </span>
            )}
            {!isQidibox && !isAfc && !mmuStatus.isHomed && (
              <span className="px-1.5 py-0.5 rounded bg-pf-error/10 text-pf-error font-medium">
                NOT HOMED
              </span>
            )}
          </div>
        </div>

        {/* Gates grid */}
        <div className="flex gap-1.5 overflow-x-auto pb-1">
          {/* Currently loaded box slot */}
          {activeGateData && (
            <div className="flex flex-col items-center gap-1 p-2 rounded-lg border border-pf-border bg-pf-bg-1 min-w-[70px]">
              <span className="text-[10px] uppercase tracking-wide text-pf-text-secondary font-bold">
                {isQidibox ? 'In use' : 'Rack'}
              </span>
              <SpoolIcon
                color={activeGateData.color}
                available={activeGateData.status === MmuGateStatus.Available}
                size={48}
              />
              <span className="text-xs font-medium text-pf-text-primary">
                {activeGateData.material || '?'}
              </span>
            </div>
          )}

          {/* QidiBox external spool holder, bound through its physical toolhead */}
          {rackSlot && (
            <Button
              type="button"
              variant="unstyled"
              onClick={handleSelectRack}
              aria-pressed={showRack}
              aria-label={`Rack: ${rackSlot.material || 'Empty'}${rackSlot.spoolId != null ? ` - Spool #${rackSlot.spoolId}` : ''}`}
              className={`flex flex-col items-center gap-1 p-2 rounded-lg border min-w-[70px] transition-colors cursor-pointer ${
                showRack ? 'border-pf-accent bg-pf-accent-bg/15' : 'border-pf-border bg-pf-bg-1 hover:bg-pf-bg-2'
              }`}
            >
              <span className="text-[10px] uppercase tracking-wide text-pf-text-secondary font-bold">Rack</span>
              <SpoolIcon
                color={rackSlot.color}
                available={rackSlot.spoolId != null || rackSlot.material != null}
                size={48}
              />
              <span className="text-xs font-medium text-pf-text-primary">
                {rackSlot.material || '—'}
              </span>
            </Button>
          )}

          {/* Separator */}
          {(activeGateData || rackSlot) && <div className="w-px bg-pf-border self-stretch my-2" />}

          {/* Individual gate slots */}
          {mmuStatus.gates.map((gate) => (
            <GateSlot
              key={gate.index}
              gate={gate}
              isActive={!showRack && gate.index === (selectedGate ?? activeGate)}
              coverage={coverageByGate.get(gate.index)}
              onSelect={handleSelectGate}
            />
          ))}
        </div>

        {/* Status bar: AUTO indicator + filament state */}
        <div className="flex items-center justify-between text-xs">
          <div className="flex items-center gap-3">
            {filamentBadge}
            {mmuStatus.activeTool >= 0 && (
              <span className="text-pf-text-secondary">
                Tool <span className="font-bold text-pf-text-primary">T{mmuStatus.activeTool}</span>
              </span>
            )}
          </div>
          {pendingAction && (
            <span className="text-pf-accent text-[10px] animate-pulse font-medium">
              {pendingAction}…
            </span>
          )}
        </div>

        {/* Selected Rack detail panel */}
        {showRack && rackSlot && (
          <div className="grid grid-cols-[auto_1fr] gap-x-3 gap-y-1.5 text-xs p-3 rounded-lg bg-pf-bg-1 border border-pf-border">
            <span className="text-pf-text-secondary">Slot</span>
            <span className="font-medium text-pf-text-primary">Rack (external spool)</span>

            <span className="text-pf-text-secondary">Material</span>
            <span className="font-medium text-pf-text-primary">{rackSlot.material || '—'}</span>

            <span className="text-pf-text-secondary">Color</span>
            <div className="flex items-center gap-2">
              <ColorSwatch color={rackSlot.color} />
              <span className="text-pf-text-primary">{rackSlot.color || '—'}</span>
            </div>

            <span className="text-pf-text-secondary">Spool ID</span>
            <div className="flex items-center gap-2">
              <span className="font-medium text-pf-text-primary">
                {rackSlot.spoolId != null ? `#${rackSlot.spoolId}` : 'No spool assigned'}
              </span>
              {rackSlot.spoolId != null && (
                <Button
                  type="button"
                  variant="ghost"
                  size="sm"
                  onClick={() => void handleRelease()}
                  disabled={releaseDisabled}
                  explainedDisabled={releaseDisabled}
                  title={spoolAssignment.blockedReason ?? 'Release the spool from Rack'}
                >
                  Release
                </Button>
              )}
            </div>
          </div>
        )}

        {/* Selected gate detail panel */}
        {displayGateData && (
          <div className="grid grid-cols-[auto_1fr] gap-x-3 gap-y-1.5 text-xs p-3 rounded-lg bg-pf-bg-1 border border-pf-border">
            <span className="text-pf-text-secondary">Material</span>
            <span className="font-medium text-pf-text-primary">{displayGateData.material || '—'}</span>

            <span className="text-pf-text-secondary">Filament</span>
            <span className="font-medium text-pf-text-primary">{displayGateData.filamentName || '—'}</span>

            <span className="text-pf-text-secondary">Color</span>
            <div className="flex items-center gap-2">
              <ColorSwatch color={displayGateData.color} />
              <span className="text-pf-text-primary">{displayGateData.color || '—'}</span>
            </div>

            <span className="text-pf-text-secondary">Status</span>
            <span className={`font-medium ${gateStatusColor(displayGateData.status)}`}>
              {gateStatusLabel(displayGateData.status)}
            </span>

            {displaySpoolId != null && (
              <>
                <span className="text-pf-text-secondary">Spool ID</span>
                <div className="flex items-center gap-2">
                  <span className="font-medium text-pf-text-primary">#{displaySpoolId}</span>
                  {displaySlot?.spoolId != null && (
                    <Button
                      type="button"
                      variant="ghost"
                      size="sm"
                      onClick={() => void handleRelease()}
                      disabled={releaseDisabled}
                      explainedDisabled={releaseDisabled}
                      title={spoolAssignment.blockedReason ?? `Release the spool from ${displaySlot.label}`}
                    >
                      Release
                    </Button>
                  )}
                </div>
              </>
            )}

            {displayCoverage && (
              <>
                <span className="text-pf-text-secondary">Coverage</span>
                <div className="flex flex-wrap items-center gap-2">
                  {displayCoverage.remainingGrams != null && (
                    <span className="text-pf-text-primary">{Math.round(displayCoverage.remainingGrams)}g left</span>
                  )}
                  {displayCoverage.totalDemandGrams != null && displayCoverage.totalDemandGrams > 0 && (
                    <span className="text-pf-text-tertiary">{Math.round(displayCoverage.totalDemandGrams)}g needed</span>
                  )}
                  {displayCoverage.status !== 'covers' && (
                    <FilamentCoverageBadge
                      status={displayCoverage.status}
                      reason={displayCoverage.statusReason}
                      ariaContext={displaySlot ? `${displaySlot.label} gate` : 'Selected gate'}
                    />
                  )}
                </div>
              </>
            )}
          </div>
        )}

        {/* Action buttons */}
        <div className="flex gap-2">
          {!isAfc && (
            <Button
              type="button"
              variant="secondary"
              size="sm"
              onClick={handleEject}
              disabled={!canSendCommand || showRack || (isQidibox && commandGate === null)}
              title={showRack
                ? rackCommandReason
                : isQidibox
                  ? `Eject filament from slot ${commandGate ?? '?'}`
                  : 'Eject filament out of the MMU'}
              className="flex-1"
              iconLeft={<EjectIcon className="w-4 h-4" ariaLabel="" />}
            >
              Eject
            </Button>
          )}
          <Button
            type="button"
            variant="secondary"
            size="sm"
            onClick={handleUnload}
            disabled={!canSendCommand || showRack || ((isQidibox || isAfc) && commandGate === null)}
            title={showRack
              ? rackCommandReason
              : isQidibox
                ? `Unload filament from slot ${commandGate ?? '?'}`
                : isAfc
                  ? `Unload filament from lane ${commandGate !== null ? commandGate + 1 : '?'}`
                  : 'Unload filament from MMU'}
            className="flex-1"
          >
            Unload
          </Button>
          <Button
            type="button"
            variant="secondary"
            size="sm"
            onClick={handleLoad}
            disabled={!canSendCommand || displayGate === null}
            title={showRack
              ? rackCommandReason
              : displayGate !== null
              ? (isQidibox ? `Load slot ${displayGate}` : isAfc ? `Load lane ${displayGate + 1}` : `Load gate ${displayGate} into extruder`)
              : 'Select a gate first'}
            className="flex-1"
          >
            Load
          </Button>
          <Button
            type="button"
            variant="secondary"
            size="sm"
            onClick={openAssignPicker}
            disabled={assignDisabled}
            explainedDisabled={assignDisabled}
            title={assignTitle}
            className="flex-1"
          >
            {displaySpoolId != null ? 'Change' : 'Assign'}
          </Button>
          {!isQidibox && !isAfc && (
            <Button
              type="button"
              variant="secondary"
              size="sm"
              onClick={handleHome}
              disabled={!canSendCommand}
              title="Home the MMU"
            >
              <HomeIcon className="w-4 h-4" ariaLabel="Home MMU" />
            </Button>
          )}
          {!isQidibox && !isAfc && mmuStatus.action === 'Error' && (
            <Button
              type="button"
              variant="danger"
              size="sm"
              onClick={handleRecover}
              disabled={!isOnline || !mmuStatus.enabled}
              title="Recover MMU from error"
            >
              <GearIcon className="w-4 h-4" ariaLabel="Recover" />
            </Button>
          )}
        </div>
        {loadout && !loadout.hasResolvedTopology && spoolAssignment.blockedReason && (
          <div className="flex items-center gap-2 text-xs text-pf-text-secondary" role="status">
            <span>{spoolAssignment.blockedReason}</span>
            {topologyMismatch && (
              <Button type="button" variant="ghost" size="sm" onClick={handleRetryTopology}>
                Retry
              </Button>
            )}
          </div>
        )}
      </div>
      {pickerOpen && (
        <SpoolPickerModal
          isOpen
          onClose={closeAssignPicker}
          onSelect={handlePickerSelect}
          printerId={printerId}
          activeSpoolId={pinnedSlot?.spoolId}
        />
      )}
    </CollapsibleSection>
  );
}

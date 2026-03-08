import { useState, useCallback, type ReactNode } from 'react';
import { MmuGateStatus, type MmuStatus, type MmuGate } from '@/types/api';
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
  const hubColor = 'var(--pf-surface-tertiary, #666)';

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
        className="inline-block w-5 h-5 rounded-full bg-pf-surface-tertiary border border-pf-border"
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
  onSelect: (gateIndex: number) => void;
}

function GateSlot({ gate, isActive, onSelect }: GateSlotProps) {
  const unit = Math.floor(gate.index / 4);
  const slot = gate.index % 4;
  const label = `${unit + 1}${String.fromCharCode(65 + slot)}`;
  const available = gate.status === MmuGateStatus.Available;

  return (
    <Button
      type="button"
      variant="unstyled"
      className={`
        flex flex-col items-center gap-1 p-2 rounded-lg border transition-colors cursor-pointer min-w-[70px]
        ${isActive
          ? 'border-pf-accent bg-pf-accent-bg/15'
          : 'border-pf-border bg-pf-surface-secondary hover:bg-pf-surface-tertiary'}
      `}
      onClick={() => onSelect(gate.index)}
      aria-pressed={isActive}
      aria-label={`Gate ${label}: ${gate.material ?? 'Unknown'} - ${gateStatusLabel(gate.status)}`}
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
}

/**
 * Control Box panel for MMU/ERCF/AMS multi-material units.
 * Displays gate status with spool visualizations and provides
 * load/unload/select commands.
 */
export function MmuControlBox({ printerId, mmuStatus, isOnline }: MmuControlBoxProps) {
  const [isExpanded, setIsExpanded] = useState(true);
  const [selectedGate, setSelectedGate] = useState<number | null>(null);
  const [pendingAction, setPendingAction] = useState<string | null>(null);

  const isQidibox = mmuStatus.mmuType === MmuProtocol.Qidibox;
  const isAfc = mmuStatus.mmuType === MmuProtocol.Afc;

  // Determine which gate is actually active (from MMU state)
  const activeGate = mmuStatus.activeGate >= 0 ? mmuStatus.activeGate : null;

  // Use selected gate or fall back to active gate for detail display
  const displayGate = selectedGate ?? activeGate;
  const displayGateData = displayGate !== null
    && displayGate >= 0
    && displayGate < mmuStatus.gates.length
    ? mmuStatus.gates[displayGate]
    : null;

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
  }, []);

  const handleLoad = useCallback(() => {
    if (!canSendCommand || displayGate === null) return;
    if (isQidibox) {
      void executeCommand('Load', () => apiClient.sendGcode(printerId, `T${displayGate}`));
    } else if (isAfc) {
      // AFC uses CHANGE_TOOL LANE=<name> GCode command
      const laneName = mmuStatus.gates[displayGate]?.name ?? `lane${displayGate + 1}`;
      void executeCommand('Load', () => apiClient.sendGcode(printerId, `CHANGE_TOOL LANE=${laneName}`));
    } else {
      void executeCommand('Load', () => apiClient.mmuChangeTool(printerId, displayGate));
    }
  }, [canSendCommand, displayGate, printerId, executeCommand, isQidibox, isAfc, mmuStatus.gates]);

  const handleUnload = useCallback(() => {
    if (!canSendCommand) return;
    const unloadGate = displayGate ?? activeGate;
    if (isQidibox) {
      // Qidibox needs the slot number for unload
      if (unloadGate === null) return;
      void executeCommand('Unload', () => apiClient.sendGcode(printerId, `UNLOAD_T${unloadGate}`));
    } else if (isAfc) {
      // AFC uses TOOL_UNLOAD LANE=<name> GCode command
      if (unloadGate === null) return;
      const laneName = mmuStatus.gates[unloadGate]?.name ?? `lane${unloadGate + 1}`;
      void executeCommand('Unload', () => apiClient.sendGcode(printerId, `TOOL_UNLOAD LANE=${laneName}`));
    } else {
      void executeCommand('Unload', () => apiClient.mmuEject(printerId));
    }
  }, [canSendCommand, printerId, executeCommand, isQidibox, isAfc, displayGate, activeGate, mmuStatus.gates]);

  const handleEject = useCallback(() => {
    if (!canSendCommand) return;
    const ejectGate = displayGate ?? activeGate;
    if (isQidibox) {
      if (ejectGate === null) return;
      void executeCommand('Eject', () => apiClient.sendGcode(printerId, `EJECT_T${ejectGate}`));
    } else {
      void executeCommand('Eject', () => apiClient.mmuEject(printerId));
    }
  }, [canSendCommand, printerId, executeCommand, isQidibox, displayGate, activeGate]);

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
    <span className="inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-[10px] font-bold bg-pf-accent-bg/15 text-pf-accent">
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
          {actionBadge}
          {!isExpanded && filamentBadge}
        </div>
      }
    >
      <div className="space-y-3">
        {/* Unit tab bar */}
        <div className="flex items-center gap-2">
          <div className="flex items-center gap-1 px-3 py-1.5 rounded-t-lg bg-pf-surface-secondary border border-b-0 border-pf-border">
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
          {/* Rack spool (currently loaded tool) */}
          {activeGate !== null && activeGate < mmuStatus.gates.length && (
            <div className="flex flex-col items-center gap-1 p-2 rounded-lg border border-pf-border bg-pf-surface-secondary min-w-[70px]">
              <span className="text-[10px] uppercase tracking-wide text-pf-text-secondary font-bold">Rack</span>
              <SpoolIcon
                color={mmuStatus.gates[activeGate]?.color}
                available={mmuStatus.gates[activeGate]?.status === MmuGateStatus.Available}
                size={48}
              />
              <span className="text-xs font-medium text-pf-text-primary">
                {mmuStatus.gates[activeGate]?.material || '?'}
              </span>
            </div>
          )}

          {/* Separator */}
          {activeGate !== null && <div className="w-px bg-pf-border self-stretch my-2" />}

          {/* Individual gate slots */}
          {mmuStatus.gates.map((gate) => (
            <GateSlot
              key={gate.index}
              gate={gate}
              isActive={gate.index === (selectedGate ?? activeGate)}
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

        {/* Selected gate detail panel */}
        {displayGateData && (
          <div className="grid grid-cols-[auto_1fr] gap-x-3 gap-y-1.5 text-xs p-3 rounded-lg bg-pf-surface-secondary border border-pf-border">
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

            {displayGateData.spoolId > 0 && (
              <>
                <span className="text-pf-text-secondary">Spool ID</span>
                <span className="font-medium text-pf-text-primary">#{displayGateData.spoolId}</span>
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
              disabled={!canSendCommand || (isQidibox && (displayGate ?? activeGate) === null)}
              title={isQidibox
                ? `Eject filament from slot ${displayGate ?? activeGate ?? '?'}`
                : 'Eject filament out of the MMU'}
              className="flex-1"
            >
              <EjectIcon className="w-4 h-4 mr-1" ariaLabel="" />
              Eject
            </Button>
          )}
          <Button
            type="button"
            variant="secondary"
            size="sm"
            onClick={handleUnload}
            disabled={!canSendCommand || ((isQidibox || isAfc) && (displayGate ?? activeGate) === null)}
            title={isQidibox
              ? `Unload filament from slot ${displayGate ?? activeGate ?? '?'}`
              : isAfc
                ? `Unload filament from lane ${(displayGate ?? activeGate) !== null ? (displayGate ?? activeGate)! + 1 : '?'}`
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
            title={displayGate !== null
              ? (isQidibox ? `Load slot ${displayGate}` : isAfc ? `Load lane ${displayGate + 1}` : `Load gate ${displayGate} into extruder`)
              : 'Select a gate first'}
            className="flex-1"
          >
            Load
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
      </div>
    </CollapsibleSection>
  );
}

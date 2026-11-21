import { useRef, useState } from 'react';
import { toast } from 'sonner';
import { mutationErrorMessage } from '@/common/utils/mutationError';
import { apiClient } from '@/services/api';
import { PrinterBackend, type CommandResult, type MoveRequest, type Printer, type PrinterBackendCapabilitiesDto, type PrinterDetails } from '@/types/api';
import type { FilamentMacroControl } from '@/features/printers/components/MaterialLoadout';
import { usePrinterMovement } from '@/features/printers/hooks/use-printer-movement';
import {
  canCancel, canCooldown, canDisableMotors, canEmergencyStop, canMove,
  canPauseOrResume, canSetStep, canSetTemperatures, canUseManualMove,
  filamentControlBlockedReason, getPrinterSupport,
} from '@/features/printers/utils/printerSupport';
import { isPrinterStateShutdown } from '@/features/printers/utils/statusColors';
import { knownHomedAxes } from '@/features/printers/utils/homedAxes';
import {
  DEFAULT_EXTRUDE_DISTANCE_MM, DEFAULT_EXTRUDE_SPEED_MMS,
  getPresetTargets, temperatureTargetLimitError,
} from '@/features/printers/constants/temperaturePresets';

type ControlAction = 'pause' | 'resume' | 'cancel' | 'stop' | 'firmware-restart' | 'disable-motors';

interface CancelTarget {
  printerId: string;
  jobKey: string;
}

/**
 * Command state shared by the card and sidebar; layout stays with each surface.
 * The movement transport retains its cross-surface command lock.
 */
export function usePrinterControls(
  printer: Printer | undefined,
  backendCapabilities?: PrinterBackendCapabilitiesDto,
  printerDetails?: Pick<PrinterDetails, 'capabilities'>,
  initialStep = 1,
) {
  const motion = usePrinterMovement(printer);
  const support = getPrinterSupport(backendCapabilities);
  const printerId = printer?.id;
  const state = (printer?.state ?? '').toLowerCase();
  const isOnline = printer?.isOnline ?? false;
  const isEnabled = printer?.isEnabled ?? true;
  const isPrinting = state === 'printing';
  const isPaused = state === 'paused';
  const isShutdown = isPrinterStateShutdown(printer?.state);
  const eligibility = { isOnline, isEnabled, isPrinting, isPaused, isShutdown, support };
  const activeExtruderId = printer?.backend === PrinterBackend.Moonraker ? printer.activeExtruderId ?? null : undefined;
  const canCancelNow = !!printerId && canCancel(eligibility);
  const currentJobName = printer?.fileName ?? printer?.jobName ?? null;
  const jobKey = JSON.stringify([printer?.fileName ?? null, printer?.jobName ?? null]);
  const [cancelTarget, setCancelTarget] = useState<CancelTarget | null>(null);
  const canConfirmCancel = !!cancelTarget && cancelTarget.printerId === printerId
    && cancelTarget.jobKey === jobKey && canCancelNow;
  // Clear during render so eligibility restoring cannot resurrect an old consent.
  if (cancelTarget && !canConfirmCancel) setCancelTarget(null);

  const [controlActionPending, setControlActionPending] = useState(false);
  const [temperatureActionPending, setTemperatureActionPending] = useState(false);
  const [localMovementPending, setMovementPending] = useState(false);
  const [filamentActionPending, setFilamentActionPending] = useState(false);
  const controlLock = useRef(false);
  const temperatureLock = useRef(false);
  const movementLock = useRef(false);
  const filamentLock = useRef(false);
  const movementActionPending = localMovementPending || motion.blocked;
  const [moveX, setMoveX] = useState<number | string>('');
  const [moveY, setMoveY] = useState<number | string>('');
  const [moveZ, setMoveZ] = useState<number | string>('');
  const [step, setStep] = useState(initialStep);
  const [extrudeStep, setExtrudeStep] = useState(DEFAULT_EXTRUDE_DISTANCE_MM);
  const [extrudeSpeed, setExtrudeSpeed] = useState(DEFAULT_EXTRUDE_SPEED_MMS);

  const canMoveNow = canMove(eligibility);
  const canManualMoveNow = canUseManualMove(eligibility);
  const canDisableMotorsNow = !motion.blocked && backendCapabilities?.supportsDisableMotors !== false && canDisableMotors(eligibility);
  const canExtrudeNow = isOnline && isEnabled && !isPrinting && !isShutdown && backendCapabilities?.supportsExtrusion === true;

  const runControlAction = async (action: ControlAction) => {
    if (!printerId || controlLock.current) return;
    controlLock.current = true;
    setControlActionPending(true);
    try {
      const commands = {
        pause: () => apiClient.pausePrint(printerId),
        resume: () => apiClient.resumePrint(printerId),
        cancel: () => apiClient.cancelPrint(printerId),
        stop: () => apiClient.emergencyStop(printerId),
        'firmware-restart': () => apiClient.firmwareRestart(printerId),
        'disable-motors': () => apiClient.disableMotors(printerId),
      };
      const result = await commands[action]();
      if (!result.success) toast.error(result.error || result.message || `Failed to ${action === 'cancel' ? 'cancel print' : action}`);
    } catch (error) {
      toast.error(mutationErrorMessage(error, `Failed to ${action === 'cancel' ? 'cancel print' : action}`));
    } finally {
      controlLock.current = false;
      setControlActionPending(false);
    }
  };

  const handleControlAction = async (action: ControlAction) => {
    if (action === 'cancel') {
      if (canCancelNow && printerId && !controlLock.current) setCancelTarget({ printerId, jobKey });
      return;
    }
    await runControlAction(action);
  };
  const confirmCancel = () => {
    if (!canConfirmCancel || controlLock.current) return;
    setCancelTarget(null);
    void runControlAction('cancel');
  };

  const handleFilamentAction = async (action: 'load' | 'unload') => {
    if (!printerId || filamentLock.current || !support.supportsFilamentControl
      || filamentControlBlockedReason(eligibility)) return;
    filamentLock.current = true;
    setFilamentActionPending(true);
    try {
      const result = await (action === 'load' ? apiClient.loadFilament(printerId) : apiClient.unloadFilament(printerId));
      if (!result.success) toast.error(result.error || result.message || `Failed to ${action} filament`);
    } catch (error) {
      toast.error(mutationErrorMessage(error, `Failed to ${action} filament`));
    } finally {
      filamentLock.current = false;
      setFilamentActionPending(false);
    }
  };
  const filamentControl: FilamentMacroControl | undefined = support.supportsFilamentControl ? {
    blockedReason: filamentControlBlockedReason(eligibility),
    pending: filamentActionPending,
    supportsLoad: backendCapabilities?.supportsFilamentLoad,
    supportsUnload: backendCapabilities?.supportsFilamentUnload,
    onAction: action => void handleFilamentAction(action),
  } : undefined;

  const applyPreset = async (preset: string, heater?: 'hotend' | 'bed') => {
    const cooldown = preset.toLowerCase() === 'cooldown' && !heater;
    if (!printerId || temperatureLock.current || !canSetTemperatures(eligibility)
      || (!cooldown && (isPrinting || isPaused))
      || (cooldown && !canCooldown({ ...eligibility, supportsHeaterShutdown: backendCapabilities?.supportsHeaterShutdown }))) return;
    const presetTargets = cooldown ? { hotend: 0, bed: 0 } : getPresetTargets(preset);
    if (!presetTargets) {
      toast.error(`Unknown preset: ${preset}`);
      return;
    }
    const targets = heater ? { [heater]: presetTargets[heater] } : presetTargets;
    const limitError = cooldown && backendCapabilities?.supportsHeaterShutdown
      ? undefined : temperatureTargetLimitError(targets, printer?.sensors, printerDetails?.capabilities, activeExtruderId);
    if (limitError) {
      toast.error(limitError);
      return;
    }
    temperatureLock.current = true;
    setTemperatureActionPending(true);
    try {
      const result = cooldown && backendCapabilities?.supportsHeaterShutdown
        ? await apiClient.turnOffHeaters(printerId) : await apiClient.setTemperatures(printerId, targets);
      if (!result.success) toast.error(result.error || result.message || `Failed to apply ${heater ?? preset} preset`);
    } catch (error) {
      toast.error(mutationErrorMessage(error, `Failed to apply ${heater ?? preset} preset`));
    } finally {
      temperatureLock.current = false;
      setTemperatureActionPending(false);
    }
  };

  const runMovement = async (command: () => Promise<CommandResult>, failure: string) => {
    if (!printerId || movementActionPending || movementLock.current) return;
    movementLock.current = true;
    setMovementPending(true);
    try {
      const result = await command();
      if (!result.success) toast.error(result.error || result.message || failure);
    } catch (error) {
      toast.error(mutationErrorMessage(error, failure));
    } finally {
      movementLock.current = false;
      setMovementPending(false);
    }
  };
  const handleMove = async (axis: 'X' | 'Y' | 'Z', distance: number) => {
    if (!canMoveNow || !Number.isFinite(distance) || !knownHomedAxes(printer?.homedAxes)?.includes(axis.toLowerCase())) return;
    const move: MoveRequest = { [axis.toLowerCase()]: distance };
    await runMovement(() => motion.execute({ kind: 'Jog', ...move }), `Failed to move ${axis}`);
  };
  const handleHome = async (axes?: string) => {
    const supported = axes === 'xy' ? backendCapabilities?.supportsHomingXY
      : axes === 'z' ? backendCapabilities?.supportsHomingZ
        : !axes || axes === 'all' ? backendCapabilities?.supportsHoming : false;
    if (!canMoveNow || supported !== true) return;
    await runMovement(() => motion.execute({ kind: axes === 'xy' ? 'HomeXY' : axes === 'z' ? 'HomeZ' : 'HomeAll' }), 'Failed to home printer');
  };
  const handleMoveTo = async (position: MoveRequest) => {
    const homed = knownHomedAxes(printer?.homedAxes) ?? '';
    if (!canManualMoveNow || backendCapabilities?.supportsAbsoluteMovement !== true
      || !Object.values(position).length || !Object.values(position).every(Number.isFinite)
      || !Object.keys(position).every(axis => homed.includes(axis))) return;
    await runMovement(() => motion.execute({ kind: 'MoveTo', ...position }), 'Movement status uncertain');
  };
  const handleExtrude = async (direction: 'extrude' | 'retract') => {
    if (!canExtrudeNow || !printerId) return;
    await runMovement(() => apiClient.extrudeFilament(printerId,
      direction === 'extrude' ? extrudeStep : -extrudeStep, extrudeSpeed * 60), `Failed to ${direction}`);
  };

  return {
    activeExtruderId,
    filamentControl,
    cancelConfirmation: {
      isOpen: canConfirmCancel,
      printerName: printer?.name ?? '',
      jobName: currentJobName,
      onClose: () => setCancelTarget(null),
      onConfirm: confirmCancel,
    },
    actionBarProps: {
      isPaused, isShutdown, controlActionPending,
      canPauseOrResume: canPauseOrResume(eligibility),
      canCancel: canCancelNow,
      canEmergencyStop: canEmergencyStop(eligibility) && (!isShutdown || isEnabled),
      onControlAction: handleControlAction,
    },
    movementProps: {
      moveX, moveY, moveZ, step, extrudeStep, extrudeSpeed,
      homedAxes: printer?.homedAxes,
      movementActionPending,
      movementUnavailable: backendCapabilities?.supportsRelativeMovement === false,
      movementSupportStatus: backendCapabilities?.movementSupportStatus,
      backend: backendCapabilities?.backend,
      canMove: canMoveNow,
      canHomeAll: canMoveNow && backendCapabilities?.supportsHoming === true,
      canHomeXY: canMoveNow && backendCapabilities?.supportsHomingXY === true,
      canHomeZ: canMoveNow && backendCapabilities?.supportsHomingZ === true,
      canDisableMotors: !controlActionPending && canDisableMotorsNow,
      canSetStep: canSetStep(eligibility),
      canManualMove: canManualMoveNow,
      canExtrude: canExtrudeNow,
      onMoveXChange: setMoveX,
      onMoveYChange: setMoveY,
      onMoveZChange: setMoveZ,
      onStepChange: setStep,
      onExtrudeStepChange: setExtrudeStep,
      onExtrudeSpeedChange: setExtrudeSpeed,
      onMove: handleMove,
      onHome: handleHome,
      onMoveTo: backendCapabilities?.supportsAbsoluteMovement === true ? handleMoveTo : undefined,
      onDisableMotors: () => void handleControlAction('disable-motors'),
      onExtrude: handleExtrude,
    },
    temperatureProps: {
      activeExtruderId,
      presetsDisabled: isPrinting || isPaused,
      isOnline,
      sensors: printer?.sensors,
      maxHotendTemp: printerDetails?.capabilities?.maxHotendTemp,
      maxBedTemp: printerDetails?.capabilities?.maxBedTemp,
      temperatureActionPending,
      canSetTemperatures: canSetTemperatures(eligibility),
      canCooldown: canCooldown({ ...eligibility, supportsHeaterShutdown: backendCapabilities?.supportsHeaterShutdown }),
      onApplyPreset: (preset: string) => applyPreset(preset),
      onApplySingleHeaterPreset: (heater: 'hotend' | 'bed', preset: string) => applyPreset(preset, heater),
    },
  };
}

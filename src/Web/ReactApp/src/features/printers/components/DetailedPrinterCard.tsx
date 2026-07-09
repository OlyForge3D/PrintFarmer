import React, { useState, useRef, useEffect } from 'react';
import './DetailedPrinterCard.css';
import { PanelRightOpen, Zap } from 'lucide-react';
import { useQueryClient } from '@tanstack/react-query';
import { useSpoolmanConfigured } from '@/common/hooks/useSpoolmanConfigured';
import { apiClient } from '@/services/api';
import type { Printer, TempTargets, MoveRequest, PrinterBackendCapabilitiesDto } from '@/types/api';
import { PrinterHistoryModal } from '@/features/printers/components/PrinterHistoryModal';
import { PrinterFilesModal } from '@/features/printers/components/PrinterFilesModal';
import { SpoolPickerModal } from '@/features/printers/components/SpoolPickerModal';
import { ToolheadSpoolPicker } from '@/features/printers/components/ToolheadSpoolPicker';
import { AmsSlotVisualization } from '@/features/printers/components/AmsSlotVisualization';
import { mmuGatesToToolheads } from '@/features/printers/utils/mmuGatesToToolheads';
import { TemperatureControlSection } from '@/features/printers/components/TemperatureControlSection';
import { MovementControlSection } from '@/features/printers/components/MovementControlSection';
import { FilamentControlSection } from '@/features/printers/components/FilamentControlSection';
import { ZOffsetCalibrationWizard } from '@/features/printers/components/ZOffsetCalibrationWizard';
import { PrinterActionBar } from '@/features/printers/components/PrinterActionBar';
import { BedClearBanner } from '@/features/printers/components/BedClearBanner';
import { PrintProgressBar } from '@/features/printers/components/PrintProgressBar';
import { EstimatedCompletionBadge } from '@/features/printers/components/EstimatedCompletionBadge';
import { useAutoDispatchStatus, useSetAutoDispatchEnabled } from '@/features/printers/hooks/useAutoDispatch';
import { useFailureDetectionAlert } from '@/features/printers/hooks/useFailureDetectionAlert';
import { usePrinterFailureDetectionStatus } from '@/features/printers/hooks/usePrinterFailureDetectionStatus';
import { toast } from 'sonner';
import { Button, LoadedFilamentCard } from '@/common/components/ui';
import { 
  EditIcon, 
  ExternalLinkIcon,
  CameraIcon,
  HistoryIcon,
  FileIcon,
  FilamentChangeIcon,
  EjectIcon,
} from '@/common/components/icons/MdiIcons';
import { usePrinterDetails } from '@/common/hooks/useApi';
import type { PrinterDisplay } from '@/common/hooks/usePrinterDisplay';
import { getPrinterDisplayState, requiresBedClearConfirmation } from '@/common/utils/printerStateDisplay';
import { FailureDetectionBadge } from '@/features/printers/components/FailureDetectionBadge';
import { FailureDetectionMonitoringBadge } from '@/features/printers/components/FailureDetectionMonitoringBadge';
import { FailureDetectionMonitoringSummary } from '@/features/printers/components/FailureDetectionMonitoringSummary';
import { OfflineTroubleshootingGuide } from '@/features/printers/components/OfflineTroubleshootingGuide';
import { PrinterCameraPreview } from '@/features/printers/components/PrinterCameraPreview';
import {
  canCancel,
  canCooldown,
  canDisableMotors,
  canEmergencyStop,
  canFilamentChange,
  canFilamentControl,
  canMove,
  canOpenFiles,
  canOpenHistory,
  canPauseOrResume,
  canSetStep,
  canSetTemperatures,
  canUseManualMove,
  getPrinterSupport,
} from '@/features/printers/utils/printerSupport';
import { getStatusHeaderClassName } from '@/features/printers/utils/statusColors';
import {
  getPresetTargets,
  getExtrudeMinTemp,
  DEFAULT_EXTRUDE_DISTANCE_MM,
  DEFAULT_EXTRUDE_SPEED_MMS,
} from '@/features/printers/constants/temperaturePresets';

interface DetailedPrinterCardProps {
  /** Display-ready printer — parents should pass data already merged with SignalR (usePrinterDisplays) */
  printer: Printer | PrinterDisplay;
  backendCapabilities?: PrinterBackendCapabilitiesDto;
  onEdit?: (printer: Printer) => void;
  /** Receives the printer ID so parents can pass one stable callback for all cards */
  onOpenDetails?: (printerId: string) => void;
}

// Memoized: with stable callbacks and structural sharing upstream, a card only
// re-renders when its own printer's data actually changed.
export const DetailedPrinterCard = React.memo(function DetailedPrinterCard({ printer, backendCapabilities, onEdit, onOpenDetails }: DetailedPrinterCardProps) {
  const queryClient = useQueryClient();
  const { ready: spoolmanReady } = useSpoolmanConfigured();
  const mmuStatus = (printer as PrinterDisplay).mmuStatus;

  // Fetch printer details to check for multi-toolhead configuration
  const { data: printerDetails } = usePrinterDetails(
    printer.id,
    {
      enabled: spoolmanReady,
      staleTime: 60000,
    }
  );

  const [showCamera, setShowCamera] = useState(false);
  const [showHistory, setShowHistory] = useState(false);
  const [showFiles, setShowFiles] = useState(false);
  const [showSpoolPicker, setShowSpoolPicker] = useState(false);
  const [showZOffsetWizard, setShowZOffsetWizard] = useState(false);
  const [controlActionPending, setControlActionPending] = useState(false);
  const [temperatureActionPending, setTemperatureActionPending] = useState(false);
  const [movementActionPending, setMovementActionPending] = useState(false);
  const [filamentActionPending, setFilamentActionPending] = useState(false);
  const [spoolActionPending, setSpoolActionPending] = useState(false);
  const [hotendTemp, setHotendTemp] = useState<number | string>('');
  const [bedTemp, setBedTemp] = useState<number | string>('');
  const [moveX, setMoveX] = useState<number | string>('');
  const [moveY, setMoveY] = useState<number | string>('');
  const [moveZ, setMoveZ] = useState<number | string>('');
  const [step, setStep] = useState(1);
  const [extrudeStep, setExtrudeStep] = useState(DEFAULT_EXTRUDE_DISTANCE_MM);
  const [extrudeSpeed, setExtrudeSpeed] = useState(DEFAULT_EXTRUDE_SPEED_MMS);
  const { event: recentFailure, recentEvents = [] } = useFailureDetectionAlert(printer.id);
  const { printerStatus: failureDetectionStatus } = usePrinterFailureDetectionStatus(
    printer.id,
    !!printer.obicoEnabled
  );

  const expandedProgressRef = useRef<HTMLDivElement>(null);

  // Auto-dispatch opt-in status
  const { data: autoDispatchStatus } = useAutoDispatchStatus(printer.id);
  const setAutoDispatchEnabled = useSetAutoDispatchEnabled();

  const handleAutoDispatchToggle = async () => {
    const newEnabled = !(autoDispatchStatus?.enabled ?? false);
    try {
      await setAutoDispatchEnabled.mutateAsync({ printerId: printer.id, enabled: newEnabled });
      toast.success(newEnabled ? 'Auto-dispatch enabled' : 'Auto-dispatch disabled');
    } catch {
      toast.error('Failed to toggle auto-dispatch');
    }
  };

  // Keep target temperature inputs in sync with the printer's actual targets via SignalR
  useEffect(() => {
    const hotend = printer.hotendTarget ?? 0;
    setHotendTemp(hotend > 0 ? hotend : '');
    const bed = printer.bedTarget ?? 0;
    setBedTemp(bed > 0 ? bed : '');
  }, [printer.hotendTarget, printer.bedTarget]);

  // Determine colors based on state
  const state = printer.state ?? 'Unknown';
  const isOnline = printer.isOnline ?? false;
  const isEnabled = printer.isEnabled ?? true;
  const isPrinting = isOnline && state === 'Printing';
  const isPaused = state === 'Paused';
  const isShutdown = state === 'Offline' || state === 'Shutdown' || state === 'Halted';
  const statusLabel = getPrinterDisplayState({
    printerState: state,
    autoDispatchState: autoDispatchStatus?.state,
    autoDispatchStatus,
    isOnline,
  });
  const isPendingReady = requiresBedClearConfirmation(autoDispatchStatus);
  const support = getPrinterSupport(backendCapabilities);

  const canPauseOrResumeNow = canPauseOrResume({ isOnline, isEnabled, isPrinting, isPaused, support });
  const canCancelNow = canCancel({ isOnline, isEnabled, isPrinting, isPaused, support });
  const canEmergencyStopNow = canEmergencyStop({ isOnline, isEnabled, support });
  const canDisableMotorsNow = canDisableMotors({ isOnline, isEnabled, isPrinting, support });
  const canMoveNow = canMove({ isOnline, isEnabled, isPrinting, support });
  const canSetStepNow = canSetStep({ isOnline, support });
  const canManualMoveNow = canUseManualMove({ isOnline, isEnabled, isPrinting, support });
  const canSetTemperaturesNow = canSetTemperatures({ isOnline, isEnabled, support });
  const canCooldownNow = canCooldown({ isOnline, isEnabled, isPrinting, support });
  const canOpenHistoryNow = canOpenHistory({ isOnline, isEnabled, support });

  const canOpenFilesNow = canOpenFiles({ isOnline, isEnabled, support });

  const extrudeMinTemp = getExtrudeMinTemp(printer.spoolInfo?.material);
  const canExtrudeNow = canMoveNow && (printer.hotendTemp ?? 0) >= extrudeMinTemp;

  const homedAxesRaw = printer.homedAxes;

  const headerClassName = getStatusHeaderClassName({ state, isOnline, isPrinting, isPaused, isShutdown });

  // Camera source handling
  const cameraSnapshotUrl = printer.cameraSnapshotUrl ?? null;
  const cameraStreamUrl = printer.cameraStreamUrl ?? null;
  const hasCameraUrls = !!(cameraStreamUrl || cameraSnapshotUrl);

  const handleControlAction = async (action: string) => {
    if (controlActionPending) {
      return;
    }

    setControlActionPending(true);
    try {
      let result;
      switch (action) {
        case 'pause':
          result = await apiClient.pausePrint(printer.id);
          break;
        case 'resume':
          result = await apiClient.resumePrint(printer.id);
          break;
        case 'cancel':
          result = await apiClient.cancelPrint(printer.id);
          break;
        case 'stop':
          result = await apiClient.emergencyStop(printer.id);
          break;
        case 'firmware-restart':
          result = await apiClient.firmwareRestart(printer.id);
          break;
        case 'disable-motors':
          result = await apiClient.disableMotors(printer.id);
          break;
        default:
          console.warn(`Unknown action: ${action}`);
          return;
      }

      if (!result.success) {
        console.error(`Failed to ${action}:`, result.error);
      }
    } catch (error) {
      console.error(`Error during ${action}:`, error);
    } finally {
      setControlActionPending(false);
    }
  };

  const handleFilamentAction = async (action: 'load' | 'unload' | 'change') => {
    if (filamentActionPending) {
      return;
    }

    setFilamentActionPending(true);
    try {
      const methodMap: Record<string, () => Promise<{ success: boolean; error?: string | null }>> = {
        load: () => apiClient.loadFilament(printer.id),
        unload: () => apiClient.unloadFilament(printer.id),
        change: () => apiClient.changeFilament(printer.id),
      };
      const result = await methodMap[action]();
      if (!result.success) {
        console.error(`Failed to ${action} filament:`, result.error);
      }
    } catch (error) {
      console.error(`Error during filament ${action}:`, error);
    } finally {
      setFilamentActionPending(false);
    }
  };

  const handleStepChange = (newStep: number) => {
    setStep(newStep);
  };

  const handleHotendTempKeyDown = async (e: React.KeyboardEvent) => {
    if (e.key !== 'Enter' || hotendTemp === '' || temperatureActionPending) {
      return;
    }

    setTemperatureActionPending(true);
    try {
      const currentBedTarget = bedTemp === '' ? (printer.bedTarget ?? 0) : Number(bedTemp);
      const targets: TempTargets = { hotend: Number(hotendTemp), bed: currentBedTarget };
      const result = await apiClient.setTemperatures(printer.id, targets);
      if (!result.success) {
        console.error(`Failed to set hotend temp:`, result.error);
      }
    } catch (error) {
      console.error('Error setting hotend temperature:', error);
    } finally {
      setTemperatureActionPending(false);
    }
  };

  const handleBedTempKeyDown = async (e: React.KeyboardEvent) => {
    if (e.key !== 'Enter' || bedTemp === '' || temperatureActionPending) {
      return;
    }

    setTemperatureActionPending(true);
    try {
      const currentHotendTarget = hotendTemp === '' ? (printer.hotendTarget ?? 0) : Number(hotendTemp);
      const targets: TempTargets = { hotend: currentHotendTarget, bed: Number(bedTemp) };
      const result = await apiClient.setTemperatures(printer.id, targets);
      if (!result.success) {
        console.error(`Failed to set bed temp:`, result.error);
      }
    } catch (error) {
      console.error('Error setting bed temperature:', error);
    } finally {
      setTemperatureActionPending(false);
    }
  };

  const handleApplyPreset = async (preset: string) => {
    if (temperatureActionPending) {
      return;
    }

    setTemperatureActionPending(true);
    try {
      const targets = preset.toLowerCase() === 'cooldown'
        ? { hotend: 0, bed: 0 }
        : getPresetTargets(preset);

      if (!targets) {
        console.warn(`Unknown preset: ${preset}`);
        return;
      }
      
      const result = await apiClient.setTemperatures(printer.id, targets);
      
      if (result.success) {
        setHotendTemp(targets.hotend);
        setBedTemp(targets.bed);
      } else {
        console.error(`Failed to apply ${preset} preset:`, result.error);
      }
    } catch (error) {
      console.error(`Error applying ${preset} preset:`, error);
    } finally {
      setTemperatureActionPending(false);
    }
  };

  const handleApplySingleHeaterPreset = async (heater: 'hotend' | 'bed', preset: string) => {
    if (temperatureActionPending) {
      return;
    }

    const presetTargets = getPresetTargets(preset);
    if (!presetTargets) {
      console.warn(`Unknown preset: ${preset}`);
      return;
    }

    const currentHotend = hotendTemp === '' ? (printer.hotendTarget ?? 0) : Number(hotendTemp);
    const currentBed = bedTemp === '' ? (printer.bedTarget ?? 0) : Number(bedTemp);
    const targets: TempTargets = heater === 'hotend'
      ? { hotend: presetTargets.hotend, bed: currentBed }
      : { hotend: currentHotend, bed: presetTargets.bed };

    setTemperatureActionPending(true);
    try {
      const result = await apiClient.setTemperatures(printer.id, targets);
      if (result.success) {
        setHotendTemp(targets.hotend);
        setBedTemp(targets.bed);
      } else {
        console.error(`Failed to apply ${heater} preset:`, result.error);
      }
    } catch (error) {
      console.error(`Error applying ${heater} preset:`, error);
    } finally {
      setTemperatureActionPending(false);
    }
  };

  const handleMove = async (axis: 'X' | 'Y' | 'Z', distance: number) => {
    if (movementActionPending) {
      return;
    }

    setMovementActionPending(true);
    try {
      const move: MoveRequest = {};
      move[axis.toLowerCase() as keyof MoveRequest] = distance;
      
      const result = await apiClient.movePrinter(printer.id, move);
      
      if (!result.success) {
        console.error(`Failed to move ${axis} by ${distance}:`, result.error);
      }
    } catch (error) {
      console.error(`Error moving ${axis}:`, error);
    } finally {
      setMovementActionPending(false);
    }
  };

  const handleHome = async (axes?: string) => {
    if (movementActionPending) {
      return;
    }

    setMovementActionPending(true);
    try {
      let result;
      
      if (!axes || axes === 'all') {
        result = await apiClient.homePrinter(printer.id);
      } else if (axes === 'xy') {
        result = await apiClient.homeXY(printer.id);
      } else if (axes === 'z') {
        result = await apiClient.homeZ(printer.id);
      } else {
        console.warn(`Unknown home axes: ${axes}`);
        return;
      }
      
      if (!result.success) {
        console.error(`Failed to home ${axes || 'all'}:`, result.error);
      }
    } catch (error) {
      console.error(`Error homing ${axes || 'all'}:`, error);
    } finally {
      setMovementActionPending(false);
    }
  };

  const handleExtrude = async (direction: 'extrude' | 'retract') => {
    if (movementActionPending) return;

    setMovementActionPending(true);
    try {
      const distance = direction === 'extrude' ? extrudeStep : -extrudeStep;
      const feedrate = extrudeSpeed * 60; // mm/s to mm/min
      const gcode = `M83\nG1 E${distance} F${feedrate}\nM82`;
      const result = await apiClient.sendGcode(printer.id, gcode);
      if (!result.success) {
        console.error(`Failed to ${direction}:`, result.error);
      }
    } catch (error) {
      console.error(`Error ${direction}ing:`, error);
    } finally {
      setMovementActionPending(false);
    }
  };

  const handleViewHistory = () => {
    setShowHistory(true);
  };

  return (
    <article
      className="pf-detailed-printer-card relative rounded-xl border border-white/10 bg-pf-card p-3 shadow-lg w-full transition-all duration-200 hover:-translate-y-0.5 hover:border-pf-accent/40 hover:shadow-2xl motion-reduce:transition-none motion-reduce:hover:-translate-y-0"
      style={{ transform: 'translateZ(0)' }}
    >
      {/* Colored header — background tinted by printer state */}
      <div className={`px-3 pt-3 pb-2 rounded-t-xl -mx-3 -mt-3 ${headerClassName}`}>
        <div className="flex items-center gap-2">
          <div className="font-bold text-lg text-pf-text-primary font-bebas uppercase tracking-wide truncate">
            {printer.name}
          </div>
          <div className="inline-flex items-center px-2 py-0.5 rounded-full text-xs font-medium shrink-0 bg-black/30 border border-white/20">
            <span className="text-pf-text-primary font-medium">
              {statusLabel}
            </span>
          </div>
          <FailureDetectionMonitoringBadge
            enabled={!!printer.obicoEnabled}
            status={failureDetectionStatus}
            isPrinting={isPrinting}
            printerId={printer.id}
            printerName={printer.name}
            recentEvents={recentEvents}
          />
          {recentFailure && <FailureDetectionBadge event={recentFailure} />}
        </div>
      </div>

      {/* Header actions */}
      <div className="mb-4 mt-3">

        {/* Action buttons row */}
        <div className="flex w-full items-center justify-between gap-2" role="toolbar" aria-label="Printer actions">
          <div className="flex items-center gap-1">
            <a
              href={printer.frontendUrl}
              target="_blank"
              rel="noopener noreferrer"
              className="text-pf-text-secondary hover:text-pf-text-primary shrink-0 h-8 w-8 inline-flex items-center justify-center rounded-xs"
              aria-label={`Open printer ${printer.name} in new tab`}
              title={`Open printer ${printer.name}`}
            >
              <ExternalLinkIcon className="h-4 w-4" />
            </a>

            <Button
              type="button"
              variant="ghost"
              size="sm"
              onClick={() => setShowCamera(!showCamera)}
              disabled={!hasCameraUrls || !isEnabled}
              className="h-8 w-8 p-0 text-pf-text-secondary enabled:hover:text-pf-text-primary"
              aria-label={showCamera ? 'Hide camera preview' : 'Show camera preview'}
              title={!isEnabled ? 'Printer disabled' : hasCameraUrls ? 'Camera preview available' : 'No linked camera configured'}
              iconCenter={<CameraIcon className="h-4 w-4" />}
            >
            </Button>

            {support.supportsHistory && (
              <Button
                type="button"
                variant="ghost"
                size="sm"
                onClick={handleViewHistory}
                disabled={!canOpenHistoryNow}
                className="h-8 w-8 p-0 text-pf-text-secondary enabled:hover:text-pf-text-primary"
                title="View print history"
                aria-label="View print history"
                iconCenter={<HistoryIcon className="h-4 w-4" />}
              >
              </Button>
            )}

            <Button
              type="button"
              variant="ghost"
              size="sm"
              onClick={() => setShowFiles(true)}
              disabled={!canOpenFilesNow}
              className="h-8 w-8 p-0 text-pf-text-secondary enabled:hover:text-pf-text-primary"
              title="View printer files"
              aria-label="View printer files"
              iconCenter={<FileIcon className="h-4 w-4" />}
            >
            </Button>
          </div>

          <div className="flex items-center gap-1">
            <Button
              type="button"
              variant="unstyled"
              onClick={handleAutoDispatchToggle}
              disabled={setAutoDispatchEnabled.isPending}
              className={`h-8 w-8 p-0 rounded transition-colors ${
                autoDispatchStatus?.enabled
                  ? 'text-pf-accent bg-pf-accent-bg'
                  : 'text-pf-text-secondary hover:text-pf-text-primary'
              } disabled:opacity-50 inline-flex items-center justify-center`}
              aria-label={`Toggle auto-dispatch for ${printer.name}`}
              aria-pressed={autoDispatchStatus?.enabled ?? false}
              title={autoDispatchStatus?.enabled ? 'Auto-dispatch enabled' : 'Auto-dispatch disabled'}
            >
              <Zap className="h-4 w-4" fill={autoDispatchStatus?.enabled ? 'currentColor' : 'none'} />
            </Button>
            <Button
              type="button"
              variant="ghost"
              size="sm"
              onClick={() => onEdit?.(printer)}
              className="h-8 w-8 p-0 text-pf-text-secondary enabled:hover:text-pf-text-primary"
              title="Edit details"
              aria-label="Edit details"
              iconCenter={<EditIcon className="h-4 w-4" />}
            >
            </Button>
            {onOpenDetails && (
              <Button
                type="button"
                variant="ghost"
                size="sm"
                onClick={() => onOpenDetails(printer.id)}
                className="h-8 w-8 p-0 text-pf-text-secondary enabled:hover:text-pf-text-primary"
                title="Open details sidebar"
                aria-label="Open details sidebar"
                iconCenter={<PanelRightOpen className="h-4 w-4" />}
              >
              </Button>
            )}
          </div>
        </div>

        {showCamera && (
          <PrinterCameraPreview
            printerId={printer.id}
            printerName={printer.name}
            cameraStreamUrl={cameraStreamUrl}
            cameraSnapshotUrl={cameraSnapshotUrl}
            cameraAccessMode={printer.cameraAccessMode}
            cameraStreamFormat={printer.cameraStreamFormat}
            cameraSnapshotStrategy={printer.cameraSnapshotStrategy}
            isPrinting={isPrinting}
            className="pf-detailed-printer-camera-preview mt-3 w-full"
          />
        )}
      </div>

      {/* Bed clear confirmation banner */}
      {autoDispatchStatus && isPendingReady && (
        <div className="mb-3">
          <BedClearBanner
            printerId={printer.id}
            printerName={printer.name ?? 'Printer'}
            autoDispatchStatus={autoDispatchStatus}
          />
        </div>
      )}

      {/* Offline troubleshooting guide */}
      {!isOnline && (
        <div className="mb-4">
          <OfflineTroubleshootingGuide
            printerBackend={printer.backend}
            printerIp={printer.ipAddress}
            serverUrl={printer.serverUrl ?? printer.backendUrl}
            frontendUrl={printer.frontendUrl}
            variant="full"
          />
        </div>
      )}

      {/* Progress bar — always visible to prevent layout shift */}
      <div className="mb-4">
        <PrintProgressBar
          progress={printer.progress}
          jobName={printer.fileName ?? printer.jobName}
          isActive={isOnline && (isPrinting || isPaused)}
          progressRef={expandedProgressRef}
          showInactiveState={false}
          showTemperatures={false}
        />
      </div>

      {(isPrinting || isPaused) && (printer.estimatedCompletionTimeUtc || printer.printTimeLeftSeconds != null) && (
        <EstimatedCompletionBadge completionTimeUtc={printer.estimatedCompletionTimeUtc} printTimeLeftSeconds={printer.printTimeLeftSeconds} className="mb-3" />
      )}

      {(isPrinting || isPaused) && (
        <FailureDetectionMonitoringSummary
          enabled={!!printer.obicoEnabled}
          status={failureDetectionStatus}
          recentEvents={recentEvents}
          printerName={printer.name}
          variant="detailed"
          className="mb-4"
        />
      )}

      {/* Temps Section */}
      <TemperatureControlSection
        hotendTemp={hotendTemp}
        bedTemp={bedTemp}
        hotendTarget={printer.hotendTarget}
        bedTarget={printer.bedTarget}
        hotendCurrent={printer.hotendTemp}
        bedCurrent={printer.bedTemp}
        temperatureActionPending={temperatureActionPending}
        canSetTemperatures={canSetTemperaturesNow}
        canCooldown={canCooldownNow}
        onHotendTempChange={setHotendTemp}
        onBedTempChange={setBedTemp}
        onHotendTempKeyDown={handleHotendTempKeyDown}
        onBedTempKeyDown={handleBedTempKeyDown}
        onApplyPreset={handleApplyPreset}
        onApplySingleHeaterPreset={handleApplySingleHeaterPreset}
      />

      {/* Move and Control Section */}
      <div className="mb-2">
          <MovementControlSection
            moveX={moveX}
            moveY={moveY}
            moveZ={moveZ}
            step={step}
            extrudeStep={extrudeStep}
            extrudeSpeed={extrudeSpeed}
            printerX={printer.x}
            printerY={printer.y}
            printerZ={printer.z}
            homedAxes={homedAxesRaw}
            hotendTemp={printer.hotendTemp}
            extrudeMinTemp={extrudeMinTemp}
            movementActionPending={movementActionPending}
            canMove={canMoveNow}
            canDisableMotors={canDisableMotorsNow}
            canSetStep={canSetStepNow}
            canManualMove={canManualMoveNow}
            canExtrude={canExtrudeNow}
            onMoveXChange={setMoveX}
            onMoveYChange={setMoveY}
            onMoveZChange={setMoveZ}
            onStepChange={handleStepChange}
            onExtrudeStepChange={setExtrudeStep}
            onExtrudeSpeedChange={setExtrudeSpeed}
            onMove={handleMove}
            onHome={handleHome}
            onDisableMotors={() => handleControlAction('disable-motors')}
            onExtrude={handleExtrude}
            rightContent={
              <div className="flex flex-col gap-1 items-start">
                <PrinterActionBar
                  isPaused={isPaused}
                  isShutdown={isShutdown}
                  controlActionPending={controlActionPending}
                  canPauseOrResume={canPauseOrResumeNow}
                  canCancel={canCancelNow}
                  canEmergencyStop={canEmergencyStopNow}
                  onControlAction={handleControlAction}
                />
                {support.supportsFilamentControl && (
                  <FilamentControlSection
                    filamentActionPending={filamentActionPending}
                    canFilamentControl={canFilamentControl({ isOnline, isEnabled, isPrinting, support })}
                    canFilamentChange={canFilamentChange({ isOnline, isEnabled, support })}
                    onFilamentAction={handleFilamentAction}
                  />
                )}
                {support.supportsMovement && (
                  <Button
                    variant="secondary"
                    size="sm"
                    disabled={!isOnline || isPrinting}
                    onClick={() => setShowZOffsetWizard(true)}
                  >
                    Calibrate Z-Offset
                  </Button>
                )}
              </div>
            }
          />
      </div>

      {/* AMS/MMU Slot Visualization - Compact view for card */}
      {(() => {
        const toolheads = printerDetails?.toolheads && printerDetails.toolheads.length > 1
          ? printerDetails.toolheads
          : mmuStatus?.gates && mmuStatus.gates.length > 0
            ? mmuGatesToToolheads(mmuStatus.gates)
            : undefined;
        if (!toolheads) return null;
        return (
          <div className="mb-2">
            <div className="text-xs uppercase text-pf-text-secondary font-bold tracking-wide mb-1">Material Slots</div>
            <AmsSlotVisualization toolheads={toolheads} compact printerId={printer.id} />
          </div>
        );
      })()}

      {/* Spool Info Section - Show when Spoolman is configured (all backends) */}
      {(spoolmanReady || printer.spoolInfo) && (() => {
        const hasMultipleToolheads = printerDetails?.toolheads && printerDetails.toolheads.length > 1;
        const hasMmuGates = !hasMultipleToolheads
          && mmuStatus?.gates
          && mmuStatus.gates.length > 0;
        const hasMultipleSpoolSources = hasMultipleToolheads || hasMmuGates;
        const sectionTitle = hasMultipleSpoolSources ? 'Spools' : 'Spool';

        const effectiveToolheads = hasMultipleToolheads
          ? printerDetails!.toolheads!
          : hasMmuGates
            ? mmuGatesToToolheads(mmuStatus!.gates)
            : undefined;

        return (
          <div className="mb-2">
            <div className="flex items-center justify-between mb-1">
              <div className="text-xs uppercase text-pf-text-secondary font-bold tracking-wide">{sectionTitle}</div>
              {!hasMultipleSpoolSources && (
                <div className="flex items-center gap-0.5">
                  <Button
                    type="button"
                    variant="ghost"
                    size="sm"
                    disabled={spoolActionPending}
                    onClick={() => setShowSpoolPicker(true)}
                    className="p-0.5! h-auto!"
                    title="Change spool"
                    aria-label="Change spool"
                    iconCenter={<FilamentChangeIcon className="h-3.5 w-3.5" />}
                  ></Button>
                  {printer.spoolInfo?.hasActiveSpool && (
                    <Button
                      type="button"
                      variant="ghost"
                      size="sm"
                      disabled={spoolActionPending}
                      onClick={async () => {
                        setSpoolActionPending(true);
                        try {
                          await apiClient.clearActiveSpool(printer.id);
                          queryClient.setQueryData<Printer[]>(['printers'], (old) =>
                            old?.map(p => p.id === printer.id
                              ? { ...p, spoolInfo: { hasActiveSpool: false } }
                              : p
                            )
                          );
                        } catch (err) {
                          console.error('Failed to eject spool:', err);
                        } finally {
                          setSpoolActionPending(false);
                        }
                      }}
                      className="p-0.5! h-auto!"
                      title="Eject spool"
                      aria-label="Eject spool"
                      iconCenter={<EjectIcon className="h-3.5 w-3.5" />}
                    ></Button>
                  )}
                </div>
              )}
            </div>
            {hasMultipleSpoolSources && effectiveToolheads ? (
              <ToolheadSpoolPicker
                printerId={printer.id}
                toolheads={effectiveToolheads}
                onSpoolChange={() => {
                  queryClient.invalidateQueries({ queryKey: ['printers', printer.id, 'details'] });
                }}
              />
            ) : (
              <LoadedFilamentCard spoolInfo={printer.spoolInfo} />
            )}
          </div>
        );
      })()}

      {/* History Modal */}
      <PrinterHistoryModal
        isOpen={showHistory}
        onClose={() => setShowHistory(false)}
        printer={printer}
      />

      <PrinterFilesModal
        isOpen={showFiles}
        onClose={() => setShowFiles(false)}
        printer={printer}
      />

      <SpoolPickerModal
        isOpen={showSpoolPicker}
        onClose={() => setShowSpoolPicker(false)}
        printerId={printer.id}
        activeSpoolId={printer.spoolInfo?.activeSpoolId}
        onSelect={async (spoolId, spool) => {
          setSpoolActionPending(true);
          try {
            await apiClient.setActiveSpool(printer.id, spoolId);
            setShowSpoolPicker(false);
            queryClient.setQueryData<Printer[]>(['printers'], (old) =>
              old?.map(p => p.id === printer.id
                ? {
                    ...p,
                    currentSpoolId: spool.id,
                    spoolInfo: {
                      hasActiveSpool: true,
                      activeSpoolId: spool.id,
                      spoolName: spool.name,
                      material: spool.material,
                      colorHex: spool.colorHex ?? undefined,
                      filamentName: spool.filamentName ?? undefined,
                      vendor: spool.vendor ?? undefined,
                      remainingWeightG: spool.remainingWeightG ?? undefined,
                      spoolInUse: true,
                    },
                  }
                : p
              )
            );
          } catch (err) {
            console.error('Failed to set active spool:', err);
          } finally {
            setSpoolActionPending(false);
          }
        }}
      />

      <ZOffsetCalibrationWizard
        isOpen={showZOffsetWizard}
        onClose={() => setShowZOffsetWizard(false)}
        printer={printer}
        bedSizeX={printerDetails?.capabilities?.maxBuildVolumeX ?? 220}
        bedSizeY={printerDetails?.capabilities?.maxBuildVolumeY ?? 220}
      />
    </article>
  );
});

import React, { useState, useRef, useMemo } from 'react';
import './DetailedPrinterCard.css';
import { Zap } from 'lucide-react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useSpoolmanConfigured } from '@/common/hooks/useSpoolmanConfigured';
import { apiClient } from '@/services/api';
import {
  mutationErrorMessage,
  mutationErrorStatus,
} from '@/common/utils/mutationError';
import { queryKeys, usePrintJobObjects } from '@/common/hooks/useApi';
import type {
  Printer,
  PrinterDetails,
  PrinterBackendCapabilitiesDto,
  PrintJobObjectDto,
  PrintJobObjectListDto,
} from '@/types/api';
import { PrinterHistoryModal } from '@/features/printers/components/PrinterHistoryModal';
import { PrinterFilesModal } from '@/features/printers/components/PrinterFilesModal';
import { SpoolPickerModal } from '@/features/printers/components/SpoolPickerModal';
import { MaterialLoadout } from '@/features/printers/components/MaterialLoadout';
import { MmuControlBox } from '@/features/printers/components/MmuControlBox';
import { resolveMaterialLoadout } from '@/features/printers/utils/materialLoadout';
import { MmuProtocol } from '@/features/printers/constants/mmuProtocol';
import { TemperatureControlSection } from '@/features/printers/components/TemperatureControlSection';
import { PrinterSensorsSection } from '@/features/printers/components/PrinterSensorsPanel';
import { MovementControlSection } from '@/features/printers/components/MovementControlSection';
import { RuntimeFactorControls } from '@/features/printers/components/RuntimeFactorControls';
import { PrinterActionBar } from '@/features/printers/components/PrinterActionBar';
import { CancelPrintConfirmation } from '@/features/printers/components/CancelPrintConfirmation';
import { usePrinterControls } from '@/features/printers/hooks/use-printer-controls';
import { BedClearBanner } from '@/features/printers/components/BedClearBanner';
import { PrintProgressBar } from '@/features/printers/components/PrintProgressBar';
import { PrinterImage } from '@/common/components/PrinterImage';
import { EstimatedCompletionBadge } from '@/features/printers/components/EstimatedCompletionBadge';
import { useAutoDispatchStatus, useSetAutoDispatchEnabled } from '@/features/printers/hooks/useAutoDispatch';
import { useFailureDetectionAlert } from '@/features/printers/hooks/useFailureDetectionAlert';
import { usePrinterFailureDetectionStatus } from '@/features/printers/hooks/usePrinterFailureDetectionStatus';
import { useFailureDetectionPollingEnabled } from '@/features/printers/hooks/useFailureDetectionPolling';
import { toast } from 'sonner';
import { Button, CollapsibleSection, LoadedFilamentCard } from '@/common/components/ui';
import { Modal } from '@/common/components/modals/Modal';
import { FilamentCoverageBreakdown } from '@/features/filament-coverage/components/FilamentCoverageBreakdown';
import {
  EditIcon,
  ExternalLinkIcon,
  CameraIcon,
  HistoryIcon,
  FileIcon,
  FilamentChangeIcon,
  EjectIcon,
  RefreshIcon,
} from '@/common/components/icons/MdiIcons';
import { usePrinterDetails } from '@/common/hooks/useApi';
import type { PrinterDisplay } from '@/common/hooks/usePrinterDisplay';
import { getPrinterDisplayState, requiresBedClearConfirmation } from '@/common/utils/printerStateDisplay';
import { FailureDetectionBadge } from '@/features/printers/components/FailureDetectionBadge';
import { FailureDetectionMonitoringBadge } from '@/features/printers/components/FailureDetectionMonitoringBadge';
import { FailureDetectionMonitoringSummary } from '@/features/printers/components/FailureDetectionMonitoringSummary';
import { PrinterTroubleshootingButton } from '@/features/printers/components/PrinterTroubleshootingButton';
import { PrinterServiceStatus } from '@/features/printers/components/PrinterServiceStatus';
import { PrinterCameraPreview } from '@/features/printers/components/PrinterCameraPreview';
import {
  canExcludeObject,
  isActivePrintForObjects,
  canOpenFiles,
  canOpenHistory,
  getPrinterSupport,
} from '@/features/printers/utils/printerSupport';
import { getStatusHeaderClassName, isPrinterStateShutdown } from '@/features/printers/utils/statusColors';
import { isSafeHttpUrl, isBrowserReachableUrl, toSafeHref } from '@/common/utils/validation';

export interface DetailedPrinterCardProps {
  /** Display-ready printer — parents should pass data already merged with SignalR (usePrinterDisplays) */
  printer: Printer | PrinterDisplay;
  backendCapabilities?: PrinterBackendCapabilitiesDto;
  onEdit?: (printer: Printer) => void;
  onToggleEnabled?: (printer: Printer) => void | Promise<void>;
}

// Memoized: with stable callbacks and structural sharing upstream, a card only
// re-renders when its own printer's data actually changed.
export const DetailedPrinterCard = React.memo(function DetailedPrinterCard({ printer, backendCapabilities, onEdit, onToggleEnabled }: DetailedPrinterCardProps) {
  const queryClient = useQueryClient();
  const { ready: spoolmanReady } = useSpoolmanConfigured();
  const mmuStatus = (printer as PrinterDisplay).mmuStatus;
  // Snapmaker U1's AMS-equivalent is surfaced through its own UI elsewhere, so the
  // generic AMS control box is suppressed for it — mirrors PrinterDetailsSidebar's guard.
  const isSnapmakerU1Mmu = mmuStatus?.mmuType === MmuProtocol.SnapmakerU1;
  const browserUrl = printer.frontendUrl && isBrowserReachableUrl(printer.frontendUrl)
    ? toSafeHref(printer.frontendUrl)
    : undefined;
  // Syntactically safe but known-unreachable from the browser (e.g. TestEmulator's
  // internal `testemulator-<guid>` hostname, #1546) — distinct from "no URL at all"
  // so the disabled action can explain *why* instead of just saying "unavailable".
  const isInternalOnlyBrowserUrl = !!printer.frontendUrl
    && isSafeHttpUrl(printer.frontendUrl)
    && !isBrowserReachableUrl(printer.frontendUrl);

  const [showCamera, setShowCamera] = useState(false);
  const [showHistory, setShowHistory] = useState(false);
  const [showFiles, setShowFiles] = useState(false);
  const [showSpoolPicker, setShowSpoolPicker] = useState(false);
  const [spoolActionPending, setSpoolActionPending] = useState(false);

  const [objectToSkip, setObjectToSkip] = useState<PrintJobObjectDto | null>(null);
  const [objectToSkipPrinterId, setObjectToSkipPrinterId] = useState(printer.id);
  const support = getPrinterSupport(backendCapabilities);

  // This card always needs `printerDetails` once Spoolman is ready, because
  // every path it can take needs the persisted toolhead topology:
  //   - No live MMU gates: `printerDetails.toolheads` is the only signal that
  //     distinguishes a printer with independent physical toolheads, and the
  //     collapsed badges must render without a click.
  //   - Live MMU gates present: the persisted topology is what lets
  //     `MaterialLoadout` map a live gate index (offset by the shared hotend)
  //     to the backend API index. Without it `hasResolvedTopology` is false and
  //     the module blocks every assignment, so the materials UI is a dead end.
  //
  // Those cases are exhaustive, so there is deliberately NO deferral condition
  // here. #1146 item 4 originally deferred the fetch until the wizard opened;
  // the MMU index-mapping requirement above superseded that, since deferring
  // would leave the materials module permanently unable to assign a spool.
  // Do not reintroduce a condition on MMU gate presence — a gate-based
  // predicate is what silently produced that dead end.
  // Also fetch when the card's own printer object lacks a concurrency token
  // (`!printer.rowVersion`): the compact list DTO can omit `rowVersion`, and the
  // spool mutation guards below need an authoritative token to review against.
  // Gating that fallback on `spoolmanReady` made it flaky — a user who could see
  // an eject button could not always press it. `spoolmanReady || !printer.rowVersion`
  // is a strict superset of the previous condition, so topology still resolves
  // whenever it did before.
  // Temperature controls also require details for authoritative heater limits,
  // independently of Spoolman and the revision token.
  const { data: printerDetails } = usePrinterDetails(
    printer.id,
    {
      enabled: spoolmanReady || !printer.rowVersion || support.supportsTemperatureControl,
      staleTime: 60000,
    }
  );

  const controls = usePrinterControls(printer, backendCapabilities, printerDetails);
  const { activeExtruderId, filamentControl } = controls;

  // Authoritative reviewed revision for spool mutations. Prefer the card's own
  // token, fall back to the details record. Never fabricate one — an unresolved
  // token blocks the action rather than sending an unreviewed mutation.
  const spoolReviewedRowVersion = printer.rowVersion ?? printerDetails?.rowVersion ?? null;
  const spoolRevisionUnavailable = !spoolReviewedRowVersion;
  const spoolRevisionBlockedReason = 'Printer revision unavailable — refresh to manage spools';

  // One resolution per render, shared by the materials module and the
  // single-spool fallback below. Calling the resolver separately in each
  // branch let the two guards drift out of sync (and did the work twice).
  const materialLoadout = useMemo(
    () => resolveMaterialLoadout(mmuStatus, printerDetails?.toolheads, printer.currentSpoolId, printer.modelName, printer.spoolInfo),
    [mmuStatus, printerDetails?.toolheads, printer.currentSpoolId, printer.modelName, printer.spoolInfo],
  );

  const { event: recentFailure, recentEvents = [] } = useFailureDetectionAlert(printer.id);
  // Polling for the shared failure-detection status query is controlled once
  // at the fleet level (`FailureDetectionPollingProvider` in `PrintersPage`),
  // not per-card from just this printer's own `obicoEnabled` flag (#1146
  // item 3) — every card in the grid shares the same enabled decision.
  const failureDetectionPollingEnabled = useFailureDetectionPollingEnabled();
  const { printerStatus: failureDetectionStatus } = usePrinterFailureDetectionStatus(
    printer.id,
    failureDetectionPollingEnabled
  );

  const expandedProgressRef = useRef<HTMLDivElement>(null);

  // Auto-dispatch opt-in status
  const { data: autoDispatchStatus } = useAutoDispatchStatus(printer.id);
  const setAutoDispatchEnabled = useSetAutoDispatchEnabled();

  const handleAutoDispatchToggle = async () => {
    const newEnabled = !(autoDispatchStatus?.enabled ?? false);
    try {
      if (!autoDispatchStatus?.dispatchStateETag || !autoDispatchStatus.printerETag) {
        throw new Error('Refresh the printer before changing auto-dispatch.');
      }
      await setAutoDispatchEnabled.mutateAsync({
        printerId: printer.id,
        enabled: newEnabled,
        dispatchStateETag: autoDispatchStatus.dispatchStateETag,
        printerETag: autoDispatchStatus.printerETag,
      });
      toast.success(newEnabled ? 'Auto-dispatch enabled' : 'Auto-dispatch disabled');
    } catch {
      toast.error('Failed to toggle auto-dispatch');
    }
  };

  // Determine colors based on state
  const state = printer.state ?? 'Unknown';
  const isOnline = printer.isOnline ?? false;
  const isEnabled = printer.isEnabled ?? true;
  const isPrinting = isOnline && state === 'Printing';
  const isPaused = state === 'Paused';
  const isShutdown = isPrinterStateShutdown(state);
  const statusLabel = getPrinterDisplayState({
    printerState: state,
    autoDispatchState: autoDispatchStatus?.state,
    autoDispatchStatus,
    isOnline,
  });
  const isPendingReady = requiresBedClearConfirmation(autoDispatchStatus, state);

  const canOpenHistoryNow = canOpenHistory({ isOnline, isEnabled, support });
  const canOpenFilesNow = canOpenFiles({ isOnline, isEnabled, support });
  const canExcludeObjectNow = canExcludeObject({ isOnline, isEnabled, isPrinting, isPaused, support });

  const isActivePrintForObjectQuery = isActivePrintForObjects({ state, isOnline });
  // A pending skip confirmation never outlives its print or its printer.
  if (objectToSkipPrinterId !== printer.id) {
    setObjectToSkipPrinterId(printer.id);
    setObjectToSkip(null);
  } else if (objectToSkip !== null && !(support.supportsObjectExclusion && isActivePrintForObjectQuery)) {
    setObjectToSkip(null);
  }
  const printJobObjectsQuery = usePrintJobObjects(printer.id, {
    enabled: support.supportsObjectExclusion && isActivePrintForObjectQuery,
  });
  const printJobObjects = printJobObjectsQuery.data?.objects ?? [];

  const excludeObjectMutation = useMutation({
    mutationFn: (name: string) => apiClient.excludePrintJobObject(printer.id, name),
    onSuccess: async (result, name) => {
      if (result.success) {
        toast.success(`Skipped object "${name}"`);
        queryClient.setQueryData<PrintJobObjectListDto>(queryKeys.printJobObjects(printer.id), (old) =>
          old
            ? {
                ...old,
                objects: old.objects.map((object) =>
                  object.name === name
                    ? { ...object, isExcluded: true, isCurrent: false }
                    : object
                ),
              }
            : old
        );
        setObjectToSkip(null);
      } else {
        toast.error(`Failed to skip object: ${result.message ?? result.error ?? 'Unknown error'}`);
      }

      await queryClient.invalidateQueries({ queryKey: queryKeys.printJobObjects(printer.id) });
    },
    onError: (error: Error) => {
      toast.error(`Failed to skip object: ${error.message}`);
    },
  });

  const headerClassName = getStatusHeaderClassName({ state, isOnline, isPrinting, isPaused, isShutdown });

  // Camera source handling
  const cameraSnapshotUrl = printer.cameraSnapshotUrl ?? null;
  const cameraStreamUrl = printer.cameraStreamUrl ?? null;
  const hasCameraUrls = !!(cameraStreamUrl || cameraSnapshotUrl);

  const showExternalSpool = printer.showExternalSpool ?? printerDetails?.showExternalSpool ?? false;

  const handleViewHistory = () => {
    setShowHistory(true);
  };

  return (
    <article
      data-pf-card
      className="pf-detailed-printer-card relative rounded-lg border border-white/10 bg-pf-card p-3 shadow-lg w-full transition-all duration-200 hover:-translate-y-0.5 hover:border-pf-accent/40 hover:shadow-2xl motion-reduce:transition-none motion-reduce:hover:-translate-y-0"
      style={{ transform: 'translateZ(0)' }}
    >
      {/* Colored header — background tinted by printer state */}
      <div className={`px-3 pt-3 pb-2 rounded-t-lg -mx-3 -mt-3 ${headerClassName}`}>
        <div className="flex items-center gap-2">
          <div className="font-bold text-lg text-pf-text-primary font-bebas uppercase tracking-wide truncate">
            {printer.name}
          </div>
          <div className="inline-flex items-center px-2 py-0.5 rounded-xs text-xs font-medium shrink-0 bg-black/30 border border-white/20">
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
          {!isOnline && printer.isEnabled !== false && <PrinterTroubleshootingButton key={printer.id} printer={printer} />}
          <div className="ml-auto shrink-0">
            {browserUrl ? (
              <a
                href={browserUrl}
                target="_blank"
                rel="noopener noreferrer"
                className="text-pf-text-secondary hover:text-pf-text-primary shrink-0 h-8 w-8 inline-flex items-center justify-center rounded-xs"
                aria-label={`Open printer ${printer.name} in new tab`}
                title={`Open printer ${printer.name}`}
              >
                <ExternalLinkIcon className="h-4 w-4" />
              </a>
            ) : (
              <Button
                type="button"
                variant="ghost"
                size="sm"
                disabled
                explainedDisabled
                className="h-8 w-8 p-0 text-pf-text-secondary"
                aria-label={isInternalOnlyBrowserUrl
                  ? `Open in Browser unavailable for printer ${printer.name}: not available for simulated test printers`
                  : `Printer browser URL unavailable for ${printer.name}`}
                title={isInternalOnlyBrowserUrl
                  ? 'Not available for simulated test printers'
                  : 'Printer browser URL is unavailable'}
                iconCenter={<ExternalLinkIcon className="h-4 w-4" />}
              />
            )}
          </div>
        </div>
      </div>

      {/* Identity + actions */}
      <div className="mb-4 mt-3">
        <div className="flex items-stretch gap-3">
          <div className="flex min-w-0 flex-1 items-end gap-3" data-testid="detailed-printer-image">
            <div className="relative h-24 w-24 shrink-0 overflow-hidden rounded-md border border-pf-border bg-pf-bg-2">
              <PrinterImage
                key={`${printer.manufacturerName ?? ''}|${printer.modelName ?? ''}`}
                manufacturerName={printer.manufacturerName}
                modelName={printer.modelName}
                motionType={printer.motionType}
                alt=""
                className="h-full w-full object-contain p-1.5 drop-shadow-[0_4px_6px_rgba(0,0,0,0.45)]"
              />
            </div>
            {(printer.modelName || printer.manufacturerName || printer.ipAddress) && (
              <div className="min-w-0 pb-0.5 leading-tight">
                {printer.modelName && (
                  <p className="truncate text-sm font-medium text-pf-text-primary" title={printer.modelName}>{printer.modelName}</p>
                )}
                {printer.manufacturerName && (
                  <p className="truncate text-xs text-pf-text-secondary" title={printer.manufacturerName}>{printer.manufacturerName}</p>
                )}
                {printer.ipAddress && (
                  <p className="break-all font-mono text-xs text-pf-text-secondary">{printer.ipAddress}</p>
                )}
              </div>
            )}
          </div>

        <div
          className="grid shrink-0 grid-cols-2 content-start gap-x-1"
          role="toolbar"
          aria-label="Printer actions"
        >
            <PrinterServiceStatus printer={printer} iconOnly onToggleEnabled={onToggleEnabled} />
            <Button
              type="button"
              variant="ghost"
              size="sm"
              onClick={() => setShowCamera(!showCamera)}
              disabled={!hasCameraUrls || !isEnabled}
              className="h-11 w-11 p-0 text-pf-text-secondary enabled:hover:text-pf-text-primary"
              aria-label={showCamera ? 'Hide camera preview' : 'Show camera preview'}
              title={!isEnabled ? 'Printer Out of Service' : hasCameraUrls ? 'Camera preview available' : 'No linked camera configured'}
              iconCenter={<CameraIcon className="h-4 w-4" />}
            >
            </Button>

            <Button
              type="button"
              variant="ghost"
              size="sm"
              onClick={handleAutoDispatchToggle}
              disabled={setAutoDispatchEnabled.isPending}
              className={`h-11 w-11 p-0 rounded transition-colors ${
                autoDispatchStatus?.enabled
                  ? 'text-pf-accent'
                  : 'text-pf-text-secondary enabled:hover:text-pf-text-primary'
              } disabled:opacity-50 inline-flex items-center justify-center`}
              aria-label={`Toggle auto-dispatch for ${printer.name}`}
              aria-pressed={autoDispatchStatus?.enabled ?? false}
              title={autoDispatchStatus?.enabled ? 'Auto-dispatch enabled' : 'Auto-dispatch disabled'}
              iconCenter={<Zap className="h-4 w-4" fill={autoDispatchStatus?.enabled ? 'currentColor' : 'none'} />}
            />
            <Button
              type="button"
              variant="ghost"
              size="sm"
              onClick={() => setShowFiles(true)}
              disabled={!canOpenFilesNow}
              className="h-11 w-11 p-0 text-pf-text-secondary enabled:hover:text-pf-text-primary"
              title="View printer files"
              aria-label="View printer files"
              iconCenter={<FileIcon className="h-4 w-4" />}
            >
            </Button>
            {support.supportsHistory && (
              <Button
                type="button"
                variant="ghost"
                size="sm"
                onClick={handleViewHistory}
                disabled={!canOpenHistoryNow}
                className="h-11 w-11 p-0 text-pf-text-secondary enabled:hover:text-pf-text-primary"
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
              onClick={() => onEdit?.(printer)}
              className="h-11 w-11 p-0 text-pf-text-secondary enabled:hover:text-pf-text-primary"
              title="Edit details"
              aria-label="Edit details"
              iconCenter={<EditIcon className="h-4 w-4" />}
            >
            </Button>
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
            printerState={state}
          />
        </div>
      )}

      {/* Progress bar + ETA row — always occupy the same space (and say "No active
          print" when idle, like the compact card) so cards in a grid line up. */}
      <div className="mb-1">
        <PrintProgressBar
          progress={printer.progress}
          jobName={printer.fileName ?? printer.jobName}
          isActive={isOnline && (isPrinting || isPaused)}
          progressRef={expandedProgressRef}
          showInactiveState={true}
          showTemperatures={false}
        />
      </div>

      <EstimatedCompletionBadge
        completionTimeUtc={isPrinting || isPaused ? printer.estimatedCompletionTimeUtc : undefined}
        printTimeLeftSeconds={isPrinting || isPaused ? printer.printTimeLeftSeconds : undefined}
        reserveSpace
      />

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

      {/*
        Section order below follows the sidebar's PRINTER_DETAIL_SECTION_ORDER:
        Objects, Move (which embeds Control), Temperature, AMS, Materials, Spool
        (#1698, #1699). Statistics and Version live only in PrinterDetailsSidebar;
        they were always-collapsed rows that cost every card two lines of height.
      */}

      {/* Print Objects (skip object) — folded in from the details sidebar (#1584) */}
      {support.supportsObjectExclusion && isActivePrintForObjectQuery && (
        <div className="mb-3">
          <CollapsibleSection
            title="Objects"
            expanded={true}
            headerActions={
              <Button
                type="button"
                variant="ghost"
                size="sm"
                onClick={() => void printJobObjectsQuery.refetch()}
                disabled={!isPrinting || printJobObjectsQuery.isFetching}
                className="p-1! h-auto!"
                title="Refresh print objects"
                aria-label="Refresh print objects"
                iconCenter={<RefreshIcon className="h-4 w-4" />}
              ></Button>
            }
          >
            {printJobObjectsQuery.isLoading ? (
              <div className="text-sm text-pf-text-secondary">Loading print objects…</div>
            ) : printJobObjects.length === 0 ? (
              <div className="text-sm text-pf-text-secondary">No object metadata is available for this job.</div>
            ) : (
              <ul className="space-y-2" aria-label="Current print objects">
                {printJobObjects.map((object) => (
                  <li
                    key={object.name}
                    className="flex items-center justify-between gap-3 rounded-lg border border-white/10 bg-black/15 px-3 py-2"
                  >
                    <div className="min-w-0">
                      <div className="truncate text-sm font-medium text-pf-text-primary">{object.name}</div>
                      <div className="mt-1 flex flex-wrap gap-1 text-[10px] uppercase tracking-wide">
                        {object.isCurrent && (
                          <span className="rounded-xs border border-pf-accent/50 bg-pf-accent-bg px-2 py-0.5 text-pf-accent">
                            Printing
                          </span>
                        )}
                        {object.isExcluded && (
                          <span className="rounded-xs border border-pf-border bg-pf-bg-2 px-2 py-0.5 text-pf-text-secondary">
                            Skipped
                          </span>
                        )}
                      </div>
                    </div>
                    <Button
                      type="button"
                      variant="danger"
                      size="sm"
                      disabled={!canExcludeObjectNow || object.isExcluded || excludeObjectMutation.isPending}
                      onClick={() => setObjectToSkip(object)}
                      aria-label={`Skip object ${object.name}`}
                    >
                      Skip
                    </Button>
                  </li>
                ))}
              </ul>
            )}
          </CollapsibleSection>
        </div>
      )}

      {/* Move and Control Section */}
      <div className="mb-2">
          <MovementControlSection
            {...controls.movementProps}
            printerX={printer.x}
            printerY={printer.y}
            printerZ={printer.z}
            rightContent={<PrinterActionBar {...controls.actionBarProps} />}
          />
          <CancelPrintConfirmation {...controls.cancelConfirmation} />
      </div>

      {/* Temps Section */}
      <PrinterSensorsSection
        printerId={printer.id}
        sensors={printer.sensors}
        temperatures={{ ...printer, activeExtruderId }}
        supportsTelemetry={backendCapabilities?.supportsSensorTelemetry}
        canControl={(backendCapabilities?.supportsSensorControl ?? false) && isEnabled}
        isOnline={isOnline}
      >
      {additionalRows => <TemperatureControlSection
        {...controls.temperatureProps}
        printerId={printer.id}
        showHeading={false}
        additionalRows={additionalRows}
        hotendTarget={printer.hotendTarget}
        bedTarget={printer.bedTarget}
        hotendCurrent={printer.hotendTemp}
        bedCurrent={printer.bedTemp}
      />}

      </PrinterSensorsSection>

      <RuntimeFactorControls printer={printer} backendCapabilities={backendCapabilities} />

      {/* AMS control box — mirrors PrinterDetailsSidebar's placement immediately
          before the materials module, so gate select/load/unload/eject controls
          available in the sidebar are also available from the detail card. */}
      {mmuStatus && !isSnapmakerU1Mmu && (
        <div className="mb-2">
          <CollapsibleSection title="Materials" keepMounted>
            <MmuControlBox
              printerId={printer.id}
              mmuStatus={mmuStatus}
              isOnline={isOnline}
              toolheads={printerDetails?.toolheads}
              modelName={printer.modelName}
              reviewedRowVersion={spoolReviewedRowVersion ?? undefined}
              showExternalSpool={showExternalSpool}
              filamentControl={filamentControl}
            />
          </CollapsibleSection>
        </div>
      )}

      {/* Consolidated materials module — replaces the old Material Slots strip
          and the parallel Spools assignment list, which could disagree. Hidden
          when the AMS control box already represents the slots. */}
      {materialLoadout && !(mmuStatus && !isSnapmakerU1Mmu) && (
        <div className="mb-2">
          <CollapsibleSection title="Materials" keepMounted>
            <MaterialLoadout
              printerId={printer.id}
              mmuStatus={mmuStatus}
              toolheads={printerDetails?.toolheads}
              currentSpoolId={printer.currentSpoolId}
              spoolInfo={printer.spoolInfo}
              modelName={printer.modelName}
              reviewedRowVersion={spoolReviewedRowVersion ?? undefined}
              compact
              isOnline={isOnline}
              filamentControl={filamentControl}
            />
          </CollapsibleSection>
        </div>
      )}

      {/* Single-spool printers keep the classic spool card; multi-slot printers are
          fully described by the materials module above. */}
      {(spoolmanReady || printer.spoolInfo) && !materialLoadout && !(mmuStatus && !isSnapmakerU1Mmu) && (
        <div className="mb-2">
          <CollapsibleSection title="Materials" keepMounted headerActions={
            <div className="flex items-center gap-0.5">
              <Button
                type="button"
                variant="ghost"
                size="sm"
                disabled={spoolActionPending || spoolRevisionUnavailable}
                explainedDisabled={spoolRevisionUnavailable}
                onClick={() => setShowSpoolPicker(true)}
                className="p-0.5! h-auto!"
                title={spoolRevisionUnavailable ? spoolRevisionBlockedReason : 'Change spool'}
                aria-label={spoolRevisionUnavailable ? `Change spool — ${spoolRevisionBlockedReason}` : 'Change spool'}
                iconCenter={<FilamentChangeIcon className="h-3.5 w-3.5" />}
              ></Button>
              {printer.spoolInfo?.hasActiveSpool && (
                <Button
                  type="button"
                  variant="ghost"
                  size="sm"
                  disabled={spoolActionPending || spoolRevisionUnavailable}
                  explainedDisabled={spoolRevisionUnavailable}
                  onClick={async () => {
                    setSpoolActionPending(true);
                    try {
                      const reviewedRowVersion = spoolReviewedRowVersion;
                      if (!reviewedRowVersion) {
                        toast.error('Printer revision unavailable. Refresh and review again.');
                        return;
                      }
                      const nextRowVersion = await apiClient.clearActiveSpool(
                        printer.id,
                        reviewedRowVersion
                      );
                      queryClient.setQueryData<Printer[]>(['printers'], (old) =>
                        old?.map(p => p.id === printer.id
                          ? {
                              ...p,
                              rowVersion: nextRowVersion,
                              spoolInfo: { hasActiveSpool: false },
                            }
                          : p
                        )
                      );
                      queryClient.setQueryData<PrinterDetails>(
                        queryKeys.printerDetails(printer.id),
                        (old) => old ? { ...old, rowVersion: nextRowVersion } : old,
                      );
                      // Reconcile the optimistic update with server truth so
                      // downstream consumers (printer details, coverage) see
                      // the cleared spool. Awaiting the invalidation prevents
                      // a follow-up assignment from racing a stale refetch.
                      await queryClient.invalidateQueries({ queryKey: ['printers'] });
                    } catch (err) {
                      console.error('Failed to eject spool:', err);
                      if ([412, 428].includes(mutationErrorStatus(err) ?? 0)) {
                        await queryClient.invalidateQueries({
                          queryKey: ['printers'],
                        });
                      }
                      toast.error(
                        mutationErrorMessage(err, 'Failed to eject spool')
                      );
                    } finally {
                      setSpoolActionPending(false);
                    }
                  }}
                  className="p-0.5! h-auto!"
                  title={spoolRevisionUnavailable ? spoolRevisionBlockedReason : 'Eject spool'}
                  aria-label={spoolRevisionUnavailable ? `Eject spool — ${spoolRevisionBlockedReason}` : 'Eject spool'}
                  iconCenter={<EjectIcon className="h-3.5 w-3.5" />}
                ></Button>
              )}
            </div>
          }>
            <FilamentCoverageBreakdown printerId={printer.id} className="mb-2" isOnline={isOnline} />
            <LoadedFilamentCard spoolInfo={printer.spoolInfo} />
          </CollapsibleSection>
        </div>
      )}

      <Modal
        isOpen={objectToSkip !== null && support.supportsObjectExclusion && isActivePrintForObjectQuery}
        onClose={() => {
          if (!excludeObjectMutation.isPending) {
            setObjectToSkip(null);
          }
        }}
        title="Skip print object?"
        size="sm"
        isDisabled={excludeObjectMutation.isPending}
        footer={(
          <div className="flex justify-end gap-2">
            <Button
              type="button"
              variant="secondary"
              onClick={() => setObjectToSkip(null)}
              disabled={excludeObjectMutation.isPending}
            >
              Keep printing
            </Button>
            <Button
              type="button"
              variant="danger"
              loading={excludeObjectMutation.isPending}
              onClick={() => {
                if (objectToSkip && canExcludeObjectNow) {
                  excludeObjectMutation.mutate(objectToSkip.name);
                }
              }}
            >
              Skip object
            </Button>
          </div>
        )}
      >
        <p className="text-sm text-pf-text-primary">
          Skip <span className="font-semibold">{objectToSkip?.name}</span> for the active print on {printer.name}?
        </p>
        <p className="mt-2 text-xs text-pf-text-secondary">
          The printer will continue printing the remaining objects. This action cannot be undone from PrintFarmer.
        </p>
      </Modal>

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
            const reviewedRowVersion = spoolReviewedRowVersion;
            if (!reviewedRowVersion) {
              toast.error('Printer revision unavailable. Refresh and review again.');
              return;
            }
            const nextRowVersion = await apiClient.setActiveSpool(
              printer.id,
              spoolId,
              reviewedRowVersion
            );
            setShowSpoolPicker(false);
            queryClient.setQueryData<Printer[]>(['printers'], (old) =>
              old?.map(p => p.id === printer.id
                ? {
                    ...p,
                    rowVersion: nextRowVersion,
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
            queryClient.setQueryData<PrinterDetails>(
              queryKeys.printerDetails(printer.id),
              (old) => old ? { ...old, rowVersion: nextRowVersion } : old,
            );
          } catch (err) {
            console.error('Failed to set active spool:', err);
            if ([412, 428].includes(mutationErrorStatus(err) ?? 0)) {
              await queryClient.invalidateQueries({ queryKey: ['printers'] });
            }
            toast.error(
              mutationErrorMessage(err, 'Failed to set active spool')
            );
          } finally {
            setSpoolActionPending(false);
          }
        }}
      />
    </article>
  );
});

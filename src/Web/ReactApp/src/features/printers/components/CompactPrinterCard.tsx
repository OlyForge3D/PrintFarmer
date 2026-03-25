import React, { useEffect, useRef, useState } from 'react';
import { PanelRightOpen, Zap } from 'lucide-react';
import {
  HistoryIcon,
  FileIcon,
  EditIcon,
  CameraIcon,
  ExternalLinkIcon,
  MoreVerticalIcon,
  TagIcon,
} from '@/common/components/icons/MdiIcons';
import { Button } from '@/common/components/ui';
import { PrinterHistoryModal } from '@/features/printers/components/PrinterHistoryModal';
import { PrinterFilesModal } from '@/features/printers/components/PrinterFilesModal';
import { PrintProgressBar } from '@/features/printers/components/PrintProgressBar';
import { FailureDetectionBadge } from '@/features/printers/components/FailureDetectionBadge';
import { FailureDetectionMonitoringBadge } from '@/features/printers/components/FailureDetectionMonitoringBadge';
import { PrinterCameraPreview } from '@/features/printers/components/PrinterCameraPreview';
import { PrinterBackend, type Printer, type PrinterBackendCapabilitiesDto } from '@/types/api';
import { apiClient } from '@/services/api';
import { useAutoDispatchStatus, useSetAutoDispatchEnabled } from '@/features/printers/hooks/useAutoDispatch';
import { useFailureDetectionAlert } from '@/features/printers/hooks/useFailureDetectionAlert';
import { usePrinterFailureDetectionStatus } from '@/features/printers/hooks/usePrinterFailureDetectionStatus';
import { BedClearBanner } from '@/features/printers/components/BedClearBanner';
import { useJobQueue } from '@/common/hooks/useApi';
import { toast } from 'sonner';
import {
  canOpenFiles,
  canOpenHistory,
  getPrinterSupport,
} from '@/features/printers/utils/printerSupport';
import { getStatusHeaderStyle } from '@/features/printers/utils/statusColors';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { TaggingModal } from '@/components/TaggingModal';
import { getPrinterDisplayState, requiresBedClearConfirmation } from '@/common/utils/printerStateDisplay';
import type { TagDto } from '@/services/tagService';

interface CompactPrinterCardProps {
  printer: Printer;
  backendCapabilities?: PrinterBackendCapabilitiesDto;
  onExpand: () => void;
  onEdit?: (printer: Printer) => void;
}

export function CompactPrinterCard({
  printer: printerProp,
  backendCapabilities,
  onExpand,
  onEdit
}: CompactPrinterCardProps) {
  // Merge with realtime SignalR updates
  const printer = printerProp; // printerProp already includes display data
  const [showCamera, setShowCamera] = useState(false);
  const [showHistory, setShowHistory] = useState(false);
  const [showFiles, setShowFiles] = useState(false);
  const [showMenu, setShowMenu] = useState(false);
  const [showTagModal, setShowTagModal] = useState(false);
  const collapsedProgressRef = useRef<HTMLDivElement>(null);
  const menuRef = useRef<HTMLDivElement>(null);

  // Close ellipsis menu when clicking outside
  useEffect(() => {
    if (!showMenu) return;
    const handleClickOutside = (e: MouseEvent) => {
      if (menuRef.current && !menuRef.current.contains(e.target as Node)) {
        setShowMenu(false);
      }
    };
    document.addEventListener('mousedown', handleClickOutside);
    return () => document.removeEventListener('mousedown', handleClickOutside);
  }, [showMenu]);

  // Fetch printer tags
  const queryClient = useQueryClient();
  const { event: recentFailure } = useFailureDetectionAlert(printer.id);
  const { printerStatus: failureDetectionStatus } = usePrinterFailureDetectionStatus(
    printer.id,
    !!printer.obicoEnabled
  );
  const { data: printerTags = [] } = useQuery<TagDto[]>({
    queryKey: ['printer-tags', printer.id],
    queryFn: async () => {
      const tags = await apiClient.getObjectTags(printer.id, 'Printer');
      return tags as unknown as TagDto[];
    },
    staleTime: 5 * 60 * 1000,
  });

  // Auto-dispatch opt-in status
  const { data: autoDispatchStatus } = useAutoDispatchStatus(printer.id);
  const setAutoDispatchEnabled = useSetAutoDispatchEnabled();

  // Per-printer job queue for "X of Y" indicator
  const { data: printerQueue = [] } = useJobQueue(printer.id);
  const activeQueueJobs = printerQueue.filter(
    (j) => {
      // Analytics endpoint returns flat objects with status at top level
      const status = (j as unknown as { status?: string }).status ?? j.job?.status;
      return status === 'Queued' || status === 'Printing' || status === 'Dispatched';
    }
  );

  const handleAutoDispatchToggle = async () => {
    const newEnabled = !(autoDispatchStatus?.enabled ?? false);
    try {
      await setAutoDispatchEnabled.mutateAsync({ printerId: printer.id, enabled: newEnabled });
      toast.success(newEnabled ? 'Auto-dispatch enabled' : 'Auto-dispatch disabled');
    } catch {
      toast.error('Failed to toggle auto-dispatch');
    }
  };

  // Use printer data directly (already contains merged realtime status from API)
  const isOnline = printer.isOnline ?? false;
  const isEnabled = printer.isEnabled ?? true;
  const state = printer.state ?? 'Unknown';
  const isPrinting = state.toLowerCase().includes('printing');
  const isPaused = state.toLowerCase().includes('paused');
  const isShutdown = state.toLowerCase().includes('shutdown') || state.toLowerCase().includes('error');
  const isPendingReady = requiresBedClearConfirmation(autoDispatchStatus);
  const statusLabel = getPrinterDisplayState({
    printerState: state,
    autoDispatchState: autoDispatchStatus?.state,
    autoDispatchStatus,
    isOnline,
  });

  // Queue label: "1 of 3" when printing with more jobs queued
  const printingIndex = activeQueueJobs.findIndex(j => {
    const status = (j as unknown as { status?: string }).status ?? j.job?.status;
    return status === 'Printing';
  });
  const queueLabel = activeQueueJobs.length > 1
    ? `${(printingIndex >= 0 ? printingIndex + 1 : 1)} of ${activeQueueJobs.length}`
    : undefined;

  const support = getPrinterSupport(backendCapabilities, {
    supportsHistory: printer.backend === PrinterBackend.Moonraker || printer.backend === PrinterBackend.OctoPrint,
  });

  const canOpenFilesNow = canOpenFiles({ isOnline, isEnabled, support });
  const canOpenHistoryNow = canOpenHistory({ isOnline, isEnabled, support });
  // Check if printer has camera URLs - just verify if URLs have values from database
  const cameraSnapshotUrl = printer.cameraSnapshotUrl;
  const cameraStreamUrl = printer.cameraStreamUrl;
  const hasCameraUrls = !!(cameraSnapshotUrl || cameraStreamUrl);

  const headerStyle = getStatusHeaderStyle({ state, isOnline, isPrinting, isPaused, isShutdown });

  return (
    <div className="relative rounded-xl shadow-lg bg-pf-card border border-white/10 w-full">
      {/* Bed clear banner — overlay on top of card */}
      {autoDispatchStatus && isPendingReady && (
        <div className="absolute inset-0 z-10 flex items-center justify-center rounded-xl bg-black/75">
          <div className="w-[90%]">
            <BedClearBanner
              printerId={printer.id}
              printerName={printer.name}
              autoDispatchStatus={autoDispatchStatus}
            />
          </div>
        </div>
      )}
      {/* Colored header — background tinted by printer state */}
      <div style={headerStyle} className="px-3 pt-3 pb-2 rounded-t-xl">
          {/* Top row: Name + Status Pill + failure-detection status */}
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
            printerName={printer.name}
          />
          {recentFailure && <FailureDetectionBadge event={recentFailure} />}
        </div>
        {/* Tags row */}
        {printerTags.length > 0 && (
          <div className="flex flex-wrap gap-1 mt-1.5">
            {printerTags.map(tag => (
              <span
                key={tag.id}
                className="text-xs px-1.5 py-0.5 rounded-full bg-black/30 border border-white/10 text-pf-text-secondary"
              >
                {tag.name}
              </span>
            ))}
          </div>
        )}
      </div>

      {/* Card body — content left, action buttons right */}
      <div className="flex px-3 pb-3">
        {/* Left: content area */}
        <div className="flex-1 min-w-0">

          {/* Progress bar — always visible */}
          <div className="mt-4 mb-3">
            <PrintProgressBar
              progress={printer.progress}
              jobName={printer.fileName ?? printer.jobName}
              isActive={isOnline && (isPrinting || isPaused)}
              progressRef={collapsedProgressRef}
              showInactiveState={true}
              showTemperatures={true}
              hotendTemp={printer.hotendTemp}
              bedTemp={printer.bedTemp}
              hotendTarget={printer.hotendTarget}
              bedTarget={printer.bedTarget}
              isOnline={isOnline}
              queueLabel={queueLabel}
            />
          </div>

          {/* Camera view — centered, between progress bar and footer */}
          {showCamera && (
            <PrinterCameraPreview
              printerId={printer.id}
              printerName={printer.name}
              cameraStreamUrl={cameraStreamUrl}
              cameraSnapshotUrl={cameraSnapshotUrl}
              isPrinting={isPrinting}
              className="mt-2"
            />
          )}

        </div>

        {/* Right: action buttons column */}
        <div
          className="flex flex-col items-center gap-0.5 ml-2 mt-2"
          role="toolbar"
          aria-label="Printer actions"
        >
          <Button
            type="button"
            variant="ghost"
            size="sm"
            onClick={onExpand}
            className="h-8 w-8 p-0 text-pf-text-secondary enabled:hover:text-pf-text-primary"
            title="Open details sidebar"
            aria-label="Open details sidebar"
            iconCenter={<PanelRightOpen className="h-4 w-4" />}
          />
          <Button
            type="button"
            variant="ghost"
            size="sm"
            onClick={() => setShowCamera(!showCamera)}
            disabled={!hasCameraUrls || !isEnabled}
            className="h-8 w-8 p-0 text-pf-text-secondary enabled:hover:text-pf-text-primary"
            aria-label={showCamera ? 'Hide camera preview' : 'Show camera preview'}
            title={!isEnabled ? 'Printer disabled' : hasCameraUrls ? 'Camera preview available' : 'No camera configured'}
            iconCenter={<CameraIcon className="h-4 w-4" />}
          />
          <Button
            type="button"
            variant="unstyled"
            onClick={handleAutoDispatchToggle}
            disabled={setAutoDispatchEnabled.isPending}
            className={`h-8 w-8 p-0 rounded transition-colors inline-flex items-center justify-center ${
              autoDispatchStatus?.enabled
                ? 'text-pf-accent'
                : 'text-pf-text-secondary hover:text-pf-text-primary'
            } disabled:opacity-50`}
            aria-label={`Toggle auto-dispatch for ${printer.name}`}
            aria-pressed={autoDispatchStatus?.enabled ?? false}
            title={autoDispatchStatus?.enabled ? 'Auto-dispatch enabled' : 'Auto-dispatch disabled'}
            iconCenter={<Zap className="w-4 h-4" fill={autoDispatchStatus?.enabled ? 'currentColor' : 'none'} />}
          />
        </div>
      </div>

      {/* Bottom row: filament info + ellipsis menu */}
      <div className="flex items-center px-3 pb-3 border-t border-white/5 pt-2" ref={menuRef}>
        <div className="flex items-center gap-2 flex-1 min-w-0 text-xs text-pf-text-secondary">
          {printer.spoolInfo?.hasActiveSpool ? (
            <>
              {(() => {
                const color = printer.spoolInfo.colorHex ?? '#888';
                const remaining = printer.spoolInfo.remainingWeightG;
                const initial = printer.spoolInfo.initialWeightG;
                const percent = (remaining != null && initial != null && initial > 0)
                  ? Math.max(0, Math.min(100, (remaining / initial) * 100))
                  : null;
                const r = 8;
                const circumference = 2 * Math.PI * r;
                const offset = percent != null ? circumference * (1 - percent / 100) : circumference;
                const ringTooltip = [
                  printer.spoolInfo.filamentName ?? printer.spoolInfo.material ?? 'Unknown',
                  printer.spoolInfo.vendor,
                  percent != null ? `${Math.round(percent)}% remaining` : null,
                ].filter(Boolean).join(' · ');
                return (
                  <svg
                    width="20"
                    height="20"
                    viewBox="0 0 20 20"
                    className="shrink-0"
                    aria-label={percent != null ? `Filament ${Math.round(percent)}% remaining` : `Filament color: ${color}`}
                  >
                    <title>{ringTooltip}</title>
                    <circle cx="10" cy="10" r={r} fill="none" stroke="rgba(255,255,255,0.15)" strokeWidth="2.5" />
                    <circle
                      cx="10"
                      cy="10"
                      r={r}
                      fill="none"
                      stroke="rgba(255,255,255,0.6)"
                      strokeWidth="2.5"
                      strokeDasharray={circumference}
                      strokeDashoffset={offset}
                      strokeLinecap="round"
                      transform="rotate(-90 10 10)"
                    />
                  </svg>
                );
              })()}
              <span className="truncate">
                {printer.spoolInfo.material ?? 'Unknown'}
              </span>
              {printer.spoolInfo.remainingWeightG != null && (
                <span
                  className="shrink-0 text-pf-text-tertiary cursor-default"
                  title={`${printer.spoolInfo.remainingWeightG >= 1000 ? `${(printer.spoolInfo.remainingWeightG / 1000).toFixed(2)}kg` : `${Math.round(printer.spoolInfo.remainingWeightG)}g`} remaining${printer.spoolInfo.initialWeightG ? ` of ${printer.spoolInfo.initialWeightG >= 1000 ? `${(printer.spoolInfo.initialWeightG / 1000).toFixed(1)}kg` : `${Math.round(printer.spoolInfo.initialWeightG)}g`}` : ''}`}
                >
                  {printer.spoolInfo.remainingWeightG >= 1000
                    ? `${(printer.spoolInfo.remainingWeightG / 1000).toFixed(1)}kg`
                    : `${Math.round(printer.spoolInfo.remainingWeightG)}g`}
                </span>
              )}
              <span
                className="shrink-0 w-8 h-4 rounded-full border border-white/20"
                style={{ backgroundColor: printer.spoolInfo.colorHex ?? '#888' }}
                title={printer.spoolInfo.filamentName ?? printer.spoolInfo.material ?? 'Filament color'}
              />
            </>
          ) : (
            <span className="italic text-pf-text-tertiary">No spool loaded</span>
          )}
        </div>
        <div className="relative shrink-0 ml-2">
          <Button
            type="button"
            variant="ghost"
            size="sm"
            onClick={() => setShowMenu(v => !v)}
            className="h-8 w-8 p-0 text-pf-text-secondary enabled:hover:text-pf-text-primary"
            title="More options"
            aria-label="More options"
            aria-haspopup="true"
            aria-expanded={showMenu}
            iconCenter={<MoreVerticalIcon className="h-4 w-4" />}
          />
          {showMenu && (
            <div className="absolute right-0 bottom-full mb-1 z-50 min-w-45 bg-pf-bg-1 border border-white/10 rounded-lg shadow-xl py-1 text-sm">
                <Button
                  type="button"
                  variant="ghost"
                  size="sm"
                  className="w-full justify-start rounded-none h-auto px-3 py-2 text-pf-text-secondary enabled:hover:text-pf-text-primary enabled:hover:bg-white/5"
                  onClick={() => { setShowFiles(true); setShowMenu(false); }}
                  disabled={!canOpenFilesNow}
                  iconLeft={<FileIcon className="h-4 w-4 shrink-0" />}
                >
                  View Files
                </Button>
                {support.supportsHistory && (
                  <Button
                    type="button"
                    variant="ghost"
                    size="sm"
                    className="w-full justify-start rounded-none h-auto px-3 py-2 text-pf-text-secondary enabled:hover:text-pf-text-primary enabled:hover:bg-white/5"
                    onClick={() => { setShowHistory(true); setShowMenu(false); }}
                    disabled={!canOpenHistoryNow}
                    iconLeft={<HistoryIcon className="h-4 w-4 shrink-0" />}
                  >
                    History
                  </Button>
                )}
                <a
                  href={printer.frontendUrl}
                  target="_blank"
                  rel="noopener noreferrer"
                  className="flex w-full items-center gap-2 px-3 py-2 text-pf-text-secondary hover:text-pf-text-primary hover:bg-white/5"
                  aria-label={`Open printer ${printer.name} in new tab`}
                  onClick={() => setShowMenu(false)}
                >
                  <ExternalLinkIcon className="h-4 w-4 shrink-0" />
                  Open in Browser
                </a>
                <Button
                  type="button"
                  variant="ghost"
                  size="sm"
                  className="w-full justify-start rounded-none h-auto px-3 py-2 text-pf-text-secondary enabled:hover:text-pf-text-primary enabled:hover:bg-white/5"
                  onClick={() => { onEdit?.(printer); setShowMenu(false); }}
                  iconLeft={<EditIcon className="h-4 w-4 shrink-0" />}
                >
                  Edit Printer
                </Button>
                <div className="h-px bg-white/10 my-1" />
                <Button
                  type="button"
                  variant="ghost"
                  size="sm"
                  className="w-full justify-start rounded-none h-auto px-3 py-2 text-pf-text-secondary enabled:hover:text-pf-text-primary enabled:hover:bg-white/5"
                  onClick={() => { setShowTagModal(true); setShowMenu(false); }}
                  iconLeft={<TagIcon className="h-4 w-4 shrink-0" />}
                >
                  Manage Tags
                </Button>
              </div>
            )}
          </div>
        </div>

      {/* History Modal */}
      <PrinterHistoryModal
        isOpen={showHistory}
        onClose={() => setShowHistory(false)}
        printer={printer}
      />

      {/* Files Modal */}
      <PrinterFilesModal
        isOpen={showFiles}
        onClose={() => setShowFiles(false)}
        printer={printer}
      />

      {/* Tags Modal */}
      <TaggingModal
        objectId={printer.id}
        objectType="Printer"
        initialTags={printerTags}
        isOpen={showTagModal}
        onClose={() => {
          setShowTagModal(false);
          void queryClient.invalidateQueries({ queryKey: ['printer-tags', printer.id] });
        }}
      />
      {/* end card body */}
    </div>
  );
}

import React, { useEffect, useRef, useState } from 'react';
import { PanelRightOpen } from 'lucide-react';
import {
  HistoryIcon,
  FileIcon,
  EditIcon,
  CameraIcon,
  ExternalLinkIcon,
  ImageIcon,
  VideoIcon,
  MoreVerticalIcon,
  TagIcon,
} from '@/common/components/icons/MdiIcons';
import { Button, Toggle } from '@/common/components/ui';
import { PrinterHistoryModal } from '@/features/printers/components/PrinterHistoryModal';
import { PrinterFilesModal } from '@/features/printers/components/PrinterFilesModal';
import { PrinterBackend, type Printer, type PrinterBackendCapabilitiesDto } from '@/types/api';
import { apiClient } from '@/services/api';
import { useAutoPrintStatus, useSetAutoPrintEnabled } from '@/features/printers/hooks/useAutoPrint';
import { BedClearBanner } from '@/features/printers/components/BedClearBanner';
import { toast } from 'sonner';
import {
  canOpenFiles,
  canOpenHistory,
  getPrinterSupport,
} from '@/features/printers/utils/printerSupport';
import { getStatusHeaderStyle } from '@/features/printers/utils/statusColors';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { TaggingModal } from '@/components/TaggingModal';
import type { TagDto } from '@/services/tagService';

interface CollapsedPrinterCardProps {
  printer: Printer;
  backendCapabilities?: PrinterBackendCapabilitiesDto;
  onExpand: () => void;
  onEdit?: (printer: Printer) => void;
}

export function CollapsedPrinterCard({
  printer: printerProp,
  backendCapabilities,
  onExpand,
  onEdit
}: CollapsedPrinterCardProps) {
  // Merge with realtime SignalR updates
  const printer = printerProp; // printerProp already includes display data
  const [showCamera, setShowCamera] = useState(false);
  const [cameraMode, setCameraMode] = useState<'snapshot' | 'stream'>('snapshot');
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
  const { data: printerTags = [] } = useQuery<TagDto[]>({
    queryKey: ['printer-tags', printer.id],
    queryFn: async () => {
      const tags = await apiClient.getObjectTags(printer.id, 'Printer');
      return tags as unknown as TagDto[];
    },
    staleTime: 5 * 60 * 1000,
  });

  // Auto-print status
  const { data: autoPrintStatus } = useAutoPrintStatus(printer.id);
  const setAutoPrintEnabled = useSetAutoPrintEnabled();

  const handleAutoPrintToggle = async () => {
    const newEnabled = !(autoPrintStatus?.autoPrintEnabled ?? false);
    try {
      await setAutoPrintEnabled.mutateAsync({ printerId: printer.id, enabled: newEnabled });
      toast.success(newEnabled ? 'Auto-print enabled' : 'Auto-print disabled');
    } catch {
      toast.error('Failed to toggle auto-print');
    }
  };

  // Use printer data directly (already contains merged realtime status from API)
  const isOnline = printer.isOnline ?? false;
  const isEnabled = printer.isEnabled ?? true;
  const state = printer.state ?? 'Unknown';
  const isPrinting = state.toLowerCase().includes('printing');
  const isPaused = state.toLowerCase().includes('paused');
  const isShutdown = state.toLowerCase().includes('shutdown') || state.toLowerCase().includes('error');

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

  const toCamelCase = (str: string): string => {
    return str.charAt(0).toUpperCase() + str.slice(1).toLowerCase();
  };

  return (
    <div className="relative rounded-xl shadow-lg bg-pf-card border border-white/10 w-full">
      {/* Colored header — background tinted by printer state */}
      <div style={headerStyle} className="px-3 pt-3 pb-2 rounded-t-xl">
        {/* Top row: Name + Status Pill */}
        <div className="flex items-center gap-2">
          <div className="font-bold text-lg text-pf-text-primary font-bebas uppercase tracking-wide truncate">
            {printer.name}
          </div>
          <div className="inline-flex items-center px-2 py-0.5 rounded-full text-xs font-medium shrink-0 bg-black/30 border border-white/20">
            <span className="text-pf-text-primary font-medium">
              {isOnline ? toCamelCase(state) : 'Offline'}
            </span>
          </div>
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
          {/* Auto-print toggle */}
          <div className="flex items-center justify-between mb-2 mt-2 px-1">
            <span className="text-xs text-pf-text-secondary">Auto-print</span>
            <Toggle
              checked={autoPrintStatus?.autoPrintEnabled ?? false}
              onChange={handleAutoPrintToggle}
              disabled={setAutoPrintEnabled.isPending}
              size="sm"
              aria-label={`Toggle auto-print for ${printer.name}`}
            />
          </div>

          {/* Bed clear confirmation banner */}
          {autoPrintStatus && (
            <div className="mb-2">
              <BedClearBanner
                printerId={printer.id}
                printerName={printer.name}
                autoPrintStatus={autoPrintStatus}
              />
            </div>
          )}

          {/* Progress bar — always visible */}
          {(() => {
            const progress = printer.progress ?? 0;
            const isActive = isOnline && (isPrinting || isPaused) && progress > 0;
            return (
              <div className="mt-2">
                <div className="flex justify-between text-xs text-pf-text-secondary mb-1">
                  <span className="truncate flex-1">{isActive ? (printer.jobName || 'Printing...') : <span className="italic text-pf-text-tertiary">No active print</span>}</span>
                  {isActive && <span className="font-semibold ml-2">{Math.round(progress)}%</span>}
                </div>
                <div
                  className="w-full bg-pf-border-dark rounded-full h-2 overflow-hidden"
                  role="progressbar"
                  aria-label="Print progress"
                  aria-valuemin={0}
                  aria-valuemax={100}
                  aria-valuenow={isActive ? Math.round(Math.max(0, Math.min(100, progress))) : 0}
                >
                  <div
                    ref={collapsedProgressRef}
                    className="bg-pf-success-bg h-2 rounded-full transition-all duration-300"
                    style={{ width: `${isActive ? Math.max(0, Math.min(100, progress)) : 0}%` }}
                  >
                    <span className="sr-only">Print progress: {isActive ? Math.round(Math.max(0, Math.min(100, progress))) : 0}%</span>
                  </div>
                </div>
              </div>
            );
          })()}

          {/* Filament info */}
          {printer.spoolInfo?.hasActiveSpool && (
            <div className="flex items-center gap-2 mt-2 px-1 text-xs text-pf-text-secondary">
              {printer.spoolInfo.colorHex && (
                <span
                  className="inline-block w-3 h-3 rounded-full border border-white/20 shrink-0"
                  style={{ backgroundColor: printer.spoolInfo.colorHex }}
                  aria-label={`Filament color: ${printer.spoolInfo.colorHex}`}
                />
              )}
              <span className="truncate">
                {printer.spoolInfo.material ?? 'Unknown'}
              </span>
              {printer.spoolInfo.remainingWeightG != null && (
                <span className="shrink-0 text-pf-text-tertiary">
                  {printer.spoolInfo.remainingWeightG >= 1000
                    ? `${(printer.spoolInfo.remainingWeightG / 1000).toFixed(1)}kg`
                    : `${Math.round(printer.spoolInfo.remainingWeightG)}g`}
                </span>
              )}
            </div>
          )}
        </div>

        {/* Right: action buttons column */}
        <div
          className="flex flex-col items-center gap-0.5 ml-2 mt-2"
          role="toolbar"
          aria-label="Printer actions"
          ref={menuRef}
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
            aria-label={showCamera ? 'Hide camera stream' : 'Show camera stream'}
            title={!isEnabled ? 'Printer disabled' : hasCameraUrls ? 'Camera available' : 'No camera configured'}
            iconCenter={<CameraIcon className="h-4 w-4" />}
          />
          <div className="relative">
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
              <div className="absolute right-0 top-full mt-1 z-50 min-w-[180px] bg-pf-bg-1 border border-white/10 rounded-lg shadow-xl py-1 text-sm">
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
      </div>

      {showCamera && (
        <div className="mt-4 w-52 flex flex-col bg-pf-bg-2/30 border border-pf-border rounded-md overflow-hidden">
          {/* Camera mode toggle - show if both snapshot and stream are available */}
          {hasCameraUrls && cameraSnapshotUrl && cameraStreamUrl && (
            <div className="flex gap-1 p-2 border-b border-pf-border bg-pf-bg-1/50">
              <Button
                type="button"
                onClick={() => setCameraMode('snapshot')}
                title="Snapshot"
                aria-label="Snapshot"
                variant={cameraMode === 'snapshot' ? 'primary' : 'secondary'}
                size="sm"
                className="flex-1"
                iconCenter={<ImageIcon className="h-4 w-4" />}
              >
              </Button>
              <Button
                type="button"
                onClick={() => setCameraMode('stream')}
                title="Stream"
                aria-label="Stream"
                variant={cameraMode === 'stream' ? 'primary' : 'secondary'}
                size="sm"
                className="flex-1"
                iconCenter={<VideoIcon className="h-4 w-4" />}
              >
              </Button>
            </div>
          )}
          
          {/* Camera display */}
          <div className="w-full aspect-video bg-pf-bg-0 flex items-center justify-center overflow-hidden">
            {hasCameraUrls ? (
              cameraMode === 'snapshot' && cameraSnapshotUrl ? (
                <img 
                  src={cameraSnapshotUrl}
                  alt="webcam snapshot"
                  className="w-full h-full object-cover"
                  onError={() => {}}
                  onLoad={() => {}}
                />
              ) : cameraMode === 'stream' && cameraStreamUrl ? (
                <img 
                  src={cameraStreamUrl}
                  alt="webcam stream"
                  className="w-full h-full object-cover"
                  onError={() => {}}
                  onLoad={() => {}}
                />
              ) : cameraSnapshotUrl ? (
                <img 
                  src={cameraSnapshotUrl}
                  alt="webcam snapshot"
                  className="w-full h-full object-cover"
                  onError={() => {}}
                  onLoad={() => {}}
                />
              ) : cameraStreamUrl ? (
                <img 
                  src={cameraStreamUrl}
                  alt="webcam stream"
                  className="w-full h-full object-cover"
                  onError={() => {}}
                  onLoad={() => {}}
                />
              ) : (
                <div className="text-center text-pf-text-secondary p-4">
                  <CameraIcon className="h-8 w-8 mx-auto mb-2 opacity-50" />
                  <p className="text-sm">Camera mode not available</p>
                </div>
              )
            ) : (
              <div className="text-center text-pf-text-secondary p-4 w-full">
                <CameraIcon className="h-8 w-8 mx-auto mb-2 opacity-50" />
                <p className="text-sm">No camera configured</p>
              </div>
            )}
          </div>
        </div>
      )}

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

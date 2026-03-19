import { useState, useMemo, useEffect } from 'react';
import { PageTemplate } from '@/common/components/PageTemplate';
import { Spinner, Badge, Button, Toggle, Tooltip, Select } from '@/common/components/ui';
import { PlayIcon, CheckIcon, SkipForwardIcon, StopIcon, CheckCircleIcon } from '@/common/components/icons/MdiIcons';
import { Zap } from 'lucide-react';
import {
  useAutoDispatchGlobalStatus,
  useConfirmBedClear,
  useSkipNextJob,
  useCancelAutoDispatch,
  useSetAutoDispatchEnabled,
  useSetAllAutoDispatchEnabled,
  usePreClearBed,
} from '@/features/printers/hooks/useAutoDispatch';
import type { AutoDispatchDetailedStatus } from '@/types/api';
import clsx from 'clsx';

type AutoDispatchStateFilter = 'all' | 'pendingReady' | 'printing' | 'ready' | 'disabled' | 'idle';
type AutoDispatchSortMode = 'state' | 'name';

function getAutoDispatchStatePriority(printer: AutoDispatchDetailedStatus): number {
  if (!printer.enabled) return 4;
  if (printer.state === 'PendingReady') return 0;
  if (printer.currentJobName) return 1;
  if (printer.isReady) return 2;
  return 3;
}

export function AutoDispatchDashboardPage() {
  const { data: status, isLoading, error } = useAutoDispatchGlobalStatus();
  const markReadyMutation = useConfirmBedClear();
  const skipMutation = useSkipNextJob();
  const cancelMutation = useCancelAutoDispatch();
  const setEnabledMutation = useSetAutoDispatchEnabled();
  const setGlobalEnabledMutation = useSetAllAutoDispatchEnabled();
  const preClearMutation = usePreClearBed();

  const [stateFilter, setStateFilter] = useState<AutoDispatchStateFilter>('all');
  const [sortMode, setSortMode] = useState<AutoDispatchSortMode>(() => {
    const saved = localStorage.getItem('autoDispatchSortMode');
    if (saved === 'state' || saved === 'name') return saved;
    return 'state';
  });

  useEffect(() => {
    localStorage.setItem('autoDispatchSortMode', sortMode);
  }, [sortMode]);

  const filteredPrinters = useMemo(() => {
    let list = status?.printers ?? [];

    if (stateFilter !== 'all') {
      list = list.filter(p => {
        const isPrinting = !!p.currentJobName;
        const isPendingReady = p.state === 'PendingReady';
        if (stateFilter === 'disabled') return !p.enabled;
        if (stateFilter === 'printing') return p.enabled && isPrinting;
        if (stateFilter === 'pendingReady') return p.enabled && isPendingReady;
        if (stateFilter === 'ready') return p.enabled && p.isReady && !isPrinting;
        if (stateFilter === 'idle') return p.enabled && !isPrinting && !isPendingReady && !p.isReady;
        return true;
      });
    }

    const sorted = [...list];
    sorted.sort((a, b) => {
      if (sortMode === 'state') {
        const diff = getAutoDispatchStatePriority(a) - getAutoDispatchStatePriority(b);
        if (diff !== 0) return diff;
        return (a.printerName ?? '').localeCompare(b.printerName ?? '');
      }
      return (a.printerName ?? '').localeCompare(b.printerName ?? '');
    });
    return sorted;
  }, [status?.printers, stateFilter, sortMode]);

  const handleGlobalToggle = (enabled: boolean) => {
    setGlobalEnabledMutation.mutate(enabled);
  };

  const handlePrinterToggle = (printerId: string) => {
    const printer = status?.printers.find(p => p.printerId === printerId);
    const newEnabled = !(printer?.enabled ?? false);
    setEnabledMutation.mutate({ printerId, enabled: newEnabled });
  };

  const handleMarkReady = (printerId: string) => {
    markReadyMutation.mutate(printerId);
  };

  const handleSkip = (printerId: string) => {
    skipMutation.mutate(printerId);
  };

  const handleCancel = (printerId: string) => {
    cancelMutation.mutate(printerId);
  };

  const handlePreClear = (printerId: string) => {
    preClearMutation.mutate(printerId);
  };

  if (isLoading) {
    return (
      <PageTemplate title="Auto-Dispatch Dashboard" icon={PlayIcon}>
        <div className="flex justify-center py-12"><Spinner size="lg" /></div>
      </PageTemplate>
    );
  }

  if (error) {
    return (
      <PageTemplate title="Auto-Dispatch Dashboard" icon={PlayIcon}>
        <div className="p-4 text-pf-error">Failed to load auto-dispatch status: {error instanceof Error ? error.message : String(error)}</div>
      </PageTemplate>
    );
  }

  return (
    <PageTemplate
      title="Auto-Dispatch Dashboard"
      subtitle="Smart ready-gate status and queue automation"
      icon={PlayIcon}
      actions={
        <div className="flex items-center gap-3">
          <span className="text-sm text-pf-text-secondary">Global Auto-Dispatch:</span>
          <Toggle
            checked={status?.globalEnabled ?? false}
            onChange={handleGlobalToggle}
            disabled={setGlobalEnabledMutation.isPending}
            aria-label="Global auto-dispatch toggle"
          />
        </div>
      }
    >
      {!status?.printers || status.printers.length === 0 ? (
        <div className="rounded-xl border border-pf-border bg-pf-bg-1/50 p-12 text-center">
          <PlayIcon className="w-14 h-14 mx-auto mb-4 text-pf-text-muted opacity-30" />
          <p className="text-lg font-semibold text-pf-text-primary mb-1">No Printers Configured</p>
          <p className="text-sm text-pf-text-secondary">Configure printers to enable auto-dispatch queue management.</p>
        </div>
      ) : (
        <>
          {/* Filter/Sort toolbar */}
          <div className="flex flex-col sm:flex-row sm:items-center sm:justify-end gap-3 mb-4">
            <div className="flex items-center gap-2">
              <label htmlFor="ad-state-filter" className="text-sm text-pf-text-secondary hidden sm:inline">State:</label>
              <Select
                id="ad-state-filter"
                value={stateFilter}
                onChange={e => setStateFilter(e.target.value as AutoDispatchStateFilter)}
                aria-label="Filter by state"
                className="min-w-0"
              >
                <option value="all">All States</option>
                <option value="pendingReady">Pending Ready</option>
                <option value="printing">Printing</option>
                <option value="ready">Ready</option>
                <option value="idle">Idle</option>
                <option value="disabled">Disabled</option>
              </Select>
            </div>
            <div className="flex items-center gap-2">
              <label htmlFor="ad-sort-mode" className="text-sm text-pf-text-secondary hidden sm:inline">Sort:</label>
              <Select
                id="ad-sort-mode"
                value={sortMode}
                onChange={e => setSortMode(e.target.value as AutoDispatchSortMode)}
                aria-label="Sort printers by"
                className="min-w-0"
              >
                <option value="state">State</option>
                <option value="name">Name</option>
              </Select>
            </div>
          </div>

          {filteredPrinters.length === 0 ? (
            <div className="rounded-xl border border-pf-border bg-pf-bg-1/50 p-8 text-center">
              <p className="text-sm text-pf-text-secondary">No printers match the current filter.</p>
            </div>
          ) : (
            <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-3">
              {filteredPrinters.map((printer) => (
                <PrinterStatusCard
                  key={printer.printerId}
                  printer={printer}
                  onToggle={handlePrinterToggle}
                  onMarkReady={handleMarkReady}
                  onSkip={handleSkip}
                  onCancel={handleCancel}
                  onPreClear={handlePreClear}
                  isPending={
                    markReadyMutation.isPending ||
                    skipMutation.isPending ||
                    cancelMutation.isPending ||
                    setEnabledMutation.isPending ||
                    preClearMutation.isPending
                  }
                />
              ))}
            </div>
          )}
        </>
      )}
    </PageTemplate>
  );
}

// --- Status accent helpers ---

type AccentKey = 'disabled' | 'printing' | 'ready' | 'pending' | 'idle';

function getAccentKey(printer: AutoDispatchDetailedStatus, isPrinting: boolean, isPendingReady: boolean): AccentKey {
  if (!printer.enabled) return 'disabled';
  if (isPrinting) return 'printing';
  if (printer.isReady) return 'ready';
  if (isPendingReady) return 'pending';
  return 'idle';
}

const accentBorder: Record<AccentKey, string> = {
  disabled: 'border-l-pf-text-muted/40',
  printing: 'border-l-pf-accent',
  ready: 'border-l-pf-success',
  pending: 'border-l-pf-warning',
  idle: 'border-l-pf-border',
};

const accentGlow: Record<AccentKey, string> = {
  disabled: '',
  printing: 'shadow-[inset_0_1px_0_0_rgba(59,130,246,0.12)]',
  ready: 'shadow-[inset_0_1px_0_0_rgba(34,197,94,0.12)]',
  pending: 'shadow-[inset_0_1px_0_0_rgba(234,179,8,0.12)]',
  idle: '',
};

// --- PrinterStatusCard ---

interface PrinterStatusCardProps {
  printer: AutoDispatchDetailedStatus;
  onToggle: (printerId: string) => void;
  onMarkReady: (printerId: string) => void;
  onSkip: (printerId: string) => void;
  onCancel: (printerId: string) => void;
  onPreClear: (printerId: string) => void;
  isPending: boolean;
}

function PrinterStatusCard({
  printer,
  onToggle,
  onMarkReady,
  onSkip,
  onCancel,
  onPreClear,
  isPending,
}: PrinterStatusCardProps) {
  const isPrinting = !!printer.currentJobName;
  const isPendingReady = printer.state === 'PendingReady';
  const isIdle = !isPrinting && !isPendingReady;
  const accent = getAccentKey(printer, isPrinting, isPendingReady);

  const statusBadge = (() => {
    if (!printer.enabled) return <Badge variant="default" size="sm">Disabled</Badge>;
    if (isPrinting) return <Badge variant="primary" size="sm">Printing</Badge>;
    if (printer.isReady) return <Badge variant="success" size="sm">Ready</Badge>;
    if (isPendingReady) return <Badge variant="warning" size="sm">Awaiting Bed Clear</Badge>;
    return <Badge variant="default" size="sm">Idle</Badge>;
  })();

  const passedCount = printer.readyGateChecks.filter((c) => c.passed).length;
  const totalChecks = printer.readyGateChecks.length;

  return (
    <div
      className={clsx(
        'relative flex flex-col min-h-[220px] rounded-xl border border-pf-border border-l-[3px] bg-pf-bg-1/60 backdrop-blur-sm transition-all',
        accentBorder[accent],
        accentGlow[accent],
        !printer.enabled && 'opacity-55 grayscale-[30%]',
        printer.enabled && 'hover:border-white/15',
      )}
    >
      {/* Header */}
      <div className="flex items-start gap-3 px-4 pt-4 pb-3">
        <div className="flex-1 min-w-0">
          <div className="flex items-center gap-2 mb-1">
            <h3 className="text-base font-bold text-pf-text-primary truncate leading-tight">
              {printer.printerName}
            </h3>
            {statusBadge}
          </div>
          <div className="flex items-center gap-3 text-xs text-pf-text-tertiary">
            <span className="tabular-nums">{printer.queueDepth} {printer.queueDepth === 1 ? 'job' : 'jobs'} queued</span>
            {printer.lastActivity && (
              <>
                <span className="text-pf-border">·</span>
                <span>{formatRelativeTime(printer.lastActivity)}</span>
              </>
            )}
          </div>
        </div>

        {/* Zap toggle — matches CompactPrinterCard icon button */}
        <Tooltip content={printer.enabled ? 'Auto-dispatch enabled' : 'Auto-dispatch disabled'} position="left">
          <Button
            type="button"
            variant="unstyled"
            onClick={() => onToggle(printer.printerId)}
            disabled={isPending}
            className={clsx(
              'h-8 w-8 p-0 rounded-lg transition-colors inline-flex items-center justify-center shrink-0',
              printer.enabled
                ? 'text-pf-accent bg-pf-accent/10 hover:bg-pf-accent/20'
                : 'text-pf-text-secondary hover:text-pf-text-primary bg-white/5 hover:bg-white/10',
              'disabled:opacity-50',
            )}
            aria-label={`Toggle auto-dispatch for ${printer.printerName}`}
            aria-pressed={printer.enabled}
            iconCenter={<Zap className="w-4 h-4" fill={printer.enabled ? 'currentColor' : 'none'} />}
          />
        </Tooltip>
      </div>

      {/* Current Job */}
      {printer.currentJobName && (
        <div className="mx-4 mb-3 px-3 py-2.5 rounded-lg bg-pf-accent/5 border border-pf-accent/20">
          <div className="text-[10px] uppercase tracking-widest text-pf-accent/70 font-semibold mb-0.5">Current Job</div>
          <div className="text-sm text-pf-text-primary font-medium truncate">{printer.currentJobName}</div>
        </div>
      )}

      {/* Ready-Gate Checks — compact horizontal bar indicators */}
      {totalChecks > 0 && (
        <div className="px-4 mb-3">
          <div className="flex items-center justify-between mb-1.5">
            <span className="text-[10px] uppercase tracking-widest font-semibold text-pf-text-secondary">
              Ready Gate
            </span>
            <span className={clsx(
              'text-[10px] font-bold tabular-nums',
              passedCount === totalChecks ? 'text-pf-success' : 'text-pf-text-tertiary',
            )}>
              {passedCount}/{totalChecks}
            </span>
          </div>
          <div className="flex gap-1 mb-2">
            {printer.readyGateChecks.map((check, idx) => (
              <Tooltip key={idx} content={`${check.name}: ${check.message}`} position="top">
                <div
                  className={clsx(
                    'flex-1 h-1.5 rounded-full transition-colors',
                    check.passed ? 'bg-pf-success' : 'bg-pf-error/60',
                  )}
                  role="img"
                  aria-label={`${check.name}: ${check.passed ? 'passed' : 'failed'}`}
                />
              </Tooltip>
            ))}
          </div>
          <div className="flex flex-wrap gap-x-3 gap-y-0.5">
            {printer.readyGateChecks.map((check, idx) => (
              <span key={idx} className={clsx(
                'text-[11px] inline-flex items-center gap-1',
                check.passed ? 'text-pf-success/80' : 'text-pf-error/80',
              )}>
                <span className="text-[8px]">{check.passed ? '●' : '✕'}</span>
                {check.name}
              </span>
            ))}
          </div>
        </div>
      )}

      {/* Pre-Cleared indicator */}
      {isIdle && printer.bedPreConfirmed && (
        <div className="mx-4 mb-3 flex items-center gap-1.5 text-xs text-pf-success">
          <CheckCircleIcon className="w-3.5 h-3.5" />
          <span className="font-medium">Bed pre-cleared</span>
        </div>
      )}

      {/* Spacer pushes actions to card bottom for consistent height */}
      <div className="flex-1" />

      {/* Actions bar */}
      <div className="px-4 pb-4 pt-2 border-t border-white/5 flex gap-2 flex-wrap items-center min-h-[44px]">
        {isIdle && printer.enabled && !printer.bedPreConfirmed && (
          <Button
            variant="secondary"
            size="sm"
            onClick={() => onPreClear(printer.printerId)}
            disabled={isPending}
            iconLeft={<CheckCircleIcon />}
          >
            Pre-Clear Bed
          </Button>
        )}
        {isPendingReady && !isPrinting && (
          <Button
            variant="success"
            size="sm"
            onClick={() => onMarkReady(printer.printerId)}
            disabled={!printer.enabled || printer.isReady || isPending}
            iconLeft={<CheckIcon />}
          >
            Mark Ready
          </Button>
        )}
        {isPendingReady && printer.queueDepth > 0 && (
          <Button
            variant="secondary"
            size="sm"
            onClick={() => onSkip(printer.printerId)}
            disabled={!printer.enabled || isPending}
            iconLeft={<SkipForwardIcon />}
          >
            Skip
          </Button>
        )}
        {isPrinting && (
          <Button
            variant="danger"
            size="sm"
            onClick={() => onCancel(printer.printerId)}
            disabled={!printer.enabled || isPending}
            iconLeft={<StopIcon />}
          >
            Cancel
          </Button>
        )}
        {isIdle && (!printer.enabled || printer.bedPreConfirmed) && !isPendingReady && !isPrinting && (
          <span className="text-xs text-pf-text-muted italic">No actions available</span>
        )}
      </div>
    </div>
  );
}

function formatRelativeTime(isoDate: string): string {
  const diff = Date.now() - new Date(isoDate).getTime();
  const mins = Math.floor(diff / 60_000);
  if (mins < 1) return 'just now';
  if (mins < 60) return `${mins}m ago`;
  const hrs = Math.floor(mins / 60);
  if (hrs < 24) return `${hrs}h ago`;
  return `${Math.floor(hrs / 24)}d ago`;
}

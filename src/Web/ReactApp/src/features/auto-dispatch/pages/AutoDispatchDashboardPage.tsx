import { useState, useMemo, useEffect } from 'react';
import { PageTemplate } from '@/common/components/PageTemplate';
import { Spinner, Button, Toggle, Tooltip, Select } from '@/common/components/ui';
import { PlayIcon, CheckIcon, SkipForwardIcon, StopIcon, CheckCircleIcon } from '@/common/components/icons/MdiIcons';
import { Zap, Activity, AlertTriangle, Power, Pause, Layers } from 'lucide-react';
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

/* ── Inline keyframe styles (injected once) ──────────────────────────── */
const COMMAND_CENTER_STYLES = `
@keyframes ad-pulse-glow {
  0%, 100% { opacity: 0.6; }
  50% { opacity: 1; }
}
@keyframes ad-scan-line {
  0% { transform: translateX(-100%); }
  100% { transform: translateX(200%); }
}
@keyframes ad-pending-flash {
  0%, 100% { opacity: 0.7; }
  50% { opacity: 1; }
}
@keyframes ad-gate-fill {
  from { transform: scaleX(0); }
  to { transform: scaleX(1); }
}
@keyframes ad-beacon {
  0%, 100% { box-shadow: 0 0 0 0 currentColor; }
  50% { box-shadow: 0 0 8px 2px currentColor; }
}
@keyframes ad-float-in {
  from { opacity: 0; transform: translateY(6px); }
  to { opacity: 1; transform: translateY(0); }
}
.ad-animate-in { animation: ad-float-in 0.35s ease-out both; }
.ad-pulse-glow { animation: ad-pulse-glow 2s ease-in-out infinite; }
.ad-scan-line { animation: ad-scan-line 3s ease-in-out infinite; }
.ad-pending-flash { animation: ad-pending-flash 1.5s ease-in-out infinite; }
.ad-gate-fill { animation: ad-gate-fill 0.6s ease-out both; transform-origin: left; }
.ad-beacon { animation: ad-beacon 2s ease-in-out infinite; }
`;

type AutoDispatchStateFilter = 'all' | 'pendingReady' | 'printing' | 'ready' | 'disabled' | 'idle';
type AutoDispatchSortMode = 'state' | 'name';

function getAutoDispatchStatePriority(printer: AutoDispatchDetailedStatus): number {
  if (!printer.enabled) return 4;
  if (printer.state === 'PendingReady') return 0;
  if (printer.currentJobName) return 1;
  if (printer.isReady) return 2;
  return 3;
}

/* ── Farm Status Summary ─────────────────────────────────────────────── */

interface FarmStats {
  total: number;
  printing: number;
  ready: number;
  pending: number;
  idle: number;
  disabled: number;
  totalQueued: number;
}

function computeFarmStats(printers: AutoDispatchDetailedStatus[]): FarmStats {
  const stats: FarmStats = { total: printers.length, printing: 0, ready: 0, pending: 0, idle: 0, disabled: 0, totalQueued: 0 };
  for (const p of printers) {
    stats.totalQueued += p.queueDepth;
    if (!p.enabled) { stats.disabled++; continue; }
    if (p.currentJobName) { stats.printing++; continue; }
    if (p.state === 'PendingReady') { stats.pending++; continue; }
    if (p.isReady) { stats.ready++; continue; }
    stats.idle++;
  }
  return stats;
}

function FarmStatusBar({ stats, globalEnabled }: { stats: FarmStats; globalEnabled: boolean }) {
  const statCells: { label: string; value: number; color: string; icon: React.ReactNode }[] = [
    { label: 'Printing', value: stats.printing, color: 'text-pf-accent', icon: <Activity className="w-4 h-4" /> },
    { label: 'Ready', value: stats.ready, color: 'text-pf-success', icon: <CheckCircleIcon className="w-4 h-4" /> },
    { label: 'Attention', value: stats.pending, color: 'text-pf-warning', icon: <AlertTriangle className="w-4 h-4" /> },
    { label: 'Idle', value: stats.idle, color: 'text-pf-text-tertiary', icon: <Pause className="w-4 h-4" /> },
    { label: 'Offline', value: stats.disabled, color: 'text-pf-text-muted', icon: <Power className="w-4 h-4" /> },
    { label: 'Queued', value: stats.totalQueued, color: 'text-pf-accent-2', icon: <Layers className="w-4 h-4" /> },
  ];

  return (
    <div className="relative mb-6 rounded-xl border border-pf-border bg-pf-bg-1/40 backdrop-blur-sm overflow-hidden">
      {/* Subtle animated scan line across the top */}
      <div className="absolute top-0 left-0 right-0 h-[1px] overflow-hidden">
        <div className={clsx('h-full w-1/3 bg-gradient-to-r from-transparent via-pf-accent/40 to-transparent', globalEnabled && 'ad-scan-line')} />
      </div>

      <div className="px-5 py-4">
        {/* System status indicator */}
        <div className="flex items-center gap-2 mb-3">
          <div className={clsx(
            'w-2 h-2 rounded-full shrink-0',
            globalEnabled ? 'bg-pf-success ad-beacon' : 'bg-pf-text-muted',
          )} role="img" aria-label={globalEnabled ? 'System active' : 'System offline'} />
          <span className="text-[10px] font-bold uppercase tracking-[0.2em] text-pf-text-secondary">
            {globalEnabled ? 'Dispatch Active' : 'Dispatch Offline'}
          </span>
          <span className="text-[10px] text-pf-text-muted ml-auto tabular-nums">
            {stats.total} unit{stats.total !== 1 ? 's' : ''} registered
          </span>
        </div>

        {/* Stat cells grid */}
        <div className="grid grid-cols-3 sm:grid-cols-6 gap-3">
          {statCells.map((cell) => (
            <div key={cell.label} className="text-center">
              <div className={clsx('flex items-center justify-center gap-1.5 mb-1', cell.color)}>
                {cell.icon}
                <span className="text-2xl font-bebas leading-none tabular-nums tracking-wide">{cell.value}</span>
              </div>
              <span className="text-[9px] uppercase tracking-[0.15em] font-semibold text-pf-text-muted">
                {cell.label}
              </span>
            </div>
          ))}
        </div>
      </div>
    </div>
  );
}

/* ── Main Dashboard Page ─────────────────────────────────────────────── */

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

  const farmStats = useMemo(() => computeFarmStats(status?.printers ?? []), [status?.printers]);

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
      <PageTemplate title="Auto-Dispatch" icon={PlayIcon}>
        <div className="flex justify-center py-12"><Spinner size="lg" /></div>
      </PageTemplate>
    );
  }

  if (error) {
    return (
      <PageTemplate title="Auto-Dispatch" icon={PlayIcon}>
        <div className="p-4 text-pf-error">Failed to load auto-dispatch status: {error instanceof Error ? error.message : String(error)}</div>
      </PageTemplate>
    );
  }

  const globalEnabled = status?.globalEnabled ?? false;

  return (
    <PageTemplate
      title="Auto-Dispatch"
      subtitle="Farm queue control and ready-gate monitoring"
      icon={PlayIcon}
      actions={
        <div className="flex items-center gap-3">
          <span className={clsx(
            'text-xs font-bold uppercase tracking-[0.15em]',
            globalEnabled ? 'text-pf-success' : 'text-pf-text-muted',
          )}>
            {globalEnabled ? 'System Online' : 'System Offline'}
          </span>
          <Toggle
            checked={globalEnabled}
            onChange={handleGlobalToggle}
            disabled={setGlobalEnabledMutation.isPending}
            aria-label="Global auto-dispatch toggle"
          />
        </div>
      }
    >
      {/* Inject scoped keyframe animations */}
      <style>{COMMAND_CENTER_STYLES}</style>

      {!status?.printers || status.printers.length === 0 ? (
        <div className="rounded-xl border border-pf-border bg-pf-bg-1/50 p-12 text-center">
          <PlayIcon className="w-14 h-14 mx-auto mb-4 text-pf-text-muted opacity-30" />
          <p className="text-lg font-semibold text-pf-text-primary mb-1">No Printers Configured</p>
          <p className="text-sm text-pf-text-secondary">Configure printers to enable auto-dispatch queue management.</p>
        </div>
      ) : (
        <>
          {/* Farm status summary */}
          <FarmStatusBar stats={farmStats} globalEnabled={globalEnabled} />

          {/* Filter/Sort toolbar — integrated command bar */}
          <div className="flex flex-col sm:flex-row sm:items-center gap-3 mb-5">
            {/* Filter chips */}
            <div className="flex flex-wrap items-center gap-1.5" role="radiogroup" aria-label="Filter printers by state">
              {([
                ['all', 'All', undefined],
                ['pendingReady', 'Attention', farmStats.pending],
                ['printing', 'Printing', farmStats.printing],
                ['ready', 'Ready', farmStats.ready],
                ['idle', 'Idle', farmStats.idle],
                ['disabled', 'Offline', farmStats.disabled],
              ] as [AutoDispatchStateFilter, string, number | undefined][]).map(([key, label, count]) => (
                <Button
                  key={key}
                  type="button"
                  variant="unstyled"
                  role="radio"
                  aria-checked={stateFilter === key}
                  onClick={() => setStateFilter(key)}
                  className={clsx(
                    'px-3 py-1.5 rounded-lg text-[11px] font-bold uppercase tracking-[0.1em] transition-all border',
                    stateFilter === key
                      ? 'bg-pf-accent/15 border-pf-accent/40 text-pf-accent shadow-[0_0_10px_rgba(88,166,255,0.1)]'
                      : 'bg-pf-bg-2/50 border-pf-border/50 text-pf-text-muted hover:text-pf-text-secondary hover:border-pf-border',
                  )}
                >
                  {label}
                  {count !== undefined && count > 0 && (
                    <span className="ml-1.5 tabular-nums opacity-70">{count}</span>
                  )}
                </Button>
              ))}
            </div>

            {/* Sort control — right-aligned */}
            <div className="flex items-center gap-2 sm:ml-auto shrink-0">
              <label htmlFor="ad-sort-mode" className="text-[10px] uppercase tracking-[0.15em] font-semibold text-pf-text-muted">Sort</label>
              <Select
                id="ad-sort-mode"
                value={sortMode}
                onChange={e => setSortMode(e.target.value as AutoDispatchSortMode)}
                aria-label="Sort printers by"
                className="min-w-0 !text-xs"
              >
                <option value="state">By State</option>
                <option value="name">By Name</option>
              </Select>
            </div>
          </div>

          {filteredPrinters.length === 0 ? (
            <div className="rounded-xl border border-pf-border bg-pf-bg-1/50 p-8 text-center">
              <p className="text-sm text-pf-text-secondary">No printers match the current filter.</p>
            </div>
          ) : (
            <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-3">
              {filteredPrinters.map((printer, idx) => (
                <PrinterStatusCard
                  key={printer.printerId}
                  printer={printer}
                  index={idx}
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

/* ── Status accent system ────────────────────────────────────────────── */

type AccentKey = 'disabled' | 'printing' | 'ready' | 'pending' | 'idle';

function getAccentKey(printer: AutoDispatchDetailedStatus, isPrinting: boolean, isPendingReady: boolean): AccentKey {
  if (!printer.enabled) return 'disabled';
  if (isPrinting) return 'printing';
  if (printer.isReady) return 'ready';
  if (isPendingReady) return 'pending';
  return 'idle';
}

const accentConfig: Record<AccentKey, {
  border: string;
  glow: string;
  headerGradient: string;
  indicator: string;
  statusLabel: string;
  statusClass: string;
}> = {
  disabled: {
    border: 'border-pf-text-muted/30',
    glow: '',
    headerGradient: 'from-pf-bg-2/60 to-transparent',
    indicator: 'bg-pf-text-muted/40',
    statusLabel: 'Offline',
    statusClass: 'text-pf-text-muted bg-pf-bg-2/80 border-pf-border/50',
  },
  printing: {
    border: 'border-pf-accent/60',
    glow: 'shadow-[0_0_20px_rgba(88,166,255,0.08),inset_0_1px_0_0_rgba(88,166,255,0.15)]',
    headerGradient: 'from-pf-accent/8 to-transparent',
    indicator: 'bg-pf-accent ad-pulse-glow',
    statusLabel: 'Printing',
    statusClass: 'text-pf-accent bg-pf-accent/10 border-pf-accent/30',
  },
  ready: {
    border: 'border-pf-success/60',
    glow: 'shadow-[0_0_20px_rgba(63,185,80,0.08),inset_0_1px_0_0_rgba(63,185,80,0.15)]',
    headerGradient: 'from-pf-success/8 to-transparent',
    indicator: 'bg-pf-success',
    statusLabel: 'Ready',
    statusClass: 'text-pf-success bg-pf-success/10 border-pf-success/30',
  },
  pending: {
    border: 'border-pf-warning/60',
    glow: 'shadow-[0_0_20px_rgba(234,179,8,0.08),inset_0_1px_0_0_rgba(234,179,8,0.15)]',
    headerGradient: 'from-pf-warning/8 to-transparent',
    indicator: 'bg-pf-warning ad-pending-flash',
    statusLabel: 'Awaiting Bed Clear',
    statusClass: 'text-pf-warning bg-pf-warning/10 border-pf-warning/30 ad-pending-flash',
  },
  idle: {
    border: 'border-pf-border/60',
    glow: '',
    headerGradient: 'from-pf-bg-2/30 to-transparent',
    indicator: 'bg-pf-text-tertiary/50',
    statusLabel: 'Idle',
    statusClass: 'text-pf-text-tertiary bg-pf-bg-2/60 border-pf-border/50',
  },
};

/* ── PrinterStatusCard ───────────────────────────────────────────────── */

interface PrinterStatusCardProps {
  printer: AutoDispatchDetailedStatus;
  index: number;
  onToggle: (printerId: string) => void;
  onMarkReady: (printerId: string) => void;
  onSkip: (printerId: string) => void;
  onCancel: (printerId: string) => void;
  onPreClear: (printerId: string) => void;
  isPending: boolean;
}

function PrinterStatusCard({
  printer,
  index,
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
  const config = accentConfig[accent];

  const passedCount = printer.readyGateChecks.filter((c) => c.passed).length;
  const totalChecks = printer.readyGateChecks.length;
  const allPassed = passedCount === totalChecks && totalChecks > 0;

  return (
    <div
      className={clsx(
        'ad-animate-in relative flex flex-col min-h-[240px] rounded-xl border bg-pf-bg-1/70 backdrop-blur-sm transition-all overflow-hidden group',
        config.border,
        config.glow,
        !printer.enabled && 'opacity-50 grayscale-[40%]',
        printer.enabled && 'hover:border-white/20 hover:shadow-lg',
      )}
      style={{ animationDelay: `${index * 50}ms` }}
    >
      {/* Top accent gradient bar */}
      <div className={clsx('absolute inset-x-0 top-0 h-16 bg-gradient-to-b pointer-events-none', config.headerGradient)} />

      {/* Scan line for printing state */}
      {accent === 'printing' && (
        <div className="absolute inset-x-0 top-0 h-[1px] overflow-hidden">
          <div className="h-full w-1/4 bg-gradient-to-r from-transparent via-pf-accent/60 to-transparent ad-scan-line" />
        </div>
      )}

      {/* Header */}
      <div className="relative flex items-start gap-3 px-4 pt-4 pb-3">
        <div className="flex-1 min-w-0">
          {/* Status indicator + printer name */}
          <div className="flex items-center gap-2.5 mb-2">
            <div className={clsx('w-2.5 h-2.5 rounded-full shrink-0 transition-colors', config.indicator)}
              role="img" aria-label={config.statusLabel} />
            <h3 className="text-base font-bold text-pf-text-primary truncate leading-tight font-inter">
              {printer.printerName}
            </h3>
          </div>

          {/* Status badge + meta */}
          <div className="flex items-center gap-2 flex-wrap">
            <span className={clsx(
              'inline-flex items-center px-2 py-0.5 rounded-md text-[10px] font-bold uppercase tracking-[0.12em] border',
              config.statusClass,
            )}>
              {config.statusLabel}
            </span>
            <span className="text-[11px] text-pf-text-muted tabular-nums">
              {printer.queueDepth} {printer.queueDepth === 1 ? 'job' : 'jobs'} queued
            </span>
            {printer.lastActivity && (
              <span className="text-[11px] text-pf-text-muted tabular-nums">
                · {formatRelativeTime(printer.lastActivity)}
              </span>
            )}
          </div>
        </div>

        {/* Zap toggle */}
        <Tooltip content={printer.enabled ? 'Auto-dispatch enabled' : 'Auto-dispatch disabled'} position="left">
          <Button
            type="button"
            variant="unstyled"
            onClick={() => onToggle(printer.printerId)}
            disabled={isPending}
            className={clsx(
              'h-9 w-9 p-0 rounded-lg transition-all inline-flex items-center justify-center shrink-0 border',
              printer.enabled
                ? 'text-pf-accent bg-pf-accent/10 border-pf-accent/30 hover:bg-pf-accent/20 hover:shadow-[0_0_12px_rgba(88,166,255,0.15)]'
                : 'text-pf-text-muted bg-pf-bg-2/50 border-pf-border/50 hover:text-pf-text-secondary hover:bg-pf-bg-2',
              'disabled:opacity-40',
            )}
            aria-label={`Toggle auto-dispatch for ${printer.printerName}`}
            aria-pressed={printer.enabled}
            iconCenter={<Zap className="w-4 h-4" fill={printer.enabled ? 'currentColor' : 'none'} />}
          />
        </Tooltip>
      </div>

      {/* Current Job — active print display */}
      {printer.currentJobName && (
        <div className="relative mx-4 mb-3 px-3 py-2.5 rounded-lg bg-pf-accent/5 border border-pf-accent/20 overflow-hidden">
          {/* Subtle animated background */}
          <div className="absolute inset-0 bg-gradient-to-r from-pf-accent/5 via-transparent to-pf-accent/5 opacity-50" />
          <div className="relative">
            <div className="text-[9px] uppercase tracking-[0.2em] text-pf-accent/60 font-bold mb-0.5">Active Job</div>
            <div className="text-sm text-pf-text-primary font-semibold truncate">{printer.currentJobName}</div>
          </div>
        </div>
      )}

      {/* Ready-Gate Diagnostics */}
      {totalChecks > 0 && (
        <div className="px-4 mb-3">
          <div className="flex items-center justify-between mb-2">
            <span className="text-[9px] uppercase tracking-[0.2em] font-bold text-pf-text-muted">
              System Diagnostics
            </span>
            <span className={clsx(
              'text-[10px] font-bold tabular-nums px-1.5 py-0.5 rounded',
              allPassed ? 'text-pf-success bg-pf-success/10' : 'text-pf-text-tertiary bg-pf-bg-2/50',
            )}>
              {passedCount}/{totalChecks}
            </span>
          </div>

          {/* Segmented diagnostic bar */}
          <div className="flex gap-[3px] mb-2.5">
            {printer.readyGateChecks.map((check, idx) => (
              <Tooltip key={idx} content={`${check.name}: ${check.message}`} position="top">
                <div className={clsx(
                  'flex-1 h-2 rounded-sm overflow-hidden',
                  check.passed ? 'bg-pf-success/20' : 'bg-pf-error/15',
                )}>
                  <div
                    className={clsx(
                      'h-full rounded-sm ad-gate-fill',
                      check.passed ? 'bg-pf-success' : 'bg-pf-error/60',
                    )}
                    style={{ animationDelay: `${idx * 100 + 200}ms` }}
                    role="img"
                    aria-label={`${check.name}: ${check.passed ? 'passed' : 'failed'}`}
                  />
                </div>
              </Tooltip>
            ))}
          </div>

          {/* Check items — styled as diagnostic readout */}
          <div className="grid grid-cols-2 gap-x-3 gap-y-1">
            {printer.readyGateChecks.map((check, idx) => (
              <span key={idx} className={clsx(
                'text-[10px] inline-flex items-center gap-1.5 font-medium',
                check.passed ? 'text-pf-success/80' : 'text-pf-error/80',
              )}>
                <span className={clsx(
                  'w-1.5 h-1.5 rounded-full shrink-0',
                  check.passed ? 'bg-pf-success' : 'bg-pf-error/70',
                )} />
                {check.name}
              </span>
            ))}
          </div>
        </div>
      )}

      {/* Pre-Cleared indicator */}
      {isIdle && printer.bedPreConfirmed && (
        <div className="mx-4 mb-3 flex items-center gap-2 px-2.5 py-1.5 rounded-lg bg-pf-success/5 border border-pf-success/15">
          <CheckCircleIcon className="w-3.5 h-3.5 text-pf-success" />
          <span className="text-[11px] font-semibold text-pf-success/90">Bed pre-cleared — ready for dispatch</span>
        </div>
      )}

      {/* Spacer */}
      <div className="flex-1" />

      {/* Actions bar — dark recessed footer */}
      <div className={clsx(
        'relative px-4 pb-4 pt-3 flex gap-2 flex-wrap items-center min-h-[48px]',
        'border-t border-white/[0.04] bg-gradient-to-b from-transparent to-pf-bg-0/30',
      )}>
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
          <span className="text-[11px] text-pf-text-muted italic">No actions available</span>
        )}
      </div>
    </div>
  );
}

/* ── Helpers ──────────────────────────────────────────────────────────── */

function formatRelativeTime(isoDate: string): string {
  const diff = Date.now() - new Date(isoDate).getTime();
  const mins = Math.floor(diff / 60_000);
  if (mins < 1) return 'just now';
  if (mins < 60) return `${mins}m ago`;
  const hrs = Math.floor(mins / 60);
  if (hrs < 24) return `${hrs}h ago`;
  return `${Math.floor(hrs / 24)}d ago`;
}

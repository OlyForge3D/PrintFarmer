import React, { useMemo, useState } from 'react';
import { useParams, useNavigate } from 'react-router';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { PageTemplate } from '@/common/components/PageTemplate';
import { Button } from '@/common/components/ui/Button';
import { maintenanceService } from '@/services/maintenanceService';
import { maintenancePlanService } from '@/services/maintenancePlanService';
import { apiClient } from '@/services/api';
import type {
  CreateMaintenanceLogRequest,
  PrinterToolheadOdometer,
  ToolheadDueState,
} from '@/types/maintenance';
import type { ApiError, Printer, PrinterDetails } from '@/types/api';
import { MaintenanceAlertStatus } from '@/types/maintenance';
import { 
  WrenchIcon, 
  ClockIcon, 
  ChartBarIcon,
  ExclamationTriangleIcon,
  CheckCircleIcon,
  PlusIcon,
  ArrowLeftIcon
} from '@heroicons/react/24/outline';
import { formatDistanceToNow, format } from 'date-fns';
import { LogMaintenanceModal } from '../components/LogMaintenanceModal';
import { ToolheadOdometerCard } from '../components/ToolheadOdometerCard';
import { ToolheadScopePicker } from '../components/ToolheadScopePicker';
import {
  PRINTER_WIDE_SCOPE,
  toolheadIdFromScope,
  type ToolheadScopeValue,
} from '../components/toolheadScope';
import { selectMaintenanceEligibleToolheads } from '@/features/printers/utils/isEligibleMaintenanceToolhead';
import { useUpcomingMaintenance } from '../hooks/useUpcomingMaintenance';
import { queryKeys } from '@/common/hooks/useApi';
import { maintenanceQueryKeys } from '../queryKeys';

function shouldRetryStatisticsQuery(failureCount: number, error: unknown) {
  const statusCode = typeof error === 'object' && error
    ? (error as ApiError).statusCode ?? (error as { response?: { status?: number } }).response?.status
    : undefined;

  if (typeof statusCode === 'number' && statusCode >= 400 && statusCode < 500) {
    return false;
  }

  return failureCount < 2;
}

/**
 * Printer-specific maintenance page showing:
 * - Printer statistics (hours, jobs, filament)
 * - Active alerts for this printer
 * - Maintenance history (logs)
 * - Scheduled maintenance tasks
 * - Ability to log new maintenance
 */
export function PrinterMaintenancePage() {
  const { printerId } = useParams<{ printerId: string }>();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [showLogModal, setShowLogModal] = useState(false);
  const [modalInitialToolheadId, setModalInitialToolheadId] = useState<string | null>(null);
  const [scope, setScope] = useState<ToolheadScopeValue>(PRINTER_WIDE_SCOPE);

  // Reset scope AND close any open printer-owned modal state whenever the
  // route param changes. React Router keeps this component mounted when
  // navigating between `/printers/:id/maintenance` paths (the element type
  // is stable across route matches), so `scope`, `showLogModal`, and
  // `modalInitialToolheadId` would otherwise leak from the previous
  // printer into the new one. A stale `toolheadId` from the previous
  // printer's tool set would either filter the new printer's records
  // incorrectly (if the id happened to collide) or, more subtly, preseed
  // the log-maintenance modal with a foreign `toolheadId` value that does
  // not exist on the current printer — worse, a modal left open while the
  // route changes would remain visible with the previous printer's
  // toolhead pre-selected and could submit a log for a toolhead that does
  // not belong to the currently displayed printer.
  //
  // We use the React-endorsed "adjust state during rendering" pattern
  // (see https://react.dev/reference/react/useState#storing-information-from-previous-renders)
  // rather than `useEffect`: a `setState` during render triggers a
  // synchronous re-render before commit, avoiding the cascading-render
  // penalty of an effect-based reset and eliminating a visible frame
  // where a foreign scope, foreign toolhead id, or foreign modal state
  // could leak into any child render. Combined with `key={printerId}` on
  // `<LogMaintenanceModal>` below, this guarantees the modal's own
  // internal state (form fields, scope selection, isSubmitting) is fully
  // reset because React unmounts and remounts the subtree.
  //
  // We deliberately do NOT reset on `printerDetails` changes — the scope
  // is a user-facing filter and should only clear when the underlying
  // printer identity changes, not on every refetch of the same printer.
  const [scopeResetOwner, setScopeResetOwner] = useState<string | undefined>(printerId);
  if (scopeResetOwner !== printerId) {
    setScopeResetOwner(printerId);
    setScope(PRINTER_WIDE_SCOPE);
    setShowLogModal(false);
    setModalInitialToolheadId(null);
  }

  // Fetch printer details
  const { data: printer, isLoading: printerLoading } = useQuery({
    queryKey: ['printer', printerId],
    queryFn: async () => {
      const printers = await apiClient.getPrinters() as Printer[];
      return printers.find(p => p.id === printerId);
    },
    enabled: !!printerId,
  });

  // Fetch printer details for the toolhead list (independent of the summary
  // /printers endpoint above so both paths keep working with the current API).
  // Errors are propagated to React Query so the caller can observe them via
  // `printerDetailsError`; we do NOT swallow them into a null result, because
  // that would silently hide per-tool UI on transient network failures.
  const {
    data: printerDetails,
    isLoading: printerDetailsLoading,
    error: printerDetailsError,
  } = useQuery<PrinterDetails>({
    queryKey: queryKeys.printerDetails(printerId ?? ''),
    queryFn: () => apiClient.getPrinterDetails(printerId!),
    enabled: !!printerId,
    staleTime: 60_000,
  });

  const eligibleToolheadsRaw = useMemo(
    () => selectMaintenanceEligibleToolheads(printerDetails?.toolheads),
    [printerDetails?.toolheads]
  );

  // `supportsPerToolAttribution` gates HOUR-attributed per-tool
  // scheduling/odometer tracking (Hicks #719/2 — see
  // `MaintenanceScheduleDeploymentController.UsesPrintHourIntervals` +
  // `DeployAsync`, which blocks a per-tool deploy solely when the plan
  // uses hour intervals AND attribution is unsupported). Calendar/manual
  // per-tool scopes remain valid regardless of this flag, and simple
  // maintenance LOG scope (`MaintenanceController.
  // CreateMaintenanceLogAsync`) never checks it at all — only the
  // printer-agnostic physical-toolhead-existence check. #711 stable
  // contract at 0428c66a63511b840034d66fcf7526e4c9b95634:
  // `PrinterDetailsDto.supportsPerToolAttribution` is composed
  // server-side from the global `multiSlotFallbackEnabled` flag AND the
  // persisted per-printer capability; we trust that composition and do
  // NOT re-derive it. Optional (`?`) on the type is deliberate old-server
  // tolerance; strict `=== true` collapses undefined/false.
  const printerSupportsPerTool = printerDetails?.supportsPerToolAttribution === true;
  const perToolAllowed = printerSupportsPerTool;
  // Odometer cards and the scope-filter picker surface HOUR-attributed
  // due-state data, so they stay collapsed to `[]` when attribution is
  // unsupported — unchanged from #711.
  const eligibleToolheads = perToolAllowed ? eligibleToolheadsRaw : [];
  // Physical toolhead existence — used to validate/preserve the scope of
  // an existing LOG or DEPLOYMENT (`handleLogMaintenance` below, and the
  // `toolheads` prop passed to `LogMaintenanceModal`). This must NOT be
  // collapsed by `supportsPerToolAttribution`: the prior version reused
  // the attribution-gated `eligibleToolheads` for this purpose, which
  // caused `handleLogMaintenance`'s validation to silently coerce an
  // existing, backend-valid deployment's non-null `toolheadId` to `null`
  // whenever attribution happened to be unsupported — even for
  // calendar/manual scopes (or plain logs) the backend still considers
  // fully valid regardless of attribution support.
  const logEligibleToolheads = eligibleToolheadsRaw;

  // Fetch printer statistics
  const { data: statistics, isLoading: statsLoading } = useQuery({
    queryKey: ['printerStatistics', printerId],
    queryFn: () => maintenanceService.getPrinterStatistics(printerId!),
    enabled: !!printerId,
    retry: shouldRetryStatisticsQuery,
  });

  // Fetch maintenance logs (history)
  const { data: logs = [], isLoading: logsLoading } = useQuery({
    queryKey: ['printerMaintenanceLogs', printerId],
    queryFn: () => maintenanceService.getPrinterMaintenanceLogs(printerId!),
    enabled: !!printerId,
  });

  // Fetch V3 schedule deployments for this printer
  const { data: deployments = [], isLoading: deploymentsLoading } = useQuery({
    queryKey: ['scheduleDeployments', printerId],
    queryFn: () => maintenancePlanService.getScheduleDeployments(printerId!, undefined, true),
    enabled: !!printerId,
  });

  // Fetch active alerts for this printer
  const { data: alerts = [], isLoading: alertsLoading } = useQuery({
    queryKey: ['printerAlerts', printerId],
    queryFn: () => maintenanceService.getPrinterAlerts(printerId!),
    enabled: !!printerId,
  });

  // Real due state comes from the upcoming-maintenance feed, which the
  // backend computes from schedule intervals + last-performed timestamps.
  // Alert severity is task priority, NOT timing, so we must not conflate
  // "high-severity alert" with "overdue task" — a low-priority schedule
  // can be overdue and a high-priority one can be perfectly on time.
  //
  // When the feed is loading or errored we must NOT default the odometer
  // cards to "OK" — that gives operators a false all-clear. Instead we
  // stamp every card's `dueState` as `'unknown'` and surface a role="alert"
  // banner when the feed errors so operators can distinguish "clear" from
  // "we don't know".
  const {
    tasks: upcomingTasks,
    isLoading: upcomingLoading,
    error: upcomingError,
  } = useUpcomingMaintenance({
    printerId,
    includeOverdue: true,
  });
  const dueStateResolved = !upcomingLoading && upcomingError == null;

  // Build per-toolhead odometers in-memory from `PrinterDetailsDto.toolheads[]`
  // (#711 stable contract at feature head `1b696b954` — there is NO dedicated
  // odometer endpoint; the backend surfaces per-tool cumulative hours as a
  // field on each toolhead). Due-state joins the upcoming-maintenance feed by
  // `toolheadId` so the card reflects the schedule engine's own verdict.
  const odometers = useMemo<PrinterToolheadOdometer[]>(() => {
    if (!perToolAllowed) return [];
    return eligibleToolheadsRaw.map(t => {
      const toolheadTasks = upcomingTasks.filter(task => task.toolheadId === t.id);
      let dueState: ToolheadDueState;
      let nextDueTaskName: string | null;
      if (!dueStateResolved) {
        // Loading or errored: the schedule engine's verdict is unknown, so we
        // MUST NOT collapse to "OK" (a false all-clear). Both `nextDueTaskName`
        // and the state are stamped as unknown so the card renders "No data".
        dueState = 'unknown';
        nextDueTaskName = null;
      } else if (toolheadTasks.some(task => task.isOverdue)) {
        dueState = 'overdue';
        nextDueTaskName = toolheadTasks.find(task => task.isOverdue)?.taskName ?? null;
      } else if (toolheadTasks.some(task => task.isDueToday)) {
        dueState = 'due-today';
        nextDueTaskName = toolheadTasks.find(task => task.isDueToday)?.taskName ?? null;
      } else {
        // Feed resolved and reports no overdue / due-today tasks for this
        // toolhead → OK. This is the ONLY path that produces "OK"; we never
        // infer OK from a numeric hours count.
        dueState = 'ok';
        nextDueTaskName = toolheadTasks[0]?.taskName ?? null;
      }
      return {
        toolheadId: t.id,
        toolheadName: t.name ?? null,
        toolheadIndex: typeof t.index === 'number' ? t.index : null,
        cumulativePrintHours:
          typeof t.cumulativePrintHours === 'number' ? t.cumulativePrintHours : null,
        dueState,
        nextDueTaskName,
      };
    });
  }, [perToolAllowed, eligibleToolheadsRaw, upcomingTasks, dueStateResolved]);

  // Single aggregate live-region summary of per-toolhead due state
  // (Hicks #6). N cards × N `role="status"` nodes would fire N
  // simultaneous announcements every time the schedule feed refreshes;
  // also, when each card renders as an interactive <button> any nested
  // live region is flattened into the button's accessible name and is
  // effectively silent to assistive tech.
  //
  // The one live region below sits as a sibling to (never inside) the
  // odometer grid or any button, and is `role="status"` (implicit
  // `aria-live="polite"`, `aria-atomic="true"`). Crucially, the
  // status *element itself* is mounted persistently — outside every
  // conditional branch (the "no eligible toolheads yet" and "feed
  // errored" branches) — so screen readers subscribe to it once and
  // observe every text change. Mount/unmount of a live-region node is
  // the classic silent-announcement bug we must avoid.
  //
  // We deliberately keep the *text* empty in two situations to avoid a
  // duplicate announcement with a co-visible `role="alert"` banner:
  //   1. When the schedule feed has errored — the upcoming-maintenance
  //      alert banner above already reads out the failure, and every
  //      card has `dueState: 'unknown'`. Announcing "N with unknown
  //      state" alongside the assertive alert would double-speak the
  //      same event.
  //   2. Before the odometer grid has any content (initial load with
  //      zero eligible toolheads / zero odometers) — there is nothing
  //      to summarise yet.
  //
  // Otherwise we emit a complete verdict for every state:
  //   - all-unknown (feed loading, no error) → "loading" message
  //   - all-OK (every toolhead resolved and clear) → "All toolheads
  //     OK." (short-circuit so the announcement is a strong signal,
  //     not "Maintenance status: N OK.")
  //   - mixed → count each non-zero category and join.
  const dueStateSummary = useMemo(() => {
    if (upcomingError != null) {
      return '';
    }
    if (odometers.length === 0) {
      return '';
    }
    const overdue = odometers.filter(o => o.dueState === 'overdue').length;
    const dueToday = odometers.filter(o => o.dueState === 'due-today').length;
    const unknown = odometers.filter(o => o.dueState === 'unknown').length;
    const ok = odometers.filter(o => o.dueState === 'ok').length;

    if (unknown === odometers.length) {
      return 'Maintenance due state unavailable — schedule feed is loading or unreachable.';
    }

    if (ok === odometers.length) {
      return 'All toolheads OK.';
    }

    const parts: string[] = [];
    const toolheadWord = (n: number) => (n === 1 ? 'toolhead' : 'toolheads');
    if (overdue > 0) parts.push(`${overdue} ${toolheadWord(overdue)} overdue`);
    if (dueToday > 0) parts.push(`${dueToday} due today`);
    if (unknown > 0) parts.push(`${unknown} with unknown state`);
    if (ok > 0) parts.push(`${ok} OK`);
    if (parts.length === 0) {
      return 'All toolheads OK.';
    }
    return `Maintenance status: ${parts.join(', ')}.`;
  }, [odometers, upcomingError]);

  const toolheadLabel = (toolheadId: string | null | undefined): string => {
    if (!toolheadId) return 'Printer-wide';
    const th = (printerDetails?.toolheads ?? []).find(t => t.id === toolheadId);
    return th?.name ?? 'Toolhead';
  };

  const scopedToolheadId = toolheadIdFromScope(scope);
  const showEverything = eligibleToolheads.length < 2; // picker hidden → no way to filter
  const scopeMatches = (recordToolheadId: string | null | undefined): boolean => {
    if (showEverything) return true;
    if (scope === PRINTER_WIDE_SCOPE) {
      return recordToolheadId == null;
    }
    return recordToolheadId === scopedToolheadId;
  };

  const scopedAlerts = alerts.filter(a => scopeMatches(a.toolheadId));
  const activeAlerts = scopedAlerts.filter(a =>
    a.status === MaintenanceAlertStatus.Active ||
    a.status === MaintenanceAlertStatus.Acknowledged
  );
  const scopedDeployments = deployments.filter(d => scopeMatches(d.toolheadId));
  const scopedLogs = logs.filter(l => scopeMatches(l.toolheadId));

  const handleLogMaintenance = (toolheadId?: string | null) => {
    // Nullish coalescing (`??`) would convert an EXPLICIT `null` — the
    // caller's request for a printer-wide log — into whatever scope the
    // page picker is on, silently attributing a printer-wide deployment
    // to a specific toolhead. We must distinguish "caller did not pass a
    // value" (undefined → fall back to page scope) from "caller passed
    // null" (explicit printer-wide → preserve). Only `undefined` triggers
    // the fallback.
    const raw =
      toolheadId === undefined ? toolheadIdFromScope(scope) : toolheadId;
    const canVerifyScope = printerDetails != null && !printerDetailsError;
    // Validate the resolved id against the CURRENT printer's eligible
    // toolheads. `raw` may be a stale id if a previous mount cached it
    // from another printer, or the fallback chain might have produced an
    // id that no longer maps to a physical, maintenance-eligible tool on
    // this printer. `null` (printer-wide) is always valid.
    const validated =
      !canVerifyScope
        ? raw
        : raw == null || logEligibleToolheads.some(t => t.id === raw)
          ? raw
          : null;
    setModalInitialToolheadId(validated);
    setShowLogModal(true);
  };

  const handleLogSubmit = async (data: CreateMaintenanceLogRequest) => {
    await maintenanceService.createMaintenanceLog(data);
    // Refresh data. `printerDetails` invalidation picks up the new
    // per-tool `cumulativePrintHours` after the backend recomputes on
    // `maintenancecompleted` (#711). `scheduleDeployments` picks up the
    // "last performed" watermark that the backend updates when the log
    // resolves an active deployment. `upcoming-maintenance` uses a nested
    // options object as its second key element, so we invalidate by
    // prefix — matching every variant regardless of lookaheadDays /
    // includeOverdue / printerId filter.
    queryClient.invalidateQueries({ queryKey: ['printerMaintenanceLogs', printerId] });
    queryClient.invalidateQueries({ queryKey: ['printerStatistics', printerId] });
    queryClient.invalidateQueries({ queryKey: ['printerAlerts', printerId] });
    queryClient.invalidateQueries({ queryKey: queryKeys.printerDetails(printerId!) });
    queryClient.invalidateQueries({ queryKey: ['scheduleDeployments', printerId] });
    queryClient.invalidateQueries({ queryKey: maintenanceQueryKeys.upcomingMaintenance() });
    setShowLogModal(false);
  };

  const isLoading =
    printerLoading ||
    printerDetailsLoading ||
    statsLoading ||
    logsLoading ||
    deploymentsLoading ||
    alertsLoading;

  if (!printerId) {
    return (
      <PageTemplate title="Printer Not Found" icon={WrenchIcon}>
        <div className="text-center py-12">
          <p className="text-pf-text-secondary">Invalid printer ID</p>
          <Button onClick={() => navigate('/printers')} className="mt-4">
            Back to Printers
          </Button>
        </div>
      </PageTemplate>
    );
  }

  const getPriorityColor = (priority: number) => {
    switch (priority) {
      case 4: return 'text-pf-error bg-pf-error/10';
      case 3: return 'text-pf-warning bg-pf-warning/10';
      case 2: return 'text-pf-warning bg-pf-warning/10';
      default: return 'text-pf-accent bg-pf-accent-bg/15';
    }
  };

  const getPriorityLabel = (priority: number) => {
    switch (priority) {
      case 4: return 'Critical';
      case 3: return 'High';
      case 2: return 'Medium';
      default: return 'Low';
    }
  };

  return (
    <PageTemplate
      title={printer?.name ? `${printer.name} Maintenance` : 'Printer Maintenance'}
      subtitle={printer ? `${printer.modelName || 'Unknown Model'} • ${printer.location?.name || 'No Location'}` : undefined}
      icon={WrenchIcon}
      actions={
        <div className="flex gap-2">
          <Button
            variant="ghost"
            onClick={() => navigate(-1)}
            iconLeft={<ArrowLeftIcon className="h-4 w-4" />}
          >
            Back
          </Button>
          <Button
            variant="primary"
            onClick={() => handleLogMaintenance()}
            iconLeft={<PlusIcon className="h-4 w-4" />}
          >
            Log Maintenance
          </Button>
        </div>
      }
    >
      {/*
        Persistent live region for the aggregate per-toolhead due-state
        summary (Hicks #719/3). This node is mounted unconditionally —
        OUTSIDE the `isLoading` loading/content branch below, as well as
        every inner conditional (no dependency on `eligibleToolheads.
        length`, on `odometers.length`, on `upcomingError`) — so screen
        readers subscribe to it once at page-mount (including the very
        first, still-loading render) and observe every subsequent text
        change. The previous version mounted this element only inside the
        loaded-content branch, so a screen reader that started observing
        during the initial spinner would never see the region come into
        existence — the mount itself is silent to assistive tech, only
        content *changes* on an already-mounted live region announce.
        Placing it inside the `eligibleToolheads.length > 0 && odometers.
        length > 0 && ...` branch would additionally unmount it in the
        initial-load and single-toolhead cases and silently miss the
        arrive-of-data announcement. The text itself is empty while
        `isLoading` is true (nothing to summarise yet) and otherwise may
        still be an empty string — `dueStateSummary` short-circuits to ''
        when the feed errors (avoiding a duplicate announcement with the
        `role="alert"` banner below) and before there is anything to
        summarise. `sr-only` keeps it invisible while remaining reachable
        to assistive tech.
      */}
      <p
        role="status"
        className="sr-only"
        data-testid="toolhead-due-state-summary"
      >
        {isLoading ? '' : dueStateSummary}
      </p>
      {isLoading ? (
        <div className="flex items-center justify-center py-12">
          <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-pf-primary" />
        </div>
      ) : (
        <div className="space-y-6">
          {/*
            Surface printer-details failures explicitly instead of silently
            collapsing per-tool UI. The per-tool surface is naturally gated
            by `supportsPerToolAttribution === true`, but the operator needs
            to know that the data source failed so they can distinguish
            "this printer doesn't support per-tool" from "the request broke".
          */}
          {printerDetailsError && (
            <div
              role="alert"
              className="rounded-md border border-pf-warning/40 bg-pf-warning/10 px-4 py-3 text-sm text-pf-warning"
            >
              Could not load printer details. Per-toolhead maintenance data
              may be unavailable — try refreshing.
            </div>
          )}
          {/*
            Surface upcoming-maintenance load failures explicitly. The
            odometer cards are already stamped as `dueState: 'unknown'` when
            the feed is loading or errored, but that alone is ambiguous —
            "No data" could just mean "brand-new printer". This banner
            distinguishes "the schedule feed is broken; check back" from
            "everything is genuinely quiet". The persistent live region
            above short-circuits to empty text when this banner is visible
            so screen readers do not announce the same failure twice.
          */}
          {upcomingError && (
            <div
              role="alert"
              className="rounded-md border border-pf-warning/40 bg-pf-warning/10 px-4 py-3 text-sm text-pf-warning"
              data-testid="upcoming-maintenance-error"
            >
              Could not load upcoming maintenance. Due-state indicators are
              unavailable until the request succeeds.
            </div>
          )}
          {/* Per-toolhead odometer row (#711/#719). Hidden entirely when the
              printer has no eligible physical toolheads or no odometer data. */}
          {eligibleToolheads.length > 0 && odometers.length > 0 && (
            <section
              aria-label="Per-toolhead odometers"
              className="grid grid-cols-1 md:grid-cols-2 xl:grid-cols-3 gap-3"
            >
              {odometers.map(o => (
                <ToolheadOdometerCard
                  key={o.toolheadId}
                  odometer={o}
                  isActive={scope === o.toolheadId}
                  onActivate={id => setScope(id)}
                />
              ))}
            </section>
          )}

          {/* Scope filter — only for multi-toolhead printers. */}
          {eligibleToolheads.length >= 2 && (
            <div className="bg-pf-card border border-pf-border rounded-lg p-4">
              <ToolheadScopePicker
                toolheads={printerDetails?.toolheads ?? []}
                value={scope}
                onChange={setScope}
                label="Viewing"
                helperText="Filter alerts, deployed plans, and history by maintenance scope."
                data-testid="printer-maintenance-scope"
              />
            </div>
          )}

          {/* Statistics Cards */}
          <div className="grid grid-cols-1 md:grid-cols-4 gap-4">
            <StatCard
              icon={ClockIcon}
              label="Total Print Hours"
              value={statistics?.totalPrintHours?.toFixed(1) || '0'}
              unit="hours"
            />
            <StatCard
              icon={ChartBarIcon}
              label="Jobs Completed"
              value={statistics?.totalJobsCompleted?.toLocaleString() || '0'}
              unit="jobs"
            />
            <StatCard
              icon={ChartBarIcon}
              label="Filament Used"
              value={statistics?.totalFilamentUsedMeters?.toFixed(1) || '0'}
              unit="meters"
            />
            <StatCard
              icon={ExclamationTriangleIcon}
              label="Active Alerts"
              value={activeAlerts.length.toString()}
              unit="alerts"
              highlight={activeAlerts.length > 0}
            />
          </div>

          {/* Active Alerts Section */}
          {activeAlerts.length > 0 && (
            <section aria-label="Active alerts" className="bg-pf-card border border-pf-border rounded-lg p-6">
              <h2 className="text-lg font-semibold text-pf-text-primary mb-4 flex items-center gap-2">
                <ExclamationTriangleIcon className="h-5 w-5 text-pf-warning" />
                Active Alerts
              </h2>
              <div className="space-y-3">
                {activeAlerts.map(alert => (
                  <div 
                    key={alert.id}
                    className="flex items-center justify-between p-4 bg-pf-bg-0/50 rounded-lg border border-pf-border"
                  >
                    <div className="flex-1">
                      <div className="flex items-center gap-2">
                        <span className={`px-2 py-0.5 rounded-sm text-xs font-medium ${getPriorityColor(alert.severity)}`}>
                          {getPriorityLabel(alert.severity)}
                        </span>
                        <span className="font-medium text-pf-text-primary">{alert.title}</span>
                        {alert.toolheadId && (
                          <span
                            className="text-xs px-2 py-0.5 border border-pf-border text-pf-text-secondary rounded-xs"
                            data-testid={`alert-toolhead-tag-${alert.id}`}
                          >
                            {toolheadLabel(alert.toolheadId)}
                          </span>
                        )}
                      </div>
                      <p className="text-sm text-pf-text-secondary mt-1">{alert.message}</p>
                      <p className="text-xs text-pf-text-tertiary mt-1">
                        Created {formatDistanceToNow(new Date(alert.createdAt), { addSuffix: true })}
                      </p>
                    </div>
                    <Button
                      size="sm"
                      variant="primary"
                      onClick={() => handleLogMaintenance(alert.toolheadId ?? null)}
                    >
                      Resolve
                    </Button>
                  </div>
                ))}
              </div>
            </section>
          )}

          {/* Two-column layout for Deployed Plans and History */}
          <div className="grid grid-cols-1 lg:grid-cols-2 gap-6">
            {/* Deployed Maintenance Plans */}
            <section className="bg-pf-card border border-pf-border rounded-lg p-6">
              <h2 className="text-lg font-semibold text-pf-text-primary mb-4 flex items-center gap-2">
                <ClockIcon className="h-5 w-5 text-pf-primary" />
                Deployed Plans
              </h2>
              {scopedDeployments.length === 0 ? (
                <p className="text-pf-text-secondary text-sm">No maintenance plans deployed to this printer.</p>
              ) : (
                <div className="space-y-3">
                  {scopedDeployments.map(deployment => (
                    <div 
                      key={deployment.id}
                      className="p-4 rounded-lg border bg-pf-bg-0/50 border-pf-border"
                    >
                      <div className="flex items-start justify-between">
                        <div className="flex-1">
                          <span className="font-medium text-pf-text-primary">{deployment.planName}</span>
                          {deployment.toolheadId && (
                            <span
                              className="ml-2 text-xs px-2 py-0.5 border border-pf-border text-pf-text-secondary rounded-xs"
                              data-testid={`deployment-toolhead-tag-${deployment.id}`}
                            >
                              {toolheadLabel(deployment.toolheadId)}
                            </span>
                          )}
                          {deployment.notes && (
                            <p className="text-sm text-pf-text-secondary mt-1">{deployment.notes}</p>
                          )}
                          <div className="flex flex-wrap gap-3 mt-2 text-xs text-pf-text-tertiary">
                            <span>Deployed {formatDistanceToNow(new Date(deployment.deployedAt), { addSuffix: true })}</span>
                            <span className={deployment.isActive ? 'text-pf-success' : 'text-pf-text-tertiary'}>
                              {deployment.isActive ? 'Active' : 'Inactive'}
                            </span>
                          </div>
                        </div>
                        <Button
                          size="sm"
                          variant="ghost"
                          onClick={() => handleLogMaintenance(deployment.toolheadId ?? null)}
                        >
                          Log
                        </Button>
                      </div>
                    </div>
                  ))}
                </div>
              )}
            </section>

            {/* Maintenance History */}
            <section className="bg-pf-card border border-pf-border rounded-lg p-6">
              <h2 className="text-lg font-semibold text-pf-text-primary mb-4 flex items-center gap-2">
                <CheckCircleIcon className="h-5 w-5 text-pf-success" />
                Maintenance History
              </h2>
              {scopedLogs.length === 0 ? (
                <p className="text-pf-text-secondary text-sm">No maintenance has been logged for this printer yet.</p>
              ) : (
                <div className="space-y-3 max-h-96 overflow-y-auto">
                  {scopedLogs
                    .sort((a, b) => new Date(b.performedAt).getTime() - new Date(a.performedAt).getTime())
                    .slice(0, 10)
                    .map(log => (
                      <div 
                        key={log.id}
                        className="p-4 bg-pf-bg-0/50 rounded-lg border border-pf-border"
                      >
                        <div className="flex items-start justify-between">
                          <div className="flex-1">
                            <span className="font-medium text-pf-text-primary">{log.taskName}</span>
                            {log.component && (
                              <span className="ml-2 text-xs px-2 py-0.5 bg-pf-accent-bg/20 text-pf-primary rounded-sm">
                                {log.component}
                              </span>
                            )}
                            {log.toolheadId && (
                              <span
                                className="ml-2 text-xs px-2 py-0.5 border border-pf-border text-pf-text-secondary rounded-xs"
                                data-testid={`log-toolhead-tag-${log.id}`}
                              >
                                {toolheadLabel(log.toolheadId)}
                              </span>
                            )}
                            {log.notes && (
                              <p className="text-sm text-pf-text-secondary mt-1">{log.notes}</p>
                            )}
                            <div className="flex flex-wrap gap-3 mt-2 text-xs text-pf-text-tertiary">
                              <span>{format(new Date(log.performedAt), 'MMM d, yyyy h:mm a')}</span>
                              {log.performedBy && <span>by {log.performedBy}</span>}
                              {log.durationMinutes && <span>{log.durationMinutes} min</span>}
                              {log.cost && <span>${log.cost.toFixed(2)}</span>}
                            </div>
                            {log.partsReplaced && (
                              <p className="text-xs text-pf-text-tertiary mt-1">
                                Parts: {log.partsReplaced}
                              </p>
                            )}
                          </div>
                        </div>
                      </div>
                    ))}
                  {scopedLogs.length > 10 && (
                    <p className="text-center text-sm text-pf-text-tertiary pt-2">
                      Showing 10 of {scopedLogs.length} entries
                    </p>
                  )}
                </div>
              )}
            </section>
          </div>
        </div>
      )}

      {/* Log Maintenance Modal.
          Keyed by `printerId` so React unmounts and remounts the modal
          subtree the instant the route changes to a different printer.
          The synchronous render-phase reset above already closes and
          clears the modal's *owned* state on this page, but
          `LogMaintenanceModal` also owns internal state (form fields,
          scope selection, isSubmitting) that we cannot reset from
          outside via props — remounting is the only guarantee. This is
          the second half of Hicks #2 (route change with open modal). */}
      {printerId && (
        <LogMaintenanceModal
          key={printerId}
          isOpen={showLogModal}
          printerId={printerId}
          printerName={printer?.name || 'Unknown Printer'}
          deployments={deployments}
          toolheads={printerDetails?.toolheads ?? []}
          initialToolheadId={modalInitialToolheadId}
          onSubmit={handleLogSubmit}
          onClose={() => setShowLogModal(false)}
        />
      )}

    </PageTemplate>
  );
}

interface StatCardProps {
  icon: React.ComponentType<{ className?: string }>;
  label: string;
  value: string;
  unit?: string;
  highlight?: boolean;
}

function StatCard({ icon: Icon, label, value, highlight }: StatCardProps) {
  return (
    <div className={`p-4 rounded-lg border ${highlight ? 'bg-pf-warning/10 border-pf-warning/30' : 'bg-pf-card border-pf-border'}`}>
      <div className="flex items-center gap-3">
        <Icon className={`h-8 w-8 ${highlight ? 'text-pf-warning' : 'text-pf-primary'}`} />
        <div>
          <p className="text-2xl font-bold text-pf-text-primary">{value}</p>
          <p className="text-xs text-pf-text-tertiary">{label}</p>
        </div>
      </div>
    </div>
  );
}

export default PrinterMaintenancePage;

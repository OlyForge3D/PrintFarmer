/**
 * MaintenanceDashboardPage
 *
 * Complete maintenance command center for the printer fleet.
 * Organized into 5 logical tabs:
 *   Dashboard  — Fleet health at a glance, alerts, printer grid
 *   Schedule   — Calendar and timeline of upcoming work
 *   Library    — Task Catalog + Maintenance Plans (sub-tabs)
 *   Analytics  — Charts, fleet statistics, reports
 *   Inventory  — Spare parts, consumables, replacement tracking
 */

import React, { useState } from 'react';
import { useNavigate } from 'react-router';
import { format } from 'date-fns';
import './MaintenanceDashboardPage.css';
import { PageTemplate } from '@/common/components/PageTemplate';
import {
  WrenchIcon,
  RefreshIcon,
  CalendarIcon,
  ListIcon,
  ChartIcon,
  PackageIcon,
  DatabaseIcon,
  AlertIcon,
} from '@/common/components/icons/MdiIcons';
import { Button, Tabs, Badge } from '@/common/components/ui';

import { FleetMaintenanceOverview } from '../components/FleetMaintenanceOverview';
import { MaintenanceStatusGrid } from '../components/MaintenanceStatusGrid';
import { MaintenancePriorityList } from '../components/MaintenancePriorityList';
import { UpcomingMaintenanceCalendar } from '../components/UpcomingMaintenanceCalendar';
import { MaintenanceTimeline } from '../components/MaintenanceTimeline';
import { FleetStatisticsTable } from '../components/FleetStatisticsTable';
import { MaintenancePlansTab } from '../components/MaintenancePlansTabV2';
import { PartsInventoryTab } from '../components/PartsInventoryTab';
import { TaskCatalogTab } from '../components/TaskCatalogTab';
import { LowStockAlert } from '../components/LowStockAlert';
import { ComponentReplacementHistory } from '../components/ComponentReplacementHistory';
import { useMaintenanceStats } from '../hooks/useMaintenanceStats';
import { useMaintenanceAlerts } from '../hooks/useMaintenanceAlerts';
import { useUpcomingMaintenance } from '../hooks/useUpcomingMaintenance';
import { useComponentMaintenance } from '../hooks/useComponentMaintenance';
import type { UpcomingMaintenanceTask } from '../hooks/useUpcomingMaintenance';

import {
  MaintenanceTrendsChart,
  ComponentLifespanChart,
  MaintenanceCostAnalysis,
  PrinterUptimeChart,
} from '../components';
import { MaintenanceReport } from '../components/MaintenanceReport';

// ──────────────────────── Summary Stat Card ────────────────────────

interface SummaryStatProps {
  label: string;
  value: number | string;
  accent?: 'default' | 'amber' | 'red' | 'green';
}

function SummaryStat({ label, value, accent = 'default' }: SummaryStatProps) {
  const accentStyle: Record<string, string> = {
    default: 'text-pf-text-primary',
    amber: 'text-pf-warning',
    red: 'text-pf-error',
    green: 'text-pf-success',
  };
  return (
    <div className="text-center min-w-0">
      <p className={`text-3xl font-bold font-bebas tracking-wide ${accentStyle[accent]}`}>
        {value}
      </p>
      <p className="text-[11px] uppercase tracking-widest text-pf-text-tertiary mt-0.5 truncate">{label}</p>
    </div>
  );
}

// ──────────────────────── Main Page ────────────────────────

export function MaintenanceDashboardPage() {
  const navigate = useNavigate();
  const [selectedDate, setSelectedDate] = useState<Date | undefined>();
  const [selectedDayTasks, setSelectedDayTasks] = useState<UpcomingMaintenanceTask[]>([]);

  // ── Data hooks ──
  const { stats, isLoading: statsLoading, error: statsError, refetch: refetchStats } = useMaintenanceStats();
  const { alerts, isLoading: alertsLoading, error: alertsError, refetch: refetchAlerts } = useMaintenanceAlerts({ activeOnly: true });
  const {
    tasks: upcomingTasks,
    isLoading: tasksLoading,
    error: tasksError,
    refetch: refetchTasks,
    overdueCount,
    dueSoonCount,
  } = useUpcomingMaintenance({ lookaheadDays: 60 });
  const {
    replacements,
    componentNames,
    isLoading: componentsLoading,
    error: componentsError,
    refetch: refetchComponents,
  } = useComponentMaintenance();

  // ── Handlers ──
  const handleRefresh = () => {
    refetchStats();
    refetchAlerts();
    refetchTasks();
    refetchComponents();
  };

  const handlePrinterClick = (printerId: string) => {
    navigate(`/printers?selected=${printerId}`);
  };

  const handleDayClick = (date: Date, tasks: UpcomingMaintenanceTask[]) => {
    setSelectedDate(date);
    setSelectedDayTasks(tasks);
  };

  const handleTaskClick = (task: UpcomingMaintenanceTask) => {
    navigate(`/printers/${task.printerId}/maintenance`);
  };

  const isAnyLoading = statsLoading || alertsLoading || tasksLoading || componentsLoading;

  // ── Derived values ──
  const criticalAlerts = alerts.filter(a => a.severity >= 4).length;
  const totalAlerts = alerts.length;

  return (
    <PageTemplate
      title="Maintenance"
      subtitle={`Fleet maintenance command center${overdueCount > 0 ? ` · ${overdueCount} overdue` : ''}${dueSoonCount > 0 ? ` · ${dueSoonCount} due soon` : ''}`}
      icon={WrenchIcon}
      actions={
        <Button
          variant="secondary"
          size="sm"
          onClick={handleRefresh}
          disabled={isAnyLoading}
          iconLeft={
            <RefreshIcon
              className={`h-4 w-4 ${isAnyLoading ? 'animate-spin' : ''}`}
            />
          }
        >
          Refresh
        </Button>
      }
    >
      {/* ═══════════════════ Ribbon Stats ═══════════════════ */}
      <div className="flex items-center justify-between gap-6 bg-pf-bg-1 border border-pf-border rounded-xl px-6 py-4 mb-6 overflow-x-auto">
        <SummaryStat
          label="Printers"
          value={stats?.totalPrinters ?? '—'}
        />
        <div className="w-px h-10 bg-pf-border shrink-0" />
        <SummaryStat
          label="Online"
          value={stats?.printersOnline ?? '—'}
          accent="green"
        />
        <div className="w-px h-10 bg-pf-border shrink-0" />
        <SummaryStat
          label="Alerts"
          value={totalAlerts}
          accent={criticalAlerts > 0 ? 'red' : totalAlerts > 0 ? 'amber' : 'default'}
        />
        <div className="w-px h-10 bg-pf-border shrink-0" />
        <SummaryStat
          label="Overdue"
          value={overdueCount}
          accent={overdueCount > 0 ? 'red' : 'default'}
        />
        <div className="w-px h-10 bg-pf-border shrink-0" />
        <SummaryStat
          label="Due Soon"
          value={dueSoonCount}
          accent={dueSoonCount > 0 ? 'amber' : 'default'}
        />
        <div className="w-px h-10 bg-pf-border shrink-0" />
        <SummaryStat
          label="Needs Attention"
          value={stats?.printersNeedingAttention ?? 0}
          accent={(stats?.printersNeedingAttention ?? 0) > 0 ? 'amber' : 'default'}
        />
      </div>

      {/* ═══════════════════ Main Tabs ═══════════════════ */}
      <Tabs defaultTab="dashboard" className="space-y-0">
        <Tabs.List className="border-b border-pf-border bg-pf-bg-1 -mx-4 px-4 mb-0 overflow-x-auto">
          <Tabs.Tab id="dashboard" icon={<WrenchIcon className="h-4 w-4" />}>
            Dashboard
          </Tabs.Tab>
          <Tabs.Tab id="schedule" icon={<CalendarIcon className="h-4 w-4" />}>
            Schedule
            {overdueCount > 0 && (
              <Badge variant="error" className="pf-maintenance-schedule-badge ml-1.5 text-[10px] px-1.5 py-0">
                {overdueCount}
              </Badge>
            )}
          </Tabs.Tab>
          <Tabs.Tab id="library" icon={<DatabaseIcon className="h-4 w-4" />}>
            Library
          </Tabs.Tab>
          <Tabs.Tab id="inventory" icon={<PackageIcon className="h-4 w-4" />}>
            Inventory
          </Tabs.Tab>
          <Tabs.Tab id="analytics" icon={<ChartIcon className="h-4 w-4" />}>
            Analytics
          </Tabs.Tab>
        </Tabs.List>

        <Tabs.Panels>
          {/* ─────────── Dashboard Tab ─────────── */}
          <Tabs.Panel id="dashboard">
            <div className="space-y-8 mt-6">
              {/* Fleet Overview Cards */}
              <section aria-labelledby="fleet-heading">
                <h2 id="fleet-heading" className="sr-only">Fleet Overview</h2>
                <FleetMaintenanceOverview
                  stats={stats}
                  isLoading={statsLoading}
                  error={statsError}
                />
              </section>

              {/* Alerts + Printer Grid */}
              <div className="grid grid-cols-1 xl:grid-cols-3 gap-8">
                {/* Priority Alerts */}
                <section className="xl:col-span-1" aria-labelledby="alerts-heading">
                  <div className="bg-pf-panel border border-pf-border rounded-xl h-full">
                    <div className="px-5 py-4 border-b border-pf-border flex items-center gap-2">
                      <AlertIcon className="h-5 w-5 text-pf-warning" />
                      <div>
                        <h2 id="alerts-heading" className="text-base font-semibold text-pf-text-primary font-bebas uppercase tracking-wide">
                          Priority Alerts
                        </h2>
                        <p className="text-xs text-pf-text-tertiary">
                          {totalAlerts > 0
                            ? `${totalAlerts} alert${totalAlerts !== 1 ? 's' : ''} requiring attention`
                            : 'No active alerts'}
                        </p>
                      </div>
                    </div>
                    <div className="pf-maintenance-priority-list-scroll p-4 overflow-y-auto">
                      <MaintenancePriorityList
                        alerts={alerts}
                        isLoading={alertsLoading}
                        onActionComplete={handleRefresh}
                        maxItems={10}
                        compact
                      />
                    </div>
                  </div>
                </section>

                {/* Printer Status Grid */}
                <section className="xl:col-span-2" aria-labelledby="printer-grid-heading">
                  <div className="bg-pf-panel border border-pf-border rounded-xl h-full">
                    <div className="px-5 py-4 border-b border-pf-border">
                      <h2 id="printer-grid-heading" className="text-base font-semibold text-pf-text-primary font-bebas uppercase tracking-wide">
                        Printer Status
                      </h2>
                      <p className="text-xs text-pf-text-tertiary">
                        {stats
                          ? `${stats.totalPrinters} printer${stats.totalPrinters !== 1 ? 's' : ''} in fleet`
                          : 'Loading\u2026'}
                      </p>
                    </div>
                    <div className="p-4">
                      <MaintenanceStatusGrid
                        printers={stats?.printerStatuses || []}
                        isLoading={statsLoading}
                        onPrinterClick={handlePrinterClick}
                      />
                    </div>
                  </div>
                </section>
              </div>

              {/* Low Stock Alert */}
              <LowStockAlert maxItems={5} />
            </div>
          </Tabs.Panel>

          {/* ─────────── Schedule Tab ─────────── */}
          <Tabs.Panel id="schedule">
            <div className="space-y-6 mt-6">
              <div className="bg-pf-panel border border-pf-border rounded-xl overflow-hidden">
                <div className="px-5 py-4 border-b border-pf-border">
                  <h2 className="text-base font-semibold text-pf-text-primary font-bebas uppercase tracking-wide">
                    Upcoming Maintenance
                  </h2>
                  <p className="text-xs text-pf-text-tertiary mt-0.5">
                    {upcomingTasks.length > 0
                      ? `${upcomingTasks.length} task${upcomingTasks.length !== 1 ? 's' : ''} scheduled`
                      : 'No upcoming maintenance'}
                  </p>
                </div>

                <Tabs defaultTab="calendar" className="p-0">
                  <Tabs.List className="border-b border-pf-border bg-pf-bg-2">
                    <Tabs.Tab id="calendar" icon={<CalendarIcon className="h-4 w-4" />}>
                      Calendar
                    </Tabs.Tab>
                    <Tabs.Tab id="timeline" icon={<ListIcon className="h-4 w-4" />}>
                      Timeline
                    </Tabs.Tab>
                  </Tabs.List>

                  <Tabs.Panels>
                    <Tabs.Panel id="calendar">
                      <div className="grid grid-cols-1 lg:grid-cols-3 gap-6 p-5">
                        <div className="lg:col-span-2">
                          <UpcomingMaintenanceCalendar
                            tasks={upcomingTasks}
                            selectedDate={selectedDate}
                            onDayClick={handleDayClick}
                            isLoading={tasksLoading}
                          />
                        </div>
                        <div className="lg:col-span-1">
                          <div className="bg-pf-bg-1 border border-pf-border rounded-xl p-4">
                            <h3 className="font-semibold text-pf-text-primary mb-3">
                              {selectedDate
                                ? format(selectedDate, 'MMMM d, yyyy')
                                : 'Select a day'}
                            </h3>
                            {selectedDate ? (
                              selectedDayTasks.length > 0 ? (
                                <MaintenanceTimeline
                                  tasks={selectedDayTasks}
                                  isLoading={false}
                                  onTaskClick={handleTaskClick}
                                  maxVisible={5}
                                />
                              ) : (
                                <p className="text-sm text-pf-text-tertiary">
                                  No maintenance scheduled for this day
                                </p>
                              )
                            ) : (
                              <p className="text-sm text-pf-text-tertiary">
                                Click a day on the calendar to see scheduled maintenance
                              </p>
                            )}
                          </div>
                        </div>
                      </div>
                    </Tabs.Panel>

                    <Tabs.Panel id="timeline">
                      <div className="pf-maintenance-timeline-scroll p-5 overflow-y-auto">
                        <MaintenanceTimeline
                          tasks={upcomingTasks}
                          isLoading={tasksLoading}
                          onTaskClick={handleTaskClick}
                          maxVisible={15}
                        />
                      </div>
                    </Tabs.Panel>
                  </Tabs.Panels>
                </Tabs>
              </div>
            </div>
          </Tabs.Panel>

          {/* ─────────── Library Tab (Task Catalog + Plans) ─────────── */}
          <Tabs.Panel id="library">
            <div className="space-y-0 mt-6">
              <div className="bg-pf-panel border border-pf-border rounded-xl overflow-hidden">
                <Tabs defaultTab="plans" className="p-0">
                  <Tabs.List className="border-b border-pf-border bg-pf-bg-2 px-2">
                    <Tabs.Tab id="plans" icon={<ListIcon className="h-4 w-4" />}>
                      Maintenance Plans
                    </Tabs.Tab>
                    <Tabs.Tab id="tasks" icon={<DatabaseIcon className="h-4 w-4" />}>
                      Task Catalog
                    </Tabs.Tab>
                  </Tabs.List>

                  <Tabs.Panels>
                    <Tabs.Panel id="tasks">
                      <div className="px-5 py-4 border-b border-pf-border">
                        <h2 className="text-base font-semibold text-pf-text-primary font-bebas uppercase tracking-wide">
                          Task Catalog
                        </h2>
                        <p className="text-xs text-pf-text-tertiary mt-0.5">
                          Global library of maintenance tasks grouped by category. Create tasks, then add them to plans.
                        </p>
                      </div>
                      <div className="p-5">
                        <TaskCatalogTab />
                      </div>
                    </Tabs.Panel>

                    <Tabs.Panel id="plans">
                      <div className="px-5 py-4 border-b border-pf-border">
                        <h2 className="text-base font-semibold text-pf-text-primary font-bebas uppercase tracking-wide">
                          Maintenance Plans
                        </h2>
                        <p className="text-xs text-pf-text-tertiary mt-0.5">
                          Group tasks into plans and deploy them to printers for automated scheduling.
                        </p>
                      </div>
                      <div className="p-5">
                        <MaintenancePlansTab />
                      </div>
                    </Tabs.Panel>
                  </Tabs.Panels>
                </Tabs>
              </div>
            </div>
          </Tabs.Panel>

          {/* ─────────── Analytics Tab ─────────── */}
          <Tabs.Panel id="analytics">
            <div className="space-y-8 mt-6">
              {/* Fleet Statistics Table */}
              <section aria-labelledby="fleet-stats-heading">
                <div className="bg-pf-panel border border-pf-border rounded-xl">
                  <div className="px-5 py-4 border-b border-pf-border">
                    <h2
                      id="fleet-stats-heading"
                      className="text-base font-semibold text-pf-text-primary font-bebas uppercase tracking-wide"
                    >
                      Fleet Statistics
                    </h2>
                    <p className="text-xs text-pf-text-tertiary mt-0.5">
                      Detailed per-printer statistics with maintenance projections
                    </p>
                  </div>
                  <div className="p-5">
                    <FleetStatisticsTable />
                  </div>
                </div>
              </section>

              {/* Charts Grid */}
              <section aria-labelledby="analytics-heading">
                <div className="bg-pf-panel border border-pf-border rounded-xl overflow-hidden">
                  <div className="px-5 py-4 border-b border-pf-border">
                    <h2
                      id="analytics-heading"
                      className="text-base font-semibold text-pf-text-primary font-bebas uppercase tracking-wide"
                    >
                      Trends & Insights
                    </h2>
                    <p className="text-xs text-pf-text-tertiary mt-0.5">
                      Maintenance patterns, costs, and printer reliability
                    </p>
                  </div>
                  <div className="p-5 grid grid-cols-1 md:grid-cols-2 gap-8">
                    <MaintenanceTrendsChart />
                    <ComponentLifespanChart />
                    <MaintenanceCostAnalysis />
                    <PrinterUptimeChart />
                  </div>
                </div>
              </section>

              {/* Maintenance Report */}
              <MaintenanceReport />
            </div>
          </Tabs.Panel>

          {/* ─────────── Inventory Tab ─────────── */}
          <Tabs.Panel id="inventory">
            <div className="space-y-6 mt-6">
              {/* Low Stock Banner */}
              <LowStockAlert maxItems={5} />

              {/* Parts Inventory + Replacement History */}
              <div className="bg-pf-panel border border-pf-border rounded-xl overflow-hidden">
                <Tabs defaultTab="parts" className="p-0">
                  <Tabs.List className="border-b border-pf-border bg-pf-bg-2 px-2">
                    <Tabs.Tab id="parts" icon={<PackageIcon className="h-4 w-4" />}>
                      Spare Parts
                    </Tabs.Tab>
                    <Tabs.Tab id="replacements" icon={<RefreshIcon className="h-4 w-4" />}>
                      Replacement History
                    </Tabs.Tab>
                  </Tabs.List>

                  <Tabs.Panels>
                    <Tabs.Panel id="parts">
                      <div className="px-5 py-4 border-b border-pf-border">
                        <h2 className="text-base font-semibold text-pf-text-primary font-bebas uppercase tracking-wide">
                          Parts Inventory
                        </h2>
                        <p className="text-xs text-pf-text-tertiary mt-0.5">
                          Track spare parts, consumables, and replacement components
                        </p>
                      </div>
                      <div className="p-5">
                        <PartsInventoryTab />
                      </div>
                    </Tabs.Panel>

                    <Tabs.Panel id="replacements">
                      <div className="px-5 py-4 border-b border-pf-border">
                        <h2 className="text-base font-semibold text-pf-text-primary font-bebas uppercase tracking-wide">
                          Replacement History
                        </h2>
                        <p className="text-xs text-pf-text-tertiary mt-0.5">
                          Track component replacements across your fleet
                        </p>
                      </div>
                      <div className="pf-maintenance-timeline-scroll p-5 overflow-y-auto">
                        <ComponentReplacementHistory
                          replacements={replacements}
                          componentNames={componentNames}
                          isLoading={componentsLoading}
                        />
                      </div>
                    </Tabs.Panel>
                  </Tabs.Panels>
                </Tabs>
              </div>
            </div>
          </Tabs.Panel>
        </Tabs.Panels>
      </Tabs>

      {/* Error Display */}
      {(statsError || alertsError || tasksError || componentsError) && (
        <div className="bg-pf-error/10 border border-pf-error/30 rounded-xl p-4 mt-6" role="alert">
          <p className="text-sm text-pf-error">
            {statsError?.message || alertsError?.message || tasksError?.message || componentsError?.message || 'An error occurred loading maintenance data'}
          </p>
        </div>
      )}
    </PageTemplate>
  );
}

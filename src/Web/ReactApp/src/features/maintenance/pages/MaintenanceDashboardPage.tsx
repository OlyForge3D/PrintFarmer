/**
 * MaintenanceDashboardPage
 * 
 * Main maintenance dashboard showing fleet overview, printer grid, and priority alerts.
 * Organized with top-level tabs for better navigation of the comprehensive maintenance tools.
 */

import React, { useMemo, useState } from 'react';
import { useNavigate } from 'react-router';
import { format } from 'date-fns';
import { toast } from 'sonner';
import { PageTemplate } from '@/common/components/PageTemplate';
import { 
  WrenchIcon, 
  RefreshIcon, 
  CalendarIcon, 
  ListIcon, 
  GearIcon,
  ChartIcon,
  TableIcon
} from '@/common/components/icons/MdiIcons';
import { Button, Tabs } from '@/common/components/ui';
import { usePrintersFast } from '@/common/hooks/useApi';
import { PrinterSelectorModal } from '@/features/printers/components/PrinterSelectorModal';
import { FleetMaintenanceOverview } from '../components/FleetMaintenanceOverview';
import { MaintenanceStatusGrid } from '../components/MaintenanceStatusGrid';
import { MaintenancePriorityList } from '../components/MaintenancePriorityList';
import { UpcomingMaintenanceCalendar } from '../components/UpcomingMaintenanceCalendar';
import { MaintenanceTimeline } from '../components/MaintenanceTimeline';
import { CreateScheduleModal } from '../components/CreateScheduleModal';
import { ComponentMaintenanceTracker } from '../components/ComponentMaintenanceTracker';
import { ComponentReplacementHistory } from '../components/ComponentReplacementHistory';
import { FleetStatisticsTable } from '../components/FleetStatisticsTable';
import { useMaintenanceStats } from '../hooks/useMaintenanceStats';
import { useMaintenanceAlerts } from '../hooks/useMaintenanceAlerts';
import { useUpcomingMaintenance } from '../hooks/useUpcomingMaintenance';
import { useComponentMaintenance } from '../hooks/useComponentMaintenance';
import type { UpcomingMaintenanceTask } from '../hooks/useUpcomingMaintenance';
import type { CreateMaintenanceScheduleRequest } from '@/types/maintenance';
import { maintenanceService } from '@/services/maintenanceService';

import {
  MaintenanceTrendsChart,
  ComponentLifespanChart,
  MaintenanceCostAnalysis,
  PrinterUptimeChart
} from '../components';
import { MaintenanceReport } from '../components/MaintenanceReport';

/**
 * Main maintenance dashboard page component
 */
export function MaintenanceDashboardPage() {
  const navigate = useNavigate();
  const [selectedDate, setSelectedDate] = useState<Date | undefined>();
  const [selectedDayTasks, setSelectedDayTasks] = useState<UpcomingMaintenanceTask[]>([]);

  const { data: printersFast = [], isLoading: printersLoading } = usePrintersFast(false);
  const printerItems = useMemo(
    () =>
      printersFast.map((p) => ({
        id: p.id,
        name: p.name,
        modelName: p.modelName ?? undefined,
        manufacturerName: p.manufacturerName ?? undefined,
        isOnline: p.isOnline,
        nozzleDiameter: p.nozzleDiameter ?? undefined,
        motionType: p.motionType ?? undefined,
      })),
    [printersFast]
  );

  const [bulkMode, setBulkMode] = useState<'applyRecommended' | 'createCustom' | null>(null);
  const [isPrinterSelectorOpen, setIsPrinterSelectorOpen] = useState(false);
  const [selectedPrinterIds, setSelectedPrinterIds] = useState<string[]>([]);
  const [bulkOverwriteExisting, setBulkOverwriteExisting] = useState(false);
  const [isBulkCreateOpen, setIsBulkCreateOpen] = useState(false);
  const [isBulkBusy, setIsBulkBusy] = useState(false);
  
  // Fetch maintenance statistics
  const { stats, isLoading: statsLoading, error: statsError, refetch: refetchStats } = useMaintenanceStats();
  
  // Fetch active alerts
  const { 
    alerts, 
    isLoading: alertsLoading, 
    error: alertsError, 
    refetch: refetchAlerts 
  } = useMaintenanceAlerts({ activeOnly: true });

  // Fetch upcoming maintenance tasks
  const {
    tasks: upcomingTasks,
    isLoading: tasksLoading,
    error: tasksError,
    refetch: refetchTasks,
    overdueCount,
    dueSoonCount
  } = useUpcomingMaintenance({ lookaheadDays: 60 });

  // Fetch component maintenance data
  const {
    componentData,
    replacements,
    componentNames,
    isLoading: componentsLoading,
    error: componentsError,
    refetch: refetchComponents
  } = useComponentMaintenance();

  const handleRefresh = () => {
    refetchStats();
    refetchAlerts();
    refetchTasks();
    refetchComponents();
  };

  const openBulkApplyRecommended = () => {
    setBulkMode('applyRecommended');
    setSelectedPrinterIds([]);
    setBulkOverwriteExisting(false);
    setIsPrinterSelectorOpen(true);
  };

  const openBulkCreateCustom = () => {
    setBulkMode('createCustom');
    setSelectedPrinterIds([]);
    setBulkOverwriteExisting(false);
    setIsPrinterSelectorOpen(true);
  };

  const handlePrintersSelected = async (printerIds: string[]) => {
    setSelectedPrinterIds(printerIds);

    if (bulkMode === 'applyRecommended') {
      setIsBulkBusy(true);
      try {
        const result = await maintenanceService.bulkApplyRecommendedSchedules({
          printerIds,
          overwriteExisting: bulkOverwriteExisting,
        });
        toast.success(
          `Applied recommended schedules: ${result.schedulesCreated} created, ${result.schedulesSkipped} skipped`
        );
        handleRefresh();
      } catch (err) {
        toast.error(err instanceof Error ? err.message : 'Failed to apply recommended schedules');
      } finally {
        setIsBulkBusy(false);
      }
      return;
    }

    if (bulkMode === 'createCustom') {
      setIsBulkCreateOpen(true);
    }
  };

  const handleBulkCreateSubmit = async (data: CreateMaintenanceScheduleRequest) => {
    if (selectedPrinterIds.length === 0) {
      toast.error('Select at least one printer');
      return;
    }

    const result = await maintenanceService.bulkCreateScheduleForPrinters({
      printerIds: selectedPrinterIds,
      schedule: data,
      overwriteExisting: bulkOverwriteExisting,
    });

    toast.success(
      `Created schedules: ${result.schedulesCreated} created, ${result.schedulesSkipped} skipped`
    );
    setIsBulkCreateOpen(false);
    handleRefresh();
  };

  const handlePrinterClick = (printerId: string) => {
    // Navigate to printer detail or maintenance history
    // For now, navigate to printers page
    navigate(`/printers?selected=${printerId}`);
  };

  const handleDayClick = (date: Date, tasks: UpcomingMaintenanceTask[]) => {
    setSelectedDate(date);
    setSelectedDayTasks(tasks);
  };

  const handleTaskClick = (task: UpcomingMaintenanceTask) => {
    // Navigate to printer-specific maintenance page
    navigate(`/printers/${task.printerId}/maintenance`);
  };

  const isAnyLoading = statsLoading || alertsLoading || tasksLoading || componentsLoading;

  return (
    <>
    <PageTemplate
      title="Maintenance Dashboard"
      subtitle={`Monitor and manage maintenance across your printer fleet${
        overdueCount > 0 ? ` • ${overdueCount} overdue` : ''
      }${dueSoonCount > 0 ? ` • ${dueSoonCount} due soon` : ''}`}
      icon={WrenchIcon}
      actions={
        <Button
          variant="secondary"
          size="sm"
          onClick={handleRefresh}
          disabled={isAnyLoading}
          className="gap-2"
        >
          <RefreshIcon 
            className={`h-4 w-4 ${isAnyLoading ? 'animate-spin' : ''}`} 
            aria-hidden="true"
          />
          Refresh
        </Button>
      }
    >
      {/* Top-level navigation tabs */}
      <Tabs defaultTab="overview" className="space-y-6">
        <Tabs.List className="border-b border-pf-border bg-pf-bg-2 -mx-6 px-6 mb-6">
          <Tabs.Tab id="overview" icon={<WrenchIcon className="h-4 w-4" />}>
            Overview
          </Tabs.Tab>
          <Tabs.Tab id="statistics" icon={<TableIcon className="h-4 w-4" />}>
            Fleet Statistics
          </Tabs.Tab>
          <Tabs.Tab id="schedule" icon={<CalendarIcon className="h-4 w-4" />}>
            Schedule
          </Tabs.Tab>
          <Tabs.Tab id="analytics" icon={<ChartIcon className="h-4 w-4" />}>
            Analytics
          </Tabs.Tab>
          <Tabs.Tab id="components" icon={<GearIcon className="h-4 w-4" />}>
            Components
          </Tabs.Tab>
        </Tabs.List>

        <Tabs.Panels>
          {/* Overview Tab - Fleet status, alerts, and printer grid */}
          <Tabs.Panel id="overview">
            <div className="space-y-8">
              {/* Fleet Overview Section */}
              <section aria-labelledby="fleet-overview-heading">
                <h2 id="fleet-overview-heading" className="sr-only">
                  Fleet Overview
                </h2>
                <FleetMaintenanceOverview
                  stats={stats}
                  isLoading={statsLoading}
                  error={statsError}
                />
              </section>

              {/* Main Content Grid */}
              <div className="grid grid-cols-1 xl:grid-cols-3 gap-8">
                {/* Priority Alerts - Takes 1 column on XL, full width on smaller */}
                <section 
                  className="xl:col-span-1"
                  aria-labelledby="priority-alerts-heading"
                >
                  <div className="bg-pf-panel border border-pf-border rounded-xl">
                    <div className="px-5 py-4 border-b border-pf-border">
                      <h2 
                        id="priority-alerts-heading" 
                        className="text-lg font-semibold text-pf-text-primary"
                      >
                        Priority Alerts
                      </h2>
                      <p className="text-sm text-pf-text-tertiary mt-1">
                        {alerts.length > 0 
                          ? `${alerts.length} alert${alerts.length !== 1 ? 's' : ''} requiring attention`
                          : 'No active alerts'
                        }
                      </p>
                    </div>
                    <div className="p-5 max-h-[600px] overflow-y-auto">
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

                {/* Printer Status Grid - Takes 2 columns on XL */}
                <section 
                  className="xl:col-span-2"
                  aria-labelledby="printer-status-heading"
                >
                  <div className="bg-pf-panel border border-pf-border rounded-xl">
                    <div className="px-5 py-4 border-b border-pf-border">
                      <h2 
                        id="printer-status-heading" 
                        className="text-lg font-semibold text-pf-text-primary"
                      >
                        Printer Status
                      </h2>
                      <p className="text-sm text-pf-text-tertiary mt-1">
                        {stats 
                          ? `${stats.totalPrinters} printer${stats.totalPrinters !== 1 ? 's' : ''} in fleet`
                          : 'Loading...'
                        }
                      </p>
                    </div>
                    <div className="p-5">
                      <MaintenanceStatusGrid
                        printers={stats?.printerStatuses || []}
                        isLoading={statsLoading}
                        onPrinterClick={handlePrinterClick}
                      />
                    </div>
                  </div>
                </section>
              </div>
            </div>
          </Tabs.Panel>

          {/* Fleet Statistics Tab - Detailed printer statistics table */}
          <Tabs.Panel id="statistics">
            <div className="space-y-6">
              <section aria-labelledby="fleet-statistics-heading">
                <div className="bg-pf-panel border border-pf-border rounded-xl">
                  <div className="px-5 py-4 border-b border-pf-border">
                    <h2 
                      id="fleet-statistics-heading" 
                      className="text-lg font-semibold text-pf-text-primary"
                    >
                      Fleet Statistics
                    </h2>
                    <p className="text-sm text-pf-text-tertiary mt-1">
                      Detailed statistics for all printers with maintenance projections
                    </p>
                  </div>
                  <div className="p-5">
                    <FleetStatisticsTable />
                  </div>
                </div>
              </section>
            </div>
          </Tabs.Panel>

          {/* Schedule Tab - Calendar and timeline views */}
          <Tabs.Panel id="schedule">
            <div className="space-y-6">
              <section aria-labelledby="upcoming-maintenance-heading">
                <div className="bg-pf-panel border border-pf-border rounded-xl overflow-hidden">
                  <div className="px-5 py-4 border-b border-pf-border flex items-start justify-between gap-4">
                    <div>
                      <h2 
                        id="upcoming-maintenance-heading" 
                        className="text-lg font-semibold text-pf-text-primary"
                      >
                        Upcoming Maintenance
                      </h2>
                    <p className="text-sm text-pf-text-tertiary mt-1">
                      {upcomingTasks.length > 0 
                        ? `${upcomingTasks.length} task${upcomingTasks.length !== 1 ? 's' : ''} scheduled`
                        : 'No upcoming maintenance'
                      }
                    </p>
                    </div>

                    <div className="flex items-center gap-2 shrink-0">
                      <Button
                        type="button"
                        variant="secondary"
                        size="sm"
                        onClick={openBulkApplyRecommended}
                        disabled={printersLoading || isBulkBusy}
                      >
                        Apply Recommended
                      </Button>
                      <Button
                        type="button"
                        variant="secondary"
                        size="sm"
                        onClick={openBulkCreateCustom}
                        disabled={printersLoading || isBulkBusy}
                      >
                        Create for Printers
                      </Button>
                    </div>
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
                          {/* Calendar View */}
                          <div className="lg:col-span-2">
                            <UpcomingMaintenanceCalendar
                              tasks={upcomingTasks}
                              selectedDate={selectedDate}
                              onDayClick={handleDayClick}
                              isLoading={tasksLoading}
                            />
                          </div>
                          
                          {/* Selected Day Tasks */}
                          <div className="lg:col-span-1">
                            <div className="bg-pf-bg-1 border border-pf-border rounded-xl p-4">
                              <h3 className="font-semibold text-pf-text-primary mb-3">
                                {selectedDate 
                                  ? format(selectedDate, 'MMMM d, yyyy')
                                  : 'Select a day'
                                }
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
                        <div className="p-5 max-h-[600px] overflow-y-auto">
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
              </section>
            </div>
          </Tabs.Panel>

          {/* Analytics Tab - Charts and reports */}
          <Tabs.Panel id="analytics">
            <div className="space-y-8">
              {/* Analytics & Trends Section */}
              <section aria-labelledby="analytics-trends-heading">
                <div className="bg-pf-panel border border-pf-border rounded-xl overflow-hidden">
                  <div className="px-5 py-4 border-b border-pf-border">
                    <h2 
                      id="analytics-trends-heading" 
                      className="text-lg font-semibold text-pf-text-primary"
                    >
                      Analytics & Trends
                    </h2>
                    <p className="text-sm text-pf-text-tertiary mt-1">
                      Visualize maintenance patterns, costs, and printer reliability
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

              {/* Maintenance Report Section */}
              <MaintenanceReport />
            </div>
          </Tabs.Panel>

          {/* Components Tab - Component tracking and replacements */}
          <Tabs.Panel id="components">
            <div className="space-y-6">
              <section aria-labelledby="component-tracking-heading">
                <div className="bg-pf-panel border border-pf-border rounded-xl overflow-hidden">
                  <div className="px-5 py-4 border-b border-pf-border">
                    <h2 
                      id="component-tracking-heading" 
                      className="text-lg font-semibold text-pf-text-primary"
                    >
                      Component Tracking
                    </h2>
                    <p className="text-sm text-pf-text-tertiary mt-1">
                      {componentData.length > 0 
                        ? `${componentData.length} component type${componentData.length !== 1 ? 's' : ''} tracked`
                        : 'Track maintenance by component type'
                      }
                    </p>
                  </div>
                  
                  <Tabs defaultTab="components" className="p-0">
                    <Tabs.List className="border-b border-pf-border bg-pf-bg-2">
                      <Tabs.Tab id="components" icon={<GearIcon className="h-4 w-4" />}>
                        Components
                      </Tabs.Tab>
                      <Tabs.Tab id="replacements" icon={<RefreshIcon className="h-4 w-4" />}>
                        Replacements
                      </Tabs.Tab>
                    </Tabs.List>
                    
                    <Tabs.Panels>
                      <Tabs.Panel id="components">
                        <div className="p-5">
                          <ComponentMaintenanceTracker
                            componentData={componentData}
                            isLoading={componentsLoading}
                          />
                        </div>
                      </Tabs.Panel>
                      
                      <Tabs.Panel id="replacements">
                        <div className="p-5 max-h-[600px] overflow-y-auto">
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
              </section>
            </div>
          </Tabs.Panel>
        </Tabs.Panels>
      </Tabs>

      {/* Error display */}
      {(statsError || alertsError || tasksError || componentsError) && (
        <div className="bg-red-500/10 border border-red-500/30 rounded-xl p-4 mt-6">
          <p className="text-sm text-red-400">
            {statsError?.message || alertsError?.message || tasksError?.message || componentsError?.message || 'An error occurred loading maintenance data'}
          </p>
        </div>
      )}
    </PageTemplate>

    <PrinterSelectorModal
        isOpen={isPrinterSelectorOpen}
        printers={printerItems}
        multiSelect
        selectedPrinterIds={selectedPrinterIds}
        overwriteExisting={bulkOverwriteExisting}
        onOverwriteExistingChange={setBulkOverwriteExisting}
        title={bulkMode === 'applyRecommended' ? 'Apply Recommended Schedules' : 'Select Printers'}
        confirmLabel={bulkMode === 'applyRecommended' ? 'Apply Recommended' : 'Continue'}
        onSelectMany={handlePrintersSelected}
        onClose={() => setIsPrinterSelectorOpen(false)}
      />

    <CreateScheduleModal
        isOpen={isBulkCreateOpen}
        printerName={selectedPrinterIds.length > 0 ? `${selectedPrinterIds.length} printers` : undefined}
        onSubmit={handleBulkCreateSubmit}
        onClose={() => setIsBulkCreateOpen(false)}
      />
    </>
  );
}

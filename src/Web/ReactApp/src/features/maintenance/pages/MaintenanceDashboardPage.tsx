/**
 * MaintenanceDashboardPage
 * 
 * Main maintenance dashboard showing fleet overview, printer grid, and priority alerts.
 * Provides comprehensive view of maintenance status across all printers.
 */

import React, { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { format } from 'date-fns';
import { PageTemplate } from '@/common/components/PageTemplate';
import { WrenchIcon, RefreshIcon, CalendarIcon, ListIcon } from '@/common/components/icons/MdiIcons';
import { Button, Tabs } from '@/common/components/ui';
import { FleetMaintenanceOverview } from '../components/FleetMaintenanceOverview';
import { MaintenanceStatusGrid } from '../components/MaintenanceStatusGrid';
import { MaintenancePriorityList } from '../components/MaintenancePriorityList';
import { UpcomingMaintenanceCalendar } from '../components/UpcomingMaintenanceCalendar';
import { MaintenanceTimeline } from '../components/MaintenanceTimeline';
import { useMaintenanceStats } from '../hooks/useMaintenanceStats';
import { useMaintenanceAlerts } from '../hooks/useMaintenanceAlerts';
import { useUpcomingMaintenance } from '../hooks/useUpcomingMaintenance';
import type { UpcomingMaintenanceTask } from '../hooks/useUpcomingMaintenance';

/**
 * Main maintenance dashboard page component
 */
export function MaintenanceDashboardPage() {
  const navigate = useNavigate();
  const [selectedDate, setSelectedDate] = useState<Date | undefined>();
  const [selectedDayTasks, setSelectedDayTasks] = useState<UpcomingMaintenanceTask[]>([]);
  
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

  const handleRefresh = () => {
    refetchStats();
    refetchAlerts();
    refetchTasks();
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
    // Navigate to printer with maintenance context
    navigate(`/printers/${task.printerId}/maintenance`);
  };

  return (
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
          disabled={statsLoading || alertsLoading || tasksLoading}
          className="gap-2"
        >
          <RefreshIcon 
            className={`h-4 w-4 ${(statsLoading || alertsLoading || tasksLoading) ? 'animate-spin' : ''}`} 
            aria-hidden="true"
          />
          Refresh
        </Button>
      }
    >
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

        {/* Upcoming Maintenance Section */}
        <section aria-labelledby="upcoming-maintenance-heading">
          <div className="bg-pf-panel border border-pf-border rounded-xl overflow-hidden">
            <div className="px-5 py-4 border-b border-pf-border">
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

        {/* Error display */}
        {(statsError || alertsError || tasksError) && (
          <div className="bg-red-500/10 border border-red-500/30 rounded-xl p-4">
            <p className="text-sm text-red-400">
              {statsError?.message || alertsError?.message || tasksError?.message || 'An error occurred loading maintenance data'}
            </p>
          </div>
        )}
      </div>
    </PageTemplate>
  );
}

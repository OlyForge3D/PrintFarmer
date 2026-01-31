import React, { useState } from 'react';
import { useParams, useNavigate } from 'react-router';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { PageTemplate } from '@/common/components/PageTemplate';
import { Button } from '@/common/components/ui/Button';
import { maintenanceService } from '@/services/maintenanceService';
import { apiClient } from '@/services/api';
import type { 
  MaintenanceSchedule, 
  CreateMaintenanceLogRequest,
  CreateMaintenanceScheduleRequest
} from '@/types/maintenance';
import type { Printer } from '@/types/api';
import { MaintenanceAlertStatus } from '@/types/maintenance';
import { 
  WrenchIcon, 
  ClockIcon, 
  ChartBarIcon,
  ExclamationTriangleIcon,
  CheckCircleIcon,
  PlusIcon,
  ArrowLeftIcon,
  CalendarDaysIcon
} from '@heroicons/react/24/outline';
import { formatDistanceToNow, format } from 'date-fns';
import { LogMaintenanceModal } from '../components/LogMaintenanceModal';
import { CreateScheduleModal } from '../components/CreateScheduleModal';

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
  const [showScheduleModal, setShowScheduleModal] = useState(false);
  const [selectedSchedule, setSelectedSchedule] = useState<MaintenanceSchedule | null>(null);

  // Fetch printer details
  const { data: printer, isLoading: printerLoading } = useQuery({
    queryKey: ['printer', printerId],
    queryFn: async () => {
      const printers = await apiClient.getPrinters() as Printer[];
      return printers.find(p => p.id === printerId);
    },
    enabled: !!printerId,
  });

  // Fetch printer statistics
  const { data: statistics, isLoading: statsLoading } = useQuery({
    queryKey: ['printerStatistics', printerId],
    queryFn: () => maintenanceService.getPrinterStatistics(printerId!),
    enabled: !!printerId,
  });

  // Fetch maintenance logs (history)
  const { data: logs = [], isLoading: logsLoading } = useQuery({
    queryKey: ['printerMaintenanceLogs', printerId],
    queryFn: () => maintenanceService.getPrinterMaintenanceLogs(printerId!),
    enabled: !!printerId,
  });

  // Fetch maintenance schedules
  const { data: schedules = [], isLoading: schedulesLoading } = useQuery({
    queryKey: ['printerSchedules', printerId],
    queryFn: () => maintenanceService.getPrinterSchedules(printerId!),
    enabled: !!printerId,
  });

  // Fetch active alerts for this printer
  const { data: alerts = [], isLoading: alertsLoading } = useQuery({
    queryKey: ['printerAlerts', printerId],
    queryFn: () => maintenanceService.getPrinterAlerts(printerId!),
    enabled: !!printerId,
  });

  const activeAlerts = alerts.filter(a => 
    a.status === MaintenanceAlertStatus.Active || 
    a.status === MaintenanceAlertStatus.Acknowledged
  );

  const handleLogMaintenance = (schedule?: MaintenanceSchedule) => {
    setSelectedSchedule(schedule || null);
    setShowLogModal(true);
  };

  const handleLogSubmit = async (data: CreateMaintenanceLogRequest) => {
    await maintenanceService.createMaintenanceLog(data);
    // Refresh data
    queryClient.invalidateQueries({ queryKey: ['printerMaintenanceLogs', printerId] });
    queryClient.invalidateQueries({ queryKey: ['printerStatistics', printerId] });
    queryClient.invalidateQueries({ queryKey: ['printerAlerts', printerId] });
    setShowLogModal(false);
    setSelectedSchedule(null);
  };

  const handleScheduleSubmit = async (data: CreateMaintenanceScheduleRequest) => {
    await maintenanceService.createSchedule(data);
    // Refresh schedules
    queryClient.invalidateQueries({ queryKey: ['printerSchedules', printerId] });
    setShowScheduleModal(false);
  };

  const isLoading = printerLoading || statsLoading || logsLoading || schedulesLoading || alertsLoading;

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
      case 4: return 'text-red-500 bg-red-500/10';
      case 3: return 'text-orange-500 bg-orange-500/10';
      case 2: return 'text-yellow-500 bg-yellow-500/10';
      default: return 'text-blue-500 bg-blue-500/10';
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
      subtitle={printer ? `${printer.modelName || 'Unknown Model'} • ${printer.locationName || 'No Location'}` : undefined}
      icon={WrenchIcon}
      actions={
        <div className="flex gap-2">
          <Button
            variant="ghost"
            onClick={() => navigate(-1)}
            className="gap-2"
          >
            <ArrowLeftIcon className="h-4 w-4" />
            Back
          </Button>
          <Button
            variant="secondary"
            onClick={() => setShowScheduleModal(true)}
            className="gap-2"
          >
            <CalendarDaysIcon className="h-4 w-4" />
            Schedule Maintenance
          </Button>
          <Button
            variant="primary"
            onClick={() => handleLogMaintenance()}
            className="gap-2"
          >
            <PlusIcon className="h-4 w-4" />
            Log Maintenance
          </Button>
        </div>
      }
    >
      {isLoading ? (
        <div className="flex items-center justify-center py-12">
          <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-pf-primary" />
        </div>
      ) : (
        <div className="space-y-6">
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
            <section className="bg-pf-bg-card border border-pf-border rounded-lg p-6">
              <h2 className="text-lg font-semibold text-pf-text-primary mb-4 flex items-center gap-2">
                <ExclamationTriangleIcon className="h-5 w-5 text-yellow-500" />
                Active Alerts
              </h2>
              <div className="space-y-3">
                {activeAlerts.map(alert => (
                  <div 
                    key={alert.id}
                    className="flex items-center justify-between p-4 bg-pf-bg-dark/50 rounded-lg border border-pf-border"
                  >
                    <div className="flex-1">
                      <div className="flex items-center gap-2">
                        <span className={`px-2 py-0.5 rounded text-xs font-medium ${getPriorityColor(alert.severity)}`}>
                          {getPriorityLabel(alert.severity)}
                        </span>
                        <span className="font-medium text-pf-text-primary">{alert.title}</span>
                      </div>
                      <p className="text-sm text-pf-text-secondary mt-1">{alert.message}</p>
                      <p className="text-xs text-pf-text-tertiary mt-1">
                        Created {formatDistanceToNow(new Date(alert.createdAt), { addSuffix: true })}
                      </p>
                    </div>
                    <Button
                      size="sm"
                      variant="primary"
                      onClick={() => {
                        const schedule = schedules.find(s => s.id === alert.maintenanceScheduleId);
                        handleLogMaintenance(schedule);
                      }}
                    >
                      Resolve
                    </Button>
                  </div>
                ))}
              </div>
            </section>
          )}

          {/* Two-column layout for Schedules and History */}
          <div className="grid grid-cols-1 lg:grid-cols-2 gap-6">
            {/* Scheduled Maintenance */}
            <section className="bg-pf-bg-card border border-pf-border rounded-lg p-6">
              <h2 className="text-lg font-semibold text-pf-text-primary mb-4 flex items-center gap-2">
                <ClockIcon className="h-5 w-5 text-pf-primary" />
                Maintenance Schedule
              </h2>
              {schedules.length === 0 ? (
                <p className="text-pf-text-secondary text-sm">No maintenance schedules configured for this printer.</p>
              ) : (
                <div className="space-y-3">
                  {schedules.filter(s => s.isActive).map(schedule => {
                    const lastLog = logs.find(l => l.maintenanceScheduleId === schedule.id);
                    const hasAlert = activeAlerts.some(a => a.maintenanceScheduleId === schedule.id);
                    
                    return (
                      <div 
                        key={schedule.id}
                        className={`p-4 rounded-lg border ${hasAlert ? 'bg-yellow-500/10 border-yellow-500/30' : 'bg-pf-bg-dark/50 border-pf-border'}`}
                      >
                        <div className="flex items-start justify-between">
                          <div className="flex-1">
                            <div className="flex items-center gap-2">
                              <span className="font-medium text-pf-text-primary">{schedule.taskName}</span>
                              {hasAlert && (
                                <ExclamationTriangleIcon className="h-4 w-4 text-yellow-500" />
                              )}
                            </div>
                            {schedule.description && (
                              <p className="text-sm text-pf-text-secondary mt-1">{schedule.description}</p>
                            )}
                            <div className="flex flex-wrap gap-3 mt-2 text-xs text-pf-text-tertiary">
                              {schedule.component && (
                                <span>Component: {schedule.component}</span>
                              )}
                              {schedule.intervalHours && (
                                <span>Every {schedule.intervalHours}h</span>
                              )}
                              {schedule.intervalDays && (
                                <span>Every {schedule.intervalDays} days</span>
                              )}
                            </div>
                            {lastLog && (
                              <p className="text-xs text-pf-text-tertiary mt-2">
                                Last performed: {format(new Date(lastLog.performedAt), 'MMM d, yyyy')}
                                {lastLog.performedBy && ` by ${lastLog.performedBy}`}
                              </p>
                            )}
                          </div>
                          <Button
                            size="sm"
                            variant="ghost"
                            onClick={() => handleLogMaintenance(schedule)}
                          >
                            Log
                          </Button>
                        </div>
                      </div>
                    );
                  })}
                </div>
              )}
            </section>

            {/* Maintenance History */}
            <section className="bg-pf-bg-card border border-pf-border rounded-lg p-6">
              <h2 className="text-lg font-semibold text-pf-text-primary mb-4 flex items-center gap-2">
                <CheckCircleIcon className="h-5 w-5 text-green-500" />
                Maintenance History
              </h2>
              {logs.length === 0 ? (
                <p className="text-pf-text-secondary text-sm">No maintenance has been logged for this printer yet.</p>
              ) : (
                <div className="space-y-3 max-h-96 overflow-y-auto">
                  {logs
                    .sort((a, b) => new Date(b.performedAt).getTime() - new Date(a.performedAt).getTime())
                    .slice(0, 10)
                    .map(log => (
                      <div 
                        key={log.id}
                        className="p-4 bg-pf-bg-dark/50 rounded-lg border border-pf-border"
                      >
                        <div className="flex items-start justify-between">
                          <div className="flex-1">
                            <span className="font-medium text-pf-text-primary">{log.taskName}</span>
                            {log.component && (
                              <span className="ml-2 text-xs px-2 py-0.5 bg-pf-primary/20 text-pf-primary rounded">
                                {log.component}
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
                  {logs.length > 10 && (
                    <p className="text-center text-sm text-pf-text-tertiary pt-2">
                      Showing 10 of {logs.length} entries
                    </p>
                  )}
                </div>
              )}
            </section>
          </div>
        </div>
      )}

      {/* Log Maintenance Modal */}
      {printerId && (
        <LogMaintenanceModal
          isOpen={showLogModal}
          printerId={printerId}
          printerName={printer?.name || 'Unknown Printer'}
          schedule={selectedSchedule}
          schedules={schedules}
          onSubmit={handleLogSubmit}
          onClose={() => {
            setShowLogModal(false);
            setSelectedSchedule(null);
          }}
        />
      )}

      {/* Create Schedule Modal */}
      {printerId && (
        <CreateScheduleModal
          isOpen={showScheduleModal}
          printerId={printerId}
          printerName={printer?.name || 'Unknown Printer'}
          onSubmit={handleScheduleSubmit}
          onClose={() => setShowScheduleModal(false)}
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
    <div className={`p-4 rounded-lg border ${highlight ? 'bg-yellow-500/10 border-yellow-500/30' : 'bg-pf-bg-card border-pf-border'}`}>
      <div className="flex items-center gap-3">
        <Icon className={`h-8 w-8 ${highlight ? 'text-yellow-500' : 'text-pf-primary'}`} />
        <div>
          <p className="text-2xl font-bold text-pf-text-primary">{value}</p>
          <p className="text-xs text-pf-text-tertiary">{label}</p>
        </div>
      </div>
    </div>
  );
}

export default PrinterMaintenancePage;

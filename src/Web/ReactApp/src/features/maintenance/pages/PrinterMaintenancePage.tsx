import React, { useState } from 'react';
import { useParams, useNavigate } from 'react-router';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { PageTemplate } from '@/common/components/PageTemplate';
import { Button } from '@/common/components/ui/Button';
import { maintenanceService } from '@/services/maintenanceService';
import { maintenancePlanService } from '@/services/maintenancePlanService';
import { apiClient } from '@/services/api';
import type { 
  CreateMaintenanceLogRequest
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
  ArrowLeftIcon
} from '@heroicons/react/24/outline';
import { formatDistanceToNow, format } from 'date-fns';
import { LogMaintenanceModal } from '../components/LogMaintenanceModal';

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

  const activeAlerts = alerts.filter(a => 
    a.status === MaintenanceAlertStatus.Active || 
    a.status === MaintenanceAlertStatus.Acknowledged
  );

  const handleLogMaintenance = () => {
    setShowLogModal(true);
  };

  const handleLogSubmit = async (data: CreateMaintenanceLogRequest) => {
    await maintenanceService.createMaintenanceLog(data);
    // Refresh data
    queryClient.invalidateQueries({ queryKey: ['printerMaintenanceLogs', printerId] });
    queryClient.invalidateQueries({ queryKey: ['printerStatistics', printerId] });
    queryClient.invalidateQueries({ queryKey: ['printerAlerts', printerId] });
    queryClient.invalidateQueries({ queryKey: ['upcomingMaintenance', printerId] });
    setShowLogModal(false);
  };

  const isLoading = printerLoading || statsLoading || logsLoading || deploymentsLoading || alertsLoading;

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
            className="gap-2"
          >
            <ArrowLeftIcon className="h-4 w-4" />
            Back
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
                <ExclamationTriangleIcon className="h-5 w-5 text-pf-warning" />
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
                        <span className={`px-2 py-0.5 rounded-sm text-xs font-medium ${getPriorityColor(alert.severity)}`}>
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
                      onClick={() => handleLogMaintenance()}
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
            <section className="bg-pf-bg-card border border-pf-border rounded-lg p-6">
              <h2 className="text-lg font-semibold text-pf-text-primary mb-4 flex items-center gap-2">
                <ClockIcon className="h-5 w-5 text-pf-primary" />
                Deployed Plans
              </h2>
              {deployments.length === 0 ? (
                <p className="text-pf-text-secondary text-sm">No maintenance plans deployed to this printer.</p>
              ) : (
                <div className="space-y-3">
                  {deployments.map(deployment => (
                    <div 
                      key={deployment.id}
                      className="p-4 rounded-lg border bg-pf-bg-dark/50 border-pf-border"
                    >
                      <div className="flex items-start justify-between">
                        <div className="flex-1">
                          <span className="font-medium text-pf-text-primary">{deployment.planName}</span>
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
                          onClick={() => handleLogMaintenance()}
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
            <section className="bg-pf-bg-card border border-pf-border rounded-lg p-6">
              <h2 className="text-lg font-semibold text-pf-text-primary mb-4 flex items-center gap-2">
                <CheckCircleIcon className="h-5 w-5 text-pf-success" />
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
                              <span className="ml-2 text-xs px-2 py-0.5 bg-pf-primary/20 text-pf-primary rounded-sm">
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
          deployments={deployments}
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
    <div className={`p-4 rounded-lg border ${highlight ? 'bg-pf-warning/10 border-pf-warning/30' : 'bg-pf-bg-card border-pf-border'}`}>
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

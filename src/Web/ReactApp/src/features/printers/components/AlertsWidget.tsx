/**
 * AlertsWidget Component
 * 
 * Dashboard widget showing printer alerts (offline, maintenance).
 * Uses the common DashboardWidget for consistent styling.
 */

import React from 'react';
import { useQuery } from '@tanstack/react-query';
import { DashboardWidget } from '@/common/components/DashboardWidget';
import { AlertCircleIcon, CheckCircleIcon, WrenchIcon } from '@/common/components/icons/MdiIcons';
import { usePrinters } from '@/common/hooks/useApi';
import { usePrinterDisplays } from '@/common/hooks/usePrinterDisplay';
import { apiClient } from '@/services/api';

interface MaintenanceAlertSettings {
  enabled: boolean;
  showOfflinePrinterAlerts: boolean;
}

export interface AlertsWidgetProps {
  /** Additional CSS classes */
  className?: string;
}

/**
 * Dashboard widget showing printer alerts
 */
export function AlertsWidget({ className = '' }: AlertsWidgetProps) {
  const { data: printers } = usePrinters();
  const displayPrinters = usePrinterDisplays(printers || []);
  
  // Fetch maintenance alert settings
  const { data: alertSettings } = useQuery({
    queryKey: ['settings', 'MaintenanceAlerts'],
    queryFn: () => apiClient.getSettings<MaintenanceAlertSettings>('MaintenanceAlerts'),
    staleTime: 5 * 60 * 1000,
  });
  
  const showOfflineAlerts = alertSettings?.showOfflinePrinterAlerts ?? true;

  // Calculate stats
  const stats = React.useMemo(() => {
    const userPrinters = displayPrinters ?? [];
    const total = userPrinters.length;
    const online = userPrinters.filter(p => p.isOnline).length;
    const maintenance = userPrinters.filter(p => p.inMaintenance).length;
    const offline = total - online;
    return { offline, maintenance };
  }, [displayPrinters]);

  const hasAlerts = (showOfflineAlerts && stats.offline > 0) || stats.maintenance > 0;
  const alertCount = (showOfflineAlerts ? stats.offline : 0) + stats.maintenance;

  return (
    <DashboardWidget
      title="Alerts"
      collapsible
      icon={AlertCircleIcon}
      iconColorClass={hasAlerts ? 'text-red-400' : 'text-pf-text-tertiary'}
      iconBgClass={hasAlerts ? 'bg-red-500/20' : 'bg-pf-bg-2'}
      subtitle={
        hasAlerts
          ? `${alertCount} active alert${alertCount !== 1 ? 's' : ''}`
          : 'All systems healthy'
      }
      hasContent={hasAlerts}
      storageKey="alerts"
      className={className}
      emptyState={
        <div className="text-center py-6">
          <CheckCircleIcon className="h-10 w-10 text-green-500 mx-auto mb-2" />
          <p className="text-sm text-pf-text-primary font-medium">No Active Alerts</p>
          <p className="text-xs text-pf-text-tertiary mt-1">All systems operating normally</p>
        </div>
      }
    >
      <div className="space-y-3">
        {showOfflineAlerts && stats.offline > 0 && (
          <div className="flex items-start gap-2 p-3 bg-pf-error-bg rounded-sm border border-pf-error-border">
            <AlertCircleIcon className="h-4 w-4 text-pf-error-text shrink-0 mt-0.5" />
            <div>
              <p className="text-sm font-medium text-pf-error-text">
                {stats.offline} Printer{stats.offline > 1 ? 's' : ''} Offline
              </p>
              <p className="text-xs text-pf-error-text opacity-80">
                Check network connection and printer status
              </p>
            </div>
          </div>
        )}
        {stats.maintenance > 0 && (
          <div className="flex items-start gap-2 p-3 bg-pf-warning-bg rounded-sm border border-pf-warning-border">
            <WrenchIcon className="h-4 w-4 text-pf-warning-text shrink-0 mt-0.5" />
            <div>
              <p className="text-sm font-medium text-pf-warning-text">
                {stats.maintenance} Printer{stats.maintenance > 1 ? 's' : ''} in Maintenance
              </p>
              <p className="text-xs text-pf-warning-text opacity-80">
                These printers are not available for printing
              </p>
            </div>
          </div>
        )}
      </div>
    </DashboardWidget>
  );
}

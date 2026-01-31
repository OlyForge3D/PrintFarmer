/**
 * MaintenanceAlertsWidget Component
 * 
 * Compact widget for displaying maintenance alerts on the main dashboard.
 * Shows critical and high-priority alerts with quick action links.
 */

import React from 'react';
import { Link } from 'react-router';
import { 
  WrenchIcon, 
  CheckCircleIcon
} from '@/common/components/icons/MdiIcons';
import { Badge } from '@/common/components/ui';
import { DashboardWidget } from '@/common/components/DashboardWidget';
import { useMaintenanceAlerts } from '../hooks/useMaintenanceAlerts';

export interface MaintenanceAlertsWidgetProps {
  /** Maximum alerts to display */
  maxAlerts?: number;
  /** Additional CSS classes */
  className?: string;
}

/**
 * Get severity configuration
 */
function getSeverityConfig(severity: number): { label: string; color: string; bgColor: string } {
  switch (severity) {
    case 4: return { label: 'Critical', color: 'text-red-400', bgColor: 'bg-red-500/20' };
    case 3: return { label: 'High', color: 'text-orange-400', bgColor: 'bg-orange-500/20' };
    case 2: return { label: 'Medium', color: 'text-amber-400', bgColor: 'bg-amber-500/20' };
    default: return { label: 'Low', color: 'text-blue-400', bgColor: 'bg-blue-500/20' };
  }
}

/**
 * Dashboard widget showing maintenance alerts
 */
export function MaintenanceAlertsWidget({
  maxAlerts = 5,
  className = '',
}: MaintenanceAlertsWidgetProps) {
  const { 
    alerts, 
    isLoading, 
    error 
  } = useMaintenanceAlerts({ activeOnly: true });

  // Sort by severity (critical first) and take top N
  const topAlerts = React.useMemo(() => {
    return [...alerts]
      .sort((a, b) => b.severity - a.severity)
      .slice(0, maxAlerts);
  }, [alerts, maxAlerts]);

  const criticalCount = alerts.filter(a => a.severity >= 3).length;
  const totalCount = alerts.length;

  const subtitle = totalCount > 0 
    ? `${totalCount} alert${totalCount !== 1 ? 's' : ''} active${criticalCount > 0 ? ` • ${criticalCount} critical` : ''}`
    : 'All systems healthy';

  const emptyState = (
    <div className="text-center py-6">
      <CheckCircleIcon className="h-10 w-10 text-green-500 mx-auto mb-2" />
      <p className="text-sm text-pf-text-primary font-medium">No Active Alerts</p>
      <p className="text-xs text-pf-text-tertiary mt-1">
        Your fleet is running smoothly
      </p>
    </div>
  );

  return (
    <DashboardWidget
      title="Maintenance Alerts"
      subtitle={subtitle}
      icon={WrenchIcon}
      iconColorClass={criticalCount > 0 ? 'text-red-400' : 'text-pf-text-tertiary'}
      iconBgClass={criticalCount > 0 ? 'bg-red-500/20' : 'bg-pf-bg-2'}
      moreInfoLink="/maintenance"
      moreInfoText="View All"
      collapsible
      storageKey="maintenance-alerts-widget"
      hasContent={topAlerts.length > 0}
      emptyState={emptyState}
      className={className}
      isLoading={isLoading}
      error={error ? 'Failed to load maintenance alerts' : undefined}
    >
      <div className="space-y-2">
        {topAlerts.map((alert) => {
          const config = getSeverityConfig(alert.severity);
          return (
            <Link
              key={alert.id}
              to={`/maintenance?alert=${alert.id}`}
              className="block p-3 bg-pf-bg-1 rounded-lg border border-pf-border hover:bg-pf-border/30 transition-colors"
            >
              <div className="flex items-start gap-3">
                <div className={`w-2 h-2 rounded-full mt-1.5 flex-shrink-0 ${config.bgColor.replace('/20', '')}`} />
                <div className="flex-1 min-w-0">
                  <div className="flex items-center justify-between gap-2">
                    <p className="text-sm font-medium text-pf-text-primary truncate">
                      {alert.title}
                    </p>
                    <Badge variant={alert.severity >= 3 ? 'error' : 'warning'} className="text-xs flex-shrink-0">
                      {config.label}
                    </Badge>
                  </div>
                  <p className="text-xs text-pf-text-tertiary mt-0.5 truncate">
                    {alert.message}
                  </p>
                </div>
              </div>
            </Link>
          );
        })}

        {totalCount > maxAlerts && (
          <p className="text-xs text-center text-pf-text-tertiary py-2">
            +{totalCount - maxAlerts} more alert{totalCount - maxAlerts !== 1 ? 's' : ''}
          </p>
        )}
      </div>
    </DashboardWidget>
  );
}

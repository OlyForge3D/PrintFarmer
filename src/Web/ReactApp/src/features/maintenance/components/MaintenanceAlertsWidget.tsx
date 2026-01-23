/**
 * MaintenanceAlertsWidget Component
 * 
 * Compact widget for displaying maintenance alerts on the main dashboard.
 * Shows critical and high-priority alerts with quick action links.
 */

import React from 'react';
import { Link } from 'react-router-dom';
import { 
  WrenchIcon, 
  AlertIcon, 
  CheckCircleIcon,
  ChevronRightIcon
} from '@/common/components/icons/MdiIcons';
import { Badge, Button } from '@/common/components/ui';
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

  if (isLoading) {
    return (
      <div className={`bg-pf-panel border border-pf-border rounded-xl p-4 ${className}`}>
        <div className="flex items-center gap-3 mb-4">
          <div className="h-10 w-10 bg-pf-border rounded-lg animate-pulse" />
          <div>
            <div className="h-5 w-32 bg-pf-border rounded animate-pulse" />
            <div className="h-4 w-24 bg-pf-border rounded animate-pulse mt-1" />
          </div>
        </div>
        <div className="space-y-2">
          {Array.from({ length: 3 }).map((_, i) => (
            <div key={i} className="h-12 bg-pf-border/50 rounded-lg animate-pulse" />
          ))}
        </div>
      </div>
    );
  }

  if (error) {
    return (
      <div className={`bg-pf-panel border border-red-500/30 rounded-xl p-4 ${className}`}>
        <div className="flex items-center gap-3 text-red-400">
          <AlertIcon className="h-5 w-5" />
          <span className="text-sm">Failed to load maintenance alerts</span>
        </div>
      </div>
    );
  }

  return (
    <div className={`bg-pf-panel border border-pf-border rounded-xl overflow-hidden ${className}`}>
      {/* Header */}
      <div className="px-4 py-3 border-b border-pf-border flex items-center justify-between">
        <div className="flex items-center gap-3">
          <div className={`p-2 rounded-lg ${criticalCount > 0 ? 'bg-red-500/20' : 'bg-pf-bg-2'}`}>
            <WrenchIcon className={`h-5 w-5 ${criticalCount > 0 ? 'text-red-400' : 'text-pf-text-tertiary'}`} />
          </div>
          <div>
            <h3 className="font-semibold text-pf-text-primary text-sm">
              Maintenance Alerts
            </h3>
            <p className="text-xs text-pf-text-tertiary">
              {totalCount > 0 
                ? `${totalCount} alert${totalCount !== 1 ? 's' : ''} active${criticalCount > 0 ? ` • ${criticalCount} critical` : ''}`
                : 'All systems healthy'
              }
            </p>
          </div>
        </div>
        
        <Link to="/maintenance">
          <Button variant="subtle" size="sm">
            View All
            <ChevronRightIcon className="h-4 w-4 ml-1" />
          </Button>
        </Link>
      </div>

      {/* Alerts List */}
      <div className="p-3">
        {topAlerts.length === 0 ? (
          <div className="text-center py-6">
            <CheckCircleIcon className="h-10 w-10 text-green-500 mx-auto mb-2" />
            <p className="text-sm text-pf-text-primary font-medium">No Active Alerts</p>
            <p className="text-xs text-pf-text-tertiary mt-1">
              Your fleet is running smoothly
            </p>
          </div>
        ) : (
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
        )}
      </div>
    </div>
  );
}

import { useBasicHealth, useHealthStatus } from '@/common/hooks/useApi';
import { isDetailedHealthStatus } from '@/types/api';
import { AlertCircleIcon, CheckCircleIcon, XCircleIcon, ActivityIcon } from '@/common/components/icons/MdiIcons';
import { SystemHealthSkeleton } from '@/common/components/skeletons/SystemHealthSkeleton';
import { DashboardWidget } from '@/common/components/DashboardWidget';


export function SystemHealth() {
  const { data: basic, isLoading, error } = useBasicHealth();

  if (isLoading) return <SystemHealthSkeleton compact />;

  if (error || !basic) {
    return (
      <div className="flex items-center space-x-2">
        <XCircleIcon className="h-4 w-4 text-pf-error" />
        <span className="text-xs text-pf-error-text">System Offline</span>
      </div>
    );
  }

  const isHealthy = basic.status === 'ok';

  return (
    <div className="flex items-center space-x-2">
      {isHealthy ? (
        <CheckCircleIcon className="h-4 w-4 text-pf-success" />
      ) : (
        <AlertCircleIcon className="h-4 w-4 text-pf-warning" />
      )}
      <span className={`text-xs ${isHealthy ? 'text-pf-success' : 'text-pf-warning-text'}`}>
        System {isHealthy ? 'Healthy' : 'Warning'}
      </span>
    </div>
  );
}

interface DetailedSystemHealthProps {
  className?: string;
}

export function DetailedSystemHealth({ className }: DetailedSystemHealthProps) {
  const { data: health, isLoading, error } = useHealthStatus();
  const detailedHealth = isDetailedHealthStatus(health) ? health : undefined;

  // Convert numeric health status enum to string
  // Backend returns: 0 = Unhealthy, 1 = Degraded, 2 = Healthy
  const normalizeHealthStatus = (status: string | number): string => {
    if (typeof status === 'number') {
      switch (status) {
        case 2: return 'Healthy';
        case 1: return 'Degraded';
        case 0: return 'Unhealthy';
        default: return 'Unknown';
      }
    }
    return status;
  };

  const overallStatus = detailedHealth ? normalizeHealthStatus(detailedHealth.status ?? 'Unknown') : 'Unknown';
  const isHealthy = overallStatus === 'Healthy';
  const isWarning = overallStatus === 'Degraded' || overallStatus === 'Warning';

  const renderHealthStatus = (status: string | number, title: string) => {
    const normalizedStatus = normalizeHealthStatus(status);
    const healthy = normalizedStatus === 'Healthy';
    const warning = normalizedStatus === 'Degraded' || normalizedStatus === 'Warning';

    let icon, colorClass;
    if (healthy) {
      icon = <CheckCircleIcon className="h-5 w-5 text-white" />;
      colorClass = 'text-white bg-pf-success-bg';
    } else if (warning) {
      icon = <AlertCircleIcon className="h-5 w-5 text-white" />;
      colorClass = 'text-white bg-pf-warning';
    } else {
      icon = <XCircleIcon className="h-5 w-5 text-white" />;
      colorClass = 'text-white bg-pf-error-bg';
    }

    return (
      <div className={`flex items-center justify-between p-3 rounded-lg ${colorClass}`}>
        <div className="flex items-center space-x-2">
          {icon}
          <span className="text-sm font-medium">{title}</span>
        </div>
        <span className="text-xs font-semibold">{normalizedStatus}</span>
      </div>
    );
  };

  const emptyState = (
    <div className="flex items-center space-x-3 p-4 bg-pf-error-bg rounded-lg">
      <XCircleIcon className="h-6 w-6 text-pf-error" />
      <div>
        <p className="text-sm font-medium text-pf-error-text">Unable to check system health</p>
        <p className="text-xs text-pf-error-text">API server may be offline</p>
      </div>
    </div>
  );

  return (
    <DashboardWidget
      title="System Health"
      icon={ActivityIcon}
      iconColorClass={isHealthy ? 'text-pf-success' : isWarning ? 'text-pf-warning' : 'text-pf-error-text'}
      iconBgClass={isHealthy ? 'bg-pf-success-bg' : isWarning ? 'bg-pf-warning-bg' : 'bg-pf-error-bg'}
      collapsible
      storageKey="system-health-widget"
      hasContent={!!detailedHealth}
      emptyState={emptyState}
      isLoading={isLoading}
      error={error ? 'Unable to check system health' : undefined}
      className={className}
    >
      <div className="space-y-3">
        {/* Overall Status */}
        {detailedHealth && renderHealthStatus(detailedHealth.status ?? 'Unknown', 'Overall System')}

        {/* Database Status */}
        {detailedHealth?.results?.Database && renderHealthStatus(detailedHealth.results.Database.status ?? 'Unknown', 'Database')}

        {/* SignalR Status */}
        {detailedHealth?.results?.SignalRHub && renderHealthStatus(detailedHealth.results.SignalRHub.status ?? 'Unknown', 'SignalR Hub')}

        {/* Additional health checks */}
        {detailedHealth?.results && Object.entries(detailedHealth.results)
          .filter(([key]) => !['Database', 'SignalRHub'].includes(key))
          .map(([key, value]) => (
            <div key={key}>
              {renderHealthStatus(value.status ?? 'Unknown', key)}
            </div>
          ))}
      </div>

      {/* Last Updated */}
      <div className="mt-4 pt-3 border-t border-pf-border">
        <p className="text-xs text-pf-text-secondary">
          Last updated: {new Date().toLocaleTimeString()}
        </p>
      </div>
    </DashboardWidget>
  );
}
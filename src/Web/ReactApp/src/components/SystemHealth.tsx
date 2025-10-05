import { useBasicHealth, useHealthStatus } from '@/hooks/useApi';
import { isDetailedHealthStatus } from '@/types/api';
import { AlertCircle, CheckCircle, XCircle } from 'lucide-react';
import { SystemHealthSkeleton } from './skeletons/SystemHealthSkeleton';


export function SystemHealth() {
  const { data: basic, isLoading, error } = useBasicHealth();

  if (isLoading) return <SystemHealthSkeleton compact />;

  if (error || !basic) {
    return (
      <div className="flex items-center space-x-2">
        <XCircle className="h-4 w-4 text-pf-error" />
        <span className="text-xs text-pf-error-text">System Offline</span>
      </div>
    );
  }

  const isHealthy = basic.status === 'ok';

  return (
    <div className="flex items-center space-x-2">
      {isHealthy ? (
        <CheckCircle className="h-4 w-4 text-pf-success" />
      ) : (
        <AlertCircle className="h-4 w-4 text-pf-warning" />
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

export function DetailedSystemHealth({ className = '' }: DetailedSystemHealthProps) {
  const { data: health, isLoading, error } = useHealthStatus();
  const detailedHealth = isDetailedHealthStatus(health) ? health : undefined;

  if (isLoading) return <SystemHealthSkeleton className={className} />;

  if (error || !detailedHealth) {
    return (
      <div className={`bg-pf-bg-1 rounded-lg shadow p-4 ${className}`}>
        <h3 className="text-lg font-medium mb-4 text-pf-text-primary">System Health</h3>
        <div className="flex items-center space-x-3 p-4 bg-pf-error-bg rounded-lg">
          <XCircle className="h-6 w-6 text-pf-error" />
          <div>
            <p className="text-sm font-medium text-pf-error-text">Unable to check system health</p>
            <p className="text-xs text-pf-error-text">API server may be offline</p>
          </div>
        </div>
      </div>
    );
  }

  const renderHealthStatus = (status: string, title: string) => {
    const isHealthy = status === 'Healthy';
    const isWarning = status === 'Degraded' || status === 'Warning';

    let icon, colorClass;
    if (isHealthy) {
      icon = <CheckCircle className="h-5 w-5 text-pf-success" />;
      colorClass = 'text-pf-success bg-pf-success-bg';
    } else if (isWarning) {
      icon = <AlertCircle className="h-5 w-5 text-pf-warning" />;
      colorClass = 'text-pf-warning-text bg-pf-warning';
    } else {
      icon = <XCircle className="h-5 w-5 text-pf-error" />;
      colorClass = 'text-pf-error-text bg-pf-error-bg';
    }

    return (
      <div className={`flex items-center justify-between p-3 rounded-lg ${colorClass}`}>
        <div className="flex items-center space-x-2">
          {icon}
          <span className="text-sm font-medium">{title}</span>
        </div>
        <span className="text-xs font-semibold">{status}</span>
      </div>
    );
  };

  return (
    <div className={`bg-pf-bg-1 rounded-lg shadow p-6 ${className}`}>
      <h3 className="text-lg font-medium mb-4 text-pf-text-primary">System Health</h3>

      <div className="space-y-3">
        {/* Overall Status */}
        {renderHealthStatus(detailedHealth.status ?? 'Unknown', 'Overall System')}

        {/* Database Status */}
        {detailedHealth.results?.Database && renderHealthStatus(detailedHealth.results.Database.status ?? 'Unknown', 'Database')}

        {/* SignalR Status */}
        {detailedHealth.results?.SignalRHub && renderHealthStatus(detailedHealth.results.SignalRHub.status ?? 'Unknown', 'SignalR Hub')}

        {/* Additional health checks */}
        {detailedHealth.results && Object.entries(detailedHealth.results)
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
    </div>
  );
}
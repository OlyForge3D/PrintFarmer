import { useHealthStatus, useBasicHealth } from '@/hooks/useApi';
import { CheckCircle, AlertCircle, XCircle, Loader } from 'lucide-react';

// Narrow typing helpers for dynamic health check structure
type HealthCheckEntry = { status?: string; [key: string]: unknown };
type HealthChecks = Record<string, HealthCheckEntry>;

export function SystemHealth() {
  const { data: health, isLoading, error } = useBasicHealth();

  if (isLoading) {
    return (
      <div className="flex items-center space-x-2">
        <Loader className="h-4 w-4 animate-spin text-gray-500" />
        <span className="text-xs text-gray-500">Checking...</span>
      </div>
    );
  }

  if (error || !health) {
    return (
      <div className="flex items-center space-x-2">
        <XCircle className="h-4 w-4 text-red-500" />
        <span className="text-xs text-red-600">System Offline</span>
      </div>
    );
  }

  const isHealthy = health.status === 'ok';

  return (
    <div className="flex items-center space-x-2">
      {isHealthy ? (
        <CheckCircle className="h-4 w-4 text-green-500" />
      ) : (
        <AlertCircle className="h-4 w-4 text-yellow-500" />
      )}
      <span className={`text-xs ${isHealthy ? 'text-green-600' : 'text-yellow-600'}`}>
        System {isHealthy ? 'Healthy' : 'Warning'}
      </span>
    </div>
  );
}

interface DetailedSystemHealthProps {
  className?: string;
}

export function DetailedSystemHealth({ className = '' }: DetailedSystemHealthProps) {
  const { data: detailedHealth, isLoading, error } = useHealthStatus();

  if (isLoading) {
    return (
      <div className={`bg-white rounded-lg shadow p-4 ${className}`}>
        <h3 className="text-lg font-medium mb-4">System Health</h3>
        <div className="flex items-center justify-center py-8">
          <Loader className="h-8 w-8 animate-spin text-gray-500" />
        </div>
      </div>
    );
  }

  if (error || !detailedHealth) {
    return (
      <div className={`bg-white rounded-lg shadow p-4 ${className}`}>
        <h3 className="text-lg font-medium mb-4">System Health</h3>
        <div className="flex items-center space-x-3 p-4 bg-red-50 rounded-lg">
          <XCircle className="h-6 w-6 text-red-500" />
          <div>
            <p className="text-sm font-medium text-red-800">Unable to check system health</p>
            <p className="text-xs text-red-600">API server may be offline</p>
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
      icon = <CheckCircle className="h-5 w-5 text-green-500" />;
      colorClass = 'text-green-800 bg-green-50';
    } else if (isWarning) {
      icon = <AlertCircle className="h-5 w-5 text-yellow-500" />;
      colorClass = 'text-yellow-800 bg-yellow-50';
    } else {
      icon = <XCircle className="h-5 w-5 text-red-500" />;
      colorClass = 'text-red-800 bg-red-50';
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
    <div className={`bg-white rounded-lg shadow p-6 ${className}`}>
      <h3 className="text-lg font-medium mb-4">System Health</h3>
      
      <div className="space-y-3">
        {/* Overall Status */}
  {renderHealthStatus(String(detailedHealth.status || 'Unknown'), 'Overall System')}
        
        {/* Database Status */}
        {(() => {
          const checks = detailedHealth.checks as HealthChecks | undefined;
          const dbStatus = checks?.database?.status;
          return dbStatus ? renderHealthStatus(String(dbStatus), 'Database') : null;
        })()}
        
        {/* SignalR Status */}
        {(() => {
          const checks = detailedHealth.checks as HealthChecks | undefined;
            const sigStatus = checks?.signalr?.status;
            return sigStatus ? renderHealthStatus(String(sigStatus), 'Real-time Updates') : null;
        })()}
        
        {/* Additional health checks */}
        {(() => {
          const checks = detailedHealth.checks as HealthChecks | undefined;
          if (!checks) return null;
          return Object.entries(checks)
            .filter(([key]) => !['database', 'signalr'].includes(key))
            .map(([key, value]) => (
              <div key={key}>
                {renderHealthStatus(String(value.status || 'Unknown'), key.charAt(0).toUpperCase() + key.slice(1))}
              </div>
            ));
        })()}
      </div>

      {/* Last Updated */}
      <div className="mt-4 pt-3 border-t border-gray-200">
        <p className="text-xs text-gray-500">
          Last updated: {new Date().toLocaleTimeString()}
        </p>
      </div>
    </div>
  );
}
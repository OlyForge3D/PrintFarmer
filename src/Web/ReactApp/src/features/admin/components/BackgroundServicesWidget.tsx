/**
 * BackgroundServicesWidget
 *
 * Dashboard widget showing the status of background services.
 * Displays running/stopped indicators and error states.
 */

import React from 'react';
import { useQuery } from '@tanstack/react-query';
import { apiClient } from '@/services/api';
import {
  CheckCircleIcon,
  AlertCircleIcon,
  RefreshIcon,
  GearIcon,
  PlayIcon,
  PauseIcon,
} from '@/common/components/icons/MdiIcons';
import { Button } from '@/common/components/ui';
import { DashboardWidget } from '@/common/components/DashboardWidget';
import type { BackgroundServiceStatus, BackgroundServicesSummary } from '@/types/api';
import { formatDistanceToNow } from 'date-fns';

export interface BackgroundServicesWidgetProps {
  /** Whether to show the compact summary only */
  compact?: boolean;
  /** Maximum number of services to show in detailed view */
  maxServices?: number;
}

/**
 * Get color classes based on service status
 */
function getStatusClasses(service: BackgroundServiceStatus): {
  bgClass: string;
  textClass: string;
  borderClass: string;
} {
  if (!service.isEnabled) {
    return {
      bgClass: 'bg-pf-border-medium/20',
      textClass: 'text-pf-text-tertiary',
      borderClass: 'border-pf-border-medium',
    };
  }

  if (service.lastError) {
    return {
      bgClass: 'bg-pf-error-bg',
      textClass: 'text-pf-error-text',
      borderClass: 'border-pf-error-border',
    };
  }

  if (service.isRunning) {
    return {
      bgClass: 'bg-pf-status-online-bg',
      textClass: 'text-pf-status-online-text',
      borderClass: 'border-pf-status-online-border',
    };
  }

  return {
    bgClass: 'bg-pf-warning-bg',
    textClass: 'text-pf-warning-text',
    borderClass: 'border-pf-warning-border',
  };
}

/**
 * Get icon based on service status
 */
function getStatusIcon(service: BackgroundServiceStatus): React.ReactNode {
  if (!service.isEnabled) {
    return <PauseIcon className="h-4 w-4 text-pf-text-tertiary" aria-label="Disabled" />;
  }

  if (service.lastError) {
    return <AlertCircleIcon className="h-4 w-4 text-pf-error-text" aria-label="Error" />;
  }

  if (service.isRunning) {
    return <CheckCircleIcon className="h-4 w-4 text-pf-status-online-text" aria-label="Running" />;
  }

  return <PauseIcon className="h-4 w-4 text-pf-warning-text" aria-label="Stopped" />;
}

/**
 * Format last run time as relative time
 */
function formatLastRun(lastRunTime?: string): string {
  if (!lastRunTime) return 'Never';
  try {
    return formatDistanceToNow(new Date(lastRunTime), { addSuffix: true });
  } catch {
    return 'Unknown';
  }
}

/**
 * Individual service status row
 */
function ServiceStatusRow({ service }: { service: BackgroundServiceStatus }) {
  const { bgClass, textClass, borderClass } = getStatusClasses(service);

  return (
    <div
      className={`flex items-center justify-between p-3 rounded-lg border ${bgClass} ${borderClass}`}
    >
      <div className="flex items-center gap-3 min-w-0">
        {getStatusIcon(service)}
        <div className="min-w-0">
          <p className={`text-sm font-medium truncate ${textClass}`}>
            {service.displayName}
          </p>
          {service.description && (
            <p className="text-xs text-pf-text-tertiary truncate">{service.description}</p>
          )}
        </div>
      </div>
      <div className="flex items-center gap-2 shrink-0 ml-2">
        {service.lastError ? (
          <span
            className="text-xs text-pf-error-text max-w-32 truncate"
            title={service.lastError}
          >
            Error
          </span>
        ) : (
          <span className="text-xs text-pf-text-tertiary">
            {formatLastRun(service.lastRunTime)}
          </span>
        )}
        {service.category && (
          <span className="px-2 py-0.5 text-xs rounded-full bg-pf-bg-2 text-pf-text-secondary">
            {service.category}
          </span>
        )}
      </div>
    </div>
  );
}

/**
 * Summary stats display
 */
function SummaryStats({ summary }: { summary: BackgroundServicesSummary }) {
  const hasErrors = summary.servicesWithErrors > 0;
  const allRunning = summary.runningServices === summary.enabledServices;

  return (
    <div className="grid grid-cols-3 gap-4 mb-4">
      <div className="text-center p-2 bg-pf-bg-2 rounded-lg">
        <p className="text-2xl font-bold text-pf-text-primary">{summary.totalServices}</p>
        <p className="text-xs text-pf-text-tertiary">Total</p>
      </div>
      <div
        className={`text-center p-2 rounded-lg ${
          allRunning ? 'bg-pf-status-online-bg' : 'bg-pf-warning-bg'
        }`}
      >
        <p
          className={`text-2xl font-bold ${
            allRunning ? 'text-pf-status-online-text' : 'text-pf-warning-text'
          }`}
        >
          {summary.runningServices}
        </p>
        <p className="text-xs text-pf-text-tertiary">Running</p>
      </div>
      <div
        className={`text-center p-2 rounded-lg ${
          hasErrors ? 'bg-pf-error-bg' : 'bg-pf-bg-2'
        }`}
      >
        <p
          className={`text-2xl font-bold ${
            hasErrors ? 'text-pf-error-text' : 'text-pf-text-primary'
          }`}
        >
          {summary.servicesWithErrors}
        </p>
        <p className="text-xs text-pf-text-tertiary">Errors</p>
      </div>
    </div>
  );
}

/**
 * Background Services Status Widget
 */
export function BackgroundServicesWidget({
  compact = false,
  maxServices = 10,
}: BackgroundServicesWidgetProps) {
  // Fetch services list
  const {
    data: services,
    isLoading: servicesLoading,
    error: servicesError,
    refetch,
  } = useQuery({
    queryKey: ['background-services'],
    queryFn: () => apiClient.getBackgroundServices(),
    refetchInterval: 30000, // Refresh every 30 seconds
    staleTime: 15000,
  });

  // Fetch summary
  const { data: summary, isLoading: summaryLoading } = useQuery({
    queryKey: ['background-services-summary'],
    queryFn: () => apiClient.getBackgroundServicesSummary(),
    refetchInterval: 30000,
    staleTime: 15000,
  });

  const isLoading = servicesLoading || summaryLoading;
  const error = servicesError;

  // Sort services: errors first, then by running status, then by name
  const sortedServices = React.useMemo(() => {
    if (!services) return [];
    return [...services].sort((a, b) => {
      // Errors first
      if (a.lastError && !b.lastError) return -1;
      if (!a.lastError && b.lastError) return 1;
      // Then disabled last
      if (!a.isEnabled && b.isEnabled) return 1;
      if (a.isEnabled && !b.isEnabled) return -1;
      // Then by name
      return a.displayName.localeCompare(b.displayName);
    });
  }, [services]);

  const displayedServices = sortedServices.slice(0, maxServices);
  const hasMore = sortedServices.length > maxServices;

  const hasErrors = summary?.servicesWithErrors ?? 0 > 0;
  const allRunning = summary ? summary.runningServices === summary.enabledServices : true;

  const emptyState = (
    <div className="text-center py-6">
      <GearIcon className="h-8 w-8 text-pf-text-tertiary mx-auto mb-2" />
      <p className="text-sm text-pf-text-tertiary">No background services registered</p>
    </div>
  );

  const headerAction = (
    <Button
      variant="subtle"
      size="sm"
      onClick={() => refetch()}
      disabled={isLoading}
      className="p-1.5 rounded-lg"
      aria-label="Refresh services"
    >
      <RefreshIcon
        className={`h-4 w-4 text-pf-text-secondary ${isLoading ? 'animate-spin' : ''}`}
      />
    </Button>
  );

  return (
    <DashboardWidget
      title="Background Services"
      icon={GearIcon}
      iconColorClass={hasErrors ? 'text-pf-error-text' : allRunning ? 'text-pf-status-online-text' : 'text-pf-loading'}
      iconBgClass={hasErrors ? 'bg-pf-error-bg' : allRunning ? 'bg-pf-status-online-bg' : 'bg-pf-bg-2'}
      collapsible
      storageKey="background-services-widget"
      hasContent={displayedServices.length > 0 || (summary !== undefined)}
      emptyState={emptyState}
      isLoading={isLoading}
      error={error ? 'Failed to load services status' : undefined}
      headerAction={headerAction}
    >
      {/* Summary Stats */}
      {summary && <SummaryStats summary={summary} />}

      {/* Services List (unless compact) */}
      {!compact && displayedServices.length > 0 && (
        <div className="space-y-2">
          {displayedServices.map((service) => (
            <ServiceStatusRow key={service.serviceId} service={service} />
          ))}
          {hasMore && (
            <p className="text-xs text-pf-text-tertiary text-center py-2">
              +{sortedServices.length - maxServices} more services
            </p>
          )}
        </div>
      )}

      {/* Quick Status Indicator for Compact Mode */}
      {compact && summary && (
        <div className="flex items-center justify-center gap-2 mt-2">
          {summary.servicesWithErrors > 0 ? (
            <>
              <AlertCircleIcon className="h-4 w-4 text-pf-error-text" />
              <span className="text-sm text-pf-error-text">
                {summary.servicesWithErrors} service{summary.servicesWithErrors !== 1 ? 's' : ''} with errors
              </span>
            </>
          ) : summary.runningServices === summary.enabledServices ? (
            <>
              <CheckCircleIcon className="h-4 w-4 text-pf-status-online-text" />
              <span className="text-sm text-pf-status-online-text">All services operational</span>
            </>
          ) : (
            <>
              <PlayIcon className="h-4 w-4 text-pf-warning-text" />
              <span className="text-sm text-pf-warning-text">
                {summary.runningServices}/{summary.enabledServices} services running
              </span>
            </>
          )}
        </div>
      )}
    </DashboardWidget>
  );
}

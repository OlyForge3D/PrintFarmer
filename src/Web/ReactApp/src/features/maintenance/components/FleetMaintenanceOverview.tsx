/**
 * FleetMaintenanceOverview Component
 * 
 * Displays fleet-wide maintenance statistics in a card grid layout.
 * Shows:
 * - Total printers & online count
 * - Printers needing attention
 * - Printers in maintenance mode
 * - Alert breakdown by severity (Critical/High/Medium/Low)
 */

import React from 'react';
import { 
  AlertCircleIcon, 
  CheckCircleIcon, 
  PrinterIcon, 
  WrenchIcon,
  AlertIcon 
} from '@/common/components/icons/MdiIcons';
import type { FleetMaintenanceStats } from '../hooks/useMaintenanceStats';

interface StatsCardProps {
  title: string;
  value: number | string;
  subtitle?: string;
  icon: React.ComponentType<{ className?: string }>;
  variant: 'default' | 'success' | 'warning' | 'error' | 'info';
}

const variantStyles: Record<StatsCardProps['variant'], string> = {
  default: 'bg-pf-border-medium text-pf-text-secondary',
  success: 'bg-pf-success/20 text-pf-success',
  warning: 'bg-pf-warning/10 text-pf-warning',
  error: 'bg-pf-error/10 text-pf-error',
  info: 'bg-pf-accent-bg/15 text-pf-accent',
};

function StatsCard({ title, value, subtitle, icon: Icon, variant }: StatsCardProps) {
  return (
    <div className="bg-pf-bg-1 border border-pf-border rounded-xl p-5 shadow-lg">
      <div className="flex items-center gap-4">
        <div className={`p-3 rounded-lg ${variantStyles[variant]}`}>
          <Icon className="h-6 w-6" aria-hidden="true" />
        </div>
        <div className="flex-1 min-w-0">
          <p className="text-sm font-medium text-pf-text-tertiary uppercase tracking-wide truncate">
            {title}
          </p>
          <p className="text-2xl font-bold text-pf-text-primary">{value}</p>
          {subtitle && (
            <p className="text-xs text-pf-text-tertiary mt-1">{subtitle}</p>
          )}
        </div>
      </div>
    </div>
  );
}

interface SeverityBadgeProps {
  label: string;
  count: number;
  variant: 'critical' | 'high' | 'medium' | 'low';
}

const severityColors: Record<SeverityBadgeProps['variant'], string> = {
  critical: 'bg-pf-error/10 text-pf-error border-pf-error/30',
  high: 'bg-pf-warning/10 text-pf-warning border-pf-warning/30',
  medium: 'bg-pf-warning/10 text-pf-warning border-pf-warning/30',
  low: 'bg-pf-accent-bg/15 text-pf-accent border-pf-accent/30',
};

function SeverityBadge({ label, count, variant }: SeverityBadgeProps) {
  return (
    <div 
      className={`flex items-center justify-between px-4 py-3 rounded-lg border ${severityColors[variant]}`}
    >
      <span className="text-sm font-medium">{label}</span>
      <span className="text-lg font-bold">{count}</span>
    </div>
  );
}

export interface FleetMaintenanceOverviewProps {
  /** Fleet statistics data */
  stats: FleetMaintenanceStats | null;
  /** Whether data is loading */
  isLoading?: boolean;
  /** Error message to display */
  error?: Error | null;
  /** Additional CSS classes */
  className?: string;
}

/**
 * Fleet-wide maintenance overview with statistics cards
 */
export function FleetMaintenanceOverview({ 
  stats, 
  isLoading, 
  error,
  className = '' 
}: FleetMaintenanceOverviewProps) {
  if (isLoading) {
    return (
      <div className={`${className}`}>
        <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-4 gap-4 mb-6">
          {[1, 2, 3, 4].map((i) => (
            <div 
              key={i} 
              className="bg-pf-bg-1 border border-pf-border rounded-xl p-5 animate-pulse"
            >
              <div className="flex items-center gap-4">
                <div className="w-12 h-12 bg-pf-border rounded-lg" />
                <div className="flex-1">
                  <div className="h-4 bg-pf-border rounded-sm w-24 mb-2" />
                  <div className="h-6 bg-pf-border rounded-sm w-16" />
                </div>
              </div>
            </div>
          ))}
        </div>
      </div>
    );
  }

  if (error) {
    return (
      <div className={`bg-pf-error/10 border border-pf-error/30 rounded-xl p-6 ${className}`}>
        <div className="flex items-center gap-3">
          <AlertCircleIcon className="h-6 w-6 text-pf-error" aria-hidden="true" />
          <div>
            <p className="font-semibold text-pf-error">Failed to load maintenance statistics</p>
            <p className="text-sm text-pf-error/80">{error.message}</p>
          </div>
        </div>
      </div>
    );
  }

  if (!stats) {
    return (
      <div className={`bg-pf-bg-1 border border-pf-border rounded-xl p-6 text-center ${className}`}>
        <PrinterIcon className="h-12 w-12 mx-auto text-pf-text-tertiary mb-3" aria-hidden="true" />
        <p className="text-pf-text-secondary">No maintenance data available</p>
      </div>
    );
  }

  const hasAlerts = stats.totalActiveAlerts > 0;

  return (
    <div className={className}>
      {/* Main Statistics Cards */}
      <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-4 gap-4 mb-6">
        <StatsCard
          title="Total Printers"
          value={stats.totalPrinters}
          subtitle={`${stats.printersOnline} online`}
          icon={PrinterIcon}
          variant="default"
        />
        <StatsCard
          title="Printers Online"
          value={stats.printersOnline}
          subtitle={`${Math.round((stats.printersOnline / Math.max(stats.totalPrinters, 1)) * 100)}% of fleet`}
          icon={CheckCircleIcon}
          variant="success"
        />
        <StatsCard
          title="Needing Attention"
          value={stats.printersNeedingAttention}
          subtitle={hasAlerts ? `${stats.totalActiveAlerts} active alerts` : 'All clear'}
          icon={hasAlerts ? AlertIcon : CheckCircleIcon}
          variant={stats.printersNeedingAttention > 0 ? 'warning' : 'success'}
        />
        <StatsCard
          title="In Maintenance"
          value={stats.printersInMaintenance}
          subtitle="Currently being serviced"
          icon={WrenchIcon}
          variant={stats.printersInMaintenance > 0 ? 'info' : 'default'}
        />
      </div>

      {/* Alert Severity Breakdown */}
      <div className="bg-pf-bg-1 border border-pf-border rounded-xl p-5">
        <h3 className="text-sm font-semibold text-pf-text-primary uppercase tracking-wide mb-4">
          Alert Severity Breakdown
        </h3>
        <div className="grid grid-cols-2 md:grid-cols-4 gap-3">
          <SeverityBadge label="Critical" count={stats.alertsBySeveity.critical} variant="critical" />
          <SeverityBadge label="High" count={stats.alertsBySeveity.high} variant="high" />
          <SeverityBadge label="Medium" count={stats.alertsBySeveity.medium} variant="medium" />
          <SeverityBadge label="Low" count={stats.alertsBySeveity.low} variant="low" />
        </div>
      </div>
    </div>
  );
}

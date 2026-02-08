/**
 * MaintenancePriorityList Component
 * 
 * Displays a priority-sorted list of maintenance alerts with quick actions.
 * Features:
 * - Alerts sorted by severity (critical first)
 * - Quick actions: Acknowledge, Resolve, Dismiss
 * - Relative time display
 * - Expandable details
 */

import React, { useState } from 'react';
import { toast } from 'sonner';
import { formatDistanceToNow } from 'date-fns';
import { 
  AlertCircleIcon, 
  CheckCircleIcon,
  ChevronDownIcon,
  PrinterIcon,
  WrenchIcon
} from '@/common/components/icons/MdiIcons';
import { Button } from '@/common/components/ui';
import { maintenanceService } from '@/services/maintenanceService';
import type { MaintenanceAlert } from '@/types/maintenance';
import { MaintenanceAlertStatus } from '@/types/maintenance';

export interface MaintenancePriorityListProps {
  /** Alerts to display */
  alerts: MaintenanceAlert[];
  /** Whether data is loading */
  isLoading?: boolean;
  /** Callback to refresh data after actions */
  onActionComplete?: () => void;
  /** Maximum items to show (optional) */
  maxItems?: number;
  /** Show compact view */
  compact?: boolean;
  /** Additional CSS classes */
  className?: string;
}

interface AlertItemProps {
  alert: MaintenanceAlert;
  compact?: boolean;
  onActionComplete?: () => void;
}

const severityConfig: Record<number, { 
  label: string; 
  bgColor: string; 
  textColor: string; 
  borderColor: string;
  icon: React.ComponentType<{ className?: string }>;
}> = {
  4: { 
    label: 'Critical', 
    bgColor: 'bg-red-500/10', 
    textColor: 'text-red-400', 
    borderColor: 'border-red-500/30',
    icon: AlertCircleIcon 
  },
  3: { 
    label: 'High', 
    bgColor: 'bg-orange-500/10', 
    textColor: 'text-orange-400', 
    borderColor: 'border-orange-500/30',
    icon: AlertCircleIcon 
  },
  2: { 
    label: 'Medium', 
    bgColor: 'bg-amber-500/10', 
    textColor: 'text-amber-400', 
    borderColor: 'border-amber-500/30',
    icon: AlertCircleIcon 
  },
  1: { 
    label: 'Low', 
    bgColor: 'bg-blue-500/10', 
    textColor: 'text-blue-400', 
    borderColor: 'border-blue-500/30',
    icon: AlertCircleIcon 
  },
};

function AlertItem({ alert, compact, onActionComplete }: AlertItemProps) {
  const [isExpanded, setIsExpanded] = useState(false);
  const [isActioning, setIsActioning] = useState<'acknowledge' | 'resolve' | 'dismiss' | null>(null);
  
  const config = severityConfig[alert.severity] || severityConfig[2];
  const SeverityIcon = config.icon;
  const isAcknowledged = alert.status === MaintenanceAlertStatus.Acknowledged;

  const handleAcknowledge = async () => {
    if (isActioning) return;
    setIsActioning('acknowledge');
    try {
      await maintenanceService.acknowledgeAlert(alert.id, { acknowledgedBy: 'Current User' });
      toast.success('Alert acknowledged');
      onActionComplete?.();
    } catch (err) {
      toast.error('Failed to acknowledge alert');
      console.error('Acknowledge error:', err);
    } finally {
      setIsActioning(null);
    }
  };

  const handleResolve = async () => {
    if (isActioning) return;
    setIsActioning('resolve');
    try {
      await maintenanceService.resolveAlert(alert.id, { 
        performedBy: 'Current User',
        notes: 'Resolved from maintenance dashboard'
      });
      toast.success('Alert resolved & maintenance logged');
      onActionComplete?.();
    } catch (err) {
      toast.error('Failed to resolve alert');
      console.error('Resolve error:', err);
    } finally {
      setIsActioning(null);
    }
  };

  const handleDismiss = async () => {
    if (isActioning) return;
    setIsActioning('dismiss');
    try {
      await maintenanceService.dismissAlert(alert.id, { 
        dismissedBy: 'Current User',
        reason: 'Dismissed from maintenance dashboard'
      });
      toast.success('Alert dismissed');
      onActionComplete?.();
    } catch (err) {
      toast.error('Failed to dismiss alert');
      console.error('Dismiss error:', err);
    } finally {
      setIsActioning(null);
    }
  };

  const timeAgo = formatDistanceToNow(new Date(alert.createdAt), { addSuffix: true });

  return (
    <div 
      className={`
        ${config.bgColor} border ${config.borderColor} rounded-lg overflow-hidden
        transition-all duration-200
      `}
    >
      {/* Main row */}
      <div className="p-4">
        <div className="flex items-start gap-3">
          {/* Severity icon */}
          <div className="shrink-0 pt-0.5">
            <SeverityIcon className={`h-5 w-5 ${config.textColor}`} aria-hidden="true" />
          </div>

          {/* Content */}
          <div className="flex-1 min-w-0">
            <div className="flex items-start justify-between gap-2">
              <div className="min-w-0">
                <h4 className="font-medium text-pf-text-primary truncate">
                  {alert.title}
                </h4>
                <p className="text-sm text-pf-text-secondary line-clamp-2">
                  {alert.message}
                </p>
              </div>
              
              {/* Severity badge */}
              <span className={`
                shrink-0 px-2 py-0.5 text-xs font-medium rounded
                ${config.bgColor} ${config.textColor} border ${config.borderColor}
              `}>
                {config.label}
              </span>
            </div>

            {/* Meta info */}
            <div className="flex items-center gap-4 mt-2 text-xs text-pf-text-tertiary">
              <span className="flex items-center gap-1">
                <PrinterIcon className="h-3 w-3" aria-hidden="true" />
                Printer
              </span>
              <span>{timeAgo}</span>
              {isAcknowledged && (
                <span className="flex items-center gap-1 text-blue-400">
                  <CheckCircleIcon className="h-3 w-3" aria-hidden="true" />
                  Acknowledged
                </span>
              )}
            </div>

            {/* Quick actions */}
            {!compact && (
              <div className="flex items-center gap-2 mt-3">
                {!isAcknowledged && (
                  <Button
                    variant="secondary"
                    size="sm"
                    onClick={handleAcknowledge}
                    disabled={!!isActioning}
                    className="text-xs"
                  >
                    {isActioning === 'acknowledge' ? 'Acknowledging...' : 'Acknowledge'}
                  </Button>
                )}
                <Button
                  variant="secondary"
                  size="sm"
                  onClick={handleResolve}
                  disabled={!!isActioning}
                  className="text-xs text-emerald-400 hover:text-emerald-300"
                >
                  <WrenchIcon className="h-3 w-3 mr-1" aria-hidden="true" />
                  {isActioning === 'resolve' ? 'Resolving...' : 'Resolve'}
                </Button>
                <Button
                  variant="subtle"
                  size="sm"
                  onClick={handleDismiss}
                  disabled={!!isActioning}
                  className="text-xs text-pf-text-tertiary hover:text-pf-text-secondary"
                >
                  {isActioning === 'dismiss' ? 'Dismissing...' : 'Dismiss'}
                </Button>
              </div>
            )}
          </div>

          {/* Expand button for compact mode */}
          {compact && (
            <Button
              variant="subtle"
              type="button"
              onClick={() => setIsExpanded(!isExpanded)}
              className="p-1"
              aria-label={isExpanded ? 'Collapse' : 'Expand'}
            >
              <ChevronDownIcon 
                className={`h-4 w-4 text-pf-text-tertiary transition-transform ${isExpanded ? 'rotate-180' : ''}`} 
                aria-hidden="true"
              />
            </Button>
          )}
        </div>
      </div>

      {/* Expanded content for compact mode */}
      {compact && isExpanded && (
        <div className="px-4 pb-4 pt-0 border-t border-pf-border/50">
          <div className="flex items-center gap-2 mt-3">
            {!isAcknowledged && (
              <Button
                variant="secondary"
                size="sm"
                onClick={handleAcknowledge}
                disabled={!!isActioning}
                className="text-xs"
              >
                {isActioning === 'acknowledge' ? 'Acknowledging...' : 'Acknowledge'}
              </Button>
            )}
            <Button
              variant="secondary"
              size="sm"
              onClick={handleResolve}
              disabled={!!isActioning}
              className="text-xs text-emerald-400"
            >
              <WrenchIcon className="h-3 w-3 mr-1" aria-hidden="true" />
              {isActioning === 'resolve' ? 'Resolving...' : 'Resolve'}
            </Button>
            <Button
              variant="subtle"
              size="sm"
              onClick={handleDismiss}
              disabled={!!isActioning}
              className="text-xs"
            >
              {isActioning === 'dismiss' ? 'Dismissing...' : 'Dismiss'}
            </Button>
          </div>
        </div>
      )}
    </div>
  );
}

/**
 * Priority-sorted list of maintenance alerts with quick actions
 */
export function MaintenancePriorityList({ 
  alerts, 
  isLoading,
  onActionComplete,
  maxItems,
  compact = false,
  className = '' 
}: MaintenancePriorityListProps) {
  const displayedAlerts = maxItems ? alerts.slice(0, maxItems) : alerts;
  const remainingCount = maxItems && alerts.length > maxItems ? alerts.length - maxItems : 0;

  if (isLoading) {
    return (
      <div className={`space-y-3 ${className}`}>
        {[1, 2, 3].map((i) => (
          <div 
            key={i} 
            className="bg-pf-bg-1 border border-pf-border rounded-lg p-4 animate-pulse"
          >
            <div className="flex items-start gap-3">
              <div className="w-5 h-5 bg-pf-border rounded-sm" />
              <div className="flex-1">
                <div className="h-4 bg-pf-border rounded-sm w-48 mb-2" />
                <div className="h-3 bg-pf-border rounded-sm w-full mb-2" />
                <div className="h-3 bg-pf-border rounded-sm w-24" />
              </div>
            </div>
          </div>
        ))}
      </div>
    );
  }

  if (alerts.length === 0) {
    return (
      <div className={`bg-emerald-500/10 border border-emerald-500/30 rounded-xl p-6 text-center ${className}`}>
        <CheckCircleIcon className="h-10 w-10 mx-auto text-emerald-400 mb-3" aria-hidden="true" />
        <p className="font-medium text-emerald-400">All Clear!</p>
        <p className="text-sm text-emerald-300/80 mt-1">
          No active maintenance alerts
        </p>
      </div>
    );
  }

  return (
    <div className={className}>
      <div className="space-y-3">
        {displayedAlerts.map((alert) => (
          <AlertItem 
            key={alert.id} 
            alert={alert} 
            compact={compact}
            onActionComplete={onActionComplete}
          />
        ))}
      </div>
      
      {remainingCount > 0 && (
        <p className="text-center text-sm text-pf-text-tertiary mt-4">
          + {remainingCount} more alert{remainingCount !== 1 ? 's' : ''}
        </p>
      )}
    </div>
  );
}

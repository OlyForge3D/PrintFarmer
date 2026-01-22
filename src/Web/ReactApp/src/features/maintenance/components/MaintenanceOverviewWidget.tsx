/**
 * MaintenanceOverviewWidget Component
 * 
 * Compact overview widget showing key maintenance metrics for the main dashboard.
 * Displays upcoming tasks, overdue count, and quick stats.
 */

import React from 'react';
import { Link } from 'react-router-dom';
import { 
  WrenchIcon, 
  CheckCircleIcon,
  ChevronRightIcon,
} from '@/common/components/icons/MdiIcons';
import { Button } from '@/common/components/ui';
import { useUpcomingMaintenance } from '../hooks/useUpcomingMaintenance';
import { useMaintenanceStats } from '../hooks/useMaintenanceStats';

export interface MaintenanceOverviewWidgetProps {
  /** Additional CSS classes */
  className?: string;
}

/**
 * Compact maintenance overview for dashboard
 */
export function MaintenanceOverviewWidget({
  className = '',
}: MaintenanceOverviewWidgetProps) {
  const { 
    tasks, 
    overdueCount, 
    dueSoonCount,
    isLoading: tasksLoading 
  } = useUpcomingMaintenance({ lookaheadDays: 14 });

  const {
    stats,
    isLoading: statsLoading
  } = useMaintenanceStats();

  const isLoading = tasksLoading || statsLoading;

  // Get top 3 upcoming tasks
  const upcomingTasks = tasks.slice(0, 3);

  if (isLoading) {
    return (
      <div className={`bg-pf-panel border border-pf-border rounded-xl p-4 ${className}`}>
        <div className="flex items-center gap-3 mb-4">
          <div className="h-10 w-10 bg-pf-border rounded-lg animate-pulse" />
          <div className="h-5 w-40 bg-pf-border rounded animate-pulse" />
        </div>
        <div className="grid grid-cols-3 gap-4 mb-4">
          {Array.from({ length: 3 }).map((_, i) => (
            <div key={i} className="h-16 bg-pf-border/50 rounded-lg animate-pulse" />
          ))}
        </div>
        <div className="space-y-2">
          {Array.from({ length: 2 }).map((_, i) => (
            <div key={i} className="h-10 bg-pf-border/50 rounded-lg animate-pulse" />
          ))}
        </div>
      </div>
    );
  }

  const hasIssues = overdueCount > 0 || dueSoonCount > 0;

  return (
    <div className={`bg-pf-panel border border-pf-border rounded-xl overflow-hidden ${className}`}>
      {/* Header */}
      <div className="px-4 py-3 border-b border-pf-border flex items-center justify-between">
        <div className="flex items-center gap-3">
          <div className={`p-2 rounded-lg ${hasIssues ? 'bg-amber-500/20' : 'bg-green-500/20'}`}>
            <WrenchIcon className={`h-5 w-5 ${hasIssues ? 'text-amber-400' : 'text-green-500'}`} />
          </div>
          <h3 className="font-semibold text-pf-text-primary text-sm">
            Maintenance Overview
          </h3>
        </div>
        
        <Link to="/maintenance">
          <Button variant="subtle" size="sm">
            Dashboard
            <ChevronRightIcon className="h-4 w-4 ml-1" />
          </Button>
        </Link>
      </div>

      {/* Stats Grid */}
      <div className="px-4 py-3 grid grid-cols-3 gap-3 border-b border-pf-border">
        <div className="text-center">
          <div className={`text-2xl font-bold ${overdueCount > 0 ? 'text-red-400' : 'text-pf-text-primary'}`}>
            {overdueCount}
          </div>
          <div className="text-xs text-pf-text-tertiary">Overdue</div>
        </div>
        <div className="text-center">
          <div className={`text-2xl font-bold ${dueSoonCount > 0 ? 'text-amber-400' : 'text-pf-text-primary'}`}>
            {dueSoonCount}
          </div>
          <div className="text-xs text-pf-text-tertiary">Due Soon</div>
        </div>
        <div className="text-center">
          <div className="text-2xl font-bold text-pf-text-primary">
            {stats?.totalPrinters || 0}
          </div>
          <div className="text-xs text-pf-text-tertiary">Printers</div>
        </div>
      </div>

      {/* Upcoming Tasks */}
      <div className="p-3">
        {upcomingTasks.length === 0 ? (
          <div className="text-center py-4">
            <CheckCircleIcon className="h-8 w-8 text-green-500 mx-auto mb-2" />
            <p className="text-sm text-pf-text-primary">All caught up!</p>
            <p className="text-xs text-pf-text-tertiary">No upcoming maintenance</p>
          </div>
        ) : (
          <div className="space-y-2">
            <p className="text-xs font-medium text-pf-text-tertiary uppercase tracking-wider mb-2">
              Coming Up
            </p>
            {upcomingTasks.map((task) => (
              <div
                key={task.id}
                className={`
                  flex items-center gap-3 p-2 rounded-lg
                  ${task.isOverdue ? 'bg-red-500/10' : 'bg-pf-bg-1'}
                `}
              >
                <div className={`
                  w-2 h-2 rounded-full flex-shrink-0
                  ${task.isOverdue ? 'bg-red-500' : task.isDueToday ? 'bg-amber-500' : 'bg-blue-500'}
                `} />
                <div className="flex-1 min-w-0">
                  <p className="text-sm text-pf-text-primary truncate">
                    {task.taskName}
                  </p>
                  <p className="text-xs text-pf-text-tertiary truncate">
                    {task.printerName}
                  </p>
                </div>
                <div className="text-right flex-shrink-0">
                  <p className={`text-xs font-medium ${
                    task.isOverdue ? 'text-red-400' : task.isDueToday ? 'text-amber-400' : 'text-pf-text-secondary'
                  }`}>
                    {task.isOverdue 
                      ? `${Math.abs(task.daysUntilDue)}d overdue`
                      : task.isDueToday 
                        ? 'Today'
                        : `${task.daysUntilDue}d`
                    }
                  </p>
                </div>
              </div>
            ))}
            
            {tasks.length > 3 && (
              <Link 
                to="/maintenance"
                className="block text-xs text-center text-pf-accent hover:text-pf-accent-hover py-2"
              >
                View {tasks.length - 3} more →
              </Link>
            )}
          </div>
        )}
      </div>
    </div>
  );
}

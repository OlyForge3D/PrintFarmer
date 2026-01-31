/**
 * FleetStatisticsTable
 * 
 * Displays a table of all printers with their statistics and days until next maintenance.
 * Sorted by maintenance urgency (overdue first, then by days remaining).
 */

import React from 'react';
import { useQuery } from '@tanstack/react-query';
import { useNavigate } from 'react-router';
import { maintenanceService } from '@/services/maintenanceService';
import { ClockIcon, CheckCircleIcon, AlertCircleIcon, WrenchIcon } from '@/common/components/icons/MdiIcons';
import { Button } from '@/common/components/ui';
import type { FleetPrinterStatistics } from '@/types/maintenance';

export interface FleetStatisticsTableProps {
  /** Maximum number of rows to display (optional) */
  maxRows?: number;
}

/**
 * Formats hours into a human-readable string (e.g., "12.5h" or "1,234h")
 */
function formatHours(hours: number): string {
  if (hours < 1) return `${Math.round(hours * 60)}m`;
  if (hours < 100) return `${hours.toFixed(1)}h`;
  return `${Math.round(hours).toLocaleString()}h`;
}

/**
 * Formats filament usage (grams or kg)
 */
function formatFilament(grams: number): string {
  if (grams < 1000) return `${Math.round(grams)}g`;
  return `${(grams / 1000).toFixed(1)}kg`;
}

/**
 * Renders the maintenance status badge
 */
function MaintenanceBadge({ days, task }: { days?: number | null; task?: string | null }) {
  if (days === null || days === undefined) {
    return (
      <span className="text-pf-text-tertiary text-xs">No schedule</span>
    );
  }

  if (days < 0) {
    // Overdue
    return (
      <div className="flex items-center gap-1.5">
        <AlertCircleIcon className="w-4 h-4 text-pf-error" />
        <div className="flex flex-col">
          <span className="text-pf-error font-medium text-sm">
            {Math.abs(days)} day{Math.abs(days) !== 1 ? 's' : ''} overdue
          </span>
          {task && <span className="text-pf-text-tertiary text-xs truncate max-w-[150px]">{task}</span>}
        </div>
      </div>
    );
  }

  if (days <= 7) {
    // Due soon (within a week)
    return (
      <div className="flex items-center gap-1.5">
        <ClockIcon className="w-4 h-4 text-pf-warning" />
        <div className="flex flex-col">
          <span className="text-pf-warning font-medium text-sm">
            {days} day{days !== 1 ? 's' : ''}
          </span>
          {task && <span className="text-pf-text-tertiary text-xs truncate max-w-[150px]">{task}</span>}
        </div>
      </div>
    );
  }

  // Not urgent
  return (
    <div className="flex items-center gap-1.5">
      <CheckCircleIcon className="w-4 h-4 text-pf-success" />
      <div className="flex flex-col">
        <span className="text-pf-text-secondary text-sm">
          {days} day{days !== 1 ? 's' : ''}
        </span>
        {task && <span className="text-pf-text-tertiary text-xs truncate max-w-[150px]">{task}</span>}
      </div>
    </div>
  );
}

export function FleetStatisticsTable({ maxRows }: FleetStatisticsTableProps) {
  const navigate = useNavigate();

  const { data: stats, isLoading, error } = useQuery({
    queryKey: ['fleetStatistics'],
    queryFn: () => maintenanceService.getFleetStatistics(),
    staleTime: 2 * 60 * 1000, // 2 minutes
    refetchInterval: 5 * 60 * 1000, // Refresh every 5 minutes
  });

  const handlePrinterClick = (printerId: string) => {
    // Navigate to printer-specific maintenance page
    navigate(`/printers/${printerId}/maintenance`);
  };

  if (isLoading) {
    return (
      <div className="flex items-center justify-center py-12">
        <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-pf-primary" />
      </div>
    );
  }

  if (error) {
    return (
      <div className="bg-red-500/10 border border-red-500/30 rounded-lg p-4">
        <p className="text-sm text-red-400">Failed to load fleet statistics</p>
      </div>
    );
  }

  const displayStats = maxRows ? stats?.slice(0, maxRows) : stats;

  if (!displayStats || displayStats.length === 0) {
    return (
      <div className="text-center py-12 text-pf-text-tertiary">
        <WrenchIcon className="w-12 h-12 mx-auto mb-4 opacity-50" />
        <p>No printers found</p>
        <p className="text-sm mt-1">Add printers to see their statistics</p>
      </div>
    );
  }

  return (
    <div className="overflow-x-auto">
      <table className="w-full text-sm">
        <thead>
          <tr className="border-b border-pf-border text-left">
            <th className="py-3 px-4 font-medium text-pf-text-secondary">Printer</th>
            <th className="py-3 px-4 font-medium text-pf-text-secondary">Model</th>
            <th className="py-3 px-4 font-medium text-pf-text-secondary text-center">Status</th>
            <th className="py-3 px-4 font-medium text-pf-text-secondary text-right">Print Hours</th>
            <th className="py-3 px-4 font-medium text-pf-text-secondary text-right">Jobs</th>
            <th className="py-3 px-4 font-medium text-pf-text-secondary text-right">Filament</th>
            <th className="py-3 px-4 font-medium text-pf-text-secondary">Next Maintenance</th>
          </tr>
        </thead>
        <tbody>
          {displayStats.map((printer: FleetPrinterStatistics) => (
            <tr 
              key={printer.printerId}
              className="border-b border-pf-border/50 hover:bg-pf-bg-2 cursor-pointer transition-colors"
              onClick={() => handlePrinterClick(printer.printerId)}
            >
              <td className="py-3 px-4">
                <div className="flex flex-col">
                  <span className="font-medium text-pf-text-primary">{printer.printerName}</span>
                  {printer.manufacturerName && (
                    <span className="text-xs text-pf-text-tertiary">{printer.manufacturerName}</span>
                  )}
                </div>
              </td>
              <td className="py-3 px-4 text-pf-text-secondary">
                {printer.modelName || '—'}
              </td>
              <td className="py-3 px-4 text-center">
                <div className="flex items-center justify-center gap-2">
                  {printer.inMaintenance ? (
                    <span className="inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-pf-warning/20 text-pf-warning">
                      <WrenchIcon className="w-3 h-3 mr-1" />
                      Maintenance
                    </span>
                  ) : printer.isOnline ? (
                    <span className="inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-pf-success/20 text-pf-success">
                      Online
                    </span>
                  ) : (
                    <span className="inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-pf-border-medium text-pf-text-tertiary">
                      Offline
                    </span>
                  )}
                </div>
              </td>
              <td className="py-3 px-4 text-right font-mono text-pf-text-primary">
                {formatHours(printer.totalPrintHours)}
              </td>
              <td className="py-3 px-4 text-right">
                <div className="flex flex-col items-end">
                  <span className="font-mono text-pf-text-primary">{printer.totalJobsCompleted.toLocaleString()}</span>
                  {printer.totalJobsFailed > 0 && (
                    <span className="text-xs text-pf-error">{printer.totalJobsFailed} failed</span>
                  )}
                </div>
              </td>
              <td className="py-3 px-4 text-right font-mono text-pf-text-primary">
                {formatFilament(printer.totalFilamentUsedGrams)}
              </td>
              <td className="py-3 px-4">
                <MaintenanceBadge 
                  days={printer.daysUntilNextMaintenance} 
                  task={printer.nextMaintenanceTask} 
                />
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      
      {maxRows && stats && stats.length > maxRows && (
        <div className="text-center py-3 border-t border-pf-border">
          <Button 
            variant="link"
            className="text-sm text-pf-primary hover:text-pf-primary-hover"
            onClick={() => navigate('/maintenance?tab=statistics')}
          >
            View all {stats.length} printers →
          </Button>
        </div>
      )}
    </div>
  );
}

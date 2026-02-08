/**
 * MaintenanceStatusGrid Component
 * 
 * Displays a grid of printer cards with maintenance status indicators.
 * Features:
 * - Color-coded status indicators based on alert severity
 * - Quick view of online/offline and maintenance mode status
 * - Click to view printer details or maintenance history
 */

import React from 'react';
import { 
  PrinterIcon, 
  WrenchIcon, 
  CheckCircleIcon,
  AlertCircleIcon,
  CircleIcon 
} from '@/common/components/icons/MdiIcons';
import type { PrinterMaintenanceStatus } from '../hooks/useMaintenanceStats';

export interface MaintenanceStatusGridProps {
  /** Printer statuses to display */
  printers: PrinterMaintenanceStatus[];
  /** Whether data is loading */
  isLoading?: boolean;
  /** Callback when a printer card is clicked */
  onPrinterClick?: (printerId: string) => void;
  /** Maximum number of printers to show (optional) */
  maxItems?: number;
  /** Additional CSS classes */
  className?: string;
}

interface PrinterCardProps {
  printer: PrinterMaintenanceStatus;
  onClick?: () => void;
}

/**
 * Returns status indicator color based on alert severity
 */
function getStatusIndicator(printer: PrinterMaintenanceStatus): {
  color: string;
  bgColor: string;
  label: string;
} {
  if (!printer.isOnline) {
    return { color: 'text-gray-400', bgColor: 'bg-gray-500/20', label: 'Offline' };
  }
  if (printer.inMaintenance) {
    return { color: 'text-blue-400', bgColor: 'bg-blue-500/20', label: 'In Maintenance' };
  }
  if (printer.criticalAlerts > 0) {
    return { color: 'text-red-400', bgColor: 'bg-red-500/20', label: 'Critical' };
  }
  if (printer.highAlerts > 0) {
    return { color: 'text-orange-400', bgColor: 'bg-orange-500/20', label: 'Needs Attention' };
  }
  if (printer.mediumAlerts > 0) {
    return { color: 'text-amber-400', bgColor: 'bg-amber-500/20', label: 'Warning' };
  }
  if (printer.lowAlerts > 0) {
    return { color: 'text-blue-400', bgColor: 'bg-blue-500/20', label: 'Minor' };
  }
  return { color: 'text-emerald-400', bgColor: 'bg-emerald-500/20', label: 'Healthy' };
}

function PrinterCard({ printer, onClick }: PrinterCardProps) {
  const status = getStatusIndicator(printer);
  const hasAlerts = printer.activeAlertCount > 0;

  return (
    // eslint-disable-next-line local/pf-no-raw-html-controls -- Card component intentionally uses semantic button for accessibility
    <button
      type="button"
      onClick={onClick}
      className={`
        w-full text-left bg-pf-bg-1 border border-pf-border rounded-xl p-4
        hover:border-pf-accent/50 hover:shadow-md transition-all duration-200
        focus:outline-hidden focus:ring-2 focus:ring-pf-accent/50 focus:ring-offset-2 focus:ring-offset-pf-bg
      `}
      aria-label={`${printer.printerName} - ${status.label}`}
    >
      {/* Header with status indicator */}
      <div className="flex items-start justify-between mb-3">
        <div className="flex items-center gap-3 min-w-0">
          <div className={`p-2 rounded-lg ${status.bgColor}`}>
            <PrinterIcon className={`h-5 w-5 ${status.color}`} aria-hidden="true" />
          </div>
          <div className="min-w-0">
            <h4 className="font-medium text-pf-text-primary truncate">
              {printer.printerName}
            </h4>
            <p className="text-xs text-pf-text-tertiary flex items-center gap-1">
              <CircleIcon 
                className={`h-2 w-2 ${printer.isOnline ? 'text-emerald-400' : 'text-gray-400'}`} 
                aria-hidden="true"
              />
              {printer.isOnline ? 'Online' : 'Offline'}
            </p>
          </div>
        </div>
        
        {/* Maintenance mode indicator */}
        {printer.inMaintenance && (
          <div className="shrink-0" title="In maintenance mode">
            <WrenchIcon className="h-4 w-4 text-blue-400" aria-hidden="true" />
          </div>
        )}
      </div>

      {/* Alert counts or healthy status */}
      <div className="flex items-center justify-between">
        {hasAlerts ? (
          <div className="flex items-center gap-2">
            <AlertCircleIcon className={`h-4 w-4 ${status.color}`} aria-hidden="true" />
            <span className="text-sm text-pf-text-secondary">
              {printer.activeAlertCount} alert{printer.activeAlertCount !== 1 ? 's' : ''}
            </span>
          </div>
        ) : (
          <div className="flex items-center gap-2">
            <CheckCircleIcon className="h-4 w-4 text-emerald-400" aria-hidden="true" />
            <span className="text-sm text-pf-text-secondary">Healthy</span>
          </div>
        )}

        {/* Severity breakdown badges */}
        {hasAlerts && (
          <div className="flex items-center gap-1">
            {printer.criticalAlerts > 0 && (
              <span className="px-1.5 py-0.5 text-xs font-medium bg-red-500/20 text-red-400 rounded-sm">
                {printer.criticalAlerts}
              </span>
            )}
            {printer.highAlerts > 0 && (
              <span className="px-1.5 py-0.5 text-xs font-medium bg-orange-500/20 text-orange-400 rounded-sm">
                {printer.highAlerts}
              </span>
            )}
            {printer.mediumAlerts > 0 && (
              <span className="px-1.5 py-0.5 text-xs font-medium bg-amber-500/20 text-amber-400 rounded-sm">
                {printer.mediumAlerts}
              </span>
            )}
            {printer.lowAlerts > 0 && (
              <span className="px-1.5 py-0.5 text-xs font-medium bg-blue-500/20 text-blue-400 rounded-sm">
                {printer.lowAlerts}
              </span>
            )}
          </div>
        )}
      </div>
    </button>
  );
}

/**
 * Grid display of printer maintenance statuses
 */
export function MaintenanceStatusGrid({ 
  printers, 
  isLoading, 
  onPrinterClick,
  maxItems,
  className = '' 
}: MaintenanceStatusGridProps) {
  const displayedPrinters = maxItems ? printers.slice(0, maxItems) : printers;
  const remainingCount = maxItems && printers.length > maxItems ? printers.length - maxItems : 0;

  if (isLoading) {
    return (
      <div className={`grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4 gap-4 ${className}`}>
        {[1, 2, 3, 4, 5, 6, 7, 8].map((i) => (
          <div 
            key={i} 
            className="bg-pf-bg-1 border border-pf-border rounded-xl p-4 animate-pulse"
          >
            <div className="flex items-start gap-3 mb-3">
              <div className="w-9 h-9 bg-pf-border rounded-lg" />
              <div className="flex-1">
                <div className="h-4 bg-pf-border rounded-sm w-24 mb-2" />
                <div className="h-3 bg-pf-border rounded-sm w-16" />
              </div>
            </div>
            <div className="h-4 bg-pf-border rounded-sm w-20" />
          </div>
        ))}
      </div>
    );
  }

  if (printers.length === 0) {
    return (
      <div className={`bg-pf-bg-1 border border-pf-border rounded-xl p-8 text-center ${className}`}>
        <PrinterIcon className="h-12 w-12 mx-auto text-pf-text-tertiary mb-3" aria-hidden="true" />
        <p className="text-pf-text-secondary font-medium">No printers found</p>
        <p className="text-sm text-pf-text-tertiary mt-1">
          Add printers to see their maintenance status here
        </p>
      </div>
    );
  }

  return (
    <div className={className}>
      <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4 gap-4">
        {displayedPrinters.map((printer) => (
          <PrinterCard
            key={printer.printerId}
            printer={printer}
            onClick={() => onPrinterClick?.(printer.printerId)}
          />
        ))}
      </div>
      
      {remainingCount > 0 && (
        <p className="text-center text-sm text-pf-text-tertiary mt-4">
          + {remainingCount} more printer{remainingCount !== 1 ? 's' : ''}
        </p>
      )}
    </div>
  );
}

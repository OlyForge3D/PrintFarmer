import React, { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Printer } from '@/types/api';
import { EditIcon, DeleteIcon, ClockIcon, CheckCircleIcon, PackageIcon } from '@/common/components/icons/MdiIcons';
import { Button } from '@/common/components/ui';
import { maintenanceService } from '@/services/maintenanceService';

interface PrinterCompactCardProps {
  printer: Printer;
  onEdit: (printer: Printer) => void;
  onDelete: (printer: Printer) => void;
}

/**
 * Formats hours into a human-readable string (e.g., "12.5h" or "1,234h")
 */
function formatHours(hours: number | undefined | null): string {
  const h = hours ?? 0;
  if (h < 1) return `${Math.round(h * 60)}m`;
  if (h < 100) return `${h.toFixed(1)}h`;
  return `${Math.round(h).toLocaleString()}h`;
}

/**
 * Formats filament usage (grams or kg)
 */
function formatFilament(grams: number | undefined | null): string {
  const g = grams ?? 0;
  if (g < 1000) return `${Math.round(g)}g`;
  return `${(g / 1000).toFixed(1)}kg`;
}

export function PrinterCompactCard({
  printer: p,
  onEdit,
  onDelete
}: PrinterCompactCardProps) {
  const [imageError, setImageError] = useState(false);
  const isOnline = p.isOnline ?? false;
  const state = p.state ?? '';
  const isPrinting = state.toLowerCase().includes('printing');

  // Fetch printer statistics (cached, stale time 5 minutes)
  const { data: stats } = useQuery({
    queryKey: ['printerStatistics', p.id],
    queryFn: () => maintenanceService.getPrinterStatistics(p.id),
    staleTime: 5 * 60 * 1000, // 5 minutes
    retry: false, // Don't retry on failure (printer may not have stats yet)
  });

  const hasStats = stats && (stats.totalPrintHours > 0 || stats.totalJobsCompleted > 0 || stats.totalFilamentUsedGrams > 0);

  return (
    <div className="bg-pf-bg-1 rounded-lg p-3 shadow border border-pf-border hover:border-pf-primary transition-colors overflow-hidden flex flex-col min-h-0">
      <div className="mb-3 min-w-0">
        {p.thumbnailUrl && !imageError ? (
          <div className="w-full h-32 bg-pf-border flex items-center justify-center rounded overflow-hidden mb-3">
            <img
              src={p.thumbnailUrl}
              alt={`${p.name} thumbnail`}
              className="w-full h-full object-cover rounded"
              loading="lazy"
              onError={() => setImageError(true)}
            />
          </div>
        ) : null}
        <div className="font-bold text-lg text-pf-text-primary font-bebas uppercase mb-1 truncate">
          {p.name}
        </div>
        <div className="text-pf-text-secondary text-xs truncate">
          {p.manufacturerName ? `${p.manufacturerName} ${p.modelName ?? ''}` : (p.modelName ?? '')}
        </div>
      </div>
      <div className="flex items-center gap-2 mb-3">
        <span className={`inline-flex items-center px-2 py-0.5 rounded text-xs font-medium ${isOnline ? 'bg-pf-status-online-bg text-pf-status-online-text' : 'bg-pf-border-medium text-pf-text-secondary'}`}>
          {isOnline ? 'Online' : 'Offline'}
        </span>
        {isPrinting && <span className="inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-pf-warning text-pf-text-primary">Printing</span>}
      </div>

      {/* Print Statistics Section */}
      {hasStats && stats && (
        <div className="grid grid-cols-3 gap-1 mb-3 py-2 px-1 bg-pf-bg-2 rounded text-center">
          <div className="flex flex-col items-center" title="Total print time">
            <ClockIcon className="w-3.5 h-3.5 text-pf-text-secondary mb-0.5" />
            <span className="text-xs font-medium text-pf-text-primary">{formatHours(stats.totalPrintHours ?? 0)}</span>
          </div>
          <div className="flex flex-col items-center" title="Jobs completed">
            <CheckCircleIcon className="w-3.5 h-3.5 text-pf-text-secondary mb-0.5" />
            <span className="text-xs font-medium text-pf-text-primary">{(stats.totalJobsCompleted ?? 0).toLocaleString()}</span>
          </div>
          <div className="flex flex-col items-center" title="Filament used">
            <PackageIcon className="w-3.5 h-3.5 text-pf-text-secondary mb-0.5" />
            <span className="text-xs font-medium text-pf-text-primary">{formatFilament(stats.totalFilamentUsedGrams ?? 0)}</span>
          </div>
        </div>
      )}

      <div className="flex gap-2 mt-auto">
        <Button
          aria-label={`Edit ${p.name}`}
          title="Edit"
          variant="subtle"
          size="sm"
          onClick={() => onEdit(p)}
          className="!p-1 !h-auto"
          iconLeft={<EditIcon className="w-4 h-4" />}
        >
        </Button>
        <Button
          aria-label={`Delete ${p.name}`}
          title="Delete"
          variant="subtle"
          size="sm"
          onClick={() => onDelete(p)}
          className="!p-1 !h-auto hover:text-pf-error"
          iconLeft={<DeleteIcon className="w-4 h-4" />}
        >
        </Button>
      </div>
    </div>
  );
}

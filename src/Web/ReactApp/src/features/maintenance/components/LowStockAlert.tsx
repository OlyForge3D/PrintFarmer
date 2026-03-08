/**
 * LowStockAlert Component
 *
 * Displays a compact alert card showing parts below minimum stock levels.
 * Intended for the Overview tab of the Maintenance Dashboard.
 */

import React from 'react';
import { Badge, Button } from '@/common/components/ui';
import { AlertIcon, PackageIcon, ExternalLinkIcon } from '@/common/components/icons/MdiIcons';
import { useLowStockComponents } from '../hooks/useMaintenanceComponents';
import type { MaintenanceComponentDto } from '@/types/maintenance';

interface LowStockAlertProps {
  /** Maximum items to show before truncating */
  maxItems?: number;
  /** Callback when user clicks "View All" */
  onViewAll?: () => void;
}

export function LowStockAlert({ maxItems = 5, onViewAll }: LowStockAlertProps) {
  const { data: lowStock = [], isLoading } = useLowStockComponents();

  if (isLoading) {
    return (
      <div className="h-32 bg-pf-border/50 rounded-lg animate-pulse" />
    );
  }

  if (lowStock.length === 0) {
    return (
      <div className="flex items-center gap-3 p-4 rounded-lg border border-pf-border bg-pf-bg-2 text-pf-text-muted">
        <PackageIcon className="h-5 w-5 shrink-0" aria-hidden="true" />
        <span className="text-sm">All parts are adequately stocked.</span>
      </div>
    );
  }

  const visible = lowStock.slice(0, maxItems);
  const remaining = lowStock.length - visible.length;

  return (
    <div className="rounded-lg border border-pf-warning/40 bg-pf-warning/5">
      {/* Header */}
      <div className="flex items-center gap-2 px-4 py-3 border-b border-pf-warning/20">
        <AlertIcon className="h-5 w-5 text-pf-warning shrink-0" aria-hidden="true" />
        <h3 className="text-sm font-semibold text-pf-warning">
          Low Stock Alert
        </h3>
        <Badge variant="warning" className="text-xs ml-auto">
          {lowStock.length} part{lowStock.length !== 1 ? 's' : ''}
        </Badge>
      </div>

      {/* Items */}
      <div className="divide-y divide-pf-warning/10">
        {visible.map((part: MaintenanceComponentDto) => {
          const deficit = Math.max(0, part.minimumStock - part.inStock);
          return (
            <div key={part.id} className="flex items-center gap-3 px-4 py-2.5">
              <div className="flex-1 min-w-0">
                <div className="flex items-center gap-2">
                  <span className="text-sm font-medium text-pf-text-primary truncate">{part.name}</span>
                  <Badge variant="default" className="text-[10px]">{part.category}</Badge>
                </div>
                <p className="text-xs text-pf-warning mt-0.5">
                  {part.inStock} in stock (min: {part.minimumStock}, need {deficit} more)
                </p>
              </div>
              {part.url?.startsWith('http') && (
                <a
                  href={part.url}
                  target="_blank"
                  rel="noopener noreferrer"
                  className="text-pf-accent hover:underline shrink-0"
                  aria-label={`Buy ${part.name}`}
                >
                  <ExternalLinkIcon className="h-4 w-4" />
                </a>
              )}
            </div>
          );
        })}
      </div>

      {/* Footer */}
      {(remaining > 0 || onViewAll) && (
        <div className="px-4 py-2.5 border-t border-pf-warning/20 flex items-center justify-between">
          {remaining > 0 && (
            <span className="text-xs text-pf-text-muted">
              +{remaining} more part{remaining !== 1 ? 's' : ''} below minimum
            </span>
          )}
          {onViewAll && (
            <Button variant="ghost" size="sm" onClick={onViewAll} className="text-xs">
              View All Parts →
            </Button>
          )}
        </div>
      )}
    </div>
  );
}

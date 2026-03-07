import React, { useMemo, useState } from 'react';
import { Card, Input, Badge, Select } from '@/common/components/ui';
import { SearchIcon } from '@/common/components/icons/MdiIcons';
import type { LocationSubtreePrinter } from '@/types/api';
import clsx from 'clsx';

interface LocationPrinterListProps {
  printers: LocationSubtreePrinter[];
  isLoading?: boolean;
  onPrinterClick?: (printerId: string) => void;
}

type StatusFilter = 'all' | 'online' | 'offline' | 'printing' | 'idle';

const STATUS_BADGE_VARIANT: Record<string, 'success' | 'error' | 'primary' | 'warning' | 'default'> = {
  online: 'success',
  offline: 'error',
  printing: 'primary',
  idle: 'warning',
};

function getPrinterStatusKey(printer: LocationSubtreePrinter): string {
  if (!printer.isOnline) return 'offline';
  if (printer.currentState === 'Printing') return 'printing';
  if (printer.currentState === 'Idle' || printer.currentState === 'Ready' || printer.currentState === 'Operational') return 'idle';
  return 'online';
}

export const LocationPrinterList: React.FC<LocationPrinterListProps> = ({
  printers,
  isLoading,
  onPrinterClick,
}) => {
  const [search, setSearch] = useState('');
  const [statusFilter, setStatusFilter] = useState<StatusFilter>('all');

  const filtered = useMemo(() => {
    let result = printers;

    if (search.trim()) {
      const q = search.toLowerCase();
      result = result.filter(p => 
        p.printerName.toLowerCase().includes(q) ||
        p.locationName.toLowerCase().includes(q)
      );
    }

    if (statusFilter !== 'all') {
      result = result.filter(p => {
        const key = getPrinterStatusKey(p);
        return key === statusFilter;
      });
    }

    return result;
  }, [printers, search, statusFilter]);

  // Group printers by location for better organization
  const grouped = useMemo(() => {
    const groups = new Map<string, LocationSubtreePrinter[]>();
    for (const printer of filtered) {
      const key = printer.locationName;
      if (!groups.has(key)) {
        groups.set(key, []);
      }
      groups.get(key)!.push(printer);
    }
    return Array.from(groups.entries()).sort((a, b) => a[0].localeCompare(b[0]));
  }, [filtered]);

  if (isLoading) {
    return (
      <div className="space-y-3">
        {Array.from({ length: 3 }).map((_, i) => (
          <Card key={i}>
            <Card.Body className="h-16 animate-pulse bg-pf-bg-1 rounded" />
          </Card>
        ))}
      </div>
    );
  }

  return (
    <div className="space-y-4">
      <div className="flex flex-col sm:flex-row gap-3">
        <div className="flex-1 relative">
          <SearchIcon className="absolute left-3 top-1/2 -translate-y-1/2 w-4 h-4 text-pf-text-secondary" />
          <Input
            value={search}
            onChange={e => setSearch(e.target.value)}
            placeholder="Search printers or locations..."
            className="pl-9"
            aria-label="Search printers"
          />
        </div>
        <Select
          value={statusFilter}
          onChange={e => setStatusFilter(e.target.value as StatusFilter)}
          containerClassName="w-40"
          aria-label="Filter by status"
        >
          <option value="all">All Status</option>
          <option value="online">Online</option>
          <option value="offline">Offline</option>
          <option value="printing">Printing</option>
          <option value="idle">Idle</option>
        </Select>
      </div>

      {filtered.length === 0 ? (
        <div className="text-center py-8 text-pf-text-secondary">
          {printers.length === 0
            ? 'No printers at this location.'
            : 'No printers match the current filters.'}
        </div>
      ) : (
        <div className="space-y-6">
          {grouped.map(([locationName, locationPrinters]) => (
            <div key={locationName} className="space-y-2">
              <h4 className="text-sm font-semibold text-pf-text-secondary px-2">
                {locationName} ({locationPrinters.length})
              </h4>
              <div className="space-y-2">
                {locationPrinters.map(printer => {
                  const statusKey = getPrinterStatusKey(printer);
                  return (
                    <Card key={printer.printerId}>
                      <Card.Body
                        className={clsx(
                          'flex items-center justify-between p-3 gap-4',
                          onPrinterClick && 'cursor-pointer hover:bg-pf-bg-1 transition-colors',
                        )}
                        onClick={() => onPrinterClick?.(printer.printerId)}
                        role={onPrinterClick ? 'button' : undefined}
                        tabIndex={onPrinterClick ? 0 : undefined}
                        onKeyDown={e => {
                          if (onPrinterClick && (e.key === 'Enter' || e.key === ' ')) {
                            e.preventDefault();
                            onPrinterClick(printer.printerId);
                          }
                        }}
                      >
                        <div className="flex items-center gap-3 min-w-0">
                          <div
                            className={clsx(
                              'w-2.5 h-2.5 rounded-full flex-shrink-0',
                              printer.isOnline ? 'bg-green-500' : 'bg-red-500',
                            )}
                            aria-hidden="true"
                          />
                          <div className="min-w-0 flex-1">
                            <p className="font-medium text-pf-text-primary truncate">
                              {printer.printerName}
                            </p>
                            <div className="flex flex-col sm:flex-row sm:items-center gap-1 sm:gap-3 text-sm text-pf-text-secondary">
                              {printer.currentState && (
                                <span>
                                  {printer.currentState}
                                  {printer.progressPercent !== undefined && printer.progressPercent !== null && printer.progressPercent > 0
                                    ? ` — ${Math.round(printer.progressPercent)}%`
                                    : ''}
                                </span>
                              )}
                              {printer.currentJobName && (
                                <span className="truncate">📄 {printer.currentJobName}</span>
                              )}
                            </div>
                          </div>
                        </div>

                        <div className="flex items-center gap-2 flex-shrink-0">
                          <Badge 
                            variant={STATUS_BADGE_VARIANT[statusKey] ?? 'default'} 
                            size="sm"
                            className="hidden sm:inline-flex"
                          >
                            {statusKey}
                          </Badge>
                        </div>
                      </Card.Body>
                    </Card>
                  );
                })}
              </div>
            </div>
          ))}
        </div>
      )}
    </div>
  );
};

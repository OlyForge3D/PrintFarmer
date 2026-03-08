import React, { useMemo, useState } from 'react';
import { Card, Input, Badge, Select } from '@/common/components/ui';
import { SearchIcon } from '@/common/components/icons/MdiIcons';
import type { Printer } from '@/types/api';
import clsx from 'clsx';

interface LocationPrinterListProps {
  printers: Printer[];
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

function getPrinterStatusKey(printer: Printer): string {
  if (!printer.isOnline) return 'offline';
  if (printer.state === 'Printing') return 'printing';
  if (printer.state === 'Idle' || printer.state === 'Ready' || printer.state === 'Operational') return 'idle';
  return 'online';
}

function formatTemp(temp: number | undefined): string {
  if (temp === undefined || temp === null) return '—';
  return `${Math.round(temp)}°C`;
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
      result = result.filter(p => p.name.toLowerCase().includes(q));
    }

    if (statusFilter !== 'all') {
      result = result.filter(p => {
        const key = getPrinterStatusKey(p);
        return key === statusFilter;
      });
    }

    return result;
  }, [printers, search, statusFilter]);

  if (isLoading) {
    return (
      <div className="space-y-3">
        {Array.from({ length: 3 }).map((_, i) => (
          <Card key={i}>
            <Card.Body className="h-16 pf-animate-skeleton pf-skeleton rounded" />
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
            placeholder="Search printers..."
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
        <div className="space-y-2">
          {filtered.map(printer => {
            const statusKey = getPrinterStatusKey(printer);
            return (
              <Card key={printer.id}>
                <Card.Body
                  className={clsx(
                    'flex items-center justify-between p-3 gap-4',
                    onPrinterClick && 'cursor-pointer hover:bg-pf-bg-1 transition-colors',
                  )}
                  onClick={() => onPrinterClick?.(printer.id)}
                  role={onPrinterClick ? 'button' : undefined}
                  tabIndex={onPrinterClick ? 0 : undefined}
                  onKeyDown={e => {
                    if (onPrinterClick && (e.key === 'Enter' || e.key === ' ')) {
                      e.preventDefault();
                      onPrinterClick(printer.id);
                    }
                  }}
                >
                  <div className="flex items-center gap-3 min-w-0">
                    <div
                      className={clsx(
                        'w-2.5 h-2.5 rounded-full flex-shrink-0',
                        printer.isOnline ? 'bg-pf-success' : 'bg-pf-error',
                      )}
                      aria-hidden="true"
                    />
                    <div className="min-w-0">
                      <p className="font-medium text-pf-text-primary truncate">
                        {printer.name}
                      </p>
                      {printer.state && (
                        <p className="text-sm text-pf-text-secondary">
                          {printer.state}
                          {printer.progress !== undefined && printer.progress > 0
                            ? ` — ${Math.round(printer.progress)}%`
                            : ''}
                        </p>
                      )}
                    </div>
                  </div>

                  <div className="flex items-center gap-4 flex-shrink-0">
                    {printer.isOnline && (
                      <div className="hidden md:flex gap-3 text-sm text-pf-text-secondary">
                        <span title="Hotend">🔥 {formatTemp(printer.hotendTemp)}</span>
                        <span title="Bed">🛏️ {formatTemp(printer.bedTemp)}</span>
                      </div>
                    )}
                    <Badge variant={STATUS_BADGE_VARIANT[statusKey] ?? 'default'} size="sm">
                      {statusKey}
                    </Badge>
                  </div>
                </Card.Body>
              </Card>
            );
          })}
        </div>
      )}
    </div>
  );
};

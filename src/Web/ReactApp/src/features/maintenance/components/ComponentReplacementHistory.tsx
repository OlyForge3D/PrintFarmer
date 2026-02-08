/**
 * ComponentReplacementHistory Component
 * 
 * Displays a history of component replacements with cost tracking.
 * Shows when parts were replaced across the fleet.
 */

import React, { useState } from 'react';
import { format } from 'date-fns';
import { 
  RefreshIcon, 
  FilterIcon,
  SortIcon,
  PrinterIcon
} from '@/common/components/icons/MdiIcons';
import { Button, Badge, Select } from '@/common/components/ui';
import type { ComponentReplacement } from '../hooks/useComponentMaintenance';

export interface ComponentReplacementHistoryProps {
  /** Replacement records */
  replacements: ComponentReplacement[];
  /** Available component names for filtering */
  componentNames: string[];
  /** Loading state */
  isLoading?: boolean;
  /** Additional CSS classes */
  className?: string;
}

type SortField = 'date' | 'cost' | 'component';
type SortDirection = 'asc' | 'desc';

/**
 * Component replacement history list with filtering and sorting
 */
export function ComponentReplacementHistory({
  replacements,
  componentNames,
  isLoading,
  className = '',
}: ComponentReplacementHistoryProps) {
  const [filterComponent, setFilterComponent] = useState<string>('all');
  const [sortField, setSortField] = useState<SortField>('date');
  const [sortDirection, setSortDirection] = useState<SortDirection>('desc');

  // Filter and sort replacements
  const filteredReplacements = React.useMemo(() => {
    let result = [...replacements];

    // Apply component filter
    if (filterComponent !== 'all') {
      result = result.filter(r => r.component === filterComponent);
    }

    // Apply sorting
    result.sort((a, b) => {
      let comparison = 0;
      switch (sortField) {
        case 'date':
          comparison = a.replacedAt.getTime() - b.replacedAt.getTime();
          break;
        case 'cost':
          comparison = (a.cost || 0) - (b.cost || 0);
          break;
        case 'component':
          comparison = a.component.localeCompare(b.component);
          break;
      }
      return sortDirection === 'asc' ? comparison : -comparison;
    });

    return result;
  }, [replacements, filterComponent, sortField, sortDirection]);

  // Calculate totals
  const totalCost = filteredReplacements.reduce((sum, r) => sum + (r.cost || 0), 0);

  const handleSort = (field: SortField) => {
    if (sortField === field) {
      setSortDirection(prev => prev === 'asc' ? 'desc' : 'asc');
    } else {
      setSortField(field);
      setSortDirection('desc');
    }
  };

  if (isLoading) {
    return (
      <div className={`space-y-4 ${className}`}>
        <div className="flex items-center justify-between">
          <div className="h-8 w-48 bg-pf-border rounded-sm animate-pulse" />
          <div className="h-8 w-32 bg-pf-border rounded-sm animate-pulse" />
        </div>
        {Array.from({ length: 5 }).map((_, i) => (
          <div key={i} className="h-20 bg-pf-border/50 rounded-lg animate-pulse" />
        ))}
      </div>
    );
  }

  if (replacements.length === 0) {
    return (
      <div className={`text-center py-12 ${className}`}>
        <RefreshIcon className="h-12 w-12 text-pf-text-tertiary mx-auto mb-3" />
        <h3 className="font-medium text-pf-text-primary">No Replacements Recorded</h3>
        <p className="text-sm text-pf-text-tertiary mt-1">
          Component replacements will appear here when logged
        </p>
      </div>
    );
  }

  return (
    <div className={className}>
      {/* Header with filters */}
      <div className="flex flex-col sm:flex-row items-start sm:items-center justify-between gap-4 mb-4">
        <div className="flex items-center gap-2">
          <FilterIcon className="h-4 w-4 text-pf-text-tertiary" />
          <Select
            value={filterComponent}
            onChange={(e) => setFilterComponent(e.target.value)}
            className="w-40"
          >
            <option value="all">All Components</option>
            {componentNames.map(name => (
              <option key={name} value={name}>{name}</option>
            ))}
          </Select>
        </div>

        <div className="flex items-center gap-4">
          <div className="text-sm text-pf-text-tertiary">
            {filteredReplacements.length} replacement{filteredReplacements.length !== 1 ? 's' : ''}
            {totalCost > 0 && (
              <span className="ml-2 text-pf-text-secondary font-medium">
                • ${totalCost.toFixed(2)} total
              </span>
            )}
          </div>
          <div className="flex items-center gap-1">
            <Button
              variant="subtle"
              size="sm"
              onClick={() => handleSort('date')}
              className={sortField === 'date' ? 'bg-pf-accent/10' : ''}
            >
              <SortIcon className="h-4 w-4 mr-1" />
              Date
            </Button>
            <Button
              variant="subtle"
              size="sm"
              onClick={() => handleSort('cost')}
              className={sortField === 'cost' ? 'bg-pf-accent/10' : ''}
            >
              Cost
            </Button>
          </div>
        </div>
      </div>

      {/* Replacements List */}
      <div className="space-y-3">
        {filteredReplacements.map((replacement) => (
          <div
            key={replacement.id}
            className="bg-pf-bg-1 border border-pf-border rounded-lg p-4 hover:bg-pf-border/20 transition-colors"
          >
            <div className="flex items-start justify-between">
              <div className="flex-1">
                <div className="flex items-center gap-2 mb-1">
                  <Badge variant="default" className="text-xs">
                    {replacement.component}
                  </Badge>
                  <span className="text-xs text-pf-text-tertiary">•</span>
                  <span className="text-xs text-pf-text-tertiary">
                    {format(replacement.replacedAt, 'MMM d, yyyy')}
                  </span>
                </div>
                
                <h4 className="font-medium text-pf-text-primary">
                  {replacement.partsReplaced}
                </h4>
                
                <div className="flex items-center gap-2 mt-2 text-xs text-pf-text-secondary">
                  <PrinterIcon className="h-3.5 w-3.5" />
                  <span>{replacement.printerName}</span>
                  {replacement.performedBy && (
                    <>
                      <span className="text-pf-text-tertiary">•</span>
                      <span>by {replacement.performedBy}</span>
                    </>
                  )}
                </div>

                {replacement.notes && (
                  <p className="mt-2 text-xs text-pf-text-tertiary">
                    {replacement.notes}
                  </p>
                )}
              </div>

              {replacement.cost !== null && replacement.cost > 0 && (
                <div className="text-right">
                  <p className="text-lg font-semibold text-pf-text-primary">
                    ${replacement.cost.toFixed(2)}
                  </p>
                </div>
              )}
            </div>
          </div>
        ))}
      </div>

      {filteredReplacements.length === 0 && filterComponent !== 'all' && (
        <div className="text-center py-8">
          <p className="text-pf-text-tertiary">
            No replacements found for {filterComponent}
          </p>
          <Button
            variant="subtle"
            size="sm"
            onClick={() => setFilterComponent('all')}
            className="mt-2"
          >
            Clear filter
          </Button>
        </div>
      )}
    </div>
  );
}

import { useState, useCallback } from 'react';

/**
 * Sort direction for columns
 */
export type SortDirection = 'asc' | 'desc' | null;

/**
 * Sort state for a table
 */
export interface SortState<T extends string = string> {
  column: T | null;
  direction: SortDirection;
}

/**
 * Hook for managing table sort state
 * 
 * @example
 * const { sortState, handleSort, sortData } = useTableSort<'name' | 'date'>();
 * 
 * // In header
 * <TableHeaderCell sortable sortKey="name" sortDirection={sortState.column === 'name' ? sortState.direction : null} onSort={handleSort}>
 *   Name
 * </TableHeaderCell>
 * 
 * // Sort data
 * const sortedData = sortData(data, {
 *   name: (a, b) => a.name.localeCompare(b.name),
 *   date: (a, b) => a.date.getTime() - b.date.getTime(),
 * });
 */
export function useTableSort<T extends string>(defaultColumn?: T, defaultDirection: SortDirection = 'asc') {
  const [sortState, setSortState] = useState<SortState<T>>({
    column: defaultColumn ?? null,
    direction: defaultColumn ? defaultDirection : null,
  });

  const handleSort = useCallback((column: string) => {
    setSortState(prev => {
      if (prev.column === column) {
        // Cycle: asc -> desc -> null
        if (prev.direction === 'asc') {
          return { column: column as T, direction: 'desc' };
        } else if (prev.direction === 'desc') {
          return { column: null, direction: null };
        }
      }
      // New column or was null
      return { column: column as T, direction: 'asc' };
    });
  }, []);

  const sortData = useCallback(<D,>(
    data: D[],
    comparators: Partial<Record<T, (a: D, b: D) => number>>
  ): D[] => {
    if (!sortState.column || !sortState.direction) {
      return data;
    }
    
    const comparator = comparators[sortState.column];
    if (!comparator) {
      return data;
    }

    const sorted = [...data].sort(comparator);
    return sortState.direction === 'desc' ? sorted.reverse() : sorted;
  }, [sortState]);

  return { sortState, handleSort, sortData, setSortState };
}

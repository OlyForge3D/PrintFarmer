import { useState, useCallback } from 'react';

/**
 * Sort direction for columns
 */
export type SortDirection = 'asc' | 'desc' | null;

export type TableSortValue = string | number | boolean | null | undefined;

export function compareTableValues(a: TableSortValue, b: TableSortValue, direction: Exclude<SortDirection, null>): number {
  const aMissing = a == null || (typeof a === 'number' && !Number.isFinite(a));
  const bMissing = b == null || (typeof b === 'number' && !Number.isFinite(b));
  if (aMissing || bMissing) return aMissing === bMissing ? 0 : aMissing ? 1 : -1;
  const comparison = typeof a === 'number' && typeof b === 'number'
    ? a - b
    : typeof a === 'boolean' && typeof b === 'boolean'
      ? Number(a) - Number(b)
      : String(a).localeCompare(String(b));
  return direction === 'desc' ? -comparison : comparison;
}

/**
 * Sort state for a table
 */
export interface SortState<T extends string = string> {
  column: T | null;
  direction: SortDirection;
}

export function nextTableSort<T extends string>(state: SortState<T>, column: T): SortState<T> {
  if (state.column === column && state.direction === 'asc') {
    return { column, direction: 'desc' };
  }
  if (state.column === column && state.direction === 'desc') {
    return { column: null, direction: null };
  }
  return { column, direction: 'asc' };
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
    setSortState(prev => nextTableSort(prev, column as T));
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

    const direction = sortState.direction === 'desc' ? -1 : 1;
    return [...data].sort((a, b) => direction * comparator(a, b));
  }, [sortState]);

  const sortDataByValues = useCallback(<D,>(
    data: D[],
    values: Partial<Record<T, (row: D) => TableSortValue>>,
  ): D[] => {
    if (!sortState.column || !sortState.direction) return data;
    const value = values[sortState.column];
    if (!value) return data;
    const direction = sortState.direction;
    return [...data].sort((a, b) => compareTableValues(value(a), value(b), direction));
  }, [sortState]);

  const headerProps = (column: T) => ({
    sortable: true,
    sortKey: column,
    sortDirection: sortState.column === column ? sortState.direction : null,
    onSort: handleSort,
  });

  return { sortState, handleSort, sortData, sortDataByValues, headerProps, setSortState };
}

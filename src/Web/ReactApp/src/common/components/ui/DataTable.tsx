import React, { useState, useCallback, useMemo } from 'react';
import { Table, TableHead, TableBody, TableRow, TableHeaderCell, TableCell } from './Table';
import type { SortDirection } from '../../hooks/useTableSort';

/**
 * Column definition for DataTable
 */
export interface DataTableColumn<T> {
  /** Unique key for the column */
  key: string;
  /** Header text */
  header: React.ReactNode;
  /** Whether this column is sortable */
  sortable?: boolean;
  /** Sort comparator function (required if sortable) */
  sort?: (a: T, b: T) => number;
  /** Render function for cell content */
  render: (item: T) => React.ReactNode;
  /** Optional className for the header cell */
  headerClassName?: string;
  /** Optional className for data cells */
  cellClassName?: string;
}

/**
 * Props for DataTable component
 */
export interface DataTableProps<T> {
  /** Array of data items to display */
  data: T[];
  /** Column definitions */
  columns: DataTableColumn<T>[];
  /** Function to get unique key for each row */
  getRowKey: (item: T) => string | number;
  /** Enable keyboard navigation */
  keyboardNavigation?: boolean;
  /** Default sort column key */
  defaultSortColumn?: string;
  /** Default sort direction */
  defaultSortDirection?: SortDirection;
  /** Callback when a row is selected via keyboard (Enter) or click */
  onRowSelect?: (item: T, index: number) => void;
  /** Callback when a row is focused via keyboard */
  onRowFocus?: (item: T, index: number) => void;
  /** Optional render function for action column */
  renderActions?: (item: T) => React.ReactNode;
  /** Label for actions column (default: "Actions") */
  actionsHeader?: React.ReactNode;
  /** Width class for actions column (default: "w-24") */
  actionsWidth?: string;
  /** Empty state message */
  emptyMessage?: React.ReactNode;
  /** Additional className for the table */
  className?: string;
}

/**
 * DataTable - A higher-level table component with built-in sorting and keyboard navigation
 * 
 * @example
 * ```tsx
 * <DataTable
 *   data={filaments}
 *   columns={[
 *     { 
 *       key: 'name', 
 *       header: 'Name', 
 *       sortable: true, 
 *       sort: (a, b) => a.name.localeCompare(b.name),
 *       render: (item) => <span className="font-medium">{item.name}</span>,
 *     },
 *     { 
 *       key: 'temp', 
 *       header: 'Temperature', 
 *       sortable: true,
 *       sort: (a, b) => (a.temp ?? 0) - (b.temp ?? 0),
 *       render: (item) => item.temp ? `${item.temp}°C` : '—',
 *     },
 *   ]}
 *   getRowKey={(item) => item.id}
 *   keyboardNavigation
 *   defaultSortColumn="name"
 *   renderActions={(item) => (
 *     <Button onClick={() => handleEdit(item)}>Edit</Button>
 *   )}
 * />
 * ```
 */
export function DataTable<T>({
  data,
  columns,
  getRowKey,
  keyboardNavigation = false,
  defaultSortColumn,
  defaultSortDirection = 'asc',
  onRowSelect,
  onRowFocus,
  renderActions,
  actionsHeader = 'Actions',
  actionsWidth = 'w-24',
  emptyMessage = 'No data available.',
  className,
}: DataTableProps<T>) {
  // Internal sort state
  const [sortColumn, setSortColumn] = useState<string | null>(defaultSortColumn ?? null);
  const [sortDirection, setSortDirection] = useState<SortDirection>(
    defaultSortColumn ? defaultSortDirection : null
  );

  // Handle sort column click
  const handleSort = useCallback((columnKey: string) => {
    setSortColumn(prev => {
      if (prev === columnKey) {
        // Cycle direction: asc -> desc -> null
        setSortDirection(dir => {
          if (dir === 'asc') return 'desc';
          if (dir === 'desc') {
            // Reset to no sorting
            return null;
          }
          return 'asc';
        });
        // If we're going to null direction, also clear column
        if (sortDirection === 'desc') {
          return null;
        }
        return columnKey;
      }
      // New column
      setSortDirection('asc');
      return columnKey;
    });
  }, [sortDirection]);

  // Get column by key
  const getColumn = useCallback((key: string) => {
    return columns.find(c => c.key === key);
  }, [columns]);

  // Sort data
  const sortedData = useMemo(() => {
    if (!sortColumn || !sortDirection) {
      return data;
    }

    const column = getColumn(sortColumn);
    if (!column?.sort) {
      return data;
    }

    const sorted = [...data].sort(column.sort);
    return sortDirection === 'desc' ? sorted.reverse() : sorted;
  }, [data, sortColumn, sortDirection, getColumn]);

  // Handle row selection
  const handleRowSelect = useCallback((index: number) => {
    if (onRowSelect && sortedData[index]) {
      onRowSelect(sortedData[index], index);
    }
  }, [onRowSelect, sortedData]);

  // Handle row focus
  const handleRowFocus = useCallback((index: number) => {
    if (onRowFocus && sortedData[index]) {
      onRowFocus(sortedData[index], index);
    }
  }, [onRowFocus, sortedData]);

  // Empty state
  if (sortedData.length === 0) {
    return (
      <div className="text-center py-12 text-pf-text-secondary">
        {emptyMessage}
      </div>
    );
  }

  return (
    <Table
      keyboardNavigation={keyboardNavigation}
      onRowSelect={handleRowSelect}
      onRowFocus={handleRowFocus}
      className={className}
    >
      <TableHead>
        <TableRow isHoverable={false}>
          {columns.map(column => (
            <TableHeaderCell
              key={column.key}
              sortable={column.sortable}
              sortKey={column.key}
              sortDirection={sortColumn === column.key ? sortDirection : null}
              onSort={column.sortable ? handleSort : undefined}
              className={column.headerClassName}
            >
              {column.header}
            </TableHeaderCell>
          ))}
          {renderActions && (
            <TableHeaderCell className={actionsWidth}>
              {actionsHeader}
            </TableHeaderCell>
          )}
        </TableRow>
      </TableHead>
      <TableBody>
        {sortedData.map((item, index) => (
          <TableRow key={getRowKey(item)} rowIndex={index}>
            {columns.map(column => (
              <TableCell key={column.key} className={column.cellClassName}>
                {column.render(item)}
              </TableCell>
            ))}
            {renderActions && (
              <TableCell>
                {renderActions(item)}
              </TableCell>
            )}
          </TableRow>
        ))}
      </TableBody>
    </Table>
  );
}

export default DataTable;

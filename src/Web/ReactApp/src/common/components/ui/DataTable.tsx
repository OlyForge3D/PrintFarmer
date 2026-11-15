import React, { useState, useCallback, useId, useMemo } from 'react';
import { Table, TableHead, TableBody, TableRow, TableHeaderCell, TableCell } from './Table';
import { compareTableValues, nextTableSort, useTableSort, type SortDirection, type TableSortValue } from '../../hooks/useTableSort';

const INTERACTIVE_ROW_CHILD_SELECTOR = [
  'a',
  'button',
  'input',
  'label',
  'select',
  'summary',
  'textarea',
  '[contenteditable]:not([contenteditable="false"])',
  '[role="button"]',
  '[role="checkbox"]',
  '[role="combobox"]',
  '[role="link"]',
  '[role="menuitem"]',
  '[role="menuitemcheckbox"]',
  '[role="menuitemradio"]',
  '[role="option"]',
  '[role="radio"]',
  '[role="scrollbar"]',
  '[role="searchbox"]',
  '[role="slider"]',
  '[role="spinbutton"]',
  '[role="switch"]',
  '[role="tab"]',
  '[role="textbox"]',
  '[role="treeitem"]',
].join(', ');

function isInteractiveRowDescendant(
  target: EventTarget | null,
  row: HTMLTableRowElement,
): boolean {
  if (!(target instanceof Element)) {
    return false;
  }

  if (!row.contains(target)) {
    return true;
  }

  const interactiveDescendant = target.closest(INTERACTIVE_ROW_CHILD_SELECTOR);
  if (interactiveDescendant && row.contains(interactiveDescendant)) {
    return true;
  }

  let candidate: Element | null = target;
  while (candidate && candidate !== row) {
    if (
      (candidate instanceof HTMLElement || candidate instanceof SVGElement)
      && candidate.hasAttribute('tabindex')
      && candidate.tabIndex >= 0
    ) {
      return true;
    }
    candidate = candidate.parentElement;
  }

  return false;
}

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
  /** Sort comparator function (provide this or sortValue for local sorting) */
  sort?: (a: T, b: T) => number;
  /** Raw display value; missing/non-finite values remain last in either direction. */
  sortValue?: (item: T) => TableSortValue;
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
interface DataTableBaseProps<T> {
  /** Array of data items to display */
  data: T[];
  /** Column definitions */
  columns: DataTableColumn<T>[];
  /** Function to get unique key for each row */
  getRowKey: (item: T) => string | number;
  /** Enable keyboard navigation; inferred from row selection only when omitted */
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
  /** Accessible name for the table or keyboard-navigable grid */
  ariaLabel?: string;
}

export type DataTableProps<T> = DataTableBaseProps<T> & (
  | { sortColumn?: never; sortDirection?: never; onSortChange?: never }
  | {
    /** Controlled sorting leaves the supplied data order unchanged (including server pages). */
    sortColumn: string | null;
    sortDirection: SortDirection;
    onSortChange: (column: string | null, direction: SortDirection) => void;
  }
);

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
  keyboardNavigation,
  defaultSortColumn,
  defaultSortDirection = 'asc',
  sortColumn: controlledSortColumn,
  sortDirection: controlledSortDirection,
  onSortChange,
  onRowSelect,
  onRowFocus,
  renderActions,
  actionsHeader = 'Actions',
  actionsWidth = 'w-24',
  emptyMessage = 'No data available.',
  className,
  ariaLabel,
}: DataTableProps<T>) {
  const keyboardNavigationEnabled = keyboardNavigation ?? Boolean(onRowSelect);
  const rowIdPrefix = useId();
  const [activeRowKey, setActiveRowKey] = useState<string | number | null>(null);
  const [selectedRowKey, setSelectedRowKey] = useState<string | number | null>(null);

  const { sortState, handleSort: handleInternalSort } = useTableSort(defaultSortColumn, defaultSortDirection);
  const controlled = controlledSortColumn !== undefined;
  const sortColumn = controlled ? controlledSortColumn : sortState.column;
  const sortDirection = controlled ? controlledSortDirection : sortState.direction;

  // Handle sort column click
  const handleSort = useCallback((columnKey: string) => {
    if (controlled) {
      const next = nextTableSort({ column: sortColumn, direction: sortDirection ?? null }, columnKey);
      onSortChange?.(next.column, next.direction);
    } else {
      handleInternalSort(columnKey);
    }
  }, [controlled, sortColumn, sortDirection, onSortChange, handleInternalSort]);

  // Get column by key
  const getColumn = useCallback((key: string) => {
    return columns.find(c => c.key === key);
  }, [columns]);

  // Sort data
  const sortedData = useMemo(() => {
    if (controlled || !sortColumn || !sortDirection) {
      return data;
    }

    const column = getColumn(sortColumn);
    const sortValue = column?.sortValue;
    if (sortValue) {
      return [...data].sort((a, b) => compareTableValues(sortValue(a), sortValue(b), sortDirection));
    }
    if (!column?.sort) {
      return data;
    }

    const comparator = column.sort;
    const direction = sortDirection === 'desc' ? -1 : 1;
    return [...data].sort((a, b) => direction * comparator(a, b));
  }, [controlled, data, sortColumn, sortDirection, getColumn]);

  // Handle row selection
  const handleRowSelect = useCallback((index: number) => {
    if (onRowSelect && sortedData[index]) {
      setSelectedRowKey(getRowKey(sortedData[index]));
      onRowSelect(sortedData[index], index);
    }
  }, [getRowKey, onRowSelect, sortedData]);

  const handleRowClick = useCallback((
    event: React.MouseEvent<HTMLTableRowElement>,
    index: number,
  ) => {
    if (isInteractiveRowDescendant(event.target, event.currentTarget)) {
      return;
    }

    handleRowSelect(index);
  }, [handleRowSelect]);

  // Handle row focus
  const handleRowFocus = useCallback((index: number) => {
    if (sortedData[index]) {
      setActiveRowKey(getRowKey(sortedData[index]));
      onRowFocus?.(sortedData[index], index);
    }
  }, [getRowKey, onRowFocus, sortedData]);

  const activeRowIndex = useMemo(() => {
    if (activeRowKey === null) {
      return -1;
    }

    return sortedData.findIndex((item) => getRowKey(item) === activeRowKey);
  }, [activeRowKey, getRowKey, sortedData]);

  const getAccessibleRowId = useCallback((index: number) => {
    if (index < 0 || index >= sortedData.length) {
      return undefined;
    }

    return `pf-data-table-${rowIdPrefix}-row-${encodeURIComponent(String(getRowKey(sortedData[index])))}`;
  }, [getRowKey, rowIdPrefix, sortedData]);

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
      keyboardNavigation={keyboardNavigationEnabled}
      onRowSelect={handleRowSelect}
      onRowFocus={handleRowFocus}
      className={className}
      rowCount={sortedData.length}
      focusedRowIndex={activeRowIndex}
      selectionEnabled={Boolean(onRowSelect)}
      getRowId={getAccessibleRowId}
      aria-label={ariaLabel}
    >
      <TableHead>
        <TableRow isHoverable={false} rowIndex={-1}>
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
          <TableRow
            key={getRowKey(item)}
            rowIndex={index}
            isSelected={Boolean(onRowSelect) && selectedRowKey === getRowKey(item)}
            isHoverable={Boolean(onRowSelect)}
            onClick={onRowSelect ? (event) => handleRowClick(event, index) : undefined}
          >
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

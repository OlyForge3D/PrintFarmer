import React, { useCallback, useRef, useState, useEffect, createContext, useContext } from 'react';
import type { SortDirection } from '../../hooks/useTableSort';

// ============================================================================
// Types
// ============================================================================

/**
 * Table context for keyboard navigation
 */
interface TableContextValue {
  focusedRowIndex: number;
  setFocusedRowIndex: (index: number) => void;
  rowCount: number;
  registerRow: () => number;
  unregisterRow: (index: number) => void;
}

const TableContext = createContext<TableContextValue | null>(null);

/**
 * Table component props
 */
export interface TableProps extends React.HTMLAttributes<HTMLTableElement> {
  children: React.ReactNode;
  /** Enable keyboard navigation with arrow keys */
  keyboardNavigation?: boolean;
  /** Callback when a row is focused via keyboard */
  onRowFocus?: (index: number) => void;
  /** Callback when Enter is pressed on a focused row */
  onRowSelect?: (index: number) => void;
}

/**
 * Table header props
 */
export interface TableHeadProps extends React.HTMLAttributes<HTMLTableSectionElement> {
  children: React.ReactNode;
}

/**
 * Table body props
 */
export interface TableBodyProps extends React.HTMLAttributes<HTMLTableSectionElement> {
  children: React.ReactNode;
}

/**
 * Table row props
 */
export interface TableRowProps extends React.HTMLAttributes<HTMLTableRowElement> {
  children: React.ReactNode;
  isSelected?: boolean;
  isHoverable?: boolean;
  /** Row index for keyboard navigation (auto-assigned if using TableBody) */
  rowIndex?: number;
}

/**
 * Table header cell props
 */
export interface TableHeaderCellProps extends React.ThHTMLAttributes<HTMLTableCellElement> {
  children: React.ReactNode;
  /** Enable sorting for this column */
  sortable?: boolean;
  /** Current sort direction */
  sortDirection?: SortDirection;
  /** Column key for sorting */
  sortKey?: string;
  /** Callback when sort is requested */
  onSort?: (key: string) => void;
}

/**
 * Table cell props
 */
export interface TableCellProps extends React.TdHTMLAttributes<HTMLTableCellElement> {
  children: React.ReactNode;
}

// ============================================================================
// Hook: useTableKeyboardNavigation
// ============================================================================

/**
 * Hook for managing keyboard navigation in tables
 */
function useTableKeyboardNavigation(
  enabled: boolean,
  onRowFocus?: (index: number) => void,
  onRowSelect?: (index: number) => void
) {
  const [focusedRowIndex, setFocusedRowIndex] = useState(-1);
  const [rowCount, setRowCount] = useState(0);
  const rowIndexCounter = useRef(0);

  const registerRow = useCallback(() => {
    const index = rowIndexCounter.current++;
    setRowCount(c => c + 1);
    return index;
  }, []);

  const unregisterRow = useCallback(() => {
    setRowCount(c => Math.max(0, c - 1));
  }, []);

  // Reset counter when rows change
  useEffect(() => {
    rowIndexCounter.current = 0;
  }, [rowCount]);

  const handleKeyDown = useCallback((e: React.KeyboardEvent) => {
    if (!enabled || rowCount === 0) return;

    switch (e.key) {
      case 'ArrowDown':
        e.preventDefault();
        setFocusedRowIndex(prev => {
          const next = prev < rowCount - 1 ? prev + 1 : prev;
          onRowFocus?.(next);
          return next;
        });
        break;
      case 'ArrowUp':
        e.preventDefault();
        setFocusedRowIndex(prev => {
          const next = prev > 0 ? prev - 1 : 0;
          onRowFocus?.(next);
          return next;
        });
        break;
      case 'Home':
        e.preventDefault();
        setFocusedRowIndex(0);
        onRowFocus?.(0);
        break;
      case 'End':
        e.preventDefault();
        setFocusedRowIndex(rowCount - 1);
        onRowFocus?.(rowCount - 1);
        break;
      case 'Enter':
      case ' ':
        if (focusedRowIndex >= 0) {
          e.preventDefault();
          onRowSelect?.(focusedRowIndex);
        }
        break;
    }
  }, [enabled, rowCount, focusedRowIndex, onRowFocus, onRowSelect]);

  const setFocusedRow = useCallback((index: number) => {
    setFocusedRowIndex(index);
    onRowFocus?.(index);
  }, [onRowFocus]);

  return {
    focusedRowIndex,
    setFocusedRowIndex: setFocusedRow,
    rowCount,
    registerRow,
    unregisterRow,
    handleKeyDown,
  };
}

// ============================================================================
// Components
// ============================================================================

/**
 * Reusable Table component with consistent styling
 * 
 * Supports:
 * - Keyboard navigation (arrow keys, Home, End, Enter)
 * - Sortable columns
 * - Row selection and hover states
 * - Full accessibility with ARIA attributes
 */
export function Table({ 
  children, 
  className = '', 
  keyboardNavigation = false,
  onRowFocus,
  onRowSelect,
  ...props 
}: TableProps) {
  const tableRef = useRef<HTMLTableElement>(null);
  const {
    focusedRowIndex,
    setFocusedRowIndex,
    rowCount,
    registerRow,
    unregisterRow,
    handleKeyDown,
  } = useTableKeyboardNavigation(keyboardNavigation, onRowFocus, onRowSelect);

  return (
    <TableContext.Provider value={{ 
      focusedRowIndex, 
      setFocusedRowIndex, 
      rowCount, 
      registerRow, 
      unregisterRow 
    }}>
      <div className="overflow-x-auto rounded-lg border border-gray-200 dark:border-gray-700">
        <table
          ref={tableRef}
          className={`min-w-full divide-y divide-gray-200 dark:divide-gray-700 ${className}`}
          tabIndex={keyboardNavigation ? 0 : undefined}
          onKeyDown={keyboardNavigation ? handleKeyDown : undefined}
          {...props}
        >
          {children}
        </table>
      </div>
    </TableContext.Provider>
  );
}

/**
 * Table header section
 */
export function TableHead({ children, className = '', ...props }: TableHeadProps) {
  return (
    <thead
      className={`bg-gray-50 dark:bg-gray-800 ${className}`}
      {...props}
    >
      {children}
    </thead>
  );
}

/**
 * Table body section
 */
export function TableBody({ children, className = '', ...props }: TableBodyProps) {
  return (
    <tbody
      className={`divide-y divide-gray-200 dark:divide-gray-700 bg-white dark:bg-gray-900 ${className}`}
      {...props}
    >
      {children}
    </tbody>
  );
}

/**
 * Table row with keyboard navigation support
 */
export function TableRow({ 
  children, 
  className = '', 
  isSelected = false, 
  isHoverable = true,
  rowIndex,
  onClick,
  ...props 
}: TableRowProps) {
  const context = useContext(TableContext);
  const rowRef = useRef<HTMLTableRowElement>(null);
  // Use provided rowIndex directly, or track assigned index from context registration
  const [registeredIndex, setRegisteredIndex] = useState(-1);
  const assignedIndex = rowIndex !== undefined ? rowIndex : registeredIndex;

  // Register row for keyboard navigation (only if no explicit rowIndex provided)
  useEffect(() => {
    if (context && rowIndex === undefined) {
      const idx = context.registerRow();
      setRegisteredIndex(idx);
      return () => context.unregisterRow(idx);
    }
  }, [context, rowIndex]);

  // Focus row when it becomes the focused row
  useEffect(() => {
    if (context && assignedIndex >= 0 && context.focusedRowIndex === assignedIndex) {
      rowRef.current?.scrollIntoView({ block: 'nearest', behavior: 'smooth' });
    }
  }, [context, assignedIndex]);

  const isFocused = context ? context.focusedRowIndex === assignedIndex : false;
  const selectedClass = isSelected ? 'bg-blue-50 dark:bg-blue-900/20' : '';
  const hoverClass = isHoverable ? 'hover:bg-gray-50 dark:hover:bg-gray-800/50' : '';
  const focusClass = isFocused ? 'ring-2 ring-inset ring-blue-500 dark:ring-blue-400' : '';
  
  const handleClick = (e: React.MouseEvent<HTMLTableRowElement>) => {
    if (context && assignedIndex >= 0) {
      context.setFocusedRowIndex(assignedIndex);
    }
    onClick?.(e);
  };

  return (
    <tr
      ref={rowRef}
      className={`${selectedClass} ${hoverClass} ${focusClass} ${className}`}
      data-selected={isSelected || undefined}
      data-rowindex={assignedIndex >= 0 ? assignedIndex + 1 : undefined}
      onClick={handleClick}
      {...props}
    >
      {children}
    </tr>
  );
}

/**
 * Sort indicator icon
 */
function SortIndicator({ direction }: { direction: SortDirection }) {
  return (
    <span className="ml-1 inline-flex flex-col text-xs leading-none" aria-hidden="true">
      <svg 
        className={`w-3 h-3 ${direction === 'asc' ? 'text-blue-500' : 'text-gray-300 dark:text-gray-600'}`}
        viewBox="0 0 24 24"
      >
        <path fill="currentColor" d="M7,15L12,10L17,15H7Z" />
      </svg>
      <svg 
        className={`w-3 h-3 -mt-1 ${direction === 'desc' ? 'text-blue-500' : 'text-gray-300 dark:text-gray-600'}`}
        viewBox="0 0 24 24"
      >
        <path fill="currentColor" d="M7,10L12,15L17,10H7Z" />
      </svg>
    </span>
  );
}

/**
 * Table header cell with optional sorting
 */
export function TableHeaderCell({ 
  children, 
  className = '', 
  sortable = false,
  sortDirection = null,
  sortKey,
  onSort,
  ...props 
}: TableHeaderCellProps) {
  const handleClick = () => {
    if (sortable && sortKey && onSort) {
      onSort(sortKey);
    }
  };

  const sortableClass = sortable ? 'cursor-pointer select-none hover:bg-gray-100 dark:hover:bg-gray-700 transition-colors' : '';
  
  // Compute aria-sort value as a constant
  const ariaSortValue: 'ascending' | 'descending' | 'none' | undefined = 
    sortDirection === 'asc' ? 'ascending' : 
    sortDirection === 'desc' ? 'descending' : 
    sortable ? 'none' : undefined;
  
  return (
    <th
      className={`px-4 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider ${sortableClass} ${className}`}
      onClick={sortable ? handleClick : undefined}
      aria-sort={ariaSortValue}
      {...props}
    >
      <div className="flex items-center">
        {children}
        {sortable && <SortIndicator direction={sortDirection} />}
      </div>
    </th>
  );
}

/**
 * Table data cell
 */
export function TableCell({ children, className = '', ...props }: TableCellProps) {
  return (
    <td
      className={`px-4 py-3 text-sm text-gray-900 dark:text-gray-100 ${className}`}
      {...props}
    >
      {children}
    </td>
  );
}

// Export all table components
Table.Head = TableHead;
Table.Body = TableBody;
Table.Row = TableRow;
Table.HeaderCell = TableHeaderCell;
Table.Cell = TableCell;

export default Table;

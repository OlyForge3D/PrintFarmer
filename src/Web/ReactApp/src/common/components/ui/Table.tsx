import React, { useCallback, useRef, useState, useEffect, createContext, useContext, useId } from 'react';
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
  keyboardNavigation: boolean;
  selectionEnabled: boolean;
  getRowId: (index: number) => string | undefined;
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
  /** Explicit navigable row count when rows provide their own rowIndex values */
  rowCount?: number;
  /** Whether data rows expose selection state */
  selectionEnabled?: boolean;
  /** Resolve the DOM id for a navigable row */
  getRowId?: (index: number) => string | undefined;
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
  /** Explicitly opt into a row-level hover affordance. */
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
  onRowSelect?: (index: number) => void,
  explicitRowCount?: number
) {
  const [requestedFocusedRowIndex, setRequestedFocusedRowIndex] = useState(-1);
  const [registeredRowCount, setRegisteredRowCount] = useState(0);
  const rowCount = explicitRowCount ?? registeredRowCount;
  const focusedRowIndex = requestedFocusedRowIndex >= 0 && requestedFocusedRowIndex < rowCount
    ? requestedFocusedRowIndex
    : -1;
  const rowIndexCounter = useRef(0);

  const registerRow = useCallback(() => {
    const index = rowIndexCounter.current++;
    setRegisteredRowCount(c => c + 1);
    return index;
  }, []);

  const unregisterRow = useCallback(() => {
    setRegisteredRowCount(c => Math.max(0, c - 1));
  }, []);

  // Reset counter when rows change
  useEffect(() => {
    rowIndexCounter.current = 0;
  }, [registeredRowCount]);

  const handleKeyDown = useCallback((e: React.KeyboardEvent) => {
    if (!enabled || rowCount === 0 || e.target !== e.currentTarget) return;

    switch (e.key) {
      case 'ArrowDown':
        e.preventDefault();
        setRequestedFocusedRowIndex(prev => {
          const current = prev >= 0 && prev < rowCount ? prev : -1;
          const next = current < rowCount - 1 ? current + 1 : current;
          onRowFocus?.(next);
          return next;
        });
        break;
      case 'ArrowUp':
        e.preventDefault();
        setRequestedFocusedRowIndex(prev => {
          const current = prev >= 0 && prev < rowCount ? prev : 0;
          const next = current > 0 ? current - 1 : 0;
          onRowFocus?.(next);
          return next;
        });
        break;
      case 'Home':
        e.preventDefault();
        setRequestedFocusedRowIndex(0);
        onRowFocus?.(0);
        break;
      case 'End':
        e.preventDefault();
        setRequestedFocusedRowIndex(rowCount - 1);
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
    setRequestedFocusedRowIndex(index);
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
  rowCount: explicitRowCount,
  selectionEnabled = Boolean(onRowSelect),
  getRowId,
  ...props 
}: TableProps) {
  const tableRef = useRef<HTMLTableElement>(null);
  const generatedTableId = useId();
  const getDefaultRowId = useCallback(
    (index: number) => `pf-table-${generatedTableId}-row-${index}`,
    [generatedTableId],
  );
  const resolveRowId = getRowId ?? getDefaultRowId;
  const {
    focusedRowIndex,
    setFocusedRowIndex,
    rowCount,
    registerRow,
    unregisterRow,
    handleKeyDown,
  } = useTableKeyboardNavigation(keyboardNavigation, onRowFocus, onRowSelect, explicitRowCount);
  const activeDescendantId = focusedRowIndex >= 0 ? resolveRowId(focusedRowIndex) : undefined;

  return (
    <TableContext.Provider value={{ 
      focusedRowIndex, 
      setFocusedRowIndex, 
      rowCount, 
      registerRow, 
      unregisterRow,
      keyboardNavigation,
      selectionEnabled,
      getRowId: resolveRowId,
    }}>
      <div className="overflow-x-auto rounded-lg border border-pf-border">
        <table
          ref={tableRef}
          className={`min-w-full divide-y divide-pf-border ${className}`}
          tabIndex={keyboardNavigation ? 0 : undefined}
          onKeyDown={keyboardNavigation ? handleKeyDown : undefined}
          {...props}
          role={keyboardNavigation ? 'grid' : props.role}
          aria-activedescendant={keyboardNavigation ? activeDescendantId : undefined}
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
  const context = useContext(TableContext);

  return (
    <thead
      className={`bg-pf-bg-1 ${className}`}
      {...props}
      role={context?.keyboardNavigation ? 'rowgroup' : props.role}
    >
      {children}
    </thead>
  );
}

/**
 * Table body section
 */
export function TableBody({ children, className = '', ...props }: TableBodyProps) {
  const context = useContext(TableContext);

  return (
    <tbody
      className={`divide-y divide-pf-border bg-pf-bg-0 ${className}`}
      {...props}
      role={context?.keyboardNavigation ? 'rowgroup' : props.role}
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
  isHoverable = false,
  rowIndex,
  onClick,
  id,
  role,
  ...props 
}: TableRowProps) {
  const context = useContext(TableContext);
  const rowRef = useRef<HTMLTableRowElement>(null);
  // Use provided rowIndex directly, or track assigned index from context registration
  const [registeredIndex, setRegisteredIndex] = useState(-1);
  const assignedIndex = rowIndex !== undefined ? rowIndex : registeredIndex;

  // Register row for keyboard navigation (only if no explicit rowIndex provided)
  useEffect(() => {
    if (context?.keyboardNavigation && rowIndex === undefined) {
      const idx = context.registerRow();
      // React Compiler disallows setState directly in an effect; defer row index assignment
      // while preserving sibling registration order for keyboard navigation.
      queueMicrotask(() => setRegisteredIndex(idx));
      return () => context.unregisterRow(idx);
    }
  }, [context, rowIndex]);

  // Focus row when it becomes the focused row
  useEffect(() => {
    if (context && assignedIndex >= 0 && context.focusedRowIndex === assignedIndex) {
      rowRef.current?.scrollIntoView?.({ block: 'nearest', behavior: 'smooth' });
    }
  }, [context, assignedIndex]);

  const isFocused = context ? context.focusedRowIndex === assignedIndex : false;
  const rowId = context?.keyboardNavigation && assignedIndex >= 0
    ? context.getRowId(assignedIndex)
    : id;
  const selectedClass = isSelected ? 'bg-pf-accent-bg/15' : '';
  // The hover utility is withheld while the row is selected. `bg-*` compiles to
  // `background-color`, which replaces rather than layers, and `:hover` scores
  // (0,1,1) against the selected class's (0,1,0) inside the same `utilities`
  // layer — so an unconditional hover repainted a selected row and erased its
  // highlight. Measured in chromium across all seven themes. See #1088.
  const hoverClass = isHoverable && !isSelected ? 'hover:bg-pf-bg-1' : '';
  const focusClass = isFocused ? 'ring-2 ring-inset ring-pf-accent' : '';
  
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
      id={rowId}
      role={context?.keyboardNavigation ? 'row' : role}
      aria-selected={context?.selectionEnabled ? isSelected : props['aria-selected']}
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
        className={`w-3 h-3 ${direction === 'asc' ? 'text-pf-accent' : 'text-pf-text-secondary'}`}
        viewBox="0 0 24 24"
      >
        <path fill="currentColor" d="M7,15L12,10L17,15H7Z" />
      </svg>
      <svg 
        className={`w-3 h-3 -mt-1 ${direction === 'desc' ? 'text-pf-accent' : 'text-pf-text-secondary'}`}
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
  const context = useContext(TableContext);
  const handleClick = () => {
    if (sortable && sortKey && onSort) {
      onSort(sortKey);
    }
  };

  const sortableClass = sortable ? 'cursor-pointer select-none hover:bg-pf-bg-1 transition-colors' : '';
  
  // Compute aria-sort value as a constant
  const ariaSortValue: 'ascending' | 'descending' | 'none' | undefined = 
    sortDirection === 'asc' ? 'ascending' : 
    sortDirection === 'desc' ? 'descending' : 
    sortable ? 'none' : undefined;
  
  return (
    <th
      className={`px-4 py-3 text-left text-xs font-medium text-pf-text-secondary uppercase tracking-wider ${sortableClass} ${className}`}
      onClick={sortable ? handleClick : undefined}
      aria-sort={ariaSortValue}
      {...props}
      role={context?.keyboardNavigation ? 'columnheader' : props.role}
      scope={props.scope ?? 'col'}
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
  const context = useContext(TableContext);

  return (
    <td
      className={`px-4 py-3 text-sm text-pf-text-primary ${className}`}
      {...props}
      role={context?.keyboardNavigation ? 'gridcell' : props.role}
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

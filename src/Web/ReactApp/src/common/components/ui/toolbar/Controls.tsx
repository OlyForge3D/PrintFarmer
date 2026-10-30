import { useId, useRef, type ChangeEvent } from 'react';
import clsx from 'clsx';
import { mdiClose, mdiMagnify, mdiRefresh, mdiFilterVariant, mdiChevronLeft, mdiChevronRight } from '@mdi/js';
import { Button, type ButtonProps } from '@/common/components/ui/Button';
import { Input, type InputProps } from '@/common/components/ui/Input';
import { Select, type SelectProps } from '@/common/components/ui/Select';
import { SORT_DIRECTION_ICONS } from '@/common/components/ui/toolbar/sortIcons';
import { Toolbar } from '@/common/components/ui/toolbar/Toolbar';

export function ControlIcon({ path, className = 'w-4 h-4' }: { path: string; className?: string }) {
  return <svg viewBox="0 0 24 24" className={className} aria-hidden="true"><path fill="currentColor" d={path} /></svg>;
}

interface SearchFieldProps extends Omit<InputProps, 'value' | 'type'> {
  value: string;
  onClear: () => void;
  label?: string;
  containerClassName?: string;
}
export function SearchField({ value, onClear, label = 'Search', id, containerClassName, className, ...props }: SearchFieldProps) {
  const generatedId = useId();
  const inputRef = useRef<HTMLInputElement>(null);
  const fieldId = id ?? generatedId;
  return <div className={clsx('pf-toolbar__search pf-search', containerClassName)}>
    <ControlIcon path={mdiMagnify} />
    <Input {...props} ref={inputRef} id={fieldId} type="search" value={value}
      aria-label={props['aria-label'] ?? (props['aria-labelledby'] ? undefined : label)}
      className={clsx('pf-control', className)} data-role="field" />
    {value && <Button type="button" variant="ghost" control className="pf-control--icon pf-search__clear"
      aria-label="Clear search" title="Clear search" disabled={props.disabled} onClick={() => { onClear(); inputRef.current?.focus(); }}
      iconCenter={<ControlIcon path={mdiClose} />} />}
  </div>;
}

export function SortSelect({ className, containerClassName, ...props }: SelectProps) {
  return <Select {...props} aria-label={props['aria-label'] ?? 'Sort by'}
    containerClassName={clsx('w-auto max-w-full', containerClassName)}
    className={clsx('pf-control', className)} data-role="field" />;
}

export function SortDirectionButton({ direction, onClick, ...props }: Omit<ButtonProps, 'children' | 'onClick'> & {
  direction: 'asc' | 'desc'; onClick: () => void;
}) {
  const ascending = direction === 'asc';
  return <Button {...props} type="button" control variant={props.variant ?? 'secondary'}
    className={clsx('pf-control--icon', props.className)} onClick={onClick}
    aria-label={`Sort direction: ${ascending ? 'ascending' : 'descending'}`}
    title={`${ascending ? 'Ascending (click for descending)' : 'Descending (click for ascending)'}`}
    iconCenter={<ControlIcon path={SORT_DIRECTION_ICONS[direction]} />} />;
}

export function SortControl({ direction, onDirectionChange, children, ...props }: SelectProps & {
  direction: 'asc' | 'desc'; onDirectionChange: () => void;
}) {
  return <Toolbar.Group className="pf-toolbar__group--nowrap">
    <SortSelect {...props}>{children}</SortSelect>
    <SortDirectionButton direction={direction} onClick={onDirectionChange} disabled={props.disabled} />
  </Toolbar.Group>;
}

export function FilterChip({ selected, className, ...props }: ButtonProps & { selected: boolean }) {
  return <Button {...props} type="button" variant="unstyled" aria-pressed={selected}
    data-role="button" className={clsx('pf-control text-sm font-medium transition-colors',
      selected ? 'bg-pf-accent-bg text-[var(--pf-on-accent)]' : 'bg-pf-panel text-pf-text-secondary hover:bg-pf-bg-2',
      'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-pf-accent disabled:opacity-50 disabled:cursor-not-allowed',
      className)} />;
}

export function FilterButton({ count = 0, expanded, children = 'Filters', ...props }: ButtonProps & {
  count?: number; expanded: boolean;
}) {
  return <Button {...props} type="button" variant={props.variant ?? 'secondary'} control aria-expanded={expanded}
    aria-label={props['aria-label'] ?? (typeof children === 'string' ? `${children}${count > 0 ? ` (${count})` : ''}` : undefined)}
    iconLeft={<ControlIcon path={mdiFilterVariant} />}>
    {children}{count > 0 && <span className="ml-1 text-xs"> ({count})</span>}
  </Button>;
}

export function RefreshButton({ busy = false, ...props }: Omit<ButtonProps, 'loading'> & { busy?: boolean }) {
  return <Button {...props} type="button" control variant={props.variant ?? 'secondary'}
    disabled={props.disabled || busy} aria-busy={busy}
    aria-label={props['aria-label'] ?? 'Refresh'} title={props.title ?? 'Refresh'}
    className={clsx('pf-control--icon', props.className)}
    iconCenter={<ControlIcon path={mdiRefresh} className={clsx('w-4 h-4', busy && 'motion-safe:animate-spin')} />} />;
}

export function Pagination({ page, totalPages, onPageChange, pageSize, pageSizeOptions, onPageSizeChange, disabled = false }: {
  page: number; totalPages: number; onPageChange: (page: number) => void; disabled?: boolean;
  pageSize?: number; pageSizeOptions?: readonly number[]; onPageSizeChange?: (size: number) => void;
}) {
  return <Toolbar aria-label="Pagination">
    {pageSize !== undefined && onPageSizeChange && <Select aria-label="Page size" value={pageSize}
      className="pf-control" data-role="field"
      containerClassName="w-auto" disabled={disabled}
      onChange={(event: ChangeEvent<HTMLSelectElement>) => onPageSizeChange(Number(event.target.value))}>
      {(pageSizeOptions ?? [10, 25, 50, 100]).map(size => <option value={size} key={size}>{size} per page</option>)}
    </Select>}
    <Toolbar.Spacer />
    <Button type="button" variant="secondary" control disabled={disabled || page <= 1} onClick={() => onPageChange(page - 1)}
      iconLeft={<ControlIcon path={mdiChevronLeft} />}>Previous</Button>
    <span className="text-sm text-pf-text-secondary">Page {page} of {totalPages}</span>
    <Button type="button" variant="secondary" control disabled={disabled || page >= totalPages} onClick={() => onPageChange(page + 1)}
      iconRight={<ControlIcon path={mdiChevronRight} />}>Next</Button>
  </Toolbar>;
}

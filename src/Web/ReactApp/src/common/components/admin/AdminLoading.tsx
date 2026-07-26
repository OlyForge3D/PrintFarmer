import type { CSSProperties } from 'react';
import clsx from 'clsx';
import { Spinner } from '@/common/components/ui';

export type AdminLoadingVariant = 'spinner' | 'table' | 'form' | 'list' | 'card-grid';

export interface AdminLoadingProps {
  /**
   * Skeleton shape. Prefer a variant that matches the eventual content so the
   * layout doesn't jump when data arrives. Default is `spinner` only for
   * indeterminate short waits (< 500ms expected).
   */
  variant?: AdminLoadingVariant;
  /**
   * Screen-reader label announced while loading. Defaults to `Loading`.
   * Keep it specific: "Loading printers", "Loading settings".
   */
  label?: string;
  /** Number of skeleton rows for `table` / `list` / `form`. Sensible per-variant defaults. */
  rows?: number;
  /** Number of columns for `variant='table'`. Defaults to 5, clamped 1..8. */
  cols?: number;
  /** Extra classes on the outer wrapper. */
  className?: string;
}

const CARD_GRID_ITEMS = 4;

function clampCols(cols: number): number {
  if (!Number.isFinite(cols) || cols < 1) return 1;
  if (cols > 8) return 8;
  return Math.floor(cols);
}

/**
 * Unified loading treatment for admin pages. Prefer a shape-matching variant over
 * `spinner` — a skeleton in the eventual layout keeps the page stable when data
 * arrives, and reads as "content is coming" rather than "we're stuck".
 *
 * All variants render a live region announcing `label` to assistive tech.
 */
export function AdminLoading({
  variant = 'spinner',
  label = 'Loading',
  rows,
  cols = 5,
  className,
}: AdminLoadingProps) {
  const commonA11yProps = {
    role: 'status' as const,
    'aria-busy': true,
    'aria-live': 'polite' as const,
    'aria-label': label,
  };

  if (variant === 'spinner') {
    return (
      <div
        {...commonA11yProps}
        data-testid="admin-loading-spinner"
        className={clsx('flex items-center justify-center py-16', className)}
      >
        <Spinner size="lg" aria-label={label} />
        <span className="sr-only">{label}</span>
      </div>
    );
  }

  if (variant === 'table') {
    const rowCount = rows ?? 6;
    const colCount = clampCols(cols);
    return (
      <div
        {...commonA11yProps}
        data-testid="admin-loading-table"
        className={clsx('border border-pf-border rounded-md overflow-hidden', className)}
      >
        <div className="bg-pf-bg-1 border-b border-pf-border px-4 py-3">
          <div className="pf-skeleton pf-animate-skeleton h-4 w-40 rounded-sm" />
        </div>
        <div className="divide-y divide-pf-border">
          {Array.from({ length: rowCount }).map((_, r) => (
            <div
              key={r}
              className="grid gap-3 px-4 py-3"
              style={{ gridTemplateColumns: `repeat(${colCount}, minmax(0, 1fr))` } as CSSProperties}
            >
              {Array.from({ length: colCount }).map((__, c) => (
                <div key={c} className="pf-skeleton pf-animate-skeleton h-3 rounded-sm" />
              ))}
            </div>
          ))}
        </div>
        <span className="sr-only">{label}</span>
      </div>
    );
  }

  if (variant === 'list') {
    const rowCount = rows ?? 5;
    return (
      <div
        {...commonA11yProps}
        data-testid="admin-loading-list"
        className={clsx('flex flex-col gap-2', className)}
      >
        {Array.from({ length: rowCount }).map((_, i) => (
          <div
            key={i}
            className="flex items-center gap-3 rounded-md border border-pf-border bg-pf-bg-1 px-3 py-3"
          >
            <div className="pf-skeleton pf-animate-skeleton h-8 w-8 rounded-full shrink-0" />
            <div className="flex-1 flex flex-col gap-1.5 min-w-0">
              <div className="pf-skeleton pf-animate-skeleton h-3 w-2/3 rounded-sm" />
              <div className="pf-skeleton pf-animate-skeleton h-3 w-1/3 rounded-sm" />
            </div>
          </div>
        ))}
        <span className="sr-only">{label}</span>
      </div>
    );
  }

  if (variant === 'form') {
    const rowCount = rows ?? 4;
    return (
      <div
        {...commonA11yProps}
        data-testid="admin-loading-form"
        className={clsx('flex flex-col gap-4', className)}
      >
        {Array.from({ length: rowCount }).map((_, i) => (
          <div key={i} className="flex flex-col gap-2">
            <div className="pf-skeleton pf-animate-skeleton h-3 w-32 rounded-sm" />
            <div className="pf-skeleton pf-animate-skeleton h-9 w-full rounded-sm" />
          </div>
        ))}
        <span className="sr-only">{label}</span>
      </div>
    );
  }

  // card-grid
  return (
    <div
      {...commonA11yProps}
      data-testid="admin-loading-card-grid"
      className={clsx(
        'grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-4',
        className,
      )}
    >
      {Array.from({ length: rows ?? CARD_GRID_ITEMS }).map((_, i) => (
        <div
          key={i}
          className="rounded-md border border-pf-border bg-pf-bg-1 p-4 flex flex-col gap-3"
        >
          <div className="pf-skeleton pf-animate-skeleton h-4 w-2/3 rounded-sm" />
          <div className="pf-skeleton pf-animate-skeleton h-3 w-full rounded-sm" />
          <div className="pf-skeleton pf-animate-skeleton h-3 w-5/6 rounded-sm" />
          <div className="pf-skeleton pf-animate-skeleton h-3 w-1/2 rounded-sm" />
        </div>
      ))}
      <span className="sr-only">{label}</span>
    </div>
  );
}

export default AdminLoading;

import clsx from 'clsx';
import type { ReactNode } from 'react';
import { Badge, type BadgeVariant } from '@/common/components/ui/Badge';

export interface AdminStatTileProps {
  /** Leading icon. Already sized by the caller; rendered `aria-hidden`. */
  icon?: ReactNode;
  /** Tailwind classes tinting the icon to the tile's status. */
  iconClassName?: string;
  /** The thing being reported on. */
  label: string;
  /** Status word — "Healthy", "Degraded". Omit for a tile with no status. */
  badge?: string;
  badgeVariant?: BadgeVariant;
  /**
   * A measurement: `12 ms`, `214 GB`, `18 of 18`.
   *
   * Rendered in `--pf-font-mono` with `tabular-nums` so digits align down a
   * column of tiles, per the design language's data-forward rule. Reserve it
   * for actual measurements — prose belongs in `detail`, because monospace
   * used as a costume for "technical" is just harder to read.
   */
  value?: ReactNode;
  /** Supporting prose. Rendered in the body face. */
  detail?: ReactNode;
  /** Border tint carrying the tile's status. Colour is never the only signal. */
  borderClassName?: string;
  /** Accessible name; falls back to `label`. */
  ariaLabel?: string;
  className?: string;
  /** Escape hatch for `data-*` attributes the page needs for testing. */
  dataAttributes?: Record<string, string>;
}

/**
 * One status tile in an admin health strip.
 *
 * Status reaches the user three ways — the badge word, the icon, and the border
 * tint — so the tile still reads correctly without colour vision, which is the
 * design language's floor for status.
 *
 * `value` and `detail` are separate on purpose. `value` is a measurement and
 * gets the mono face; `detail` is a sentence and gets the body face. Collapsing
 * them would either lose digit alignment or set prose in monospace.
 */
export function AdminStatTile({
  icon,
  iconClassName,
  label,
  badge,
  badgeVariant = 'default',
  value,
  detail,
  borderClassName,
  ariaLabel,
  className,
  dataAttributes,
}: AdminStatTileProps) {
  return (
    <div
      role="group"
      aria-label={ariaLabel ?? label}
      className={clsx(
        'flex flex-col gap-2 rounded-md border bg-pf-panel p-4',
        borderClassName,
        className,
      )}
      {...dataAttributes}
    >
      <div className="flex items-center justify-between gap-2">
        <div className="flex min-w-0 items-center gap-2">
          {icon && (
            <span className={clsx('shrink-0', iconClassName)} aria-hidden="true">
              {icon}
            </span>
          )}
          <span className="min-w-0 truncate text-sm font-semibold text-pf-text-primary">
            {label}
          </span>
        </div>
        {badge && (
          <Badge variant={badgeVariant} size="sm">
            {badge}
          </Badge>
        )}
      </div>

      {value !== undefined && value !== null && (
        <p className="font-pf-mono text-lg leading-none tabular-nums text-pf-text-primary">
          {value}
        </p>
      )}

      {detail && <p className="text-xs text-pf-text-secondary">{detail}</p>}
    </div>
  );
}

export default AdminStatTile;

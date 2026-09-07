import { useEffect, useId, useMemo, useRef, useState } from 'react';
import clsx from 'clsx';
import { AttentionRow } from '@/common/components/admin';
import { Button } from '@/common/components/ui';
import { ChevronDownIcon, ChevronUpIcon } from '@/common/components/icons/MdiIcons';
import { useIsMobileBreakpoint } from '@/common/hooks/useMediaQuery';
import {
  canAccessDestination,
  getDestinationById,
} from '@/features/admin/registry';
import type { AttentionItemDto } from '@/types/adminOverview';

/**
 * The bounded "Needs attention" list on the `/admin` Control Center.
 *
 * The hub previously mapped *every* server-ranked attention item into one
 * unbounded `<ul>`. With a realistic alert volume that region grew without
 * limit and pushed system health and the operational tools below the fold —
 * the dashboard became a single list (#2517).
 *
 * The fix is a preview cap plus a real disclosure, not a fixed pixel height:
 * clipping with `max-height` would hide alerts outright and would break at
 * 200% zoom, where content must reflow rather than be cut off. So the
 * *collapsed* region is bounded by item count, and only the *expanded* region
 * — which by definition shows everything — gets a scroll ceiling.
 *
 * Three invariants this component exists to hold:
 *
 * 1. **Collapse never implies resolution.** The summary always states the
 *    total and the severity breakdown, and explicitly calls out Errors that
 *    are not currently on screen. A user who never expands still knows how
 *    many problems exist and how bad they are.
 * 2. **Server order is authoritative.** Items are rendered in the order the
 *    API returned them (Error > Warning > Info, stable within a severity).
 *    We slice that order; we never re-sort it.
 * 3. **Nothing becomes unreachable.** Every item stays in the DOM path of a
 *    keyboard user: the toggle is a real button outside the scroll container,
 *    and the expanded list is a focusable labeled region, so it can be
 *    scrolled from the keyboard without trapping focus.
 */

/** Preview cap at `sm` and wider. */
export const ATTENTION_PREVIEW_LIMIT_DESKTOP = 3;

/**
 * Preview cap below `sm`. One row, because three stacked rows at 320px are
 * already taller than the viewport's usable half.
 */
export const ATTENTION_PREVIEW_LIMIT_NARROW = 1;

/**
 * Scroll ceiling for the expanded list. `min()` keeps it honest on short
 * viewports, and `dvh` (not `vh`) accounts for mobile browser chrome.
 */
export const ATTENTION_EXPANDED_MAX_HEIGHT_CLASS = 'max-h-[min(560px,70dvh)]';

interface AttentionAccess {
  hasRole: (role: string) => boolean;
  hasPermission: (resource: string, action: string) => boolean;
}

/**
 * Resolve an attention item's navigation target.
 *
 * The backend emits either a stable `actionDestinationId` (preferred: keeps route
 * knowledge on the frontend) or a raw `actionRoute` fallback for pages outside the
 * ADMIN_DESTINATIONS registry (e.g. `/printers`). We prefer the id lookup so the
 * backend cannot silently ship a stale path; if the id doesn't resolve — because
 * someone renamed a registry entry without updating the backend — we fall back to
 * `actionRoute`, and if that's also missing, the link disappears (visible failure,
 * not a silent broken navigation).
 */
function resolveAttentionActionRoute(
  item: AttentionItemDto,
  access: AttentionAccess,
): string | null {
  const isRetiredManageRoute = (route: string) =>
    route === '/admin/manage' ||
    route.startsWith('/admin/manage?') ||
    route.startsWith('/admin/manage#') ||
    route.startsWith('/admin/manage/');
  const fallbackRoute =
    item.actionRoute &&
    item.actionRoute.startsWith('/') &&
    !isRetiredManageRoute(item.actionRoute)
      ? item.actionRoute
      : null;

  if (item.actionDestinationId) {
    const destination = getDestinationById(item.actionDestinationId);
    if (!destination) {
      return fallbackRoute;
    }
    if (!canAccessDestination(destination, access)) {
      return null;
    }
    return destination.path;
  }
  return fallbackRoute;
}

function AttentionRowFromDto({
  item,
  access,
}: {
  item: AttentionItemDto;
  access: AttentionAccess;
}) {
  const actionRoute = resolveAttentionActionRoute(item, access);
  return (
    <AttentionRow
      severity={item.severity}
      title={item.title}
      detail={item.detail}
      action={
        item.actionLabel && actionRoute
          ? { label: item.actionLabel, to: actionRoute }
          : undefined
      }
      dataAttributes={{
        'data-testid': 'admin-hub-attention-item',
        'data-attention-key': item.key,
        'data-attention-severity': item.severity,
      }}
    />
  );
}

function pluralize(count: number, singular: string): string {
  return `${count} ${singular}${count === 1 ? '' : 's'}`;
}

/**
 * Severity tallies for the summary line.
 *
 * Unknown severities are grouped as "Other" rather than folded into Info: the
 * row already degrades an unrecognized severity to the info *treatment*, but
 * counting it as Info would misreport a severity this build simply doesn't
 * know how to rank, which is exactly the kind of quiet reassurance this issue
 * is about.
 */
function summarizeSeverities(items: AttentionItemDto[]): string {
  const counts = { Error: 0, Warning: 0, Info: 0, Other: 0 };
  for (const item of items) {
    if (item.severity === 'Error' || item.severity === 'Warning' || item.severity === 'Info') {
      counts[item.severity] += 1;
    } else {
      counts.Other += 1;
    }
  }
  return (
    [
      counts.Error > 0 ? pluralize(counts.Error, 'Error') : null,
      counts.Warning > 0 ? pluralize(counts.Warning, 'Warning') : null,
      counts.Info > 0 ? `${counts.Info} Info` : null,
      counts.Other > 0 ? `${counts.Other} of unknown severity` : null,
    ]
      .filter((part): part is string => part !== null)
      .join(', ')
  );
}

export interface AdminAttentionPanelProps {
  /** Server-ranked items, already in Error > Warning > Info order. */
  items: AttentionItemDto[];
  access: AttentionAccess;
}

export function AdminAttentionPanel({ items, access }: AdminAttentionPanelProps) {
  const isNarrow = useIsMobileBreakpoint();
  const [isExpanded, setIsExpanded] = useState(false);
  const toggleRef = useRef<HTMLButtonElement | null>(null);
  const restoreFocusRef = useRef(false);
  const listRegionId = useId();

  const previewLimit = isNarrow
    ? ATTENTION_PREVIEW_LIMIT_NARROW
    : ATTENTION_PREVIEW_LIMIT_DESKTOP;
  const total = items.length;
  const overflows = total > previewLimit;

  // `isExpanded` is remembered across breakpoint changes, but it only *means*
  // anything while there is something to expand — otherwise a list that shrank
  // below the cap would keep rendering as a scroll region with no rows hidden.
  const showAll = !overflows || isExpanded;
  const visibleItems = useMemo(
    () => (showAll ? items : items.slice(0, previewLimit)),
    [items, previewLimit, showAll],
  );

  const hiddenErrorCount = useMemo(
    () =>
      showAll
        ? 0
        : items.slice(previewLimit).filter((item) => item.severity === 'Error').length,
    [items, previewLimit, showAll],
  );

  // Collapsing unmounts the rows below the cap. If focus was inside them it
  // would fall back to <body>, dumping a keyboard user at the top of the
  // document; park it on the control that caused the collapse instead.
  useEffect(() => {
    if (!isExpanded && restoreFocusRef.current) {
      restoreFocusRef.current = false;
      toggleRef.current?.focus();
    }
  }, [isExpanded]);

  const isScrollable = overflows && isExpanded;

  return (
    <div className="flex flex-col gap-2" data-testid="admin-hub-attention-panel">
      <div
        id={listRegionId}
        data-testid="admin-hub-attention-region"
        data-attention-scrollable={isScrollable ? 'true' : 'false'}
        {...(isScrollable
          ? {
              role: 'region',
              'aria-label': `All ${pluralize(total, 'attention item')}`,
              tabIndex: 0,
            }
          : {})}
        className={clsx(
          isScrollable && [
            ATTENTION_EXPANDED_MAX_HEIGHT_CLASS,
            'overflow-y-auto rounded-md',
            'focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-pf-accent',
          ],
        )}
      >
        <ul className="flex flex-col gap-2" data-testid="admin-hub-attention">
          {visibleItems.map((item) => (
            <AttentionRowFromDto key={item.key} item={item} access={access} />
          ))}
        </ul>
      </div>

      {overflows && (
        <div className="flex flex-wrap items-center justify-between gap-2">
          <p className="text-xs text-pf-text-secondary" data-testid="admin-hub-attention-summary">
            {isExpanded
              ? `Showing all ${pluralize(total, 'item')} — ${summarizeSeverities(items)}.`
              : `Showing ${visibleItems.length} of ${pluralize(total, 'item')} — ${summarizeSeverities(items)}.`}
            {!isExpanded && hiddenErrorCount > 0 && (
              <span
                className="font-semibold text-pf-error"
                data-testid="admin-hub-attention-hidden-errors"
              >
                {' '}
                {pluralize(hiddenErrorCount, 'Error')} not shown.
              </span>
            )}
          </p>
          <Button
            ref={toggleRef}
            type="button"
            variant="secondary"
            size="sm"
            aria-expanded={isExpanded}
            aria-controls={listRegionId}
            data-testid="admin-hub-attention-toggle"
            onClick={() => {
              if (isExpanded) {
                restoreFocusRef.current = true;
              }
              setIsExpanded(!isExpanded);
            }}
            iconRight={
              isExpanded ? (
                <ChevronUpIcon className="h-3.5 w-3.5" ariaLabel="" />
              ) : (
                <ChevronDownIcon className="h-3.5 w-3.5" ariaLabel="" />
              )
            }
          >
            {isExpanded ? 'Show fewer' : `Show all ${total}`}
          </Button>
        </div>
      )}
    </div>
  );
}

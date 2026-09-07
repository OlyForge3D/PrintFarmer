import {
  useCallback,
  useId,
  useLayoutEffect,
  useMemo,
  useRef,
  useState,
  type FocusEvent,
} from 'react';
import clsx from 'clsx';
import { AttentionRow } from '@/common/components/admin';
import { Button } from '@/common/components/ui';
import { ChevronDownIcon, ChevronUpIcon } from '@/common/components/icons/MdiIcons';
import { useIsMobileBreakpoint } from '@/common/hooks/useMediaQuery';
import {
  canAccessDestination,
  getDestinationById,
} from '@/features/admin/registry';
import {
  canonicalizeInternalRoute,
  isControlCenterSelfRoute,
} from '@/features/admin/utils/internalRoute';
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
 *
 * `actionRoute` is untrusted payload rendered into a link target, so it goes
 * through `canonicalizeInternalRoute` (#2526) rather than any prefix test local
 * to this file. A lexical check cannot see that `/foo/..//evil.test/steal` is
 * app-relative on the way in and protocol-relative on the way out.
 *
 * Returns `null` for a target that resolves to `/admin` itself (#2526): this
 * panel renders *on* `/admin`, and a hub must not self-link from its own content.
 */
function resolveAttentionActionRoute(
  item: AttentionItemDto,
  access: AttentionAccess,
): string | null {
  const fallbackRoute = canonicalizeInternalRoute(item.actionRoute);

  if (item.actionDestinationId) {
    const destination = getDestinationById(item.actionDestinationId);
    if (!destination) {
      return fallbackRoute;
    }
    if (!canAccessDestination(destination, access)) {
      return null;
    }
    if (isControlCenterSelfRoute(destination.path)) {
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
      // `overflow-wrap` is inherited, so setting it on the row covers the title
      // and detail without touching the shared AttentionRow — settings
      // validation uses the same component and is explicitly out of scope here.
      // Server details include unbroken tokens (hostnames, URLs) that would
      // otherwise force horizontal page overflow at 320px.
      className="break-words"
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
  /**
   * Id of the surrounding section heading. The panel container is a focus
   * target (see the recovery effect below), so it needs an accessible name —
   * otherwise a screen reader landing there after a shrink announces an
   * anonymous group instead of "Needs attention".
   */
  labelledBy?: string;
}

export function AdminAttentionPanel({ items, access, labelledBy }: AdminAttentionPanelProps) {
  const isNarrow = useIsMobileBreakpoint();
  const [isExpanded, setIsExpanded] = useState(false);
  const toggleRef = useRef<HTMLButtonElement | null>(null);
  const containerRef = useRef<HTMLDivElement | null>(null);
  const hasFocusWithinRef = useRef(false);
  const lastFocusedRef = useRef<Element | null>(null);
  const listRegionId = useId();

  const previewLimit = isNarrow
    ? ATTENTION_PREVIEW_LIMIT_NARROW
    : ATTENTION_PREVIEW_LIMIT_DESKTOP;
  const total = items.length;
  const overflows = total > previewLimit;

  // `isExpanded` is remembered across breakpoint changes, but it only *means*
  // anything while there is something to expand. If the feed shrinks below the
  // cap the toggle unmounts, so a stale `true` would silently re-expand the
  // panel the moment the feed grows back. Reset on that transition using
  // React's "adjust state during render" pattern rather than an effect, which
  // would cost an extra render pass (and is what `set-state-in-effect` warns
  // about). Keyed on the transition, not on `total`, so an ordinary background
  // poll that changes the count does not collapse a list the user opened.
  const [wasOverflowing, setWasOverflowing] = useState(overflows);
  if (wasOverflowing !== overflows) {
    setWasOverflowing(overflows);
    if (!overflows && isExpanded) {
      setIsExpanded(false);
    }
  }

  const showAll = !overflows || isExpanded;
  const visibleItems = useMemo(
    () => (showAll ? items : items.slice(0, previewLimit)),
    [items, previewLimit, showAll],
  );

  // Identity of what is currently rendered, so the focus-recovery effect below
  // runs when rows are added or removed but not on every unrelated re-render.
  const visibleKeySignature = visibleItems.map((item) => item.key).join('\u0000');

  const hiddenErrorCount = useMemo(
    () =>
      showAll
        ? 0
        : items.slice(previewLimit).filter((item) => item.severity === 'Error').length,
    [items, previewLimit, showAll],
  );

  // Rows unmount for two different reasons: the user collapsed the list, or a
  // background refresh shrank the feed below the cap (which also resets
  // `isExpanded` above, unmounting the toggle with it). In both cases, if focus
  // was inside those rows the browser drops it on <body>, dumping a keyboard
  // user at the top of the document with no way back.
  //
  // Recovery is deliberately conditional on focus having been *inside this
  // panel*. Moving focus that the user put somewhere else would be unrequested
  // focus theft — far worse than the bug being fixed — so `hasFocusWithinRef`
  // gates it, and a real blur to anywhere outside the panel clears that flag.
  // `document.activeElement` only lands on <body> here when the element holding
  // focus was removed, which is exactly the case worth repairing.
  useLayoutEffect(() => {
    if (!hasFocusWithinRef.current) {
      return;
    }
    const active = document.activeElement;
    if (active && active !== document.body) {
      return;
    }
    // Focus is on <body> for two very different reasons: the element holding it
    // was removed (repair that), or the user deliberately clicked dead space
    // (leave that alone). Distinguish by asking whether the element that last
    // held focus is still in the document. Checked here rather than at blur
    // time because a browser fires the removal blur *during* the mutation,
    // when the node can still report itself connected.
    const previouslyFocused = lastFocusedRef.current;
    if (previouslyFocused && previouslyFocused.isConnected) {
      return;
    }
    // Prefer the toggle: after a user-initiated collapse it is the control that
    // caused the change. It is unmounted after a shrink, so fall back to the
    // panel container, which is focusable precisely for this.
    const target = toggleRef.current ?? containerRef.current;
    if (target) {
      target.focus();
    } else {
      hasFocusWithinRef.current = false;
    }
  }, [visibleKeySignature, isExpanded]);

  // React's onFocus/onBlur map to focusin/focusout, so these fire for anything
  // inside the panel.
  const handleFocusCapture = useCallback((event: FocusEvent<HTMLDivElement>) => {
    hasFocusWithinRef.current = true;
    lastFocusedRef.current = event.target as Element | null;
  }, []);

  const handleBlurCapture = useCallback((event: FocusEvent<HTMLDivElement>) => {
    const next = event.relatedTarget as Node | null;
    // Only a move to a node genuinely outside the panel counts as an exit. A
    // null `relatedTarget` is deliberately *not* treated as one: that is what a
    // real browser reports when the focused row is removed, firing focusout
    // synchronously before the layout effect above runs, so clearing here would
    // suppress the very recovery this flag gates. (jsdom fires no event at all
    // on removal, so that path is only observable when dispatched explicitly —
    // the bounds suite does exactly that.) Preserving the flag is safe because
    // recovery additionally requires focus to be on <body> and the previously
    // focused node to have left the document.
    if (next && !event.currentTarget.contains(next)) {
      hasFocusWithinRef.current = false;
      lastFocusedRef.current = null;
    }
  }, []);

  const isScrollable = overflows && isExpanded;

  return (
    <div
      ref={containerRef}
      tabIndex={-1}
      aria-labelledby={labelledBy}
      onFocus={handleFocusCapture}
      onBlur={handleBlurCapture}
      className="flex flex-col gap-2 focus:outline-none"
      data-testid="admin-hub-attention-panel"
    >
      <div
        id={listRegionId}
        data-testid="admin-hub-attention-region"
        data-attention-scrollable={isScrollable ? 'true' : 'false'}
        {...(isScrollable
          ? {
              // `group`, not `region`: AdminSection already renders a named
              // <section>, which is a region landmark. Nesting another one just
              // pads the screen-reader landmark menu. `group` + aria-label keeps
              // the scroll container named and keyboard-scrollable without it.
              role: 'group',
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
            // The visible label is deliberately terse, but "Show fewer" alone
            // is contextless when a screen reader user reaches it out of
            // sequence. The visible text is a prefix of this name, so
            // label-in-name (WCAG 2.5.3) and voice control still hold.
            aria-label={
              isExpanded
                ? 'Show fewer attention items'
                : `Show all ${pluralize(total, 'attention item')}`
            }
            data-testid="admin-hub-attention-toggle"
            onClick={() => {
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

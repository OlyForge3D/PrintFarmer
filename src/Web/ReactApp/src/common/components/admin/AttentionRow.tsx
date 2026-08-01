import type { ReactNode } from 'react';
import clsx from 'clsx';
import { Link } from 'react-router';
import { Badge, Button } from '@/common/components/ui';
import { ArrowRightIcon } from '@/common/components/icons/MdiIcons';
import { presentationForAttentionSeverity } from './attentionPresentation';

/**
 * The one row that renders "something needs your attention".
 *
 * Two surfaces show attention items and they must look identical: the Admin
 * Control Center (server-supplied items that link elsewhere) and the settings
 * page (client-derived validation issues that focus a field on the same page).
 * The only real difference is what the action *does*, so that is the only thing
 * a caller varies — everything visual lives here.
 *
 * This component is deliberately presentational. It knows nothing about
 * `AttentionItemDto`, route registries, or settings metadata; callers resolve
 * their own domain into `severity` / `title` / `detail` / `action`.
 */

/** Navigate away to fix the problem (Admin Control Center). */
export interface AttentionRouteAction {
  label: string;
  to: string;
  onClick?: never;
}

/** Act in place to fix the problem (settings focuses the offending field). */
export interface AttentionCallbackAction {
  label: string;
  onClick: () => void;
  to?: never;
}

export type AttentionAction = AttentionRouteAction | AttentionCallbackAction;

export interface AttentionRowProps {
  /** Raw severity; unknown values degrade to the info treatment. */
  severity: string;
  title: string;
  detail?: ReactNode;
  /** Omitted when there is nothing actionable to offer. */
  action?: AttentionAction;
  /** Merged onto the `<li>` so each surface keeps its own test hooks. */
  dataAttributes?: Record<string, string | undefined>;
  /**
   * Show the severity badge next to the title. On by default, because the
   * Admin Control Center mixes info, warning and error items in one list and
   * the badge is what tells them apart. Settings turns it off: every item there
   * is an error, so the badge repeats what the icon, the rule and the tint have
   * already said. The screen-reader prefix is announced either way.
   */
  showSeverity?: boolean;
  className?: string;
}

/**
 * Both action shapes share one class list so a link and a button are
 * indistinguishable. The button goes through `Button variant="unstyled"`, which
 * applies no styles of its own — that keeps the two in lockstep while still
 * being a real `<Button>`, as the design system requires.
 *
 * The row follows the proposal's `.p-attn` exactly — 14px/16px padding, 12px
 * gap, an 18px icon, a 13.5px/600 title and a 13px secondary consequence line —
 * so the two surfaces that render attention items (this and the Admin Control
 * Center) stay identical.
 *
 * The action is deliberately *not* the proposal's `.btn-sm` (28px tall, 12px
 * horizontal, 12.5px text). At 12.5px it sits below the 13px consequence line
 * it is meant to answer, which reads as a footnote rather than the way out. It
 * ships one step up — 13px on 10px/4px — matching the app's own small button
 * rather than the mockup's.
 */
const ACTION_CLASS =
  'inline-flex items-center gap-1.5 rounded-md border border-pf-border bg-pf-bg-1 px-2.5 py-1 text-[13px] font-medium text-pf-text-primary transition-colors hover:border-pf-accent hover:text-pf-accent focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-pf-accent';

export function AttentionRow({
  severity,
  title,
  detail,
  action,
  dataAttributes,
  showSeverity = true,
  className,
}: AttentionRowProps) {
  const presentation = presentationForAttentionSeverity(severity);
  const { Icon } = presentation;

  return (
    <li
      className={clsx(
        'flex flex-col gap-3 rounded-md border px-4 py-3.5 sm:flex-row sm:items-start',
        presentation.rowBorderClass,
        presentation.rowBgClass,
        className,
      )}
      {...dataAttributes}
    >
      <span className={clsx('mt-0.5 shrink-0', presentation.iconClass)} aria-hidden="true">
        <Icon className="h-[18px] w-[18px]" ariaLabel="" />
      </span>
      <div className="min-w-0 flex-1">
        <div className="flex flex-wrap items-center gap-2">
          <span className="sr-only">{presentation.srPrefix}: </span>
          <p className="text-[13.5px] font-semibold text-pf-text-primary">{title}</p>
          {showSeverity && (
            <Badge variant={presentation.badgeVariant} size="sm">
              {presentation.label}
            </Badge>
          )}
        </div>
        {detail !== undefined && detail !== null && detail !== '' && (
          <p className="mt-0.5 text-[13px] text-pf-text-secondary">{detail}</p>
        )}
      </div>
      {action && (
        <div className="shrink-0 sm:ml-auto sm:self-center">
          {action.to !== undefined ? (
            <Link to={action.to} className={ACTION_CLASS}>
              {action.label}
              <ArrowRightIcon className="h-3.5 w-3.5" ariaLabel="" />
            </Link>
          ) : (
            <Button
              type="button"
              variant="unstyled"
              onClick={action.onClick}
              className={ACTION_CLASS}
              iconRight={<ArrowRightIcon className="h-3.5 w-3.5" ariaLabel="" />}
            >
              {action.label}
            </Button>
          )}
        </div>
      )}
    </li>
  );
}

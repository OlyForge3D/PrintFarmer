import { useLayoutEffect, useRef, type ReactNode } from 'react';
import { Link } from 'react-router';
import { Badge } from '@/common/components/ui';
import { canAccessDestination, getDestinationById } from '@/features/admin/registry';
import { ADMIN_HUB_ROUTE_STATE } from '@/features/admin/utils/adminHubParentState';
import { canonicalizeInternalRoute, isControlCenterSelfRoute } from '@/features/admin/utils/internalRoute';
import type { AttentionItemDto } from '@/types/adminOverview';

interface AttentionAccess {
  hasRole: (role: string) => boolean;
  hasPermission: (resource: string, action: string) => boolean;
}

function resolveAttentionActionRoute(item: AttentionItemDto, access: AttentionAccess): string | null {
  const fallback = canonicalizeInternalRoute(item.actionRoute);
  if (!item.actionDestinationId) return fallback;
  const destination = getDestinationById(item.actionDestinationId);
  if (!destination) return fallback;
  if (!canAccessDestination(destination, access) || isControlCenterSelfRoute(destination.path)) return null;
  return destination.path;
}

interface IssueGroup {
  key: string;
  label: string;
  severity: string;
  items: AttentionItemDto[];
}

/** Group identity is structural; never infer severity or resolution from the title. */
function groupOperationalIssues(items: AttentionItemDto[]): IssueGroup[] {
  const groups = new Map<string, IssueGroup>();
  for (const item of items) {
    const printerIssue = /^printer-[0-9a-f-]+-(unreachable|unknown)$/i.test(item.key);
    const unknownPrinter = printerIssue && item.key.endsWith('-unknown');
    const type = printerIssue ? (unknownPrinter ? 'printer-unknown' : 'printer-offline') : item.key;
    const label = printerIssue
      ? unknownPrinter ? 'Printer status unknown' : 'Unexpected offline printers'
      : item.title;
    const key = `${item.severity}:${type}`;
    const group = groups.get(key);
    if (group) group.items.push(item);
    else groups.set(key, { key, label, severity: item.severity, items: [item] });
  }
  // The API ranks severity. Map insertion preserves that order, including unknown severities.
  return [...groups.values()];
}

function severitySummary(items: AttentionItemDto[]): string {
  const counts = new Map<string, number>();
  for (const item of items) counts.set(item.severity, (counts.get(item.severity) ?? 0) + 1);
  return [...counts].map(([severity, count]) =>
    `${count} ${['Error', 'Warning', 'Info'].includes(severity) ? severity : 'unknown severity'}${count !== 1 && severity !== 'Info' ? 's' : ''}`,
  ).join(', ');
}

export interface AdminAttentionPanelProps {
  items: AttentionItemDto[];
  access: AttentionAccess;
  labelledBy?: string;
  emptyState?: ReactNode;
}

/** Compact type/severity disclosures; all totals remain visible without opening a group. */
export function AdminAttentionPanel({ items, access, labelledBy, emptyState }: AdminAttentionPanelProps) {
  const containerRef = useRef<HTMLDivElement>(null);
  const focusedRef = useRef<Element | null>(null);
  const groups = groupOperationalIssues(items);
  const signature = items.map(item => item.key).join('\0');
  useLayoutEffect(() => {
    const focused = focusedRef.current;
    if (focused && !focused.isConnected && document.activeElement === document.body) {
      containerRef.current?.focus();
    }
  }, [signature]);

  return (
    <div
      ref={containerRef}
      tabIndex={-1}
      aria-labelledby={labelledBy}
      className="min-w-0 focus-visible:outline focus-visible:outline-2 focus-visible:outline-pf-accent"
      data-testid="admin-hub-attention-panel"
      onFocusCapture={event => { focusedRef.current = event.target; }}
      onBlurCapture={event => {
        if (event.relatedTarget && !event.currentTarget.contains(event.relatedTarget)) focusedRef.current = null;
      }}
    >
      {items.length === 0 ? emptyState : (
        <>
          <p className="mb-2 text-xs text-pf-text-secondary" data-testid="admin-hub-attention-summary">
            {items.length} operational {items.length === 1 ? 'issue' : 'issues'}: {severitySummary(items)}.
            {' '}Expand a group for details and actions.
          </p>
          <div className="divide-y divide-pf-border border-y border-pf-border" data-testid="admin-hub-attention">
            {groups.map(group => (
              <details key={group.key} open={group.severity === 'Error' ? true : undefined}
                className="group min-w-0" data-testid="admin-hub-issue-group">
                <summary className="min-h-11 cursor-pointer px-2 py-3 text-sm text-pf-text-primary focus-visible:outline focus-visible:outline-2 focus-visible:outline-pf-accent">
                  <span className="ml-1 font-medium">{group.label}</span>
                  <span className="ml-2">
                    <Badge variant={group.severity === 'Error' ? 'error' : group.severity === 'Warning' ? 'warning' : 'default'} size="sm">
                      {group.items.length} {group.severity}
                    </Badge>
                  </span>
                </summary>
                <ul className="divide-y divide-pf-border/50 px-3 pb-2">
                  {group.items.map(item => {
                    const route = resolveAttentionActionRoute(item, access);
                    return (
                      <li key={item.key}
                        className="flex flex-wrap items-start justify-between gap-2 py-3 text-sm"
                        data-testid="admin-hub-attention-item"
                        data-attention-key={item.key} data-attention-severity={item.severity}>
                        <div className="min-w-0 flex-1 basis-48 break-words">
                          <p className="font-medium text-pf-text-primary">{item.title}</p>
                          {item.detail && <p className="mt-1 text-xs text-pf-text-secondary">{item.detail}</p>}
                        </div>
                        {route && item.actionLabel && (
                          <Link to={route} state={ADMIN_HUB_ROUTE_STATE}
                            className="inline-flex min-h-11 items-center text-xs text-pf-accent-text underline underline-offset-2 focus-visible:outline focus-visible:outline-2 focus-visible:outline-pf-accent">
                            {item.actionLabel}<span className="sr-only">: {item.title}</span>
                          </Link>
                        )}
                      </li>
                    );
                  })}
                </ul>
              </details>
            ))}
          </div>
        </>
      )}
    </div>
  );
}

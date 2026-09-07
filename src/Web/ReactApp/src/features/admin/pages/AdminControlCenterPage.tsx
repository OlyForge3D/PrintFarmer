import { useMemo } from 'react';
import clsx from 'clsx';
import { Link } from 'react-router';
import { Badge, Button, Card } from '@/common/components/ui';
import {
  AdminEmpty,
  AdminError,
  AdminLoading,
  AdminSection,
  AdminStatTile,
  AttentionRow,
} from '@/common/components/admin';
import { PageTemplate } from '@/common/components/PageTemplate';
import {
  AlertCircleIcon,
  AlertIcon,
  ArrowRightIcon,
  CheckCircleIcon,
  HelpCircleIcon,
  HomeIcon,
  RefreshIcon,
} from '@/common/components/icons/MdiIcons';
import { useAuth } from '@/features/auth/hooks/useAuth';
import {
  canAccessDestination,
  getDestinationById,
  getStandaloneConfigurationDestinations,
  hasAccessibleDestinationWithPrefix,
  type AdminDestination,
} from '@/features/admin/registry';
import { useAdminOverview } from '@/features/admin/hooks/useAdminOverview';
import {
  isKnownSubsystemStatus,
  type AttentionItemDto,
  type KnownSubsystemStatus,
  type SubsystemHealthDto,
} from '@/types/adminOverview';

// ─────────────────────────────────────────────────────────────────────────────
// Status presentation

interface StatusPresentation {
  label: string;
  Icon: (props: { className?: string; ariaLabel?: string }) => JSX.Element;
  iconClass: string;
  badgeVariant: 'success' | 'warning' | 'error' | 'default' | 'info';
  tileBorderClass: string;
  srPrefix: string;
}

const SUBSYSTEM_PRESENTATION: Record<KnownSubsystemStatus, StatusPresentation> = {
  Healthy: {
    label: 'Healthy',
    Icon: CheckCircleIcon,
    iconClass: 'text-pf-success',
    badgeVariant: 'success',
    tileBorderClass: 'border-pf-border',
    srPrefix: 'Healthy',
  },
  Degraded: {
    label: 'Degraded',
    Icon: AlertIcon,
    iconClass: 'text-pf-warning',
    badgeVariant: 'warning',
    tileBorderClass: 'border-pf-warning/40',
    srPrefix: 'Degraded',
  },
  Unhealthy: {
    label: 'Unhealthy',
    Icon: AlertCircleIcon,
    iconClass: 'text-pf-error',
    badgeVariant: 'error',
    tileBorderClass: 'border-pf-error/40',
    srPrefix: 'Unhealthy',
  },
  Unknown: {
    label: 'Unknown',
    Icon: HelpCircleIcon,
    iconClass: 'text-pf-text-tertiary',
    badgeVariant: 'default',
    tileBorderClass: 'border-pf-border',
    srPrefix: 'Status unknown',
  },
};

function presentationForSubsystemStatus(raw: string): StatusPresentation {
  if (isKnownSubsystemStatus(raw)) {
    return SUBSYSTEM_PRESENTATION[raw];
  }
  // Unknown enum value → degrade gracefully so the tile still renders.
  return {
    ...SUBSYSTEM_PRESENTATION.Unknown,
    label: raw || 'Unknown',
    srPrefix: `Unknown status "${raw || 'unspecified'}"`,
  };
}

// Attention severity presentation now lives in the shared AttentionRow so the
// hub and the settings page cannot drift apart. See
// `common/components/admin/AttentionRow.tsx`.

function OverallStatusBadge({ status }: { status: string }) {
  const presentation = presentationForSubsystemStatus(status);
  const { Icon } = presentation;
  return (
    <span data-testid="admin-hub-overall-status" data-overall-status={status}>
      <Badge variant={presentation.badgeVariant} size="sm" className="gap-1.5">
        <Icon className={clsx('h-3.5 w-3.5', presentation.iconClass)} ariaLabel="" />
        System {presentation.label}
      </Badge>
    </span>
  );
}

// ─────────────────────────────────────────────────────────────────────────────
// Formatting helpers

function formatCheckedAt(iso: string): string {
  try {
    const parsed = new Date(iso);
    if (Number.isNaN(parsed.getTime())) return iso;
    return parsed.toLocaleTimeString(undefined, {
      hour: '2-digit',
      minute: '2-digit',
      second: '2-digit',
    });
  } catch {
    return iso;
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Tile / row primitives

function SubsystemTile({ subsystem }: { subsystem: SubsystemHealthDto }) {
  const presentation = presentationForSubsystemStatus(subsystem.status);
  const { Icon } = presentation;
  return (
    <AdminStatTile
      icon={<Icon className="h-5 w-5" ariaLabel="" />}
      iconClassName={presentation.iconClass}
      label={subsystem.name}
      badge={presentation.label}
      badgeVariant={presentation.badgeVariant}
      detail={subsystem.detail}
      borderClassName={presentation.tileBorderClass}
      ariaLabel={`${subsystem.name}: ${presentation.srPrefix}`}
      dataAttributes={{
        'data-testid': 'admin-hub-subsystem',
        'data-subsystem-key': subsystem.key,
        'data-subsystem-status': subsystem.status,
      }}
    />
  );
}

/**
 * Synthetic origin used only to run the WHATWG URL parser over an app-relative
 * route. It is never navigated to; `.invalid` is reserved by RFC 2606 precisely
 * so it can never resolve to a real host.
 */
const INTERNAL_ROUTE_ORIGIN = 'https://printfarmer.invalid';

/** Parse an app-relative route against the synthetic origin. `null` if it isn't same-origin. */
function parseInternalRoute(route: string): URL | null {
  try {
    const parsed = new URL(route, INTERNAL_ROUTE_ORIGIN);
    return parsed.origin === INTERNAL_ROUTE_ORIGIN ? parsed : null;
  } catch {
    return null;
  }
}

/** Percent-decode a pathname the way the router does when matching. `null` on malformed encoding. */
function decodeRoutePathname(pathname: string): string | null {
  try {
    return decodeURIComponent(pathname);
  } catch {
    return null;
  }
}

/**
 * Reduce a route to the pathname React Router will actually match on, so route
 * *identity* checks cannot be evaded by an equivalent spelling.
 *
 * Normalising by hand is not enough here, because a browser applies full URL
 * semantics to an href before the router ever sees it: `/foo/../admin` resolves
 * to `/admin`, `/admin\` folds to `/admin/`, and `/%61dmin` decodes to `/admin`.
 * A lexical check misses all three and would emit exactly the self-link Issue
 * 2526 forbids. So delegate to the URL parser — the same algorithm the browser
 * uses — then decode, fold trailing slashes, and lowercase for the comparison
 * (React Router matches case-insensitively and treats a trailing slash as
 * equivalent).
 */
function routePathname(route: string): string {
  const parsed = parseInternalRoute(route);
  let pathname: string;
  if (parsed) {
    pathname = parsed.pathname;
  } else {
    // Off-origin or unparseable: it is not an in-app route, so it can never be
    // an in-app route *identity*. Strip query/hash lexically and let the
    // comparison fall through to "not a match".
    const queryOrHash = route.search(/[?#]/);
    pathname = queryOrHash === -1 ? route : route.slice(0, queryOrHash);
  }
  const decoded = decodeRoutePathname(pathname) ?? pathname;
  const withoutTrailingSlash = decoded.length > 1 ? decoded.replace(/\/+$/, '') : decoded;
  return withoutTrailingSlash.toLowerCase();
}

/** `/admin` itself, however spelled — but not `/admin/status` (a legitimate child) or `/admin-something` (an unrelated sibling). */
function isControlCenterSelfRoute(route: string): boolean {
  return routePathname(route) === '/admin';
}

/** `/admin/manage` was retired and is not a registered route; never link to it. */
function isRetiredManageRoute(route: string): boolean {
  const pathname = routePathname(route);
  return pathname === '/admin/manage' || pathname.startsWith('/admin/manage/');
}

/**
 * `actionRoute` is untrusted backend payload rendered straight into a link
 * target, so prove it is an in-app route before it becomes one. Anything that
 * is not plainly app-relative is dropped rather than sanitised — a malformed or
 * compromised payload should make the link disappear (a visible failure), never
 * navigate somewhere unexpected.
 */
function canonicalizeInternalRoute(rawRoute: string | null | undefined): string | null {
  if (!rawRoute) {
    return null;
  }
  const route = rawRoute.trim();

  // Must be app-relative. A single leading slash rejects absolute URLs
  // ("https://evil.test/x") and non-HTTP schemes ("javascript:alert(1)").
  if (!route.startsWith('/')) {
    return null;
  }
  // "//evil.test/x" is protocol-relative and navigates off-origin despite the
  // leading slash; browsers also fold backslashes into slashes, so "/\evil.test"
  // is the same attack spelled differently.
  if (route.startsWith('//') || route.startsWith('/\\')) {
    return null;
  }
  // Control characters can be stripped by the browser after our check runs,
  // changing what the string means. Reject rather than guess.
  // eslint-disable-next-line no-control-regex
  if (/[\u0000-\u001F\u007F]/.test(route)) {
    return null;
  }
  // Browsers fold a literal backslash into a path separator for HTTP(S) URLs,
  // so "/admin\" is really "/admin/". No legitimate in-app route contains one.
  if (route.includes('\\')) {
    return null;
  }

  // Run the browser's own URL algorithm rather than trusting the raw string:
  // it resolves dot segments ("/foo/../admin" -> "/admin") and re-rejects
  // anything that escapes to another origin.
  const parsed = parseInternalRoute(route);
  if (!parsed) {
    return null;
  }
  // Malformed percent-encoding: we cannot know what the router will match, so drop it.
  if (decodeRoutePathname(parsed.pathname) === null) {
    return null;
  }

  if (isRetiredManageRoute(route) || isControlCenterSelfRoute(route)) {
    return null;
  }

  // Emit exactly what was validated. The parser has already resolved dot
  // segments, so the emitted href cannot renormalise into a different route
  // than the one these guards approved.
  return `${parsed.pathname}${parsed.search}${parsed.hash}`;
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
 * Returns `null` for a target that resolves to `/admin` itself (Issue 2526): this
 * page *is* `/admin`, and a hub must not self-link from its own content. That can
 * arise from a backend item pointing at `admin-home` or emitting `/admin` as a raw
 * route, so it is a guard rather than an expected path.
 */
function resolveAttentionActionRoute(
  item: AttentionItemDto,
  access: {
    hasRole: (role: string) => boolean;
    hasPermission: (resource: string, action: string) => boolean;
  },
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
  access: {
    hasRole: (role: string) => boolean;
    hasPermission: (resource: string, action: string) => boolean;
  };
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

/**
 * Operational destinations the Control Center owns, in display order.
 *
 * This list plus `getStandaloneConfigurationDestinations` is the hub's link
 * composition, and — since #2526 — the *single default home* for every entry in
 * it. Maintenance, Analytics and Auto-Dispatch previously also had anchored
 * navbar entries; those were removed, so adding a destination here now means
 * this page is where users find it.
 *
 * `admin-home` is deliberately absent and must stay absent: a hub does not
 * self-link. It is `kind: 'hub'`, so `getStandaloneConfigurationDestinations`
 * (which keeps only `kind: 'configuration'`) cannot reintroduce it either, and
 * `resolveAttentionActionRoute` drops any attention action that resolves to
 * `/admin`. The global Admin nav entry and child pages' back links to the hub
 * are separate surfaces and are unaffected.
 */
const OPERATIONAL_DESTINATION_IDS = [
  'ops-status',
  'ops-workers',
  'users-audit',
  'data-management',
  'ops-maintenance',
  'ops-analytics',
  'ops-auto-dispatch',
] as const;

function getDashboardDestinations(
  access: {
    hasRole: (role: string) => boolean;
    hasPermission: (resource: string, action: string) => boolean;
  },
): {
  operational: AdminDestination[];
  configuration: AdminDestination[];
  settings: boolean;
} {
  const operational = OPERATIONAL_DESTINATION_IDS
    .map((id) => getDestinationById(id))
    .filter((destination): destination is AdminDestination => Boolean(destination))
    .filter((destination) => canAccessDestination(destination, access))
    .map((destination) =>
      destination.id === 'ops-workers'
        ? { ...destination, path: '/admin/workers?workerTab=jobs' }
        : destination,
    );

  // Configuration destinations that live outside the /admin/settings shell
  // (`/catalog`, `/locations`, `/admin/power-monitors`) get their own cards.
  // The Admin nav entry lights up for *any* accessible configuration
  // destination (`hasAccessibleHubTile`), so omitting these left delegates —
  // most sharply `power_monitors:admin`, whose destination is only reachable
  // through the admin surface — on a dead-end hub (#2508, Hicks review).
  const configuration = getStandaloneConfigurationDestinations(access);

  // Gate on actual reachability of the settings shell itself (not merely
  // holding *some* configuration-kind permission) — several configuration
  // destinations (data-catalog, hw-locations, hw-power-monitors) live outside
  // /admin/settings, so a user whose only configuration grant unlocks one of
  // those would otherwise see a "Farm & Admin Settings" card that leads
  // nowhere (Bishop review, #2508). Those destinations are surfaced directly
  // via `configuration` above instead.
  const settings = hasAccessibleDestinationWithPrefix(access, '/admin/settings');

  return { operational, configuration, settings };
}

function DestinationCard({ destination }: { destination: AdminDestination }) {
  const Icon = destination.icon;
  return (
    <Link
      to={destination.path}
      className="group block h-full rounded-lg focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-pf-accent"
      data-testid="admin-hub-destination"
      data-destination-id={destination.id}
    >
      <Card hoverable className="h-full">
        <Card.Body className="flex h-full items-start gap-3">
          <span
            className="mt-0.5 shrink-0 text-pf-text-secondary transition-colors group-hover:text-pf-accent"
            aria-hidden="true"
          >
            <Icon className="h-5 w-5" />
          </span>
          <div className="min-w-0 flex-1">
            <p className="text-sm font-semibold text-pf-text-primary">{destination.label}</p>
            <p className="mt-1 text-xs text-pf-text-secondary">{destination.description}</p>
          </div>
          <span
            className="mt-0.5 shrink-0 text-pf-text-tertiary transition-colors group-hover:text-pf-accent"
            aria-hidden="true"
          >
            <ArrowRightIcon className="h-4 w-4" ariaLabel="" />
          </span>
        </Card.Body>
      </Card>
    </Link>
  );
}

// ─────────────────────────────────────────────────────────────────────────────
// Page

/**
 * `/admin` Control Center hub.
 *
 * Three bands, always in this order:
 * 1. **Needs attention** — pre-sorted list of items from the API.
 * 2. **System health** — compact, truthful subsystem status.
 * 3. **Operations and settings** — only permitted day-to-day tools, any
 *    permitted configuration destination that lives outside the settings shell
 *    (`/catalog`, `/locations`, `/admin/power-monitors`), plus one Farm & Admin
 *    Settings entry point.
 *
 * The page is fully usable at 430px. Loading uses `AdminLoading`, and a failed
 * overview fetch renders `AdminError` with a working retry — the hub is what an
 * operator opens precisely when things are broken, so its own failure mode matters.
 */
export function AdminControlCenterPage() {
  const { hasRole, hasPermission } = useAuth();
  const canViewOverview = hasRole('farm_admin') || hasPermission('system_settings', 'admin');
  const { data, isLoading, isError, error, isFetching, refetch } = useAdminOverview({
    enabled: canViewOverview,
  });

  const dashboardDestinations = useMemo(
    () => getDashboardDestinations({ hasRole, hasPermission }),
    [hasRole, hasPermission],
  );

  // The "no operational tools" empty state is only truthful when the whole
  // band is empty — a delegate who reaches Power Monitors but no operational
  // tool still has somewhere to go, so we must not tell them otherwise.
  const hasAnyDestination =
    dashboardDestinations.operational.length > 0 ||
    dashboardDestinations.configuration.length > 0 ||
    dashboardDestinations.settings;

  const refreshButton = canViewOverview ? (
    <Button
      variant="secondary"
      size="sm"
      onClick={() => {
        void refetch();
      }}
      disabled={isFetching}
      iconLeft={<RefreshIcon className="h-3.5 w-3.5" ariaLabel="" />}
    >
      {isFetching ? 'Refreshing…' : 'Refresh'}
    </Button>
  ) : null;

  return (
    <PageTemplate
      title="Admin Control Center"
      subtitle={
        canViewOverview
          ? 'See what needs attention first, then open the tools your role can use.'
          : 'Open the admin tools and settings available to your role.'
      }
      icon={HomeIcon}
      actions={refreshButton}
      maxWidth="max-w-7xl"
      titleWrap
    >
      <div className="flex flex-col gap-8">
        {/* ── Band 1: attention ── */}
        {canViewOverview && (
          <AdminSection
            caption="Needs attention"
            captionId="admin-hub-attention-heading"
            count={isError ? undefined : data?.attention.length}
          >
            {isLoading && (
              <AdminLoading
                variant="list"
                label="Loading attention items"
                rows={3}
              />
            )}

            {isError && (
              <AdminError
                title="Couldn't load the admin overview"
                description="The admin overview endpoint didn't respond, so health and attention are unavailable. Your admin destinations below still work."
                error={error}
                onRetry={() => {
                  void refetch();
                }}
              />
            )}

            {!isLoading && !isError && data && data.attention.length === 0 && (
              <p
                className="flex items-center gap-2 text-sm text-pf-text-secondary"
                data-testid="admin-hub-attention-clear"
              >
                {data.overallStatus === 'Healthy' && data.subsystems.length > 0 &&
                data.subsystems.every((subsystem) => subsystem.status === 'Healthy') ? (
                  <CheckCircleIcon className="h-4 w-4 shrink-0 text-pf-success" ariaLabel="" />
                ) : (
                  <HelpCircleIcon className="h-4 w-4 shrink-0 text-pf-text-tertiary" ariaLabel="" />
                )}
                {data.overallStatus === 'Healthy' && data.subsystems.length > 0 &&
                data.subsystems.every((subsystem) => subsystem.status === 'Healthy')
                  ? 'Nothing needs your attention — every subsystem is reporting healthy.'
                  : 'No attention items were reported. Review system health below for the current status.'}
              </p>
            )}

            {!isLoading && !isError && data && data.attention.length > 0 && (
              <ul
                className="flex flex-col gap-2"
                data-testid="admin-hub-attention"
              >
                {data.attention.map((item) => (
                  <AttentionRowFromDto
                    key={item.key}
                    item={item}
                    access={{ hasRole, hasPermission }}
                  />
                ))}
              </ul>
            )}
          </AdminSection>
        )}

        {/* ── Band 2: health ── */}
        {canViewOverview && !isError && (
          <AdminSection
            caption="System health"
            captionId="admin-hub-health-heading"
            captionAside={data ? <OverallStatusBadge status={data.overallStatus} /> : null}
            headerAside={
              data?.checkedAt ? (
                <p className="text-xs text-pf-text-tertiary">
                  Checked at {formatCheckedAt(data.checkedAt)}
                </p>
              ) : null
            }
          >
            {isLoading && (
              <AdminLoading variant="card-grid" label="Loading system health" rows={4} />
            )}
            {!isLoading && data && data.subsystems.length > 0 && (
              <div
                className="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-4"
                data-testid="admin-hub-subsystems"
              >
                {data.subsystems.map((subsystem) => (
                  <SubsystemTile key={subsystem.key} subsystem={subsystem} />
                ))}
              </div>
            )}
            {!isLoading && data && data.subsystems.length === 0 && (
              <AdminEmpty
                icon={<HelpCircleIcon className="h-8 w-8" ariaLabel="" />}
                title="No subsystems reported"
                description="The overview endpoint returned an empty subsystem list."
                size="compact"
              />
            )}
          </AdminSection>
        )}

        {/* ── Band 3: operations, standalone configuration, and settings ── */}
        <AdminSection caption="Operations" captionId="admin-hub-operations-heading" gap="loose">
          <div data-testid="admin-hub-operations">
            {dashboardDestinations.operational.length === 0 ? (
              hasAnyDestination ? (
                <p className="text-sm text-pf-text-secondary">
                  Your account has no day-to-day operational tools. The destinations you can
                  reach are listed below.
                </p>
              ) : (
                <AdminEmpty
                  icon={<HomeIcon className="h-8 w-8" ariaLabel="" />}
                  title="No operational tools available"
                  description="Your account does not have access to any operational tools."
                  size="compact"
                />
              )
            ) : (
              <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-3">
                {dashboardDestinations.operational.map((destination) => (
                  <DestinationCard key={destination.id} destination={destination} />
                ))}
              </div>
            )}
          </div>
        </AdminSection>

        {dashboardDestinations.configuration.length > 0 && (
          <AdminSection
            caption="Configuration"
            captionId="admin-hub-configuration-heading"
            gap="loose"
          >
            <div
              className="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-3"
              data-testid="admin-hub-configuration"
            >
              {dashboardDestinations.configuration.map((destination) => (
                <DestinationCard key={destination.id} destination={destination} />
              ))}
            </div>
          </AdminSection>
        )}

        {dashboardDestinations.settings && (
          <AdminSection caption="Farm & Admin Settings" captionId="admin-hub-settings-heading">
            <DestinationCard
              destination={{
                id: 'admin-settings',
                kind: 'configuration',
                label: 'Farm & Admin Settings',
                description: 'Configure farm-wide behavior, access, and integrations.',
                path: '/admin/settings?scope=system',
                icon: HomeIcon,
                group: 'general',
              }}
            />
          </AdminSection>
        )}
      </div>
    </PageTemplate>
  );
}

export default AdminControlCenterPage;

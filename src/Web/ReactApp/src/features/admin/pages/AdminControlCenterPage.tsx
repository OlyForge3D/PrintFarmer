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
  ADMIN_DESTINATIONS,
  canAccessDestination,
  getDestinationById,
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
  access: {
    hasRole: (role: string) => boolean;
    hasPermission: (resource: string, action: string) => boolean;
  },
): string | null {
  const fallbackRoute =
    item.actionRoute &&
    item.actionRoute.startsWith('/') &&
    !item.actionRoute.startsWith('/admin/manage')
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
): { operational: AdminDestination[]; settings: boolean } {
  const operational = OPERATIONAL_DESTINATION_IDS
    .map((id) => getDestinationById(id))
    .filter((destination): destination is AdminDestination => Boolean(destination))
    .filter((destination) => canAccessDestination(destination, access))
    .map((destination) =>
      destination.id === 'ops-workers'
        ? { ...destination, path: '/admin/workers?workerTab=jobs' }
        : destination,
    );

  const settings = ADMIN_DESTINATIONS.some(
    (destination) =>
      destination.kind === 'configuration' && canAccessDestination(destination, access),
  );

  return { operational, settings };
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
 * 3. **Operations and settings** — only permitted day-to-day tools plus one
 *    Farm & Admin Settings entry point.
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

        {/* ── Band 3: operations and settings ── */}
        <AdminSection caption="Operations" captionId="admin-hub-operations-heading" gap="loose">
          <div data-testid="admin-hub-operations">
            {dashboardDestinations.operational.length === 0 ? (
              <AdminEmpty
                icon={<HomeIcon className="h-8 w-8" ariaLabel="" />}
                title="No operational tools available"
                description="Your account does not have access to any operational tools."
                size="compact"
              />
            ) : (
              <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-3">
                {dashboardDestinations.operational.map((destination) => (
                  <DestinationCard key={destination.id} destination={destination} />
                ))}
              </div>
            )}
          </div>
        </AdminSection>

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

import { useMemo } from 'react';
import clsx from 'clsx';
import { Link } from 'react-router';
import { Badge, Button, Card } from '@/common/components/ui';
import { AdminEmpty, AdminError, AdminLoading, AdminSection, AdminStatTile } from '@/common/components/admin';
import { PageTemplate } from '@/common/components/PageTemplate';
import {
  AlertCircleIcon,
  AlertIcon,
  ArrowRightIcon,
  CheckCircleIcon,
  HelpCircleIcon,
  HomeIcon,
  InfoIcon,
  RefreshIcon,
} from '@/common/components/icons/MdiIcons';
import { useAuth } from '@/features/auth/hooks/useAuth';
import {
  getDestinationById,
  getHubGroupedDestinations,
  type AdminDestination,
} from '@/features/admin/registry';
import { useAdminOverview } from '@/features/admin/hooks/useAdminOverview';
import {
  isKnownAttentionSeverity,
  isKnownSubsystemStatus,
  type AttentionItemDto,
  type KnownAttentionSeverity,
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

interface AttentionPresentation {
  label: string;
  Icon: (props: { className?: string; ariaLabel?: string }) => JSX.Element;
  iconClass: string;
  badgeVariant: 'error' | 'warning' | 'info' | 'default';
  rowBorderClass: string;
  rowBgClass: string;
  srPrefix: string;
}

const ATTENTION_PRESENTATION: Record<KnownAttentionSeverity, AttentionPresentation> = {
  Error: {
    label: 'Error',
    Icon: AlertCircleIcon,
    iconClass: 'text-pf-error',
    badgeVariant: 'error',
    rowBorderClass: 'border-pf-error/40',
    rowBgClass: 'bg-pf-error-bg/40',
    srPrefix: 'Error',
  },
  Warning: {
    label: 'Warning',
    Icon: AlertIcon,
    iconClass: 'text-pf-warning',
    badgeVariant: 'warning',
    rowBorderClass: 'border-pf-warning/40',
    rowBgClass: 'bg-pf-bg-1',
    srPrefix: 'Warning',
  },
  Info: {
    label: 'Info',
    Icon: InfoIcon,
    iconClass: 'text-pf-accent',
    badgeVariant: 'info',
    rowBorderClass: 'border-pf-border',
    rowBgClass: 'bg-pf-bg-1',
    srPrefix: 'Info',
  },
};

function presentationForAttentionSeverity(raw: string): AttentionPresentation {
  if (isKnownAttentionSeverity(raw)) {
    return ATTENTION_PRESENTATION[raw];
  }
  return {
    ...ATTENTION_PRESENTATION.Info,
    label: raw || 'Notice',
    srPrefix: `Unknown severity "${raw || 'unspecified'}"`,
  };
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
function resolveAttentionActionRoute(item: AttentionItemDto): string | null {
  if (item.actionDestinationId) {
    const destination = getDestinationById(item.actionDestinationId);
    if (destination) {
      return destination.path;
    }
  }
  return item.actionRoute ?? null;
}

function AttentionRow({ item }: { item: AttentionItemDto }) {
  const presentation = presentationForAttentionSeverity(item.severity);
  const { Icon } = presentation;
  const actionRoute = resolveAttentionActionRoute(item);
  const hasAction = Boolean(item.actionLabel && actionRoute);
  return (
    <li
      className={clsx(
        'flex flex-col gap-3 rounded-md border p-4 sm:flex-row sm:items-start',
        presentation.rowBorderClass,
        presentation.rowBgClass,
      )}
      data-testid="admin-hub-attention-item"
      data-attention-key={item.key}
      data-attention-severity={item.severity}
    >
      <span
        className={clsx('mt-0.5 shrink-0', presentation.iconClass)}
        aria-hidden="true"
      >
        <Icon className="h-5 w-5" ariaLabel="" />
      </span>
      <div className="min-w-0 flex-1">
        <div className="flex flex-wrap items-center gap-2">
          <span className="sr-only">{presentation.srPrefix}: </span>
          <p className="text-sm font-semibold text-pf-text-primary">{item.title}</p>
          <Badge variant={presentation.badgeVariant} size="sm">
            {presentation.label}
          </Badge>
        </div>
        <p className="mt-1 text-sm text-pf-text-secondary">{item.detail}</p>
      </div>
      {hasAction && actionRoute && (
        <div className="shrink-0 sm:ml-auto sm:self-center">
          <Link
            to={actionRoute}
            className="inline-flex items-center gap-1.5 rounded-md border border-pf-border bg-pf-bg-1 px-3 py-1.5 text-sm font-medium text-pf-text-primary transition-colors hover:border-pf-accent hover:text-pf-accent focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-pf-accent"
          >
            {item.actionLabel}
            <ArrowRightIcon className="h-3.5 w-3.5" ariaLabel="" />
          </Link>
        </div>
      )}
    </li>
  );
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
 * 1. **Health strip** — one tile per subsystem returned by the overview endpoint.
 * 2. **Needs attention** — pre-sorted list of items from the API, plus a genuinely
 *    reassuring empty state.
 * 3. **Domain cards** — every admin destination the current user can reach,
 *    grouped by domain from the registry.
 *
 * The page is fully usable at 430px. Loading uses `AdminLoading`, and a failed
 * overview fetch renders `AdminError` with a working retry — the hub is what an
 * operator opens precisely when things are broken, so its own failure mode matters.
 */
export function AdminControlCenterPage() {
  const { hasRole, hasPermission } = useAuth();
  const { data, isLoading, isError, error, isFetching, refetch } = useAdminOverview();

  const groupedDestinations = useMemo(
    () => getHubGroupedDestinations({ hasRole, hasPermission }),
    [hasRole, hasPermission],
  );

  const refreshButton = (
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
  );

  return (
    <PageTemplate
      title="Admin Control Center"
      subtitle="System health, alerts, and every admin destination in one place."
      icon={HomeIcon}
      actions={refreshButton}
      maxWidth="max-w-7xl"
    >
      <div className="flex flex-col gap-8">
        {/* ── Band 1: health ── */}
        <AdminSection
          caption="System health"
          captionId="admin-hub-health-heading"
          headerAside={
            data?.checkedAt ? (
              <p className="text-xs text-pf-text-tertiary">
                Checked at {formatCheckedAt(data.checkedAt)}
              </p>
            ) : null
          }
        >
          {isLoading && (
            <AdminLoading
              variant="card-grid"
              label="Loading system health"
              rows={4}
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

          {!isLoading && !isError && data && data.subsystems.length > 0 && (
            <div
              className="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-4"
              data-testid="admin-hub-subsystems"
            >
              {data.subsystems.map((subsystem) => (
                <SubsystemTile key={subsystem.key} subsystem={subsystem} />
              ))}
            </div>
          )}

          {!isLoading && !isError && data && data.subsystems.length === 0 && (
            <AdminEmpty
              icon={<HelpCircleIcon className="h-8 w-8" ariaLabel="" />}
              title="No subsystems reported"
              description="The overview endpoint returned an empty subsystem list."
              size="compact"
            />
          )}
        </AdminSection>

        {/* ── Band 2: attention ── */}
        {/* Suppressed on error — band 1 already reports the failure, and an
            "Needs attention" heading with nothing beneath it reads as broken. */}
        {!isError && (
        <AdminSection
          caption="Needs attention"
          captionId="admin-hub-attention-heading"
          count={data?.attention.length}
        >
          {isLoading && <AdminLoading variant="list" label="Loading attention items" rows={3} />}

          {!isLoading && data && data.attention.length === 0 && (
            /* An all-clear is one line of good news, not a 206px empty state.
               Measured: the centred illustration treatment pushed the entire
               destination grid down by a fifth of the viewport to say nothing
               happened. The band still announces itself via the caption, so the
               body only has to confirm it. */
            <p
              className="flex items-center gap-2 text-sm text-pf-text-secondary"
              data-testid="admin-hub-attention-clear"
            >
              <CheckCircleIcon className="h-4 w-4 shrink-0 text-pf-success" ariaLabel="" />
              Nothing needs your attention — every subsystem is reporting healthy.
            </p>
          )}

          {!isLoading && data && data.attention.length > 0 && (
            <ul
              className="flex flex-col gap-2"
              data-testid="admin-hub-attention"
            >
              {data.attention.map((item) => (
                <AttentionRow key={item.key} item={item} />
              ))}
            </ul>
          )}
        </AdminSection>
        )}

        {/* ── Band 3: domains ── */}
        <AdminSection
          caption="Everything you can manage"
          captionId="admin-hub-domains-heading"
          gap="loose"
        >
          {groupedDestinations.length === 0 ? (
            <AdminEmpty
              icon={<HomeIcon className="h-8 w-8" ariaLabel="" />}
              title="No admin destinations available"
              description="Your account does not have access to any admin destinations."
              size="compact"
            />
          ) : (
            <div className="flex flex-col gap-6" data-testid="admin-hub-domains">
              {groupedDestinations.map(({ group, destinations }) => (
                <div key={group.id} className="flex flex-col gap-3">
                  <div className="flex flex-col gap-0.5">
                    <h3 className="text-sm font-semibold uppercase tracking-wide text-pf-text-secondary">
                      {group.label}
                    </h3>
                    <p className="text-xs text-pf-text-tertiary">{group.description}</p>
                  </div>
                  <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-3">
                    {destinations.map((destination) => (
                      <DestinationCard key={destination.id} destination={destination} />
                    ))}
                  </div>
                </div>
              ))}
            </div>
          )}
        </AdminSection>
      </div>
    </PageTemplate>
  );
}

export default AdminControlCenterPage;

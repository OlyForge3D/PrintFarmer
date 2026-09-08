import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import clsx from 'clsx';
import { Link } from 'react-router';
import { Alert, Badge, Button, Card } from '@/common/components/ui';
import {
  AdminEmpty,
  AdminError,
  AdminLoading,
  AdminSection,
  AdminStatTile,
} from '@/common/components/admin';
import { PageTemplate } from '@/common/components/PageTemplate';
import {
  AlertCircleIcon,
  AlertIcon,
  ArrowDownIcon,
  ArrowRightIcon,
  ArrowUpIcon,
  CheckCircleIcon,
  HelpCircleIcon,
  HomeIcon,
  RefreshIcon,
} from '@/common/components/icons/MdiIcons';
import { useAuth } from '@/features/auth/hooks/useAuth';
import {
  canAccessDestination,
  ADMIN_DESTINATIONS,
  getDestinationById,
  getStandaloneConfigurationDestinations,
  hasAccessibleDestinationWithPrefix,
  resolveDestinationPath,
  type AdminDestination,
} from '@/features/admin/registry';
import { useAdminNavPins } from '@/common/contexts/useAdminNavPins';
import { getNavMoveFocusTarget } from '@/common/utils/navPreferences';
import { AdminAttentionPanel } from '@/features/admin/components/AdminAttentionPanel';
import { useAdminOverview } from '@/features/admin/hooks/useAdminOverview';
import { ADMIN_HUB_ROUTE_STATE } from '@/features/admin/utils/adminHubParentState';
import {
  isKnownSubsystemStatus,
  type KnownSubsystemStatus,
  type SubsystemHealthDto,
} from '@/types/adminOverview';

type MoveButtonDirection = 'up' | 'down';

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

function OverallStatusBadge({ status, isStale = false }: { status: string; isStale?: boolean }) {
  const presentation = presentationForSubsystemStatus(status);
  const { Icon } = presentation;
  return (
    <span
      data-testid="admin-hub-overall-status"
      data-overall-status={status}
      data-overall-stale={isStale ? 'true' : 'false'}
    >
      <Badge variant={presentation.badgeVariant} size="sm" className="gap-1.5">
        <Icon className={clsx('h-3.5 w-3.5', presentation.iconClass)} ariaLabel="" />
        {/*
          An operator who navigates by badge alone must not read a cached
          "Healthy" as a live one. The stale caveat rides on the badge itself
          rather than only on the notice above it, so the qualifier cannot be
          missed by skipping straight to the status.
        */}
        Health checks: {presentation.label}
        {isStale && ' (cached)'}
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
 * Attention item rendering — including the stable-id → route resolution — lives
 * in `AdminAttentionPanel`, which also owns the preview/expansion bound (#2517).
 */

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
  'parts-inventory',
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
    .map((destination) => ({ ...destination, path: resolveDestinationPath(destination) }));

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
      state={ADMIN_HUB_ROUTE_STATE}
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
 * 1. **Needs attention** — server-ranked items, bounded to a preview with an
 *    explicit "Show all N" disclosure so a busy farm cannot bury the rest of
 *    the hub (#2517). See `AdminAttentionPanel`.
 * 2. **System health checks** — compact, truthful subsystem status from the
 *    admin overview. Deliberately *not* the same domain as the header's System
 *    pill, which reports service health from `/api/system/info`; both bands say
 *    which feed they speak for.
 * 3. **Operations and settings** — only permitted day-to-day tools, any
 *    permitted configuration destination that lives outside the settings shell
 *    (`/catalog`, `/locations`, `/admin/power-monitors`), plus one Farm & Admin
 *    Settings entry point.
 *
 * The page is fully usable at 430px. Loading uses `AdminLoading`. Failure is
 * split in two, because the hub is what an operator opens precisely when things
 * are broken (#2517):
 * - **No snapshot at all** — `AdminError` with a working retry.
 * - **Refresh failed over a snapshot we already have** — keep the last-known
 *   attention and subsystem state, label it stale, keep its original checked-at
 *   and offer retry. Never let a stale healthy snapshot read as a live all-clear.
 */
export function AdminControlCenterPage() {
  const { hasRole, hasPermission } = useAuth();
  const { pinnedIds, setPinned, movePinned } = useAdminNavPins();
  const [showPinChooser, setShowPinChooser] = useState(false);
  const pinChooserButtonRef = useRef<HTMLButtonElement | null>(null);
  const pinChooserRef = useRef<HTMLDivElement | null>(null);
  const pinMoveButtonRefs = useRef(new Map<string, { up: HTMLButtonElement | null; down: HTMLButtonElement | null; row: HTMLElement | null }>());
  const pendingPinMoveFocusRef = useRef<{ destinationId: string; direction: MoveButtonDirection } | null>(null);
  const [draggingPinId, setDraggingPinId] = useState<string | null>(null);
  const pinAnnouncementRef = useRef<HTMLDivElement | null>(null);
  const pinAnnouncementTimeoutRef = useRef<number | null>(null);
  const canViewOverview = hasRole('farm_admin') || hasPermission('system_settings', 'admin');
  const { data, isLoading, isError, error, isFetching, refetch } = useAdminOverview({
    enabled: canViewOverview,
  });

  // React Query keeps the last successful `data` when a background refetch
  // fails, so `isError` alone cannot distinguish "we have nothing" from "we
  // have a snapshot that just went stale". Collapsing the two threw away the
  // operator's last-known state — including its checked-at — at exactly the
  // moment they needed it (#2517).
  const hasSnapshot = data !== undefined;
  const isHardError = isError && !hasSnapshot;
  const isStale = isError && hasSnapshot;

  // A stale snapshot must never render as a live all-clear: we cannot claim
  // nothing needs attention using numbers we failed to refresh.
  const isAllClear =
    !isStale &&
    data?.overallStatus === 'Healthy' &&
    data.subsystems.length > 0 &&
    data.subsystems.every((subsystem) => subsystem.status === 'Healthy');

  const dashboardDestinations = useMemo(
    () => getDashboardDestinations({ hasRole, hasPermission }),
    [hasRole, hasPermission],
  );
  const eligiblePinDestinations = useMemo(
    () => ADMIN_DESTINATIONS
      .filter((destination) => destination.kind !== 'hub')
      .filter((destination) => canAccessDestination(destination, { hasRole, hasPermission })),
    [hasPermission, hasRole],
  );
  const orderedPinnedDestinations = useMemo(
    () => pinnedIds
      .map((id) => getDestinationById(id))
      .filter((destination): destination is AdminDestination => Boolean(destination))
      .filter((destination) => destination.kind !== 'hub' && canAccessDestination(destination, { hasRole, hasPermission })),
    [hasPermission, hasRole, pinnedIds],
  );
  const orderedPinnedIds = useMemo(
    () => orderedPinnedDestinations.map((destination) => destination.id),
    [orderedPinnedDestinations],
  );
  const pinPositionById = useMemo(
    () => new Map(orderedPinnedIds.map((id, index) => [id, index])),
    [orderedPinnedIds],
  );

  const announcePinChange = useCallback((message: string) => {
    if (pinAnnouncementTimeoutRef.current !== null) {
      window.clearTimeout(pinAnnouncementTimeoutRef.current);
    }
    if (pinAnnouncementRef.current) {
      pinAnnouncementRef.current.textContent = message;
    }
    pinAnnouncementTimeoutRef.current = window.setTimeout(() => {
      if (pinAnnouncementRef.current) {
        pinAnnouncementRef.current.textContent = '';
      }
      pinAnnouncementTimeoutRef.current = null;
    }, 1_500);
  }, []);

  const movePin = useCallback((destinationId: string, targetIndex: number, focusDirection?: MoveButtonDirection) => {
    const destination = orderedPinnedDestinations.find((candidate) => candidate.id === destinationId);
    const targetPosition = Math.max(1, Math.min(targetIndex + 1, orderedPinnedDestinations.length));
    if (focusDirection) {
      pendingPinMoveFocusRef.current = { destinationId, direction: focusDirection };
    }
    movePinned(destinationId, targetIndex, orderedPinnedIds);
    if (destination) {
      announcePinChange(`Moved ${destination.label} to position ${targetPosition} of ${orderedPinnedDestinations.length}.`);
    }
  }, [announcePinChange, movePinned, orderedPinnedDestinations, orderedPinnedIds]);

  const pinChooserDestinations = useMemo(() => {
    const pinnedDestinationIds = new Set(orderedPinnedIds);
    return [
      ...orderedPinnedDestinations,
      ...eligiblePinDestinations.filter((destination) => !pinnedDestinationIds.has(destination.id)),
    ];
  }, [eligiblePinDestinations, orderedPinnedDestinations, orderedPinnedIds]);

  useEffect(() => {
    if (!showPinChooser) return;
    const focusFrame = window.requestAnimationFrame(() => {
      pinChooserRef.current?.querySelector<HTMLElement>('button')?.focus();
    });
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        setShowPinChooser(false);
        pinChooserButtonRef.current?.focus();
      }
    };
    window.addEventListener('keydown', onKeyDown);
    return () => {
      window.cancelAnimationFrame(focusFrame);
      window.removeEventListener('keydown', onKeyDown);
    };
  }, [showPinChooser]);

  useEffect(() => {
    const pendingFocus = pendingPinMoveFocusRef.current;
    if (!pendingFocus) {
      return;
    }

    const moveRefs = pinMoveButtonRefs.current.get(pendingFocus.destinationId);
    getNavMoveFocusTarget(moveRefs, pendingFocus.direction)?.focus();
    pendingPinMoveFocusRef.current = null;
  }, [orderedPinnedIds]);

  // The "no operational tools" empty state is only truthful when the whole
  // band is empty — a delegate who reaches Power Monitors but no operational
  // tool still has somewhere to go, so we must not tell them otherwise.
  const hasAnyDestination =
    dashboardDestinations.operational.length > 0 ||
    dashboardDestinations.configuration.length > 0 ||
    dashboardDestinations.settings;

  const refreshButton = (
    <div className="flex flex-wrap gap-2">
      <Button
        ref={pinChooserButtonRef}
        variant={showPinChooser ? 'secondary' : 'subtle'}
        size="sm"
        aria-expanded={showPinChooser}
        aria-controls="admin-pin-chooser"
        onClick={() => setShowPinChooser((open) => !open)}
      >
        Pin admin links
      </Button>
      {canViewOverview && (
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
      )}
    </div>
  );

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
      <div ref={pinAnnouncementRef} className="sr-only" aria-live="polite" aria-atomic="true" />
      {showPinChooser && (
        <div
          ref={pinChooserRef}
          id="admin-pin-chooser"
          role="region"
          aria-label="Pin admin links"
          className="mb-6 rounded-lg border border-pf-border bg-pf-bg-1 p-4"
        >
          <div className="mb-3">
            <h2 className="text-base font-semibold text-pf-text-primary">Pin admin links</h2>
            <p className="text-sm text-pf-text-secondary">
              Choose authorized admin destinations to show in your navbar. Pinned links stay in the order shown and are saved in this browser for your account.
            </p>
          </div>
          <div className="space-y-2" role="list" aria-label="Authorized admin destinations">
            {pinChooserDestinations.map((destination) => {
              const pinPosition = pinPositionById.get(destination.id);
              const pinned = pinPosition !== undefined;
              return (
                <div
                  key={destination.id}
                  ref={(node) => {
                    const current = pinMoveButtonRefs.current.get(destination.id) ?? { up: null, down: null, row: null };
                    current.row = node;
                    pinMoveButtonRefs.current.set(destination.id, current);
                  }}
                  role="listitem"
                  tabIndex={-1}
                  draggable={pinned}
                  aria-label={pinned ? `${destination.label}, draggable to reorder` : undefined}
                  onDragStart={() => pinned && setDraggingPinId(destination.id)}
                  onDragEnd={() => setDraggingPinId(null)}
                  onDragOver={(event) => {
                    if (pinned && draggingPinId) event.preventDefault();
                  }}
                  onDrop={(event) => {
                    if (!pinned || !draggingPinId) return;
                    event.preventDefault();
                    if (draggingPinId !== destination.id) {
                      movePin(draggingPinId, pinPosition ?? 0);
                    }
                    setDraggingPinId(null);
                  }}
                  className={clsx(
                    'rounded-md border border-pf-border p-2',
                    pinned && draggingPinId === destination.id && 'opacity-60',
                  )}
                >
                  <div className="flex flex-col gap-2 sm:flex-row sm:items-center sm:justify-between">
                    <div className="min-w-0 w-full">
                      <span className="block break-words text-sm font-medium text-pf-text-primary">{destination.label}</span>
                      <span className="block break-words text-xs text-pf-text-secondary">{destination.description}</span>
                    </div>
                    <div className="flex flex-wrap items-center gap-1">
                      {pinned ? (
                        <>
                          <Button
                            type="button"
                            variant="subtle"
                            size="sm"
                            className="h-8 w-8 px-0"
                            aria-label={`Move ${destination.label} up`}
                            disabled={pinPosition === 0}
                            ref={(node) => {
                              const current = pinMoveButtonRefs.current.get(destination.id) ?? { up: null, down: null, row: null };
                              current.up = node;
                              pinMoveButtonRefs.current.set(destination.id, current);
                            }}
                            onClick={() => movePin(destination.id, (pinPosition ?? 0) - 1, 'up')}
                            iconCenter={<ArrowUpIcon className="h-4 w-4" />}
                          />
                          <Button
                            type="button"
                            variant="subtle"
                            size="sm"
                            className="h-8 w-8 px-0"
                            aria-label={`Move ${destination.label} down`}
                            disabled={pinPosition === orderedPinnedIds.length - 1}
                            ref={(node) => {
                              const current = pinMoveButtonRefs.current.get(destination.id) ?? { up: null, down: null, row: null };
                              current.down = node;
                              pinMoveButtonRefs.current.set(destination.id, current);
                            }}
                            onClick={() => movePin(destination.id, (pinPosition ?? 0) + 1, 'down')}
                            iconCenter={<ArrowDownIcon className="h-4 w-4" />}
                          />
                        </>
                      ) : null}
                      <Button
                        type="button"
                        size="sm"
                        variant={pinned ? 'secondary' : 'subtle'}
                        aria-pressed={pinned}
                        aria-label={`${pinned ? 'Unpin' : 'Pin'} ${destination.label} from navbar`}
                        onClick={() => setPinned(destination.id, !pinned)}
                      >
                        {pinned ? 'Pinned' : 'Pin'}
                      </Button>
                    </div>
                  </div>
                </div>
              );
            })}
          </div>
          {eligiblePinDestinations.length === 0 && (
            <p className="text-sm text-pf-text-secondary">No authorized admin destinations are available to pin.</p>
          )}
        </div>
      )}
      <div className="flex flex-col gap-8">
        {/*
          A failed refresh over an existing snapshot keeps that snapshot on
          screen, clearly labelled, with retry — rather than blanking both
          bands and losing the operator's last-known state (#2517).
        */}
        {canViewOverview && isStale && (
          <div role="status" data-testid="admin-hub-stale-notice">
            <Alert type="warning" title="Showing the last successful check">
              <p>
                The admin overview didn&apos;t respond to the latest refresh, so the attention
                items and health checks below are from{' '}
                {data?.checkedAt ? formatCheckedAt(data.checkedAt) : 'an earlier check'} and may
                be out of date. Nothing here has been resolved.
              </p>
              <Button
                variant="secondary"
                size="sm"
                className="mt-2"
                onClick={() => {
                  void refetch();
                }}
                disabled={isFetching}
                iconLeft={<RefreshIcon className="h-3.5 w-3.5" ariaLabel="" />}
              >
                {isFetching ? 'Retrying…' : 'Try again'}
              </Button>
            </Alert>
          </div>
        )}

        {/* ── Band 1: attention ── */}
        {canViewOverview && (
          <AdminSection
            caption="Needs attention"
            captionId="admin-hub-attention-heading"
            count={isHardError ? undefined : data?.attention.length}
          >
            {isLoading && (
              <AdminLoading
                variant="list"
                label="Loading attention items"
                rows={3}
              />
            )}

            {isHardError && (
              <AdminError
                title="Couldn't load the admin overview"
                description="The admin overview endpoint didn't respond, so health and attention are unavailable. Your admin destinations below still work."
                error={error}
                onRetry={() => {
                  void refetch();
                }}
              />
            )}

            {!isLoading && !isHardError && data && data.attention.length === 0 && (
              <p
                className="flex items-center gap-2 text-sm text-pf-text-secondary"
                data-testid="admin-hub-attention-clear"
              >
                {isAllClear ? (
                  <CheckCircleIcon className="h-4 w-4 shrink-0 text-pf-success" ariaLabel="" />
                ) : (
                  <HelpCircleIcon className="h-4 w-4 shrink-0 text-pf-text-tertiary" ariaLabel="" />
                )}
                {isAllClear
                  ? 'Nothing needs your attention — every subsystem health check is reporting healthy.'
                  : isStale
                    ? 'The last successful check reported no attention items, but that refresh failed — this may be out of date.'
                    : 'The admin overview reported no attention items. Review the system health checks below for the current status.'}
              </p>
            )}

            {!isLoading && !isHardError && data && data.attention.length > 0 && (
              <AdminAttentionPanel
                items={data.attention}
                access={{ hasRole, hasPermission }}
                labelledBy="admin-hub-attention-heading"
              />
            )}
          </AdminSection>
        )}

        {/* ── Band 2: health ── */}
        {canViewOverview && !isHardError && (
          <AdminSection
            caption="System health checks"
            captionId="admin-hub-health-heading"
            captionAside={
              data ? <OverallStatusBadge status={data.overallStatus} isStale={isStale} /> : null
            }
            headerAside={
              data?.checkedAt ? (
                <p className="text-xs text-pf-text-tertiary">
                  {isStale ? 'Last checked at' : 'Checked at'} {formatCheckedAt(data.checkedAt)}
                </p>
              ) : null
            }
          >
            {/*
              Two health summaries are visible at once and they measure
              different things: this band is the admin overview's *subsystem
              health checks*, while the System pill in the top bar reports
              *service health* (versions and host load) from /api/system/info.
              A user seeing "Critical" there and no attention items here was
              reading a domain difference as a contradiction (#2517), so each
              summary now says what it covers instead of both saying "system".
            */}
            <p
              className="text-xs text-pf-text-secondary"
              data-testid="admin-hub-health-domain"
            >
              Backend subsystem probes reported by the admin overview. The System pill in the
              top bar reports service health — versions and host load — separately, so the two
              can legitimately disagree.
            </p>
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

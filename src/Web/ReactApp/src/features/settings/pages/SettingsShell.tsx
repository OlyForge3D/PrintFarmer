import { lazy, Suspense, useCallback, useEffect, useMemo, useRef, useState, type ReactNode, useContext } from 'react';
import { Link, useLocation, useNavigate, useNavigationType, useSearchParams, useBlocker, UNSAFE_DataRouterContext, type BlockerFunction } from 'react-router';
import { ConfirmationModal } from '@/common/components/modals/ConfirmationModal';
import { SearchIcon } from '@/common/components/icons/MdiIcons';
import {
  SettingsSaveRegistryContext,
  type GroupDirtySummary,
  type GroupSaveActions,
  type RegisteredSection,
} from '@/features/admin/settings/settingsSaveRegistry';
import { PageTemplate } from '@/common/components/PageTemplate';
import {
  ADMIN_HUB_PARENT,
  ADMIN_DESTINATIONS,
  canAccessDestination,
  canAccessSettingsTab,
  getDestinationForTab,
  filterDestinationsByAccess,
  isPathWithin,
  type AdminDestination,
} from '@/features/admin/registry/adminDestinations';
import { ThemeSwitcher } from '@/common/components/ThemeSwitcher';
import { FormSkeleton } from '@/common/components/skeletons/FormSkeleton';
import { Skeleton } from '@/common/components/skeletons/Skeleton';
import { Button } from '@/common/components/ui';
import { useAuth } from '@/features/auth/hooks/useAuth';
import { useCommandPalette } from '@/features/settings/components/commandPaletteContext';
import { SettingsHeaderSlotContext } from '@/features/settings/components/settingsHeaderSlotContext';
import { SettingsFooterSlotContext } from '@/features/settings/components/settingsFooterSlotContext';
import { commandPaletteShortcutLabel } from '@/features/settings/components/commandPaletteShortcut';
import { SettingsContentTransition } from '@/features/settings/components/SettingsContentTransition';
import { SettingsSection } from '@/features/settings/components/SettingsSection';
import { SettingsSidebar } from '@/features/settings/components/SettingsSidebar';
import { SettingsSubTabs } from '@/features/settings/components/SettingsSubTabs';
import { UserSettingsSection } from '@/features/settings/components/UserSettingsSection';
import { FarmSettingsSection } from '@/features/settings/components/FarmSettingsSection';
import { TelegramSettingsCard } from '@/features/settings/components/TelegramSettingsCard';
import { HomeAssistantSettingsCard, SpoolmanSettingsCard } from '@/features/settings/components/IntegrationSettingsCards';
import { WorkspaceSearchResults } from '@/features/settings/components/WorkspaceSearchResults';
import { resolveSettingsNavigationTarget, withRetainedQuery, type SettingsCommandItem } from '@/features/settings/settings-navigation';
import { useSettingsSearchIndex } from '@/features/settings/hooks/useSettingsSearchIndex';
import {
  DEFAULT_SCOPE,
  SETTINGS_SCOPES,
  getDefaultCategoryForScope,
  getDefaultSubPage,
  getSettingsCategoriesForScope,
  getSettingsScope,
  getSettingsScopeForCategory,
} from '@/features/settings/types';
import { SettingsPage } from '@/features/admin/pages/SettingsPage';
import { BedTypeAdminPage } from '@/features/admin/pages/BedTypeAdminPage';
import { NfcDevicesPage } from '@/features/nfc/pages/NfcDevicesPage';
import { CamerasPage } from '@/features/cameras/pages/CamerasPage';
import { CustomFieldsAdminPage } from '@/features/admin/pages/CustomFieldsAdminPage';
import { WebhooksAdminPage } from '@/features/webhooks/pages/WebhooksAdminPage';
import { TagAdminPage } from '@/features/admin/pages/TagAdminPage';
import { UserManagementPage } from '@/features/admin/pages/UserManagementPage';
import { ApiKeysPage } from '@/features/profile/pages/ApiKeysPage';
import { NotificationPreferencesPage } from '@/features/notifications/pages/NotificationPreferencesPage';
import { QuotaManagementPage } from '@/features/quotas/pages/QuotaManagementPage';
import { RoleManagementPage } from '@/features/admin/pages/RoleManagementPage';
import { PrinterGroupsPage } from '@/features/printer-groups/pages/PrinterGroupsPage';
import { NfcBindingsPage } from '@/features/nfc/pages/NfcBindingsPage';
import { PasskeysPage } from '@/features/profile/pages/PasskeysPage';
import { SUB_PAGE_ALLOWED_GROUPS } from '@/features/settings/subpage-groups';

const LazySlicerProfilesPage = lazy(() =>
  import('@/features/slicer/pages/SlicerProfilesPage').then((mod) => ({ default: mod.SlicerProfilesPage })),
);

function scrollBehavior(): ScrollBehavior {
  if (typeof window === 'undefined' || typeof window.matchMedia !== 'function') {
    return 'smooth';
  }

  return window.matchMedia('(prefers-reduced-motion: reduce)').matches ? 'auto' : 'smooth';
}

function TabLoader() {
  return (
    <div className="space-y-5 py-3" role="status" aria-label="Loading settings section">
      <div className="space-y-2">
        <Skeleton width="40%" />
        <Skeleton width="70%" />
      </div>
      <FormSkeleton fields={4} />
    </div>
  );
}

function IntegrationConnectionsPanel() {
  const { hasPermission } = useAuth();
  const canEditMetadata = hasPermission('system_settings', 'admin');
  const serviceCards = (
    <div className="space-y-6">
      {!canEditMetadata && hasPermission('spoolman', 'admin') && <SpoolmanSettingsCard />}
      {hasPermission('home_assistant', 'admin') && <HomeAssistantSettingsCard />}
      {hasPermission('telegram', 'admin') && <TelegramSettingsCard />}
    </div>
  );
  return (
    <SettingsSection>
      {canEditMetadata ? (
        <SettingsPage
          allowedGroups={SUB_PAGE_ALLOWED_GROUPS['integrations.connections']}
          introText="Configure third-party services, Smart Plugs, and slicer API connections."
          afterContent={serviceCards}
        />
      ) : serviceCards}
    </SettingsSection>
  );
}

function UserPreferencesPanel() {
  return (
    <SettingsSection>
      <div className="space-y-6">
        <section className="rounded-md border border-pf-border bg-pf-card px-5 py-5">
          <h3 className="text-lg font-semibold text-pf-text-primary">Appearance</h3>
          <p className="mt-1 text-sm text-pf-text-secondary">
            Choose a theme and preview the dashboard surface in real time.
          </p>
          <div className="mt-4">
            <ThemeSwitcher />
          </div>
        </section>
        <UserSettingsSection />
      </div>
    </SettingsSection>
  );
}

const SINGLE_PAGE_CONTENT: Record<string, ReactNode> = {
  quotas: (
    <SettingsSection>
      <QuotaManagementPage embedded />
    </SettingsSection>
  ),
};

const SUB_PAGE_CONTENT: Record<string, ReactNode> = {
  'general.farm': (
    <SettingsSection>
      <SettingsPage
        allowedGroups={SUB_PAGE_ALLOWED_GROUPS['general.farm']}
        introText="Configure farm identity, timezone, and other farm-wide defaults."
        afterContent={<FarmSettingsSection />}
      />
    </SettingsSection>
  ),
  'general.system': (
    <SettingsSection>
      <SettingsPage
        allowedGroups={SUB_PAGE_ALLOWED_GROUPS['general.system']}
        introText="Configure database, logging, network discovery, and file parameters."
      />
    </SettingsSection>
  ),
  'general.automation': (
    <SettingsSection>
      <SettingsPage
        allowedGroups={SUB_PAGE_ALLOWED_GROUPS['general.automation']}
        introText="Configure cost tracking, Obico print failure detection, and automatic tag rules."
      />
    </SettingsSection>
  ),
  'integrations.connections': <IntegrationConnectionsPanel />,
  'integrations.webhooks': (
    <SettingsSection>
      <WebhooksAdminPage embedded />
    </SettingsSection>
  ),
  'profile.preferences': <UserPreferencesPanel />,
  'profile.api-keys': (
    <SettingsSection>
      <ApiKeysPage embedded />
    </SettingsSection>
  ),
  'profile.notifications': (
    <SettingsSection>
      <NotificationPreferencesPage embedded />
    </SettingsSection>
  ),
  'profile.passkeys': (
    <SettingsSection>
      <PasskeysPage embedded />
    </SettingsSection>
  ),
  'slicing.defaults': (
    <SettingsSection>
      <SettingsPage
        allowedGroups={SUB_PAGE_ALLOWED_GROUPS['slicing.defaults']}
        introText="Configure slicer defaults, process behavior, and plate-related settings for the farm."
      />
    </SettingsSection>
  ),
  'slicing.bed-types': <BedTypeAdminPage embedded />,
  'slicing.profiles': (
    <Suspense fallback={<TabLoader />}>
      <LazySlicerProfilesPage embedded />
    </Suspense>
  ),
  'hardware.cameras': <CamerasPage embedded />,
  'hardware.nfc': <NfcDevicesPage embedded />,
  'hardware.printer-groups': <PrinterGroupsPage embedded />,
  'hardware.nfc-bindings': <NfcBindingsPage embedded />,
  'hardware.custom-fields': <CustomFieldsAdminPage embedded />,
  'users.accounts': <UserManagementPage embedded />,
  'users.roles': <RoleManagementPage embedded />,
  'data.tags': <TagAdminPage embedded />,
};

/**
 * Position of the current entry in the browser history stack.
 *
 * React Router stamps this index into `history.state` and derives its own POP
 * deltas from it (`history.js`: `delta = getIndex() - index`), so reading it here
 * gives the guard the same notion of "how far did the user jump" that the router
 * uses. Returns `null` for entries the router did not create.
 */
function readHistoryIndex(): number | null {
  const index = (window.history.state as { idx?: unknown } | null)?.idx;
  return typeof index === 'number' ? index : null;
}

function DataRouterBlocker({
  shouldBlock,
  onBlockerChange,
}: {
  shouldBlock: BlockerFunction;
  onBlockerChange: (blocker: {
    state: 'unblocked' | 'blocked';
    proceed?: () => void;
    reset?: () => void;
    target?: string;
  }) => void;
}) {
  const blocker = useBlocker(shouldBlock);
  useEffect(() => {
    onBlockerChange({
      state: blocker.state === 'blocked' ? 'blocked' : 'unblocked',
      proceed: blocker.state === 'blocked' && typeof blocker.proceed === 'function' ? blocker.proceed : undefined,
      reset: blocker.state === 'blocked' && typeof blocker.reset === 'function' ? blocker.reset : undefined,
      target:
        blocker.state === 'blocked' && blocker.location
          ? `${blocker.location.pathname}${blocker.location.search}`
          : undefined,
    });
  }, [blocker, onBlockerChange]);
  return null;
}

function getFieldParamFromHref(href: string | undefined): string | null {
  const [, queryString] = href?.split('?') ?? [];
  return new URLSearchParams(queryString ?? '').get('field');
}

interface SettingsShellProps {
  /** Lock the shell to a specific route-level scope group.
   * - 'user': only user settings (no scope switcher)
   * - 'system': combined farm and admin configuration
   * If omitted, shows all scopes the user can access (legacy behavior). */
  routeScope?: 'user' | 'system';
}

export const SettingsShell: React.FC<SettingsShellProps> = ({ routeScope }) => {
  const { hasRole, hasPermission } = useAuth();
  // Passed to adminDestinations.ts helpers so scope/tab access checks share the
  // exact same permission semantics as the Control Center hub and nav (issue 1457).
  const destinationAccess = useMemo(() => ({ hasRole, hasPermission }), [hasRole, hasPermission]);
  const configurationDestinations = useMemo(
    () => filterDestinationsByAccess(ADMIN_DESTINATIONS, destinationAccess)
      .filter((destination) => destination.kind === 'configuration'),
    [destinationAccess],
  );
  const canReachSystemScope = configurationDestinations.length > 0;
  // Issue 2526 — configuration destinations that render their own page instead
  // of a `/admin/settings` category (Catalog, Locations, Power Monitors). Their
  // one default home is the Admin Control Center, so the shell no longer lists
  // them as a second directory. The exception below is a recovery affordance,
  // not a directory: a delegate whose only configuration grant is one of these
  // can still open the settings shell (`canReachSystemScope` is true for them)
  // but has no category to render, so without a link out they land on an empty
  // workspace with no way forward.
  const standaloneDestinations = useMemo(
    () => configurationDestinations.filter((destination) => !isPathWithin(destination.path, '/admin/settings')),
    [configurationDestinations],
  );
  const hasEmbeddedSettingsDestination = useMemo(
    () => configurationDestinations.some((destination) => isPathWithin(destination.path, '/admin/settings')),
    [configurationDestinations],
  );
  const showStandaloneRecoveryLinks = standaloneDestinations.length > 0 && !hasEmbeddedSettingsDestination;
  const navigate = useNavigate();
  const [searchParams, setSearchParams] = useSearchParams();
  const { open: openCommandPalette, registerNavigationGuard } = useCommandPalette();

  // Callback ref, not useRef: the slot's DOM node has to be a *rendered* value so
  // the context re-renders its consumers once the node exists. A ref mutation
  // would not trigger that, and the portal would never find its target.
  const [headerSlot, setHeaderSlot] = useState<HTMLElement | null>(null);
  const [footerSlot, setFooterSlot] = useState<HTMLElement | null>(null);
  const commandPaletteShortcut = useMemo(() => commandPaletteShortcutLabel(), []);

  const requestedScope = searchParams.get('scope');
  const requestedCategory = searchParams.get('tab');
  const requestedSubPage = searchParams.get('sub');
  const requestedField = searchParams.get('field');
  const query = searchParams.get('q') || '';
  const normalizedQuery = query.trim().toLowerCase();

  // GH-2505: distinguish a `q` that the persistent workspace search box itself
  // just wrote (via its debounced, replace-only commit below) from a `q`
  // that arrived some other way — a direct link, a bookmark, or browser
  // back/forward. Typing in the persistent search box must never
  // auto-navigate a leaf; that's what makes it navigation "chrome" rather
  // than another mounted settings page. But a `q` present on initial load
  // still has to drive the pre-existing label-matching auto-navigation
  // below for backward compatibility with direct `?q=` deep links. Comparing
  // the live `q` against the last value *we* wrote captures exactly "this
  // changed only because the box committed a keystroke"; once the URL's `q`
  // diverges from that (navigate away and back, a fresh deep link, etc.) the
  // ref and the live query stop matching and auto-navigation resumes.
  //
  // Value-equality alone isn't sufficient, though: browser back/forward can
  // return to an *older* URL whose `q` happens to equal a value we wrote
  // earlier (e.g. the user typed "quotas", navigated to another category —
  // which clears `q` — then hit Back). The ref would still hold "quotas"
  // and wrongly read as self-authored, suppressing legacy auto-navigation
  // for what is really an external history restoration. `commitSearchQuery`
  // only ever writes `q` via a `replace`, so a genuine browser back/forward
  // is always reported as a POP navigation; a same-value match is trusted
  // only when the most recent navigation wasn't a POP.
  const navigationType = useNavigationType();
  const lastSelfWrittenQueryRef = useRef<string | null>(null);
  const isSelfAuthoredQuery =
    navigationType !== 'POP' &&
    lastSelfWrittenQueryRef.current !== null &&
    lastSelfWrittenQueryRef.current === query;

  const commitSearchQuery = useCallback((nextQuery: string) => {
    lastSelfWrittenQueryRef.current = nextQuery;
    setSearchParams((prev) => {
      const next = new URLSearchParams(prev);
      if (nextQuery) {
        next.set('q', nextQuery);
      } else {
        next.delete('q');
      }
      return next;
    }, { replace: true });
  }, [setSearchParams]);

  const isAdminRoute = routeScope === 'system';
  const fieldSearchIndex = useSettingsSearchIndex({
    enabled: isAdminRoute && Boolean(requestedField),
  });

  const availableScopes = useMemo(() => {
    if (routeScope === 'user') {
      return SETTINGS_SCOPES.filter((scope) => scope.id === 'user');
    }
    if (routeScope === 'system') {
      return SETTINGS_SCOPES.filter((scope) => scope.id === 'system' && canReachSystemScope);
    }
    return SETTINGS_SCOPES.filter((scope) => {
      if (scope.id === 'system') return canReachSystemScope;
      return !scope.adminOnly;
    });
  }, [canReachSystemScope, routeScope]);
  const fallbackScopeId = routeScope ?? availableScopes[0]?.id ?? DEFAULT_SCOPE;
  // Issue 1457 (Hicks review) — filter both the category list AND each category's
  // sub-pages by canAccessSettingsTab, not just the rendered content of the
  // active tab. Without this, SettingsSidebar/SettingsSubTabs still listed
  // every tab in scope regardless of permission, so a user with e.g. only
  // `printers:admin` could see Cameras/NFC/other inaccessible Hardware
  // sub-tabs in the nav and only find out they were denied after clicking.
  // A category with zero remaining accessible sub-pages (or, for a
  // no-sub-page category, an inaccessible root tab) is dropped entirely.
  const accessibleCategories = useMemo(
    () => availableScopes
      .flatMap((scope) => getSettingsCategoriesForScope(scope.id))
      .map((category) => ({
        ...category,
        subPages: category.subPages.filter((subPage) => canAccessSettingsTab(category.id, subPage.id, destinationAccess)),
      }))
      .filter((category) => (
        category.subPages.length > 0
        || canAccessSettingsTab(category.id, undefined, destinationAccess)
      )),
    [availableScopes, destinationAccess],
  );

  const resolvedRequestedTarget = useMemo(
    () => resolveSettingsNavigationTarget(requestedCategory, requestedSubPage, routeScope ?? requestedScope),
    [requestedCategory, requestedScope, requestedSubPage, routeScope],
  );

  const requestedFieldTarget = useMemo(() => {
    if (!requestedField) {
      return undefined;
    }

    const matchingField = fieldSearchIndex.settingFieldItems.find((item) => getFieldParamFromHref(item.href) === requestedField);
    if (!matchingField) {
      return undefined;
    }

    const scopeConstraint = routeScope ?? (requestedScope === 'user' || requestedScope === 'system' ? requestedScope : undefined);
    if (scopeConstraint && matchingField.scopeId !== scopeConstraint) {
      return undefined;
    }

    return {
      scopeId: matchingField.scopeId,
      categoryId: matchingField.categoryId,
      subPageId: matchingField.subPageId,
    };
  }, [fieldSearchIndex.settingFieldItems, requestedField, requestedScope, routeScope]);

  const activeScope = useMemo(() => {
    return availableScopes.some((scope) => scope.id === resolvedRequestedTarget.scopeId)
      ? resolvedRequestedTarget.scopeId
      : fallbackScopeId;
  }, [availableScopes, fallbackScopeId, resolvedRequestedTarget.scopeId]);

  const activeCategory = useMemo(() => {
    if (accessibleCategories.some((category) => category.id === resolvedRequestedTarget.categoryId)) {
      return resolvedRequestedTarget.categoryId;
    }
    // Defaults follow the same access predicate as navigation.
    const firstAccessibleInScope = accessibleCategories.find((category) => category.scopeId === activeScope);
    return firstAccessibleInScope?.id ?? getDefaultCategoryForScope(activeScope);
  }, [accessibleCategories, activeScope, resolvedRequestedTarget.categoryId]);

  const shouldFocusSectionRef = useRef(false);
  const previousRenderedKeyRef = useRef<string | null>(null);

  // ── Save Registry & Draft Safety ───────────────────────────────────────────
  const [dirtyByGroup, setDirtyByGroup] = useState<Record<string, GroupDirtySummary>>({});
  const [registeredSections, setRegisteredSections] = useState<Record<string, RegisteredSection>>({});
  const groupActionsRef = useRef(new Map<string, GroupSaveActions>());
  const registeredSectionsRef = useRef(new Map<string, RegisteredSection>());

  const [showDraftModal, setShowDraftModal] = useState(false);
  const [pendingNavigation, setPendingNavigation] = useState<(() => void) | null>(null);

  const isDirty = useMemo(
    () => Object.keys(dirtyByGroup).length > 0 || Object.values(registeredSections).some((s) => s.isDirty),
    [dirtyByGroup, registeredSections],
  );

  /**
   * Mirror of {@link isDirty} that the router blocker predicate reads instead of
   * the state value. `handleDiscardAndNavigate` discards and then proceeds in the
   * same tick, so a predicate closing over React state would still observe the
   * pre-discard `true` and re-block the very navigation the user just confirmed.
   * The ref is cleared synchronously in `handleDiscardAll` to close that window.
   */
  const isDirtyRef = useRef(isDirty);
  useEffect(() => {
    isDirtyRef.current = isDirty;
  }, [isDirty]);

  const handleWorkspaceResultSelect = useCallback((item: SettingsCommandItem, queryText: string) => {
    const doNavigate = () => {
      if (item.onExecute) {
        item.onExecute();
        return;
      }
      if (item.href) {
        // Destination/setting items already carry a fully-qualified path
        // (including `field=Section.property` for exact field matches, see
        // `buildSettingCommandItems`); retain the in-progress search text so
        // the workspace search keeps reflecting it once we land.
        navigate(withRetainedQuery(item.href, queryText));
        return;
      }
      // Settings-nav (category/sub-page) items carry no `href` — navigate
      // within the shell exactly like a sidebar/sub-tab click, but as a
      // *push* (new history entry), per "explicit selection ... adds
      // destination history" — unlike `executeCategoryChange`'s `replace`.
      setSearchParams((prev) => {
        const next = new URLSearchParams(prev);
        next.set('scope', item.scopeId);
        next.set('tab', item.categoryId);
        if (item.subPageId) {
          next.set('sub', item.subPageId);
        } else {
          next.delete('sub');
        }
        next.delete('field');
        if (queryText) {
          next.set('q', queryText);
        } else {
          next.delete('q');
        }
        return next;
      });
    };

    if (isDirty) {
      setPendingNavigation(() => doNavigate);
      setShowDraftModal(true);
      return;
    }
    doNavigate();
  }, [isDirty, navigate, setSearchParams]);

  const publishSummary = useCallback((group: string, summary: GroupDirtySummary | null) => {
    setDirtyByGroup((prev) => {
      if (!summary) {
        if (!prev[group]) return prev;
        const next = { ...prev };
        delete next[group];
        return next;
      }
      return { ...prev, [group]: summary };
    });
  }, []);

  const publishIssues = useCallback(() => {}, []);

  const registerActions = useCallback((group: string, actions: GroupSaveActions | null) => {
    if (actions) {
      groupActionsRef.current.set(group, actions);
    } else {
      groupActionsRef.current.delete(group);
    }
  }, []);

  const registerSection = useCallback((section: RegisteredSection | null, sectionId?: string) => {
    if (section) {
      registeredSectionsRef.current.set(section.id, section);
      setRegisteredSections((prev) => ({ ...prev, [section.id]: section }));
    } else {
      const idToRemove = sectionId || (section as unknown as { id?: string })?.id;
      if (idToRemove) {
        registeredSectionsRef.current.delete(idToRemove);
        setRegisteredSections((prev) => {
          if (!(idToRemove in prev)) return prev;
          const next = { ...prev };
          delete next[idToRemove];
          return next;
        });
      }
    }
  }, []);

  const unregisterSection = useCallback((sectionId: string) => {
    registeredSectionsRef.current.delete(sectionId);
    setRegisteredSections((prev) => {
      if (!(sectionId in prev)) return prev;
      const next = { ...prev };
      delete next[sectionId];
      return next;
    });
  }, []);

  const saveRegistry = useMemo(
    () => ({ publishSummary, publishIssues, registerActions, registerSection, unregisterSection }),
    [publishSummary, publishIssues, registerActions, registerSection, unregisterSection],
  );

  const handleDiscardAll = useCallback(() => {
    for (const actions of groupActionsRef.current.values()) {
      actions.discard();
    }
    for (const section of registeredSectionsRef.current.values()) {
      section.onDiscard?.();
    }
    isDirtyRef.current = false;
    setDirtyByGroup({});
    setRegisteredSections({});
    registeredSectionsRef.current.clear();
  }, []);

  /**
   * `useBlocker` requires a data router, so the adapter below stays conditional —
   * a non-data-router host (tests, Storybook) would otherwise crash on mount.
   * The app itself always supplies one via `AppRouterProvider`; a missing context
   * in a browser means the router-level draft guard is inert, which is exactly
   * the silent data-loss defect from issue 2525, so say so loudly in development.
   */
  const hasDataRouter = Boolean(useContext(UNSAFE_DataRouterContext));
  useEffect(() => {
    if (!hasDataRouter && import.meta.env.DEV) {
      console.warn(
        '[SettingsShell] No data router in context — unsaved-draft protection for navbar links ' +
          'and browser Back/Forward is inactive. See issue 2525.',
      );
    }
  }, [hasDataRouter]);

  const [dataBlocker, setDataBlocker] = useState<{
    state: 'unblocked' | 'blocked';
    proceed?: () => void;
    reset?: () => void;
    target?: string;
  }>({
    state: 'unblocked',
    proceed: undefined,
    reset: () => {},
  });

  /**
   * The destination of the navigation the router most recently blocked.
   *
   * React Router resets *every* blocker to idle whenever any navigation
   * completes. The shell navigates to itself constantly (search-query commits,
   * tab/sub param normalisation), so a blocked `proceed`/`reset` handle can be
   * torn down a tick after it is handed to us — while the confirmation modal is
   * still on screen. Remembering the destination ourselves keeps the modal open
   * and keeps "Discard" working even when the handle has gone stale.
   */
  const [blockedTarget, setBlockedTarget] = useState<string | null>(null);

  /**
   * How far through the history stack the blocked navigation was trying to jump.
   *
   * Only meaningful for a POP. Resuming a confirmed Back/Forward by URL would
   * push a new entry and corrupt the stack (a discarded Back would leave
   * `[Printers, Settings, Printers]`, so the *next* Back would surprise the user
   * by returning to Settings). Replaying the delta instead moves the cursor to
   * the entry the user actually asked for, preserving its key and state.
   */
  const [blockedDelta, setBlockedDelta] = useState<number | null>(null);

  /**
   * A resume via `blocker.proceed()` that has been attempted but not yet
   * confirmed. `proceed()` resumes asynchronously and can be a silent no-op on
   * a stale handle, so the attempt is verified against an observed `location`
   * change (see the effect that consumes this) rather than trusted outright.
   */
  const [resumeAttempt, setResumeAttempt] = useState<{
    target: string;
    delta: number | null;
    /**
     * Whether this attempt went through `blocker.proceed()` (a real router
     * transition we can track via `blocker.state`) or a manual `navigate()`
     * call we made ourselves because the blocker was already stale/unblocked.
     * `blocker.state` never enters `'blocked'` for our own manual call, so it
     * cannot be used as a "settled" signal there — using it anyway made the
     * poll declare defeat on its very first tick and fire a second, doubling
     * `navigate()` call while the first one was still in flight.
     */
    viaProceed: boolean;
  } | null>(null);

  const location = useLocation();
  const historyIndexRef = useRef<number | null>(readHistoryIndex());
  useEffect(() => {
    // Only committed locations move the cursor; a blocked POP is rolled back by
    // the router and never reaches here, so this stays pinned to where we are.
    historyIndexRef.current = readHistoryIndex();
  }, [location.key]);

  /**
   * Whether the user has already told us what to do about the currently
   * recorded block (Stay or Discard). `blockedTarget`/`blockedDelta` are kept
   * alive after that response so a stale/no-op `proceed()` still has replay
   * data to fall back on (see `handleDiscardAndNavigate`) — but the modal
   * itself must not stay visually open for that entire window, so its
   * `isOpen` check needs to know a response already happened.
   */
  const [respondedToBlock, setRespondedToBlock] = useState(false);

  const handleBlockerChange = useCallback(
    (next: { state: 'unblocked' | 'blocked'; proceed?: () => void; reset?: () => void; target?: string }) => {
      setDataBlocker(next);
      if (next.state === 'blocked' && next.target) {
        setBlockedTarget(next.target);
        setRespondedToBlock(false);
      }
    },
    [],
  );

  const shouldBlockNav = useCallback(
    ({
      currentLocation,
      nextLocation,
      historyAction,
    }: {
      currentLocation: { pathname: string; search: string };
      nextLocation: { pathname: string; search: string };
      historyAction?: string;
    }) => {
      if (!isDirtyRef.current) return false;
      // The shell rewrites its own query string constantly (`?q=` is committed on
      // every search keystroke, `?tab=`/`?sub=` on every in-page move). Those are
      // shell-authored and already guarded by the in-page modal, so blocking them
      // here would fire the confirmation on each keystroke. A POP is different:
      // it is a genuine history restoration the user asked for, and its target
      // may well be the same pathname.
      if (historyAction !== 'POP' && currentLocation.pathname === nextLocation.pathname) return false;
      const block =
        currentLocation.pathname + currentLocation.search !== nextLocation.pathname + nextLocation.search;
      if (block) {
        // Record the destination here rather than waiting to observe
        // `blocker.state === 'blocked'` from an effect. React Router clears every
        // blocker back to idle as soon as *any* navigation completes, and the
        // shell self-navigates (param normalisation, `?q=` commits) constantly —
        // so the blocked state can be created and destroyed inside a single React
        // batch, and the effect then only ever sees `unblocked`. The predicate is
        // the one place that reliably knows a navigation was stopped and where it
        // was headed.
        setBlockedTarget(`${nextLocation.pathname}${nextLocation.search}`);
        setRespondedToBlock(false);
        if (historyAction === 'POP') {
          const nextIndex = readHistoryIndex();
          const currentIndex = historyIndexRef.current;
          const delta = nextIndex !== null && currentIndex !== null ? nextIndex - currentIndex : 0;
          setBlockedDelta(delta !== 0 ? delta : null);
        } else {
          setBlockedDelta(null);
        }
      }
      return block;
    },
    [],
  );

  const blocker = dataBlocker;

  /**
   * Live mirror of `blocker.state` for the resume-verification poll below.
   * That poll runs its own recursive `setTimeout` chain rather than a fresh
   * effect per tick, so it must not close over `blocker` from the render that
   * scheduled it — captured at the moment `proceed()` was just called, that
   * value is `'blocked'` almost every time and never updates within the
   * closure, defeating the early-exit and burning the full bounded window on
   * every resume regardless of how quickly the router actually settles.
   */
  const blockerStateRef = useRef(blocker.state);
  blockerStateRef.current = blocker.state;

  const isBlocked = blocker.state === 'blocked' || blockedTarget !== null;
  // Once the user has answered (Stay or Discard), the modal must not stay
  // visually open just because `blockedTarget`/`blockedDelta` are still
  // retained as fallback replay data — that retention outlives the response
  // by design (see `handleDiscardAndNavigate`), so it can't drive visibility.
  const isModalOpen = showDraftModal || (isBlocked && !respondedToBlock);

  const handleStay = useCallback(() => {
    if (blocker.state === 'blocked' && blocker.reset) {
      // The handle may already be stale — React Router clears blockers whenever
      // any navigation completes, and it throws on an invalid state transition.
      // Staying put is still the correct outcome, so a dead handle is harmless.
      try {
        blocker.reset();
      } catch {
        /* blocker already released by the router */
      }
    }
    setBlockedTarget(null);
    setBlockedDelta(null);
    setRespondedToBlock(false);
    setResumeAttempt(null);
    setShowDraftModal(false);
    setPendingNavigation(null);
  }, [blocker]);

  const handleDiscardAndNavigate = useCallback(() => {
    setShowDraftModal(false);
    setRespondedToBlock(true);
    handleDiscardAll();

    const targetLocation = blocker.target ?? blockedTarget;
    const delta = blockedDelta;
    let proceeded = false;
    if (blocker.state === 'blocked' && blocker.proceed) {
      try {
        blocker.proceed();
        proceeded = true;
      } catch {
        // Stale handle: the router released this blocker between the render
        // that captured it and this click. Fall through to the manual replay
        // below instead of trusting a proceed() that never actually ran.
        proceeded = false;
      }
    }

    if (proceeded && targetLocation) {
      // `proceed()` not throwing is not proof the router actually reached the
      // blocked destination — a stale/released blocker can be a silent
      // no-op — and it resumes asynchronously, so we can't check
      // `window.location` synchronously here without racing it. Do NOT also
      // call `navigate(delta)` ourselves: that transition is already in
      // flight from `proceed()`, and re-applying the same delta on top of it
      // would double-traverse the history stack. Record what we attempted
      // and let the effect below verify it against an observed location
      // change, replaying the traversal only if it never arrives.
      setResumeAttempt({ target: targetLocation, delta, viaProceed: true });
    } else if (delta !== null && targetLocation) {
      // The blocker was already stale/unblocked by the time this handler ran
      // (a re-render between the block and the click can flip `blocker.state`
      // to `'unblocked'` before we ever get here) — nothing is in flight to
      // resume, so we must replay the traversal ourselves. Do NOT call
      // `navigate(delta)` synchronously right here: the block that was just
      // discarded may itself still be settling in the browser's own history
      // machinery (a blocked POP is "undone" by the router issuing a reverse
      // traversal, which is itself async), and issuing our own history jump
      // on top of that before it settles can race and silently cancel out.
      // Record the attempt and let the effect below issue the replay a tick
      // later, once state has committed, then verify it the same way as a
      // `proceed()` attempt.
      setResumeAttempt({ target: targetLocation, delta, viaProceed: false });
    } else if (targetLocation) {
      setResumeAttempt({ target: targetLocation, delta: null, viaProceed: false });
    } else if (pendingNavigation) {
      pendingNavigation();
    }
  }, [blocker, blockedDelta, blockedTarget, handleDiscardAll, navigate, pendingNavigation]);

  /**
   * Verifies a resume attempted in `handleDiscardAndNavigate`, whether it went
   * through `blocker.proceed()` or a manual `navigate()` call.
   *
   * Both resume through an async pipeline, so completion is only observable a
   * render (or more) later, via `location` changing — and either can be a
   * silent no-op that never changes `location` at all. A single tick is not a
   * safe cutoff: a `proceed()` transition can still be in flight
   * (`blocker.state` still `'blocked'`) well after the first check, and
   * replaying the traversal while it lands double-applies the delta (observed
   * landing two entries back instead of one). So this polls a bounded number
   * of times, at a real interval.
   *
   * `blocker.state` is only a meaningful "settled" signal for a `proceed()`
   * attempt (`resumeAttempt.viaProceed`) — it is the router's own state for
   * that transition. For a manual `navigate()` call we made ourselves, the
   * blocker never enters `'blocked'` at all, so treating it as "already
   * settled" would give up on attempt 0 and fire a second, doubling
   * `navigate()` call while the first one was still in flight (observed in
   * practice). For that path the bounded attempt count is the only cutoff.
   *
   * A manual (non-`viaProceed`) replay is issued from here — on the poll's
   * first tick, a render after the click was handled — rather than
   * synchronously in the click handler. The block that was just discarded is
   * itself "undone" by the router reversing the browser's already-completed
   * POP, which is its own async history operation; issuing our replacement
   * traversal in the very same synchronous tick as the click can race that
   * reversal and cancel it out (observed: a `popstate` fires but `location`
   * never actually moves). Deferring by even one tick avoids the overlap.
   */
  useEffect(() => {
    if (!resumeAttempt) return;
    const currentLocation = `${location.pathname}${location.search}`;
    if (currentLocation === resumeAttempt.target) {
      setResumeAttempt(null);
      setBlockedTarget(null);
      setBlockedDelta(null);
      setRespondedToBlock(false);
      setPendingNavigation(null);
      return;
    }

    let cancelled = false;
    let navigated = resumeAttempt.viaProceed; // a proceed() attempt already navigated; a manual one hasn't yet.
    const maxAttempts = 25; // ~500ms total at 20ms/attempt: generous for a real transition, bounded against a stale handle.
    const pollIntervalMs = 20;

    const replay = (current: { target: string; delta: number | null }) => {
      if (current.delta !== null) {
        navigate(current.delta);
      } else {
        navigate(current.target);
      }
    };

    const check = (attempt: number) => {
      if (cancelled) return;
      if (!navigated) {
        navigated = true;
        replay(resumeAttempt);
        setTimeout(() => check(attempt + 1), pollIntervalMs);
        return;
      }
      const latestLocation = `${window.location.pathname}${window.location.search}`;
      if (latestLocation === resumeAttempt.target) {
        setResumeAttempt(null);
        setBlockedTarget(null);
        setBlockedDelta(null);
        setRespondedToBlock(false);
        setPendingNavigation(null);
        return;
      }
      const routerSettled = resumeAttempt.viaProceed ? blockerStateRef.current !== 'blocked' : false;
      if (!routerSettled && attempt < maxAttempts) {
        setTimeout(() => check(attempt + 1), pollIntervalMs);
        return;
      }
      // Either the router reports nothing in flight (settled, immediately
      // replay — there is nothing left to wait for) or the bound is exhausted
      // while still blocked (a pathologically stuck handle; replay rather
      // than wait forever). Either way, the resume never actually happened.
      setResumeAttempt((current) => {
        if (current !== resumeAttempt) return current; // superseded by a newer attempt
        replay(current);
        return null;
      });
    };
    const timer = setTimeout(() => check(0), pollIntervalMs);
    return () => {
      cancelled = true;
      clearTimeout(timer);
    };
  }, [resumeAttempt, location.pathname, location.search, blocker.state, navigate]);

  const previousLocationRef = useRef<string | null>(null);
  useEffect(() => {
    const currentLocation = `${location.pathname}${location.search}`;
    const hadBlockedFallback = blockedTarget !== null || blockedDelta !== null;
    if (hadBlockedFallback && previousLocationRef.current && previousLocationRef.current !== currentLocation) {
      setBlockedTarget(null);
      setBlockedDelta(null);
      setRespondedToBlock(false);
      setPendingNavigation(null);
    }
    previousLocationRef.current = currentLocation;
  }, [blockedDelta, blockedTarget, location.pathname, location.search]);


  useEffect(() => {
    if (!isDirty) return;
    const handleBeforeUnload = (e: BeforeUnloadEvent) => {
      e.preventDefault();
      e.returnValue = '';
    };
    window.addEventListener('beforeunload', handleBeforeUnload);
    return () => window.removeEventListener('beforeunload', handleBeforeUnload);
  }, [isDirty]);

  const executeCategoryChange = useCallback(
    (categoryId: string, explicitSubPageId?: string) => {
      const target = resolveSettingsNavigationTarget(categoryId, explicitSubPageId, activeScope);
      const targetCategory = accessibleCategories.find((category) => category.id === target.categoryId);
      shouldFocusSectionRef.current = true;
      setSearchParams((prev) => {
        const next = new URLSearchParams(prev);
        next.set('scope', target.scopeId);
        next.set('tab', target.categoryId);
        next.delete('q');
        next.delete('field');
        next.delete('workerTab');

        const subToUse = explicitSubPageId ?? target.subPageId;
        if (subToUse) {
          next.set('sub', subToUse);
        } else {
          const matchingTargetSubPage = !normalizedQuery || !targetCategory
            ? undefined
            : targetCategory.subPages.find((subPage) => (
                subPage.label.toLowerCase().includes(normalizedQuery)
                || subPage.keywords.some((keyword) => keyword.includes(normalizedQuery))
              ));

          if (matchingTargetSubPage) {
            next.set('sub', matchingTargetSubPage.id);
          } else {
            const defaultSubPage = targetCategory?.subPages[0]?.id;
            if (defaultSubPage) {
              next.set('sub', defaultSubPage);
            } else {
              next.delete('sub');
            }
          }
        }

        return next;
      });
    },
    [accessibleCategories, activeScope, normalizedQuery, setSearchParams],
  );

  const handleCategoryChange = useCallback(
    (categoryId: string, explicitSubPageId?: string) => {
      if (isDirty) {
        setPendingNavigation(() => () => executeCategoryChange(categoryId, explicitSubPageId));
        setShowDraftModal(true);
      } else {
        executeCategoryChange(categoryId, explicitSubPageId);
      }
    },
    [executeCategoryChange, isDirty],
  );

  const handleSubPageChange = useCallback(
    (subPageId: string) => {
      const doNavigate = () => {
        shouldFocusSectionRef.current = true;
        setSearchParams((prev) => {
          const next = new URLSearchParams(prev);
          next.set('scope', activeScope);
          next.set('tab', activeCategory);
          next.set('sub', subPageId);
          next.delete('q');
          next.delete('field');
          return next;
        });
      };

      if (isDirty) {
        setPendingNavigation(() => doNavigate);
        setShowDraftModal(true);
      } else {
        doNavigate();
      }
    },
    [activeCategory, activeScope, isDirty, setSearchParams],
  );

  const handleSelectDestination = useCallback(
    (dest: AdminDestination) => {
      const doNavigate = () => {
        if (dest.path.startsWith('/admin/settings')) {
          const queryString = dest.path.includes('?') ? dest.path.split('?')[1] : '';
          const queryParams = new URLSearchParams(queryString);
          const destTab = queryParams.get('tab');
          const destSub = queryParams.get('sub');
          if (destTab) {
            executeCategoryChange(destTab, destSub ?? undefined);
            return;
          }
        }
        navigate(dest.path);
      };

      if (isDirty) {
        setPendingNavigation(() => doNavigate);
        setShowDraftModal(true);
      } else {
        doNavigate();
      }
    },
    [executeCategoryChange, isDirty, navigate],
  );

  useEffect(() => registerNavigationGuard((href) => {
    if (!isDirty) {
      return false;
    }
    setPendingNavigation(() => () => navigate(href));
    setShowDraftModal(true);
    return true;
  }), [isDirty, navigate, registerNavigationGuard]);

  const { matchingCategoryIds, matchingSubPageIds, firstMatchingSubPageCategoryId, isFiltering } = useMemo(() => {
    if (!normalizedQuery) {
      return {
        matchingCategoryIds: undefined,
        matchingSubPageIds: undefined,
        firstMatchingSubPageCategoryId: undefined,
        isFiltering: false,
      };
    }

    const categoryIds: string[] = [];
    const subPageIds: string[] = [];
    let firstSubPageCategoryId: string | undefined;

    for (const category of accessibleCategories) {
      const categoryDirectlyMatches = category.label.toLowerCase().includes(normalizedQuery)
        || category.keywords.some((keyword) => keyword.includes(normalizedQuery));

      if (categoryDirectlyMatches && !categoryIds.includes(category.id)) {
        categoryIds.push(category.id);
      }

      for (const subPage of category.subPages) {
        const subPageMatches = subPage.label.toLowerCase().includes(normalizedQuery)
          || subPage.keywords.some((keyword) => keyword.includes(normalizedQuery));

        if (subPageMatches) {
          subPageIds.push(subPage.id);
          firstSubPageCategoryId ??= category.id;
          if (!categoryIds.includes(category.id)) {
            categoryIds.push(category.id);
          }
        }
      }
    }

    // If no direct category/subPage match, fall back to scope-level keyword match
    if (categoryIds.length === 0 && subPageIds.length === 0) {
      for (const category of accessibleCategories) {
        const scopeMeta = getSettingsScope(category.scopeId);
        const scopeMatches = scopeMeta?.label.toLowerCase().includes(normalizedQuery)
          || scopeMeta?.keywords.some((keyword) => keyword.includes(normalizedQuery));
        if (scopeMatches && !categoryIds.includes(category.id)) {
          categoryIds.push(category.id);
        }
      }
    }

    return {
      matchingCategoryIds: categoryIds,
      matchingSubPageIds: subPageIds,
      firstMatchingSubPageCategoryId: firstSubPageCategoryId,
      isFiltering: true,
    };
  }, [accessibleCategories, normalizedQuery]);

  // GH-2505: `isFiltering` above stays live while the persistent workspace
  // search box is typed into, so sidebar/sub-tab highlighting keeps working.
  // Auto-*navigating* off `isFiltering` (switching scope/category/sub-page,
  // or swapping the whole content pane for a "No matching settings" empty
  // state) must NOT fire while the mounted leaf has unsaved edits — that
  // would remount/destroy the dirty editor on every keystroke, which is
  // exactly what GH-2506's draft-safety boundary exists to prevent. It also
  // must not fire while `q` only changed because the persistent search box
  // itself just committed a keystroke (`isSelfAuthoredQuery`) — typing there
  // must never select a leaf, dirty or not; only a `q` that arrived some
  // other way (a direct `?q=` deep link, browser back/forward) still drives
  // this legacy label-matching auto-navigation. Every auto-navigation
  // decision below is gated on `canAutoNavigate`, not the raw `isFiltering`,
  // while highlighting-only consumers keep using the raw flag/id lists
  // unchanged.
  const canAutoNavigate = isFiltering && !isDirty && !isSelfAuthoredQuery;

  const effectiveScope = useMemo(() => {
    if (!isDirty && requestedFieldTarget) {
      return requestedFieldTarget.scopeId;
    }

    if (!canAutoNavigate || !matchingCategoryIds || matchingCategoryIds.length === 0) {
      return activeScope;
    }
    if (matchingCategoryIds.includes(activeCategory)) {
      return getSettingsScopeForCategory(activeCategory);
    }
    if (firstMatchingSubPageCategoryId && matchingCategoryIds.includes(firstMatchingSubPageCategoryId)) {
      return getSettingsScopeForCategory(firstMatchingSubPageCategoryId);
    }
    return getSettingsScopeForCategory(matchingCategoryIds[0]);
  }, [activeCategory, activeScope, canAutoNavigate, firstMatchingSubPageCategoryId, isDirty, matchingCategoryIds, requestedFieldTarget]);

  const scopeCategories = useMemo(
    () => getSettingsCategoriesForScope(effectiveScope),
    [effectiveScope],
  );

  const effectiveCategory = useMemo(() => {
    if (!isDirty && requestedFieldTarget && scopeCategories.some((category) => category.id === requestedFieldTarget.categoryId)) {
      return requestedFieldTarget.categoryId;
    }

    if (!canAutoNavigate || !matchingCategoryIds || matchingCategoryIds.length === 0) {
      return scopeCategories.some((category) => category.id === activeCategory)
        ? activeCategory
        : getDefaultCategoryForScope(effectiveScope);
    }

    if (scopeCategories.some((category) => category.id === activeCategory) && matchingCategoryIds.includes(activeCategory)) {
      return activeCategory;
    }

    const firstMatchingCategory = scopeCategories.find((category) => matchingCategoryIds.includes(category.id));
    return firstMatchingCategory?.id ?? scopeCategories[0]?.id ?? getDefaultCategoryForScope(effectiveScope);
  }, [activeCategory, canAutoNavigate, effectiveScope, isDirty, matchingCategoryIds, requestedFieldTarget, scopeCategories]);

  const currentCategory = useMemo(
    () => scopeCategories.find((category) => category.id === effectiveCategory) ?? scopeCategories[0],
    [effectiveCategory, scopeCategories],
  );

  const currentScopeMeta = useMemo(
    () => getSettingsScope(effectiveScope) ?? availableScopes[0],
    [availableScopes, effectiveScope],
  );

  const currentCategoryMatchesQuery = useMemo(() => {
    if (!normalizedQuery || !currentCategory) {
      return false;
    }

    return currentCategory.label.toLowerCase().includes(normalizedQuery)
      || currentCategory.keywords.some((keyword) => keyword.includes(normalizedQuery));
  }, [currentCategory, normalizedQuery]);

  const directMatchingCurrentSubPageIds = useMemo(() => {
    if (!currentCategory || !isFiltering || !matchingSubPageIds) {
      return [];
    }

    return currentCategory.subPages
      .map((subPage) => subPage.id)
      .filter((subPageId) => matchingSubPageIds.includes(subPageId));
  }, [currentCategory, isFiltering, matchingSubPageIds]);

  const matchingCurrentSubPageIds = useMemo(() => {
    if (!currentCategory) {
      return [];
    }

    if (directMatchingCurrentSubPageIds.length > 0) {
      return directMatchingCurrentSubPageIds;
    }

    if (currentCategoryMatchesQuery) {
      return currentCategory.subPages
        .filter((subPage) => canAccessSettingsTab(currentCategory.id, subPage.id, destinationAccess))
        .map((subPage) => subPage.id);
    }

    return [];
  }, [currentCategory, currentCategoryMatchesQuery, destinationAccess, directMatchingCurrentSubPageIds]);

  const activeSubPage = useMemo(() => {
    if (!currentCategory || currentCategory.subPages.length === 0) {
      return '';
    }

    const accessibleSubPages = accessibleCategories
      .find((category) => category.id === currentCategory.id)
      ?.subPages ?? [];

    const requestedFieldSubPage = requestedFieldTarget?.categoryId === currentCategory.id
      ? requestedFieldTarget.subPageId
      : undefined;
    const isExplicitSubPage = Boolean(requestedSubPage || requestedFieldSubPage);
    const requestedTargetSubPage = requestedFieldSubPage ?? (resolvedRequestedTarget.categoryId === currentCategory.id
      ? (requestedSubPage ?? resolvedRequestedTarget.subPageId)
      : undefined);

    const isAccessibleSubPage = requestedTargetSubPage
      ? accessibleSubPages.some((subPage) => subPage.id === requestedTargetSubPage)
      : false;

    const isValidSubPage = requestedTargetSubPage
      ? currentCategory.subPages.some((subPage) => subPage.id === requestedTargetSubPage)
      : false;

    // If an explicit ?sub= param was in the URL, honour it if valid (even if inaccessible,
    // so canAccessActiveTab shows permission denied). If no explicit ?sub= was in the URL,
    // only use the target sub-page if it is accessible to this user; otherwise fall back
    // to the first accessible sub-page.
    const canUseRequestedSubPage = requestedTargetSubPage && (isExplicitSubPage ? isValidSubPage : isAccessibleSubPage);

    // GH-2505: mirror `canAutoNavigate` here — while the mounted leaf is dirty,
    // treat the sub-page match set as empty so a live-typed query can never
    // steer `activeSubPage` away from the sub-page already on screen. The
    // set still feeds SettingsSubTabs highlighting unguarded above.
    const autoNavigateSubPageIds = canAutoNavigate ? matchingCurrentSubPageIds : [];

    if (canUseRequestedSubPage && requestedTargetSubPage) {
      if (!canAutoNavigate || autoNavigateSubPageIds.length === 0 || autoNavigateSubPageIds.includes(requestedTargetSubPage)) {
        return requestedTargetSubPage;
      }
    }

    if (autoNavigateSubPageIds.length > 0) {
      return autoNavigateSubPageIds[0];
    }

    const firstAccessibleSubPage = accessibleSubPages[0]?.id;

    return firstAccessibleSubPage ?? getDefaultSubPage(currentCategory.id);
  }, [accessibleCategories, canAutoNavigate, currentCategory, matchingCurrentSubPageIds, requestedFieldTarget, requestedSubPage, resolvedRequestedTarget.categoryId, resolvedRequestedTarget.subPageId]);

  const hasSubTabs = accessibleCategories.length > 0 && currentCategory.subPages.length >= 2;
  const renderedContentKey = currentCategory.subPages.length === 0
    ? currentCategory.id
    : `${currentCategory.id}.${activeSubPage}`;
  const activeSubPageLabel = currentCategory.subPages.find((subPage) => subPage.id === activeSubPage)?.label;
  // Issue 1457 (Hicks review) — the sub-tab bar itself must only list sub-pages the
  // user can actually reach, not every sub-page the category defines. Looked
  // up from the already permission-filtered `accessibleCategories` (falls
  // back to the unfiltered list if the category isn't present there, which
  // shouldn't happen for a category the user can currently see at all).
  const visibleSubPages = useMemo(
    () => accessibleCategories.find((category) => category.id === currentCategory.id)?.subPages ?? [],
    [accessibleCategories, currentCategory],
  );
  const sectionHeadingRef = useRef<HTMLHeadingElement>(null);

  const sectionAnnouncement = useMemo(() => {
    const scopeLabel = currentScopeMeta?.label ?? 'Settings';
    if (!hasSubTabs) {
      return `${scopeLabel}, ${currentCategory.label} selected`;
    }

    return activeSubPageLabel
      ? `${scopeLabel}, ${currentCategory.label}, ${activeSubPageLabel} section selected`
      : `${scopeLabel}, ${currentCategory.label} selected`;
  }, [activeSubPageLabel, currentCategory.label, currentScopeMeta, hasSubTabs]);

  useEffect(() => {
    if (isAdminRoute && accessibleCategories.length === 0) {
      if (requestedScope !== 'system' || requestedCategory !== null || requestedSubPage !== null || searchParams.has('field')) {
        setSearchParams((prev) => {
          const next = new URLSearchParams(prev);
          next.set('scope', 'system');
          next.delete('tab');
          next.delete('sub');
          next.delete('field');
          return next;
        }, { replace: true });
      }
      return;
    }

    // GH-2505: never auto-navigate (or strip tab/sub params) while the mounted
    // leaf has unsaved edits, or while `q` only changed because the
    // persistent search box itself just committed a keystroke.
    // `effectiveScope`/`effectiveCategory`/`activeSubPage` are already frozen
    // to the current values in both cases (see `canAutoNavigate` above), so
    // every mismatch check below would be false anyway — this early return
    // just makes that guarantee explicit and skips the "clear tab/sub when
    // there are no matches" branch, which would otherwise strip an
    // already-selected tab out from under a dirty editor, or out from under
    // the user while they're still typing into the persistent search box,
    // the moment a query stops matching anything by the legacy label match.
    if (isDirty || isSelfAuthoredQuery) {
      return;
    }
    if (requestedField && (fieldSearchIndex.isLoading || fieldSearchIndex.isError)) {
      return;
    }
    if (isFiltering && !requestedFieldTarget && matchingCategoryIds?.length === 0) {
      if (requestedCategory === null && requestedSubPage === null) {
        return;
      }

      setSearchParams((prev) => {
        const next = new URLSearchParams(prev);
        next.delete('tab');
        next.delete('sub');
        return next;
      }, { replace: true });
      return;
    }

    const syncScope = requestedFieldTarget?.scopeId ?? activeScope;
    const syncCategory = requestedFieldTarget?.categoryId ?? effectiveCategory;
    const shouldSyncScope = Boolean(requestedFieldTarget) || isFiltering || requestedScope !== null || requestedCategory !== null || activeScope !== DEFAULT_SCOPE;
    const shouldSyncCategory = Boolean(requestedFieldTarget) || isFiltering || requestedCategory !== null || activeScope !== DEFAULT_SCOPE;
    const shouldSyncSub = requestedSubPage !== null
      || Boolean(requestedFieldTarget)
      || (activeSubPage !== '' && currentCategory.subPages.length > 0 && (requestedCategory !== null || isFiltering || activeScope !== DEFAULT_SCOPE));
    const scopeMismatch = shouldSyncScope && requestedScope !== syncScope;
    const categoryMismatch = shouldSyncCategory && requestedCategory !== syncCategory;
    const subMismatch = shouldSyncSub && (requestedSubPage ?? '') !== activeSubPage;

    if (!scopeMismatch && !categoryMismatch && !subMismatch) {
      return;
    }

    setSearchParams((prev) => {
      const next = new URLSearchParams(prev);
      if (shouldSyncScope) {
        next.set('scope', syncScope);
      }
      if (shouldSyncCategory) {
        next.set('tab', syncCategory);
      }
      if (shouldSyncSub) {
        if (activeSubPage) {
          next.set('sub', activeSubPage);
        } else {
          next.delete('sub');
        }
      }
      return next;
    }, { replace: true });
  }, [
    accessibleCategories.length,
    isAdminRoute,
    isDirty,
    isSelfAuthoredQuery,
    searchParams,
    activeScope,
    activeSubPage,
    currentCategory.subPages.length,
    effectiveCategory,
    fieldSearchIndex.isError,
    fieldSearchIndex.isLoading,
    isFiltering,
    matchingCategoryIds,
    requestedCategory,
    requestedField,
    requestedFieldTarget,
    requestedScope,
    requestedSubPage,
    setSearchParams,
  ]);

  useEffect(() => {
    const activeDestinationKey = hasSubTabs && activeSubPage
      ? `${currentCategory.id}.${activeSubPage}`
      : currentCategory.id;

    if (previousRenderedKeyRef.current === null) {
      previousRenderedKeyRef.current = activeDestinationKey;
      return;
    }

    if (!shouldFocusSectionRef.current) {
      previousRenderedKeyRef.current = activeDestinationKey;
      return;
    }

    // Focus first, then scroll. The heading is `sr-only` until `:focus-visible`
    // un-hides it (see its className), so measuring a scroll target before the
    // reveal would aim at a 1×1 box and land the pane 44px off once it expands.
    sectionHeadingRef.current?.focus();

    if (typeof sectionHeadingRef.current?.scrollIntoView === 'function') {
      sectionHeadingRef.current.scrollIntoView({ block: 'start', behavior: scrollBehavior() });
    }

    shouldFocusSectionRef.current = false;
    previousRenderedKeyRef.current = activeDestinationKey;
  }, [currentCategory.id, hasSubTabs, activeSubPage]);

  // Per-tab/sub-page permission gate (issue 1457). Reuses the same
  // canAccessDestination predicate the registry's bulk filter and the
  // Layout nav use, so a directly-linked tab honours requiredRole,
  // requiredPermission, AND requiredPermissionAnyOf — not just one of them.
  // That distinction matters for the one remaining role-only exception
  // (slicing-profiles, which has no requiredPermission at all) which would
  // otherwise render as accessible to anyone who reaches the
  // /admin/settings scope. Tabs with no matching destination (e.g. the
  // `user`-scope profile tabs) are not gated here at all; the server
  // remains the actual enforcement point either way. This is a UX
  // tightening only: previously any `farm_admin` saw every tab regardless
  // of a hypothetical narrower permission — nobody loses access they
  // previously had, this only prevents landing on a tab the API would
  // refuse.
  const activeTabDestination = useMemo(
    () => getDestinationForTab(currentCategory.id, currentCategory.subPages.length > 0 ? activeSubPage : undefined),
    [activeSubPage, currentCategory],
  );
  const canAccessActiveTab = useMemo(() => {
    if (isAdminRoute && accessibleCategories.length === 0) return false;
    if (!activeTabDestination) {
      return true;
    }
    return canAccessDestination(activeTabDestination, destinationAccess);
  }, [accessibleCategories.length, activeTabDestination, destinationAccess, isAdminRoute]);

  const content = useMemo(() => {
    if (isAdminRoute && accessibleCategories.length === 0) {
      return (
        <SettingsSection>
          <p className="py-8 text-sm text-pf-text-secondary">No settings editor is available with your permissions. Use the authorized configuration links, if shown.</p>
        </SettingsSection>
      );
    }
    if (!canAccessActiveTab) {
      return (
        <SettingsSection>
          <div className="py-8 text-center text-pf-text-secondary">
            <p className="text-sm">You don't have permission to view {activeSubPageLabel ?? currentCategory.label}.</p>
          </div>
        </SettingsSection>
      );
    }

    if (currentCategory.subPages.length === 0) {
      return SINGLE_PAGE_CONTENT[currentCategory.id] ?? (
        <SettingsSection>
          <div className="py-8 text-center text-pf-text-secondary">
            <p className="text-sm">{currentCategory.label} settings will be available here.</p>
          </div>
        </SettingsSection>
      );
    }

    return SUB_PAGE_CONTENT[renderedContentKey] ?? (
      <div className="py-8 text-center text-pf-text-secondary">
        <p className="text-sm">Content not found for {renderedContentKey}</p>
      </div>
    );
  }, [accessibleCategories.length, activeSubPageLabel, canAccessActiveTab, currentCategory, isAdminRoute, renderedContentKey]);

  const pageTitle = currentScopeMeta?.label ?? 'Settings';
  const pageDescription = currentScopeMeta?.description ?? 'Manage PrintFarmer settings and administration.';

  const hasNoMatches = accessibleCategories.length > 0 && canAutoNavigate && !requestedFieldTarget && matchingCategoryIds && matchingCategoryIds.length === 0;

  // Page-level actions. The mode toggle arrives by portal from whichever content
  // page owns it (see SettingsHeaderPortal); the palette is always available, so
  // the shell renders it directly. Slot first so the page's own control sits to
  // the left of the shell-wide one.
  //
  // The persistent workspace search (GH-2505) is deliberately scoped to the
  // admin/system settings route only — it's the cross-group/advanced field
  // search called for by the issue, distinct from the always-global modal
  // palette button beside it, and personal `/settings` intentionally keeps
  // its existing (much smaller) navigation surface untouched.
  const headerActions = (
    <div className="flex flex-wrap items-center justify-end gap-2">
      <div ref={setHeaderSlot} className="contents" />
      {isAdminRoute ? (
        <WorkspaceSearchResults
          initialQuery={query}
          onQueryCommit={commitSearchQuery}
          onSelect={handleWorkspaceResultSelect}
        />
      ) : null}
      <Button
        type="button"
        variant="subtle"
        size="sm"
        onClick={openCommandPalette}
        iconLeft={<SearchIcon className="h-4 w-4" />}
        className="rounded-md border border-pf-border bg-pf-bg-0 text-pf-text-secondary hover:bg-pf-bg-1 hover:text-pf-text-primary"
      >
        <span className="inline-flex items-center gap-2">
          <span>Search settings</span>
          <kbd className="rounded-xs border border-pf-border bg-pf-bg-1 px-1.5 py-0.5 font-sans text-[10px] font-semibold uppercase tracking-[0.16em] text-pf-text-tertiary">
            {commandPaletteShortcut}
          </kbd>
        </span>
      </Button>
    </div>
  );

  const showSubTabs = !isAdminRoute && effectiveScope !== 'system' && !hasNoMatches && visibleSubPages.length > 0;
  const subTabs = showSubTabs ? (
      <div className="border-b border-pf-border px-4 pt-4 md:px-6">
        <SettingsSubTabs
          subPages={visibleSubPages}
          activeSubPage={activeSubPage}
          onSubPageChange={handleSubPageChange}
          matchingSubPageIds={matchingCurrentSubPageIds}
          isFiltering={isFiltering}
          ariaLabel={`${currentCategory.label} settings`}
          searchQuery={query}
        />
      </div>
    ) : null;

  return (
    <SettingsSaveRegistryContext.Provider value={saveRegistry}>
      {hasDataRouter && <DataRouterBlocker shouldBlock={shouldBlockNav} onBlockerChange={handleBlockerChange} />}
      <SettingsHeaderSlotContext.Provider value={headerSlot}>
        <SettingsFooterSlotContext.Provider value={footerSlot}>
          <PageTemplate
            title={pageTitle}
            subtitle={pageDescription}
            showHeader
            fill
            parent={isAdminRoute ? ADMIN_HUB_PARENT : undefined}
            actions={headerActions}
          >
            {isAdminRoute && showStandaloneRecoveryLinks && (
              <nav aria-label="Standalone configuration" className="flex flex-wrap gap-3 pb-4">
                {standaloneDestinations.map((destination) => (
                  <Link
                    key={destination.id}
                    to={destination.path}
                    onClick={(e) => {
                      if (e.defaultPrevented || e.button !== 0 || e.metaKey || e.ctrlKey || e.shiftKey || e.altKey) {
                        return;
                      }
                      if (isDirty) {
                        e.preventDefault();
                        setPendingNavigation(() => () => navigate(destination.path));
                        setShowDraftModal(true);
                      }
                    }}
                    className="rounded-md border border-pf-border px-3 py-2 text-sm text-pf-text-primary hover:bg-pf-bg-1 focus-visible:outline-hidden focus-visible:ring-2 focus-visible:ring-pf-accent"
                  >
                    {destination.label}
                  </Link>
                ))}
              </nav>
            )}
            <div className="relative flex flex-1 min-h-0 flex-col overflow-hidden rounded-md border border-pf-border bg-pf-panel">
              <div className="relative flex min-h-0 flex-1 flex-col">
              {hasNoMatches ? (
                <div className="relative flex flex-1 min-h-0 flex-col">
                  <div className="min-h-0 flex-1 overflow-y-auto overscroll-contain">
                    <div className="flex min-h-[60%] items-center justify-center px-4 py-10 md:px-6">
                      <div className="mx-auto max-w-md rounded-md border border-dashed border-pf-border bg-pf-bg-1 px-6 py-10 text-center">
                        <p className="text-sm font-medium text-pf-text-primary">No matching settings</p>
                        <p className="mt-2 text-sm text-pf-text-secondary">
                          We couldn&apos;t find anything for &ldquo;{query}&rdquo;. Try a broader term like hardware, theme, or users.
                        </p>
                      </div>
                    </div>
                  </div>
                </div>
              ) : (
                <div className="flex flex-1 min-h-0 flex-col md:grid md:grid-cols-[13.5rem_minmax(0,1fr)]">
                  <SettingsSidebar
                    categories={accessibleCategories}
                    activeScope={effectiveScope}
                    activeCategory={effectiveCategory}
                    activeSubPage={activeSubPage}
                    availableScopes={availableScopes}
                    onCategoryChange={handleCategoryChange}
                    onSelectDestination={handleSelectDestination}
                    destinationAccess={destinationAccess}
                    matchingCategoryIds={matchingCategoryIds}
                    isFiltering={isFiltering}
                    searchQuery={query}
                  />

                  <div className="relative flex min-h-0 flex-1 flex-col border-t border-pf-border md:border-t-0 md:border-l md:border-pf-border">
                    <p className="sr-only" aria-live="polite">
                      {sectionAnnouncement}
                    </p>

                    <div className="pf-settings-scroll-pane min-h-0 flex-1 overflow-y-auto overscroll-contain">
                      {subTabs}
                      <div className="px-4 pb-10 pt-5 md:px-6 md:pb-12 md:pt-6">
                        <h2
                          id="settings-content-heading"
                          ref={sectionHeadingRef}
                          tabIndex={-1}
                          className="sr-only focus-visible:not-sr-only focus-visible:mb-4 focus-visible:block focus-visible:w-fit focus-visible:rounded-md focus-visible:text-xl focus-visible:leading-none focus-visible:outline-hidden focus-visible:ring-2 focus-visible:ring-pf-accent"
                        >
                          {isAdminRoute && accessibleCategories.length === 0 ? 'Configuration' : currentCategory.label}
                        </h2>

                        <SettingsContentTransition key={renderedContentKey} className="relative">
                          {showSubTabs ? (
                            <section role="tabpanel" id={`panel-${activeSubPage}`} aria-labelledby={`tab-${activeSubPage}`}>
                              {content}
                            </section>
                          ) : (
                            <section aria-labelledby="settings-content-heading">{content}</section>
                          )}
                        </SettingsContentTransition>
                      </div>
                    </div>

                    <div ref={setFooterSlot} className="shrink-0 empty:hidden" />
                  </div>
                </div>
              )}
              </div>
            </div>
          </PageTemplate>
          <ConfirmationModal
            isOpen={isModalOpen}
            onCancel={handleStay}
            onConfirm={handleDiscardAndNavigate}
            title="Unsaved Changes"
            message="You have unsaved changes. Do you want to stay on this page or discard your changes?"
            cancelButtonText="Stay"
            confirmButtonText="Discard Changes"
            isDangerous
          />
        </SettingsFooterSlotContext.Provider>
      </SettingsHeaderSlotContext.Provider>
    </SettingsSaveRegistryContext.Provider>
  );
};

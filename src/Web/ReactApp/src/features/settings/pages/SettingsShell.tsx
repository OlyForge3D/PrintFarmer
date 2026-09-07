import { lazy, Suspense, useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { Link, useSearchParams } from 'react-router';
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
import { resolveSettingsNavigationTarget } from '@/features/settings/settings-navigation';
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
  const standaloneDestinations = useMemo(
    () => configurationDestinations.filter((destination) => !destination.path.startsWith('/admin/settings?')),
    [configurationDestinations],
  );
  const [searchParams, setSearchParams] = useSearchParams();
  const { open: openCommandPalette } = useCommandPalette();

  // Callback ref, not useRef: the slot's DOM node has to be a *rendered* value so
  // the context re-renders its consumers once the node exists. A ref mutation
  // would not trigger that, and the portal would never find its target.
  const [headerSlot, setHeaderSlot] = useState<HTMLElement | null>(null);
  const [footerSlot, setFooterSlot] = useState<HTMLElement | null>(null);
  const commandPaletteShortcut = useMemo(() => commandPaletteShortcutLabel(), []);

  const requestedScope = searchParams.get('scope');
  const requestedCategory = searchParams.get('tab');
  const requestedSubPage = searchParams.get('sub');
  const query = searchParams.get('q') || '';
  const normalizedQuery = query.trim().toLowerCase();

  const isAdminRoute = routeScope === 'system';

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

  const registerSection = useCallback((section: RegisteredSection | null) => {
    if (section) {
      registeredSectionsRef.current.set(section.id, section);
      setRegisteredSections((prev) => ({ ...prev, [section.id]: section }));
    } else if (section?.id) {
      registeredSectionsRef.current.delete(section.id);
      setRegisteredSections((prev) => {
        const next = { ...prev };
        delete next[section.id];
        return next;
      });
    }
  }, []);

  const saveRegistry = useMemo(
    () => ({ publishSummary, publishIssues, registerActions, registerSection }),
    [publishSummary, publishIssues, registerActions, registerSection],
  );

  const handleDiscardAll = useCallback(() => {
    for (const actions of groupActionsRef.current.values()) {
      actions.discard();
    }
    for (const section of registeredSectionsRef.current.values()) {
      section.onDiscard?.();
    }
    setDirtyByGroup({});
    setRegisteredSections({});
    registeredSectionsRef.current.clear();
  }, []);

  const handleStay = useCallback(() => {
    setShowDraftModal(false);
    setPendingNavigation(null);
  }, []);

  const handleDiscardAndNavigate = useCallback(() => {
    setShowDraftModal(false);
    handleDiscardAll();
    if (pendingNavigation) {
      pendingNavigation();
    }
    setPendingNavigation(null);
  }, [handleDiscardAll, pendingNavigation]);

  useEffect(() => {
    if (!isDirty) return;
    const handleBeforeUnload = (e: BeforeUnloadEvent) => {
      e.preventDefault();
      e.returnValue = '';
    };
    window.addEventListener('beforeunload', handleBeforeUnload);
    return () => window.removeEventListener('beforeunload', handleBeforeUnload);
  }, [isDirty]);

  const handleCategoryChange = useCallback(
    (categoryId: string, explicitSubPageId?: string) => {
      const doNavigate = () => {
        const target = resolveSettingsNavigationTarget(categoryId, explicitSubPageId, activeScope);
        const targetCategory = accessibleCategories.find((category) => category.id === target.categoryId);
        shouldFocusSectionRef.current = true;
        setSearchParams((prev) => {
          const next = new URLSearchParams(prev);
          next.set('scope', target.scopeId);
          next.set('tab', target.categoryId);
          next.delete('q');
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
      };

      if (isDirty) {
        setPendingNavigation(() => doNavigate);
        setShowDraftModal(true);
      } else {
        doNavigate();
      }
    },
    [accessibleCategories, activeScope, isDirty, normalizedQuery, setSearchParams],
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

  const effectiveScope = useMemo(() => {
    if (!isFiltering || !matchingCategoryIds || matchingCategoryIds.length === 0) {
      return activeScope;
    }
    if (matchingCategoryIds.includes(activeCategory)) {
      return getSettingsScopeForCategory(activeCategory);
    }
    if (firstMatchingSubPageCategoryId && matchingCategoryIds.includes(firstMatchingSubPageCategoryId)) {
      return getSettingsScopeForCategory(firstMatchingSubPageCategoryId);
    }
    return getSettingsScopeForCategory(matchingCategoryIds[0]);
  }, [activeCategory, activeScope, firstMatchingSubPageCategoryId, isFiltering, matchingCategoryIds]);

  const scopeCategories = useMemo(
    () => getSettingsCategoriesForScope(effectiveScope),
    [effectiveScope],
  );

  const effectiveCategory = useMemo(() => {
    if (!isFiltering || !matchingCategoryIds || matchingCategoryIds.length === 0) {
      return scopeCategories.some((category) => category.id === activeCategory)
        ? activeCategory
        : getDefaultCategoryForScope(effectiveScope);
    }

    if (scopeCategories.some((category) => category.id === activeCategory) && matchingCategoryIds.includes(activeCategory)) {
      return activeCategory;
    }

    const firstMatchingCategory = scopeCategories.find((category) => matchingCategoryIds.includes(category.id));
    return firstMatchingCategory?.id ?? scopeCategories[0]?.id ?? getDefaultCategoryForScope(effectiveScope);
  }, [activeCategory, effectiveScope, isFiltering, matchingCategoryIds, scopeCategories]);

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

    const isExplicitSubPage = Boolean(requestedSubPage);
    const requestedTargetSubPage = resolvedRequestedTarget.categoryId === currentCategory.id
      ? (requestedSubPage ?? resolvedRequestedTarget.subPageId)
      : undefined;

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

    if (canUseRequestedSubPage && requestedTargetSubPage) {
      if (!isFiltering || matchingCurrentSubPageIds.length === 0 || matchingCurrentSubPageIds.includes(requestedTargetSubPage)) {
        return requestedTargetSubPage;
      }
    }

    if (matchingCurrentSubPageIds.length > 0) {
      return matchingCurrentSubPageIds[0];
    }

    const firstAccessibleSubPage = accessibleSubPages[0]?.id;

    return firstAccessibleSubPage ?? getDefaultSubPage(currentCategory.id);
  }, [accessibleCategories, currentCategory, isFiltering, matchingCurrentSubPageIds, requestedSubPage, resolvedRequestedTarget.categoryId, resolvedRequestedTarget.subPageId]);

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
    if (isFiltering && matchingCategoryIds?.length === 0) {
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

    const shouldSyncScope = isFiltering || requestedScope !== null || requestedCategory !== null || activeScope !== DEFAULT_SCOPE;
    const shouldSyncCategory = isFiltering || requestedCategory !== null || activeScope !== DEFAULT_SCOPE;
    const shouldSyncSub = requestedSubPage !== null
      || (activeSubPage !== '' && currentCategory.subPages.length > 0 && (requestedCategory !== null || isFiltering || activeScope !== DEFAULT_SCOPE));
    const scopeMismatch = shouldSyncScope && requestedScope !== activeScope;
    const categoryMismatch = shouldSyncCategory && requestedCategory !== effectiveCategory;
    const subMismatch = shouldSyncSub && (requestedSubPage ?? '') !== activeSubPage;

    if (!scopeMismatch && !categoryMismatch && !subMismatch) {
      return;
    }

    setSearchParams((prev) => {
      const next = new URLSearchParams(prev);
      if (shouldSyncScope) {
        next.set('scope', activeScope);
      }
      if (shouldSyncCategory) {
        next.set('tab', effectiveCategory);
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
    searchParams,
    activeScope,
    activeSubPage,
    currentCategory.subPages.length,
    effectiveCategory,
    isFiltering,
    matchingCategoryIds,
    requestedCategory,
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

  const hasNoMatches = accessibleCategories.length > 0 && isFiltering && matchingCategoryIds && matchingCategoryIds.length === 0;

  // Page-level actions. The mode toggle arrives by portal from whichever content
  // page owns it (see SettingsHeaderPortal); the palette is always available, so
  // the shell renders it directly. Slot first so the page's own control sits to
  // the left of the shell-wide one.
  const headerActions = (
    <div className="flex flex-wrap items-center justify-end gap-2">
      <div ref={setHeaderSlot} className="contents" />
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
            {isAdminRoute && standaloneDestinations.length > 0 && (
              <nav aria-label="Standalone configuration" className="flex flex-wrap gap-3 pb-4">
                {standaloneDestinations.map((destination) => (
                  <Link
                    key={destination.id}
                    to={destination.path}
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
                          {hasSubTabs ? (
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
            isOpen={showDraftModal}
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

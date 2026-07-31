import { lazy, Suspense, useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { useSearchParams } from 'react-router';
import { toast } from 'sonner';
import { SearchIcon } from '@/common/components/icons/MdiIcons';
import { PageTemplate } from '@/common/components/PageTemplate';
import { ThemeSwitcher } from '@/common/components/ThemeSwitcher';
import { FormSkeleton } from '@/common/components/skeletons/FormSkeleton';
import { Skeleton } from '@/common/components/skeletons/Skeleton';
import { Button } from '@/common/components/ui';
import { useAuth } from '@/features/auth/hooks/useAuth';
import { useCommandPalette } from '@/features/settings/components/commandPaletteContext';
import { SettingsHeaderSlotContext } from '@/features/settings/components/settingsHeaderSlotContext';
import { commandPaletteShortcutLabel } from '@/features/settings/components/commandPaletteShortcut';
import { SettingsContentTransition } from '@/features/settings/components/SettingsContentTransition';
import { SettingsSection } from '@/features/settings/components/SettingsSection';
import { SettingsSidebar } from '@/features/settings/components/SettingsSidebar';
import { SettingsSubTabs } from '@/features/settings/components/SettingsSubTabs';
import { UserSettingsSection } from '@/features/settings/components/UserSettingsSection';
import { FarmSettingsSection } from '@/features/settings/components/FarmSettingsSection';
import { TelegramSettingsCard } from '@/features/settings/components/TelegramSettingsCard';
import { resolveSettingsNavigationTarget } from '@/features/settings/settings-navigation';
import {
  DEFAULT_SCOPE,
  SETTINGS_SCOPES,
  getDefaultCategoryForScope,
  getDefaultSubPage,
  getSettingsCategoriesForScope,
  getSettingsCategory,
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
import { DataManagementPage } from '@/features/admin/pages/DataManagementPage';
import { UserManagementPage } from '@/features/admin/pages/UserManagementPage';
import { ApiKeysPage } from '@/features/profile/pages/ApiKeysPage';
import { NotificationPreferencesPage } from '@/features/notifications/pages/NotificationPreferencesPage';
import { QuotaManagementPage } from '@/features/quotas/pages/QuotaManagementPage';
import { LoginAuditPage } from '@/features/admin/pages/LoginAuditPage';
import { PrinterGroupsPage } from '@/features/printer-groups/pages/PrinterGroupsPage';
import { NfcBindingsPage } from '@/features/nfc/pages/NfcBindingsPage';
import { PasskeysPage } from '@/features/profile/pages/PasskeysPage';
import { SystemStatusPage } from '@/features/system/pages/SystemStatusPage';
import { SUB_PAGE_ALLOWED_GROUPS } from '@/features/settings/subpage-groups';

const LazySlicerProfilesPage = lazy(() =>
  import('@/features/slicer/pages/SlicerProfilesPage').then((mod) => ({ default: mod.SlicerProfilesPage })),
);

const LazyWorkerManagementPage = lazy(() =>
  import('@/features/slicer/pages/WorkerManagementPage').then((mod) => ({ default: mod.WorkerManagementPage })),
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
  'integrations.connections': (
    <SettingsSection>
      <SettingsPage
        allowedGroups={SUB_PAGE_ALLOWED_GROUPS['integrations.connections']}
        introText="Configure third-party services, Smart Plugs, and slicer API connections."
        afterContent={<TelegramSettingsCard />}
      />
    </SettingsSection>
  ),
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
  'operations.status': <SystemStatusPage />,
  'operations.workers': (
    <Suspense fallback={<TabLoader />}>
      <LazyWorkerManagementPage tabQueryParamName="workerTab" embedded />
    </Suspense>
  ),
  'users.accounts': <UserManagementPage embedded />,
  'users.audit': <LoginAuditPage embedded />,
  'data.tags': <TagAdminPage embedded />,
  'data.management': <DataManagementPage embedded />,
};

interface SettingsShellProps {
  /** Lock the shell to a specific route-level scope group.
   * - 'user': only user settings (no scope switcher)
   * - 'admin': admin scope only (Operations, Users, Data)
   * - 'system': system scope only (General, Slicing, Hardware, Integrations, Quotas)
   * If omitted, shows all scopes the user can access (legacy behavior). */
  routeScope?: 'user' | 'admin' | 'system';
}

export const SettingsShell: React.FC<SettingsShellProps> = ({ routeScope }) => {
  const { hasRole } = useAuth();
  const isFarmAdmin = hasRole('farm_admin');
  const [searchParams, setSearchParams] = useSearchParams();
  const { open: openCommandPalette } = useCommandPalette();

  // Callback ref, not useRef: the slot's DOM node has to be a *rendered* value so
  // the context re-renders its consumers once the node exists. A ref mutation
  // would not trigger that, and the portal would never find its target.
  const [headerSlot, setHeaderSlot] = useState<HTMLElement | null>(null);
  const commandPaletteShortcut = useMemo(() => commandPaletteShortcutLabel(), []);

  const requestedScope = searchParams.get('scope');
  const requestedCategory = searchParams.get('tab');
  const requestedSubPage = searchParams.get('sub');
  const query = searchParams.get('q') || '';
  const normalizedQuery = query.trim().toLowerCase();

  const availableScopes = useMemo(() => {
    if (routeScope === 'user') {
      return SETTINGS_SCOPES.filter((scope) => scope.id === 'user');
    }
    if (routeScope === 'system') {
      return SETTINGS_SCOPES.filter((scope) => scope.id === 'system' && isFarmAdmin);
    }
    if (routeScope === 'admin') {
      return SETTINGS_SCOPES.filter((scope) => scope.id === 'admin' && isFarmAdmin);
    }
    return SETTINGS_SCOPES.filter((scope) => !scope.adminOnly || isFarmAdmin);
  }, [isFarmAdmin, routeScope]);
  const fallbackScopeId = availableScopes[0]?.id ?? DEFAULT_SCOPE;
  const accessibleCategories = useMemo(
    () => availableScopes.flatMap((scope) => getSettingsCategoriesForScope(scope.id)),
    [availableScopes],
  );

  const resolvedRequestedTarget = useMemo(
    () => resolveSettingsNavigationTarget(requestedCategory, requestedSubPage, requestedScope),
    [requestedCategory, requestedScope, requestedSubPage],
  );

  const activeScope = useMemo(() => {
    return availableScopes.some((scope) => scope.id === resolvedRequestedTarget.scopeId)
      ? resolvedRequestedTarget.scopeId
      : fallbackScopeId;
  }, [availableScopes, fallbackScopeId, resolvedRequestedTarget.scopeId]);

  const activeCategory = useMemo(() => {
    return accessibleCategories.some((category) => category.id === resolvedRequestedTarget.categoryId)
      ? resolvedRequestedTarget.categoryId
      : getDefaultCategoryForScope(activeScope);
  }, [accessibleCategories, activeScope, resolvedRequestedTarget.categoryId]);

  const shouldFocusSectionRef = useRef(false);
  const previousRenderedKeyRef = useRef<string | null>(null);

  const handleCategoryChange = useCallback(
    (categoryId: string) => {
      const target = resolveSettingsNavigationTarget(categoryId, undefined, activeScope);
      const targetCategory = getSettingsCategory(target.categoryId);
      shouldFocusSectionRef.current = true;
      setSearchParams((prev) => {
        const next = new URLSearchParams(prev);
        next.set('scope', target.scopeId);
        next.set('tab', target.categoryId);
        next.delete('q');
        next.delete('workerTab');

        const matchingTargetSubPage = !normalizedQuery || !targetCategory
          ? undefined
          : targetCategory.subPages.find((subPage) => (
              subPage.label.toLowerCase().includes(normalizedQuery)
              || subPage.keywords.some((keyword) => keyword.includes(normalizedQuery))
            ));

        if (matchingTargetSubPage) {
          next.set('sub', matchingTargetSubPage.id);
        } else {
          const defaultSubPage = getDefaultSubPage(target.categoryId);
          if (defaultSubPage) {
            next.set('sub', defaultSubPage);
          } else {
            next.delete('sub');
          }
        }

        return next;
      });
    },
    [activeScope, normalizedQuery, setSearchParams],
  );

  const handleSubPageChange = useCallback(
    (subPageId: string) => {
      shouldFocusSectionRef.current = true;
      setSearchParams((prev) => {
        const next = new URLSearchParams(prev);
        next.set('scope', activeScope);
        next.set('tab', activeCategory);
        next.set('sub', subPageId);
        next.delete('q');
        if (subPageId !== 'workers') {
          next.delete('workerTab');
        }
        return next;
      });
    },
    [activeCategory, activeScope, setSearchParams],
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
      return currentCategory.subPages.map((subPage) => subPage.id);
    }

    return [];
  }, [currentCategory, currentCategoryMatchesQuery, directMatchingCurrentSubPageIds]);

  const activeSubPage = useMemo(() => {
    if (!currentCategory || currentCategory.subPages.length === 0) {
      return '';
    }

    const requestedTargetSubPage = resolvedRequestedTarget.categoryId === currentCategory.id
      ? resolvedRequestedTarget.subPageId
      : undefined;
    const requestedSubPageIsValid = requestedTargetSubPage
      && currentCategory.subPages.some((subPage) => subPage.id === requestedTargetSubPage);

    if (requestedSubPageIsValid) {
      if (!isFiltering || matchingCurrentSubPageIds.length === 0 || matchingCurrentSubPageIds.includes(requestedTargetSubPage)) {
        return requestedTargetSubPage;
      }
    }

    if (matchingCurrentSubPageIds.length > 0) {
      return matchingCurrentSubPageIds[0];
    }

    return getDefaultSubPage(currentCategory.id);
  }, [currentCategory, isFiltering, matchingCurrentSubPageIds, resolvedRequestedTarget.categoryId, resolvedRequestedTarget.subPageId]);

  const hasSubTabs = currentCategory.subPages.length >= 2;
  const renderedContentKey = currentCategory.subPages.length === 0
    ? currentCategory.id
    : `${currentCategory.id}.${activeSubPage}`;
  const activeSubPageLabel = currentCategory.subPages.find((subPage) => subPage.id === activeSubPage)?.label;
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

    if (typeof sectionHeadingRef.current?.scrollIntoView === 'function') {
      sectionHeadingRef.current.scrollIntoView({ block: 'start', behavior: scrollBehavior() });
    }

    sectionHeadingRef.current?.focus();

    shouldFocusSectionRef.current = false;
    previousRenderedKeyRef.current = activeDestinationKey;
  }, [currentCategory.id, hasSubTabs, activeSubPage]);

  useEffect(() => {
    if (requestedScope === 'admin' && !isFarmAdmin) {
      toast.info("You don't have access to admin settings. Showing your user settings instead.");
    }
  }, [isFarmAdmin, requestedScope]);

  const content = useMemo(() => {
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
  }, [currentCategory, renderedContentKey]);

  const pageTitle = effectiveScope === 'admin' ? 'Admin Console' : 'Settings';
  const pageDescription = currentScopeMeta?.description ?? 'Manage PrintFarmer settings and administration.';

  const hasNoMatches = isFiltering && matchingCategoryIds && matchingCategoryIds.length === 0;

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
        className="rounded-md border border-pf-border bg-pf-bg-0 text-pf-text-secondary hover:text-pf-text-primary"
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

  const subTabs =
    !hasNoMatches && currentCategory.subPages.length > 0 ? (
      <div className="border-b border-pf-border px-4 pt-4 md:px-6">
        <SettingsSubTabs
          subPages={currentCategory.subPages}
          activeSubPage={activeSubPage}
          onSubPageChange={handleSubPageChange}
          matchingSubPageIds={matchingCurrentSubPageIds}
          isFiltering={isFiltering}
          ariaLabel={`${currentCategory.label} ${effectiveScope === 'admin' ? 'admin' : 'settings'}`}
          searchQuery={query}
        />
      </div>
    ) : null;

  return (
    <SettingsHeaderSlotContext.Provider value={headerSlot}>
      <PageTemplate
        title={pageTitle}
        subtitle={pageDescription}
        padding="px-0"
        showHeader
        actions={headerActions}
      >
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
            <div className="flex flex-1 min-h-0 flex-col md:grid md:grid-cols-[14rem_minmax(0,1fr)]">
              <SettingsSidebar
                categories={accessibleCategories}
                activeScope={effectiveScope}
                activeCategory={effectiveCategory}
                availableScopes={availableScopes}
                onCategoryChange={handleCategoryChange}
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
                    {/* Subordinate to the page's own H1. This rendered at 32px
                        against a 24px "Settings" H1, so the category name — a
                        restatement of the highlighted nav item — was the
                        largest text on the page. The scale now descends:
                        24 (page) → 20 (category) → 18 (band) → 14 (card).
                        The element itself stays: `aria-labelledby` and the
                        section-change focus target both point at it. */}
                    <h2
                      id="settings-content-heading"
                      ref={sectionHeadingRef}
                      tabIndex={-1}
                      className="mb-4 w-fit text-xl leading-none focus:outline-hidden focus-visible:rounded-md focus-visible:ring-2 focus-visible:ring-pf-accent md:mb-5"
                    >
                      {currentCategory.label}
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
              </div>
            </div>
          )}
          </div>
        </div>
      </PageTemplate>
    </SettingsHeaderSlotContext.Provider>
  );
};

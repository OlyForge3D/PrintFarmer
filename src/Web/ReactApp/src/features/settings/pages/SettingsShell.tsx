import { lazy, Suspense, useCallback, useEffect, useMemo, useRef } from 'react';
import { useSearchParams } from 'react-router';
import { SettingsSearch } from '@/features/settings/components/SettingsSearch';
import { SettingsSidebar } from '@/features/settings/components/SettingsSidebar';
import { SettingsSubTabs } from '@/features/settings/components/SettingsSubTabs';
import { SettingsSection } from '@/features/settings/components/SettingsSection';
import {
  SETTINGS_CATEGORIES,
  DEFAULT_CATEGORY,
  getDefaultSubPage,
} from '@/features/settings/types';
import { SettingsPage } from '@/features/admin/pages/SettingsPage';
import { BedTypeAdminPage } from '@/features/admin/pages/BedTypeAdminPage';
import { NfcDevicesPage } from '@/features/nfc/pages/NfcDevicesPage';
import { CamerasPage } from '@/features/cameras/pages/CamerasPage';
import { LocationManagementAdminPage } from '@/features/admin/pages/LocationManagementAdminPage';
import { CustomFieldsAdminPage } from '@/features/admin/pages/CustomFieldsAdminPage';
import { WebhooksAdminPage } from '@/features/webhooks/pages/WebhooksAdminPage';
import { TagAdminPage } from '@/features/admin/pages/TagAdminPage';
import { DataManagementPage } from '@/features/admin/pages/DataManagementPage';
import { UserManagementPage } from '@/features/admin/pages/UserManagementPage';
import { ApiKeysPage } from '@/features/profile/pages/ApiKeysPage';
import { QuotaManagementPage } from '@/features/quotas/pages/QuotaManagementPage';
import { LoginAuditPage } from '@/features/admin/pages/LoginAuditPage';
import { PrinterGroupsPage } from '@/features/printer-groups/pages/PrinterGroupsPage';
import { NfcBindingsPage } from '@/features/nfc/pages/NfcBindingsPage';
import { SystemStatusPage } from '@/features/system/pages/SystemStatusPage';

const LazySlicerProfilesPage = lazy(() =>
  import('@/features/slicer/pages/SlicerProfilesPage').then((mod) => ({ default: mod.SlicerProfilesPage }))
);

const LazyWorkerManagementPage = lazy(() =>
  import('@/features/slicer/pages/WorkerManagementPage').then((mod) => ({ default: mod.WorkerManagementPage }))
);

function TabLoader() {
  return (
    <div className="flex items-center justify-center py-12" role="status" aria-label="Loading">
      <div className="pf-animate-spin rounded-full h-6 w-6 border-b-2 border-pf-accent"></div>
    </div>
  );
}

/** Content mapping for categories with no sub-pages */
const SINGLE_PAGE_CONTENT: Record<string, React.ReactNode> = {
  general: (
    <SettingsSection title="General Settings" description="Farm name, timezone, and system configuration.">
      <SettingsPage />
    </SettingsSection>
  ),
  notifications: (
    <SettingsSection title="Notifications" description="Configure alerts, email, and push notifications.">
      <div className="py-8 text-center text-pf-text-secondary">
        <p className="text-sm">Notification settings coming soon.</p>
      </div>
    </SettingsSection>
  ),
  integrations: (
    <SettingsSection title="Integrations" description="Webhooks, external APIs, and automation endpoints.">
      <WebhooksAdminPage />
    </SettingsSection>
  ),
};

/** Content mapping for sub-pages (category.subPage) */
const SUB_PAGE_CONTENT: Record<string, React.ReactNode> = {
  'slicing.bed-types': <BedTypeAdminPage />,
  'slicing.profiles': (
    <Suspense fallback={<TabLoader />}>
      <LazySlicerProfilesPage />
    </Suspense>
  ),
  'hardware.cameras': <CamerasPage />,
  'hardware.nfc': <NfcDevicesPage />,
  'hardware.printer-groups': <PrinterGroupsPage embedded />,
  'hardware.nfc-bindings': <NfcBindingsPage embedded />,
  'hardware.locations': <LocationManagementAdminPage />,
  'hardware.custom-fields': <CustomFieldsAdminPage />,
  'system.status': <SystemStatusPage />,
  'system.workers': (
    <Suspense fallback={<TabLoader />}>
      <LazyWorkerManagementPage tabQueryParamName="workerTab" embedded />
    </Suspense>
  ),
  'data.tags': <TagAdminPage />,
  'data.quotas': <QuotaManagementPage />,
  'data.management': <DataManagementPage />,
  'users.accounts': <UserManagementPage />,
  'users.api-keys': <ApiKeysPage embedded />,
  'users.audit': <LoginAuditPage />,
};

export const SettingsShell: React.FC = () => {
  const [searchParams, setSearchParams] = useSearchParams();

  const requestedCategory = searchParams.get('tab');
  const requestedSubPage = searchParams.get('sub');
  const query = searchParams.get('q') || '';
  const normalizedQuery = query.trim().toLowerCase();

  const activeCategory = useMemo(
    () => SETTINGS_CATEGORIES.some((category) => category.id === requestedCategory) ? requestedCategory : DEFAULT_CATEGORY,
    [requestedCategory]
  );

  const shouldFocusSectionRef = useRef(false);

  const handleCategoryChange = useCallback(
    (categoryId: string) => {
      shouldFocusSectionRef.current = true;
      setSearchParams((prev) => {
        const next = new URLSearchParams(prev);
        next.set('tab', categoryId);

        const targetCategory = SETTINGS_CATEGORIES.find((category) => category.id === categoryId);
        const matchingTargetSubPage = !normalizedQuery || !targetCategory
          ? undefined
          : targetCategory.subPages.find((subPage) =>
              subPage.label.toLowerCase().includes(normalizedQuery)
              || subPage.keywords.some((keyword) => keyword.includes(normalizedQuery))
            );

        if (matchingTargetSubPage) {
          next.set('sub', matchingTargetSubPage.id);
        } else if (!normalizedQuery) {
          const defaultSub = getDefaultSubPage(categoryId);
          if (defaultSub) {
            next.set('sub', defaultSub);
          } else {
            next.delete('sub');
          }
        } else {
          next.delete('sub');
        }

        return next;
      });
    },
    [normalizedQuery, setSearchParams]
  );

  const handleSubPageChange = useCallback(
    (subPageId: string) => {
      setSearchParams((prev) => {
        const next = new URLSearchParams(prev);
        next.set('sub', subPageId);
        return next;
      });
    },
    [setSearchParams]
  );

  const handleSearchChange = useCallback(
    (value: string) => {
      setSearchParams((prev) => {
        const next = new URLSearchParams(prev);
        if (value) {
          next.set('q', value);
        } else {
          next.delete('q');
        }
        return next;
      }, { replace: true });
    },
    [setSearchParams]
  );

  // Filter categories and sub-pages based on search query
  const { matchingCategoryIds, matchingSubPageIds, firstMatchingSubPageCategoryId, isFiltering } = useMemo(() => {
    if (!normalizedQuery) {
      return { matchingCategoryIds: undefined, matchingSubPageIds: undefined, firstMatchingSubPageCategoryId: undefined, isFiltering: false };
    }

    const lower = normalizedQuery;
    const categoryIds: string[] = [];
    const subPageIds: string[] = [];
    let firstSubPageCategoryId: string | undefined;

    for (const cat of SETTINGS_CATEGORIES) {
      const categoryMatches =
        cat.label.toLowerCase().includes(lower) || cat.keywords.some((kw) => kw.includes(lower));

      if (categoryMatches) {
        categoryIds.push(cat.id);
      }

      for (const sub of cat.subPages) {
        const subMatches =
          sub.label.toLowerCase().includes(lower) || sub.keywords.some((kw) => kw.includes(lower));

        if (subMatches) {
          subPageIds.push(sub.id);
          firstSubPageCategoryId ??= cat.id;
          if (!categoryIds.includes(cat.id)) {
            categoryIds.push(cat.id);
          }
        }
      }
    }

    return {
      matchingCategoryIds: categoryIds,
      matchingSubPageIds: subPageIds,
      firstMatchingSubPageCategoryId: firstSubPageCategoryId,
      isFiltering: true,
    };
  }, [normalizedQuery]);

  // Auto-navigate to first matching category if current is not in results
  const effectiveCategory = useMemo(() => {
    if (!isFiltering || !matchingCategoryIds || matchingCategoryIds.length === 0) {
      return activeCategory;
    }
    if (matchingCategoryIds.includes(activeCategory)) {
      return activeCategory;
    }
    if (firstMatchingSubPageCategoryId && matchingCategoryIds.includes(firstMatchingSubPageCategoryId)) {
      return firstMatchingSubPageCategoryId;
    }
    return matchingCategoryIds[0];
  }, [activeCategory, firstMatchingSubPageCategoryId, isFiltering, matchingCategoryIds]);

  const currentCategory = useMemo(
    () => SETTINGS_CATEGORIES.find((category) => category.id === effectiveCategory) ?? SETTINGS_CATEGORIES[0],
    [effectiveCategory]
  );

  const currentCategoryMatchesQuery = useMemo(() => {
    if (!normalizedQuery) {
      return false;
    }

    return currentCategory.label.toLowerCase().includes(normalizedQuery)
      || currentCategory.keywords.some((keyword) => keyword.includes(normalizedQuery));
  }, [currentCategory, normalizedQuery]);

  const directMatchingCurrentSubPageIds = useMemo(() => {
    if (!isFiltering || !matchingSubPageIds) {
      return [];
    }

    return currentCategory.subPages
      .map((subPage) => subPage.id)
      .filter((subPageId) => matchingSubPageIds.includes(subPageId));
  }, [currentCategory, isFiltering, matchingSubPageIds]);

  const matchingCurrentSubPageIds = useMemo(() => {
    if (directMatchingCurrentSubPageIds.length > 0) {
      return directMatchingCurrentSubPageIds;
    }

    if (currentCategoryMatchesQuery) {
      return currentCategory.subPages.map((subPage) => subPage.id);
    }

    return [];
  }, [currentCategory, currentCategoryMatchesQuery, directMatchingCurrentSubPageIds]);

  const activeSubPage = useMemo(() => {
    if (currentCategory.subPages.length === 0) {
      return '';
    }

    const requestedSubPageIsValid = requestedSubPage && currentCategory.subPages.some((subPage) => subPage.id === requestedSubPage);
    if (requestedSubPageIsValid) {
      if (!isFiltering || matchingCurrentSubPageIds.length === 0 || matchingCurrentSubPageIds.includes(requestedSubPage)) {
        return requestedSubPage;
      }
    }

    if (matchingCurrentSubPageIds.length > 0) {
      return matchingCurrentSubPageIds[0];
    }

    return getDefaultSubPage(currentCategory.id);
  }, [currentCategory, isFiltering, matchingCurrentSubPageIds, requestedSubPage]);

  const hasSubTabs = currentCategory.subPages.length >= 2;
  const sectionHeadingRef = useRef<HTMLHeadingElement>(null);
  const previousCategoryRef = useRef<string | null>(null);

  const sectionAnnouncement = useMemo(() => {
    if (!hasSubTabs) {
      return `${currentCategory.label} settings selected`;
    }

    const activeSubPageLabel = currentCategory.subPages.find((subPage) => subPage.id === activeSubPage)?.label;
    return activeSubPageLabel
      ? `${currentCategory.label} settings, ${activeSubPageLabel} section selected`
      : `${currentCategory.label} settings selected`;
  }, [currentCategory, activeSubPage, hasSubTabs]);

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

    const shouldSyncCategory = isFiltering || requestedCategory !== null;
    const shouldSyncSub = requestedSubPage !== null
      || (activeSubPage !== '' && currentCategory.subPages.length > 0 && (requestedCategory !== null || isFiltering));
    const categoryMismatch = shouldSyncCategory && requestedCategory !== effectiveCategory;
    const subMismatch = shouldSyncSub && (requestedSubPage ?? '') !== activeSubPage;
    if (!categoryMismatch && !subMismatch) {
      return;
    }

    setSearchParams((prev) => {
      const next = new URLSearchParams(prev);
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
    activeSubPage,
    currentCategory.subPages.length,
    effectiveCategory,
    isFiltering,
    matchingCategoryIds,
    requestedCategory,
    requestedSubPage,
    setSearchParams,
  ]);

  useEffect(() => {
    if (previousCategoryRef.current === null) {
      previousCategoryRef.current = currentCategory.id;
      return;
    }

    if (previousCategoryRef.current === currentCategory.id) {
      return;
    }

    if (!shouldFocusSectionRef.current) {
      previousCategoryRef.current = currentCategory.id;
      return;
    }

    if (hasSubTabs && activeSubPage) {
      const activeTab = document.getElementById(`tab-${activeSubPage}`);
      if (activeTab instanceof HTMLElement) {
        activeTab.focus();
      } else {
        sectionHeadingRef.current?.focus();
      }
    } else {
      sectionHeadingRef.current?.focus();
    }

    shouldFocusSectionRef.current = false;
    previousCategoryRef.current = currentCategory.id;
  }, [currentCategory.id, hasSubTabs, activeSubPage]);

  // Render content based on current category and sub-page
  const content = useMemo(() => {
    // Categories with no sub-pages use SINGLE_PAGE_CONTENT
    if (currentCategory.subPages.length === 0) {
      return SINGLE_PAGE_CONTENT[currentCategory.id] ?? (
        <SettingsSection>
          <div className="py-8 text-center text-pf-text-secondary">
            <p className="text-sm">{currentCategory.label} settings will be available here.</p>
          </div>
        </SettingsSection>
      );
    }

    // Categories with sub-pages use SUB_PAGE_CONTENT
    const contentKey = `${currentCategory.id}.${activeSubPage}`;
    return SUB_PAGE_CONTENT[contentKey] ?? (
      <div className="py-8 text-center text-pf-text-secondary">
        <p className="text-sm">Content not found for {contentKey}</p>
      </div>
    );
  }, [currentCategory, activeSubPage]);

  // Show no results message if search returns nothing
  if (isFiltering && matchingCategoryIds && matchingCategoryIds.length === 0) {
    return (
      <div className="space-y-4">
        <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-3">
          <h1 className="text-xl font-semibold text-pf-text-primary">Settings</h1>
          <SettingsSearch value={query} onChange={handleSearchChange} />
        </div>
        <div className="py-12 text-center text-pf-text-secondary">
          <p className="text-sm">No settings found matching &ldquo;{query}&rdquo;</p>
        </div>
      </div>
    );
  }

  return (
    <div className="space-y-4">
      {/* Header with title and search */}
      <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-3">
        <h1 className="text-xl font-semibold text-pf-text-primary">Settings</h1>
        <SettingsSearch value={query} onChange={handleSearchChange} />
      </div>

      {/* Main layout: sidebar + content */}
      <div className="flex flex-col md:flex-row gap-0 md:gap-0 min-h-[500px] border border-pf-border rounded-lg overflow-hidden bg-pf-bg-0">
        {/* Sidebar navigation */}
        <SettingsSidebar
          categories={SETTINGS_CATEGORIES}
          activeCategory={effectiveCategory}
          onCategoryChange={handleCategoryChange}
          matchingCategoryIds={matchingCategoryIds}
          isFiltering={isFiltering}
        />

        {/* Content area */}
        <div className="flex-1 p-4 md:p-6">
          <p className="sr-only" aria-live="polite">
            {sectionAnnouncement}
          </p>

          <div className="mb-4">
            <h2
              id="settings-content-heading"
              ref={sectionHeadingRef}
              tabIndex={-1}
              className="text-lg font-semibold text-pf-text-primary"
            >
              {currentCategory.label}
            </h2>
          </div>

          {/* Sub-tabs (only for categories with 2+ sub-pages) */}
          <SettingsSubTabs
            subPages={currentCategory.subPages}
            activeSubPage={activeSubPage}
            onSubPageChange={handleSubPageChange}
            matchingSubPageIds={matchingCurrentSubPageIds}
            isFiltering={isFiltering}
            ariaLabel={`${currentCategory.label} settings`}
          />

          {/* Page content */}
          {hasSubTabs ? (
            <section role="tabpanel" id={`panel-${activeSubPage}`} aria-labelledby={`tab-${activeSubPage}`}>
              {content}
            </section>
          ) : (
            <section aria-labelledby="settings-content-heading">{content}</section>
          )}
        </div>
      </div>
    </div>
  );
};

import { lazy, Suspense, useCallback, useEffect, useMemo, useRef } from 'react';
import { useSearchParams } from 'react-router';
import { PageTemplate } from '@/common/components/PageTemplate';
import { SettingsIcon } from '@/common/components/icons/MdiIcons';
import { FormSkeleton } from '@/common/components/skeletons/FormSkeleton';
import { Skeleton } from '@/common/components/skeletons/Skeleton';
import { SettingsContentTransition } from '@/features/settings/components/SettingsContentTransition';
import { SettingsSearch } from '@/features/settings/components/SettingsSearch';
import { SettingsSection } from '@/features/settings/components/SettingsSection';
import { SettingsSidebar } from '@/features/settings/components/SettingsSidebar';
import { SettingsSubTabs } from '@/features/settings/components/SettingsSubTabs';
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
import { NotificationPreferencesPage } from '@/features/notifications/pages/NotificationPreferencesPage';
import { QuotaManagementPage } from '@/features/quotas/pages/QuotaManagementPage';
import { LoginAuditPage } from '@/features/admin/pages/LoginAuditPage';
import { PrinterGroupsPage } from '@/features/printer-groups/pages/PrinterGroupsPage';
import { NfcBindingsPage } from '@/features/nfc/pages/NfcBindingsPage';
import { SystemStatusPage } from '@/features/system/pages/SystemStatusPage';

const LazySlicerProfilesPage = lazy(() =>
  import('@/features/slicer/pages/SlicerProfilesPage').then((mod) => ({ default: mod.SlicerProfilesPage })),
);

const LazyWorkerManagementPage = lazy(() =>
  import('@/features/slicer/pages/WorkerManagementPage').then((mod) => ({ default: mod.WorkerManagementPage })),
);

const SETTINGS_FRAME_NOISE = "url(\"data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 64 64'%3E%3Cfilter id='n'%3E%3CfeTurbulence type='fractalNoise' baseFrequency='.85' numOctaves='2' stitchTiles='stitch'/%3E%3C/filter%3E%3Crect width='64' height='64' filter='url(%23n)' opacity='1'/%3E%3C/svg%3E\")";

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

const SINGLE_PAGE_CONTENT: Record<string, React.ReactNode> = {
  general: (
    <SettingsSection title="General Settings" description="Farm name, timezone, and system configuration.">
      <SettingsPage />
    </SettingsSection>
  ),
  notifications: (
    <SettingsSection title="Notifications" description="Configure alerts, email, and push notifications.">
      <NotificationPreferencesPage embedded />
    </SettingsSection>
  ),
  integrations: (
    <SettingsSection title="Integrations" description="Webhooks, external APIs, and automation endpoints.">
      <WebhooksAdminPage />
    </SettingsSection>
  ),
};

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
    () => (SETTINGS_CATEGORIES.some((category) => category.id === requestedCategory) ? requestedCategory : DEFAULT_CATEGORY),
    [requestedCategory],
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
          : targetCategory.subPages.find((subPage) => (
              subPage.label.toLowerCase().includes(normalizedQuery)
              || subPage.keywords.some((keyword) => keyword.includes(normalizedQuery))
            ));

        if (matchingTargetSubPage) {
          next.set('sub', matchingTargetSubPage.id);
        } else if (!normalizedQuery) {
          const defaultSubPage = getDefaultSubPage(categoryId);
          if (defaultSubPage) {
            next.set('sub', defaultSubPage);
          } else {
            next.delete('sub');
          }
        } else {
          next.delete('sub');
        }

        return next;
      });
    },
    [normalizedQuery, setSearchParams],
  );

  const handleSubPageChange = useCallback(
    (subPageId: string) => {
      setSearchParams((prev) => {
        const next = new URLSearchParams(prev);
        next.set('sub', subPageId);
        return next;
      });
    },
    [setSearchParams],
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
    [setSearchParams],
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

    for (const category of SETTINGS_CATEGORIES) {
      const categoryMatches = category.label.toLowerCase().includes(normalizedQuery)
        || category.keywords.some((keyword) => keyword.includes(normalizedQuery));

      if (categoryMatches) {
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

    return {
      matchingCategoryIds: categoryIds,
      matchingSubPageIds: subPageIds,
      firstMatchingSubPageCategoryId: firstSubPageCategoryId,
      isFiltering: true,
    };
  }, [normalizedQuery]);

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
    [effectiveCategory],
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
  const renderedContentKey = currentCategory.subPages.length === 0
    ? currentCategory.id
    : `${currentCategory.id}.${activeSubPage}`;
  const activeSubPageLabel = currentCategory.subPages.find((subPage) => subPage.id === activeSubPage)?.label;
  const sectionHeadingRef = useRef<HTMLHeadingElement>(null);
  const previousCategoryRef = useRef<string | null>(null);

  const sectionAnnouncement = useMemo(() => {
    if (!hasSubTabs) {
      return `${currentCategory.label} settings selected`;
    }

    return activeSubPageLabel
      ? `${currentCategory.label} settings, ${activeSubPageLabel} section selected`
      : `${currentCategory.label} settings selected`;
  }, [activeSubPageLabel, currentCategory.label, hasSubTabs]);

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

  const shellContent = isFiltering && matchingCategoryIds && matchingCategoryIds.length === 0 ? (
    <div className="relative px-6 py-12 text-center">
      <div className="mx-auto max-w-md rounded-3xl border border-dashed border-pf-border bg-pf-bg-0/75 px-6 py-10 shadow-[inset_0_1px_0_rgba(255,255,255,0.05)]">
        <p className="text-sm font-medium text-pf-text-primary">No matching settings</p>
        <p className="mt-2 text-sm text-pf-text-secondary">
          We couldn&apos;t find anything for &ldquo;{query}&rdquo;. Try a broader term like hardware, theme, or users.
        </p>
      </div>
    </div>
  ) : (
    <div className="relative flex flex-col md:flex-row">
      <SettingsSidebar
        categories={SETTINGS_CATEGORIES}
        activeCategory={effectiveCategory}
        onCategoryChange={handleCategoryChange}
        matchingCategoryIds={matchingCategoryIds}
        isFiltering={isFiltering}
        searchQuery={query}
      />

      <div className="relative flex-1 px-4 py-4 md:px-6 md:py-6">
        <div className="pointer-events-none absolute inset-x-0 top-0 h-8 bg-gradient-to-b from-pf-bg-1/90 via-pf-bg-1/30 to-transparent" aria-hidden="true" />
        <div className="pointer-events-none absolute inset-y-0 left-0 hidden w-px bg-gradient-to-b from-transparent via-pf-border to-transparent md:block" aria-hidden="true" />

        <p className="sr-only" aria-live="polite">
          {sectionAnnouncement}
        </p>

        <div className="relative mb-5 space-y-1">
          <p className="text-[11px] font-semibold uppercase tracking-[0.18em] text-pf-text-tertiary">
            {activeSubPageLabel ? `${currentCategory.label} / ${activeSubPageLabel}` : currentCategory.label}
          </p>
          <h3
            id="settings-content-heading"
            ref={sectionHeadingRef}
            tabIndex={-1}
            className="text-xl font-semibold text-pf-text-primary"
          >
            {currentCategory.label}
          </h3>
        </div>

        <SettingsSubTabs
          subPages={currentCategory.subPages}
          activeSubPage={activeSubPage}
          onSubPageChange={handleSubPageChange}
          matchingSubPageIds={matchingCurrentSubPageIds}
          isFiltering={isFiltering}
          ariaLabel={`${currentCategory.label} settings`}
          searchQuery={query}
        />

        <SettingsContentTransition key={renderedContentKey}>
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
  );

  return (
    <PageTemplate
      title="Settings"
      subtitle="Configure your farm, hardware, and account"
      icon={SettingsIcon}
      actions={<SettingsSearch value={query} onChange={handleSearchChange} />}
      backgroundColor="bg-transparent"
      includeTopPadding={false}
    >
      <div className="relative isolate overflow-hidden rounded-[1.5rem] border border-pf-border/70 bg-pf-bg-0/95 shadow-[0_18px_60px_-38px_rgba(0,0,0,0.78)] backdrop-blur-sm">
        <div className="pointer-events-none absolute inset-0 opacity-[0.02]" style={{ backgroundImage: SETTINGS_FRAME_NOISE, backgroundSize: '160px 160px' }} aria-hidden="true" />
        <div className="pointer-events-none absolute inset-x-0 top-0 h-8 bg-gradient-to-b from-pf-bg-0 via-pf-bg-0/95 to-pf-bg-1/20" aria-hidden="true" />
        {shellContent}
      </div>
    </PageTemplate>
  );
};

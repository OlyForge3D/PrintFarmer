import { lazy, Suspense, useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { useSearchParams } from 'react-router';
import { SearchIcon } from '@/common/components/icons/MdiIcons';
import { FormSkeleton } from '@/common/components/skeletons/FormSkeleton';
import { Skeleton } from '@/common/components/skeletons/Skeleton';
import { Button } from '@/common/components/ui';
import { CommandPalette } from '@/features/settings/components/CommandPalette';
import { SettingsContentTransition } from '@/features/settings/components/SettingsContentTransition';
import { SettingsSearch } from '@/features/settings/components/SettingsSearch';
import { SettingsSection } from '@/features/settings/components/SettingsSection';
import { SettingsSidebar } from '@/features/settings/components/SettingsSidebar';
import { SettingsSubTabs } from '@/features/settings/components/SettingsSubTabs';
import { buildSettingsCommandItems, resolveSettingsNavigationTarget, type SettingsCommandItem } from '@/features/settings/settings-navigation';
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
const SETTINGS_FRAME_GRID = [
  'linear-gradient(rgba(56, 189, 248, 0.08) 1px, transparent 1px)',
  'linear-gradient(90deg, rgba(56, 189, 248, 0.08) 1px, transparent 1px)',
].join(', ');

function isEditableTarget(target: EventTarget | null): boolean {
  if (!(target instanceof HTMLElement)) {
    return false;
  }

  if (target.isContentEditable) {
    return true;
  }

  return ['INPUT', 'TEXTAREA', 'SELECT'].includes(target.tagName);
}

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

const SINGLE_PAGE_CONTENT: Record<string, ReactNode> = {
  general: (
    <SettingsSection>
      <SettingsPage />
    </SettingsSection>
  ),
  notifications: (
    <SettingsSection>
      <NotificationPreferencesPage embedded />
    </SettingsSection>
  ),
  integrations: (
    <SettingsSection>
      <WebhooksAdminPage />
    </SettingsSection>
  ),
};

const SUB_PAGE_CONTENT: Record<string, ReactNode> = {
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
  const [isCommandPaletteOpen, setIsCommandPaletteOpen] = useState(false);

  const requestedCategory = searchParams.get('tab');
  const requestedSubPage = searchParams.get('sub');
  const query = searchParams.get('q') || '';
  const normalizedQuery = query.trim().toLowerCase();

  const activeCategory = useMemo(
    () => (SETTINGS_CATEGORIES.some((category) => category.id === requestedCategory) ? requestedCategory : DEFAULT_CATEGORY),
    [requestedCategory],
  );

  const shouldFocusSectionRef = useRef(false);
  const previousRenderedKeyRef = useRef<string | null>(null);
  const commandPaletteItems = useMemo(() => buildSettingsCommandItems(), []);

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
      shouldFocusSectionRef.current = true;
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

  const openCommandPalette = useCallback(() => {
    setIsCommandPaletteOpen(true);
  }, []);

  const closeCommandPalette = useCallback(() => {
    setIsCommandPaletteOpen(false);
  }, []);

  const navigateToSetting = useCallback(
    (item: SettingsCommandItem) => {
      const target = resolveSettingsNavigationTarget(item.categoryId, item.subPageId);
      shouldFocusSectionRef.current = true;
      setSearchParams((prev) => {
        const next = new URLSearchParams(prev);
        next.set('tab', target.categoryId);
        if (target.subPageId) {
          next.set('sub', target.subPageId);
        } else {
          next.delete('sub');
        }
        return next;
      });
      setIsCommandPaletteOpen(false);
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

  const sectionAnnouncement = useMemo(() => {
    if (!hasSubTabs) {
      return `${currentCategory.label} settings selected`;
    }

    return activeSubPageLabel
      ? `${currentCategory.label} settings, ${activeSubPageLabel} section selected`
      : `${currentCategory.label} settings selected`;
  }, [activeSubPageLabel, currentCategory.label, hasSubTabs]);

  useEffect(() => {
    const handleKeyDown = (event: KeyboardEvent) => {
      if (
        event.key.toLowerCase() !== 'k'
        || (!event.ctrlKey && !event.metaKey)
        || event.altKey
        || event.shiftKey
        || isEditableTarget(event.target)
      ) {
        return;
      }

      event.preventDefault();
      setIsCommandPaletteOpen(true);
    };

    window.addEventListener('keydown', handleKeyDown);
    return () => window.removeEventListener('keydown', handleKeyDown);
  }, []);

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

  const hasNoMatches = isFiltering && matchingCategoryIds && matchingCategoryIds.length === 0;

  const toolbar = (
    <div className="sticky top-0 z-20 border-b border-pf-border/70 bg-pf-bg-0/88 px-4 py-4 backdrop-blur-xl supports-[backdrop-filter]:bg-pf-bg-0/78 md:px-6">
      <div className="flex flex-col gap-3 lg:flex-row lg:items-center lg:justify-between">
        <SettingsSearch value={query} onChange={handleSearchChange} />
        <Button
          type="button"
          variant="subtle"
          size="md"
          onClick={openCommandPalette}
          iconLeft={<SearchIcon className="h-4 w-4" ariaLabel="Open command palette" />}
          className="h-11 justify-between rounded-2xl border border-pf-border/70 bg-pf-bg-0/70 px-4 text-sm text-pf-text-primary shadow-[inset_0_1px_0_rgba(255,255,255,0.06)] backdrop-blur-sm sm:min-w-[12rem]"
        >
          <span className="inline-flex items-center gap-3">
            <span>Command palette</span>
            <span className="rounded-md border border-pf-border bg-pf-bg-1 px-1.5 py-0.5 text-[10px] font-semibold uppercase tracking-[0.16em] text-pf-text-tertiary">
              {typeof navigator !== 'undefined' && navigator.platform.toLowerCase().includes('mac') ? '⌘K' : 'Ctrl K'}
            </span>
          </span>
        </Button>
      </div>

      {!hasNoMatches ? (
        <SettingsSubTabs
          subPages={currentCategory.subPages}
          activeSubPage={activeSubPage}
          onSubPageChange={handleSubPageChange}
          matchingSubPageIds={matchingCurrentSubPageIds}
          isFiltering={isFiltering}
          ariaLabel={`${currentCategory.label} settings`}
          searchQuery={query}
        />
      ) : null}
    </div>
  );

  return (
    <>
      <div
        data-settings-shell
        className="pf-settings-surface relative isolate flex h-full min-h-0 flex-col"
      >
        <div className="pointer-events-none absolute inset-0 overflow-hidden rounded-[1.5rem]" aria-hidden="true">
          <div className="absolute inset-0 rounded-[1.5rem] bg-pf-bg-0/95" />
          <div className="absolute inset-0 rounded-[1.5rem] opacity-[0.08]" style={{ backgroundImage: SETTINGS_FRAME_GRID, backgroundSize: '24px 24px' }} />
          <div className="absolute inset-0 rounded-[1.5rem] opacity-[0.05]" style={{ backgroundImage: SETTINGS_FRAME_NOISE, backgroundSize: '160px 160px' }} />
        </div>

        <div className="relative flex h-full min-h-0 flex-col overflow-hidden rounded-[1.5rem] border border-pf-border/70 shadow-[0_24px_80px_-46px_rgba(0,0,0,0.82)] backdrop-blur-sm">
          {hasNoMatches ? (
            <div className="relative flex h-full min-h-0 flex-col">
              <div className="pointer-events-none absolute inset-x-0 top-0 z-10 h-10 bg-gradient-to-b from-pf-bg-0 via-pf-bg-0/70 to-transparent" aria-hidden="true" />
              <div className="pointer-events-none absolute inset-x-0 bottom-0 z-10 h-10 bg-gradient-to-t from-pf-bg-0 via-pf-bg-0/70 to-transparent" aria-hidden="true" />
              <div className="h-full overflow-y-auto overscroll-contain">
                {toolbar}
                <div className="flex min-h-[60%] items-center justify-center px-4 py-10 md:px-6">
                  <div className="mx-auto max-w-md rounded-3xl border border-dashed border-pf-border bg-pf-bg-0/80 px-6 py-10 text-center shadow-[inset_0_1px_0_rgba(255,255,255,0.06)]">
                    <p className="text-sm font-medium text-pf-text-primary">No matching settings</p>
                    <p className="mt-2 text-sm text-pf-text-secondary">
                      We couldn&apos;t find anything for &ldquo;{query}&rdquo;. Try a broader term like hardware, theme, or users.
                    </p>
                  </div>
                </div>
              </div>
            </div>
          ) : (
            <div className="flex h-full min-h-0 flex-col md:grid md:grid-cols-[18.5rem_minmax(0,1fr)]">
              <SettingsSidebar
                categories={SETTINGS_CATEGORIES}
                activeCategory={effectiveCategory}
                onCategoryChange={handleCategoryChange}
                matchingCategoryIds={matchingCategoryIds}
                isFiltering={isFiltering}
                searchQuery={query}
              />

              <div className="relative flex-1 min-h-0 border-t border-pf-border/70 md:border-t-0 md:border-l md:border-pf-border/70">
                <div className="pointer-events-none absolute inset-x-0 top-0 z-10 h-10 bg-gradient-to-b from-pf-bg-0 via-pf-bg-0/70 to-transparent" aria-hidden="true" />
                <div className="pointer-events-none absolute inset-x-0 bottom-0 z-10 h-10 bg-gradient-to-t from-pf-bg-0 via-pf-bg-0/70 to-transparent" aria-hidden="true" />

                <p className="sr-only" aria-live="polite">
                  {sectionAnnouncement}
                </p>

                <div className="pf-settings-scroll-pane h-full overflow-y-auto overscroll-contain">
                  {toolbar}
                  <div className="px-4 pb-10 pt-5 md:px-6 md:pb-12 md:pt-6">
                    {/* Mobile-only h1 — desktop h1 lives in SettingsSidebar (hidden below md) */}
                    <h1 className="mb-3 text-lg leading-none text-pf-text-primary md:hidden">Settings</h1>
                    <h2
                      id="settings-content-heading"
                      ref={sectionHeadingRef}
                      tabIndex={-1}
                      className="mb-5 w-fit text-2xl leading-none focus:outline-hidden focus-visible:rounded-md focus-visible:ring-2 focus-visible:ring-pf-accent md:mb-6 md:text-[2rem]"
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

      <CommandPalette
        isOpen={isCommandPaletteOpen}
        items={commandPaletteItems}
        onClose={closeCommandPalette}
        onSelect={navigateToSetting}
      />
    </>
  );
};

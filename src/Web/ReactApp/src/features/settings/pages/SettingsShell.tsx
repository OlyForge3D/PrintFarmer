import { lazy, Suspense, useCallback, useMemo } from 'react';
import { useSearchParams } from 'react-router';
import { SettingsSearch } from '@/features/settings/components/SettingsSearch';
import { SettingsTabStrip } from '@/features/settings/components/SettingsTabStrip';
import { SettingsSection } from '@/features/settings/components/SettingsSection';
import { SETTINGS_TABS, DEFAULT_TAB } from '@/features/settings/types';
import { SettingsPage } from '@/features/admin/pages/SettingsPage';
import { FilamentManagementPage } from '@/features/filamentManagement/pages/FilamentManagementPage';
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

const LazySlicerProfilesPage = lazy(() =>
  import('@/features/slicer/pages/SlicerProfilesPage').then(mod => ({ default: mod.SlicerProfilesPage }))
);

function TabLoader() {
  return (
    <div className="flex items-center justify-center py-12" role="status" aria-label="Loading">
      <div className="pf-animate-spin rounded-full h-6 w-6 border-b-2 border-pf-accent"></div>
    </div>
  );
}

export const SettingsShell: React.FC = () => {
  const [searchParams, setSearchParams] = useSearchParams();

  const activeTab = searchParams.get('tab') || DEFAULT_TAB;
  const query = searchParams.get('q') || '';

  const handleTabChange = useCallback(
    (tabId: string) => {
      setSearchParams((prev) => {
        const next = new URLSearchParams(prev);
        next.set('tab', tabId);
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
      });
    },
    [setSearchParams]
  );

  const filteredTabIds = useMemo(() => {
    if (!query.trim()) return undefined;
    const lower = query.toLowerCase();
    return SETTINGS_TABS.filter(
      (tab) =>
        tab.label.toLowerCase().includes(lower) ||
        tab.keywords.some((kw) => kw.includes(lower))
    ).map((tab) => tab.id);
  }, [query]);

  const effectiveTab = useMemo(() => {
    if (!filteredTabIds || filteredTabIds.length === 0) return activeTab;
    if (filteredTabIds.includes(activeTab)) return activeTab;
    return filteredTabIds[0];
  }, [activeTab, filteredTabIds]);

  const tabContent = useMemo<Record<string, React.ReactNode>>(() => ({
    general: (
      <SettingsSection title="General Settings" description="Farm name, timezone, and system configuration.">
        <SettingsPage />
      </SettingsSection>
    ),
    filament: (
      <SettingsSection title="Filament Management" description="Manage spools, materials, and inventory.">
        <FilamentManagementPage />
      </SettingsSection>
    ),
    slicing: (
      <SettingsSection>
        <div className="space-y-8">
          <div>
            <h3 className="text-base font-medium text-pf-text-primary mb-3">Bed Types</h3>
            <BedTypeAdminPage />
          </div>
          <div>
            <h3 className="text-base font-medium text-pf-text-primary mb-3">Slicer Profiles</h3>
            <Suspense fallback={<TabLoader />}>
              <LazySlicerProfilesPage />
            </Suspense>
          </div>
        </div>
      </SettingsSection>
    ),
    hardware: (
      <SettingsSection>
        <div className="space-y-8">
          <div>
            <h3 className="text-base font-medium text-pf-text-primary mb-3">Cameras</h3>
            <CamerasPage />
          </div>
          <div>
            <h3 className="text-base font-medium text-pf-text-primary mb-3">NFC Devices</h3>
            <NfcDevicesPage />
          </div>
          <div>
            <h3 className="text-base font-medium text-pf-text-primary mb-3">Locations</h3>
            <LocationManagementAdminPage />
          </div>
          <div>
            <h3 className="text-base font-medium text-pf-text-primary mb-3">Custom Fields</h3>
            <CustomFieldsAdminPage />
          </div>
        </div>
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
    data: (
      <SettingsSection>
        <div className="space-y-8">
          <div>
            <h3 className="text-base font-medium text-pf-text-primary mb-3">Tags</h3>
            <TagAdminPage />
          </div>
          <div>
            <h3 className="text-base font-medium text-pf-text-primary mb-3">Quotas</h3>
            <QuotaManagementPage />
          </div>
          <div>
            <h3 className="text-base font-medium text-pf-text-primary mb-3">Data Management</h3>
            <DataManagementPage />
          </div>
        </div>
      </SettingsSection>
    ),
    users: (
      <SettingsSection>
        <div className="space-y-8">
          <div>
            <h3 className="text-base font-medium text-pf-text-primary mb-3">User Accounts</h3>
            <UserManagementPage />
          </div>
          <div>
            <h3 className="text-base font-medium text-pf-text-primary mb-3">API Keys</h3>
            <ApiKeysPage />
          </div>
          <div>
            <h3 className="text-base font-medium text-pf-text-primary mb-3">Login Audit</h3>
            <LoginAuditPage />
          </div>
        </div>
      </SettingsSection>
    ),
  }), []);

  return (
    <div className="space-y-4">
      <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-3">
        <h1 className="text-xl font-semibold text-pf-text-primary">Settings</h1>
        <SettingsSearch value={query} onChange={handleSearchChange} />
      </div>

      {filteredTabIds && filteredTabIds.length === 0 ? (
        <div className="py-12 text-center text-pf-text-secondary">
          <p className="text-sm">No settings found matching &ldquo;{query}&rdquo;</p>
        </div>
      ) : (
        <SettingsTabStrip
          activeTab={effectiveTab}
          onTabChange={handleTabChange}
          filteredTabIds={filteredTabIds}
          tabContent={tabContent}
        />
      )}
    </div>
  );
};

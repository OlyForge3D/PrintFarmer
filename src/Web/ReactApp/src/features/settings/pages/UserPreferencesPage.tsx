import { PageTemplate } from '@/common/components/PageTemplate';
import { SettingsIcon } from '@/common/components/icons/MdiIcons';
import { FarmSettingsSection } from '@/features/settings/components/FarmSettingsSection';
import { UserSettingsSection } from '@/features/settings/components/UserSettingsSection';

export function UserPreferencesPage() {
  return (
    <PageTemplate
      title="Preferences"
      subtitle="Farm-wide defaults and personal settings"
      icon={SettingsIcon}
    >
      <div className="space-y-6 max-w-4xl">
        <FarmSettingsSection />
        <UserSettingsSection />
      </div>
    </PageTemplate>
  );
}

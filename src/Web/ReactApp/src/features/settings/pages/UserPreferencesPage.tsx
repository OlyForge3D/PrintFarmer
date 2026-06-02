import { PageTemplate } from '@/common/components/PageTemplate';
import { SettingsIcon } from '@/common/components/icons/MdiIcons';
import { ThemeSwitcher } from '@/common/components/ThemeSwitcher';
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
        {/* Appearance */}
        <section className="rounded-xl border border-[var(--pf-border)] bg-[var(--pf-card-bg)] p-5">
          <h2 className="mb-1 text-sm font-semibold uppercase tracking-wider text-[var(--pf-text-secondary)]">
            Appearance
          </h2>
          <p className="mb-4 text-xs text-[var(--pf-text-tertiary)]">
            Choose a color theme for the interface.
          </p>
          <ThemeSwitcher />
        </section>

        <FarmSettingsSection />
        <UserSettingsSection />
      </div>
    </PageTemplate>
  );
}

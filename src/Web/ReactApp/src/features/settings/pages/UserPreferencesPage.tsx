import { useNavigate } from 'react-router';
import { PageTemplate } from '@/common/components/PageTemplate';
import { SettingsIcon, BellIcon, KeyIcon, ShieldIcon } from '@/common/components/icons/MdiIcons';
import { ThemeSwitcher } from '@/common/components/ThemeSwitcher';
import { Button } from '@/common/components/ui';
import { FarmSettingsSection } from '@/features/settings/components/FarmSettingsSection';
import { UserSettingsSection } from '@/features/settings/components/UserSettingsSection';

const profileLinks = [
  {
    title: 'API Keys',
    description: 'Create, rotate, and revoke personal API keys.',
    href: '/profile/api-keys',
    icon: <KeyIcon className="h-5 w-5" />,
  },
  {
    title: 'Notifications',
    description: 'Choose how PrintFarmer notifies you about print events.',
    href: '/profile/notifications',
    icon: <BellIcon className="h-5 w-5" />,
  },
  {
    title: 'Passkeys',
    description: 'Manage passwordless sign-in devices for your account.',
    href: '/profile/passkeys',
    icon: <ShieldIcon className="h-5 w-5" />,
  },
] as const;

export function UserPreferencesPage() {
  const navigate = useNavigate();

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

        <section className="rounded-xl border border-[var(--pf-border)] bg-[var(--pf-card-bg)] p-5">
          <h2 className="mb-1 text-sm font-semibold uppercase tracking-wider text-[var(--pf-text-secondary)]">
            Profile & security
          </h2>
          <p className="mb-4 text-xs text-[var(--pf-text-tertiary)]">
            Manage your personal access, sign-in, and notification settings.
          </p>
          <div className="grid gap-3 md:grid-cols-3">
            {profileLinks.map((link) => (
              <Button
                key={link.href}
                type="button"
                variant="subtle"
                className="h-auto w-full items-start justify-start rounded-lg border border-pf-border px-4 py-3 text-left"
                iconLeft={link.icon}
                onClick={() => navigate(link.href)}
              >
                <span className="flex flex-col items-start gap-1">
                  <span className="text-sm font-semibold text-pf-text-primary">{link.title}</span>
                  <span className="text-xs text-pf-text-secondary">{link.description}</span>
                </span>
              </Button>
            ))}
          </div>
        </section>

        <FarmSettingsSection />
        <UserSettingsSection />
      </div>
    </PageTemplate>
  );
}

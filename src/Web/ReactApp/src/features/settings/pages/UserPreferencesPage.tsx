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
      <div className="max-w-6xl space-y-6">
        <section className="rounded-2xl border border-pf-border bg-pf-card p-5 shadow-sm">
          <h2 className="mb-1 text-sm font-semibold uppercase tracking-[0.18em] text-pf-text-secondary">
            Appearance
          </h2>
          <p className="mb-4 text-xs text-pf-text-tertiary">
            Choose a color theme and preview the dashboard surface in real time.
          </p>
          <ThemeSwitcher />
        </section>

        <section className="rounded-2xl border border-pf-border bg-pf-card p-5 shadow-sm">
          <h2 className="mb-1 text-sm font-semibold uppercase tracking-[0.18em] text-pf-text-secondary">
            Profile & security
          </h2>
          <p className="mb-4 text-xs text-pf-text-tertiary">
            Manage your personal access, sign-in, and notification settings.
          </p>
          <div className="grid gap-3 md:grid-cols-3">
            {profileLinks.map((link) => (
              <Button
                key={link.href}
                type="button"
                variant="subtle"
                className="h-auto min-w-0 w-full items-start justify-start rounded-xl border border-pf-border px-4 py-3 text-left whitespace-normal"
                iconLeft={link.icon}
                onClick={() => navigate(link.href)}
              >
                <span className="flex min-w-0 flex-col items-start gap-1">
                  <span className="text-sm font-semibold text-pf-text-primary">{link.title}</span>
                  <span className="break-words text-xs text-pf-text-secondary">{link.description}</span>
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

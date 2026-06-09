import clsx from 'clsx';
import {
  AccountCheckIcon,
  AccountIcon,
  ChevronDownIcon,
  LoginIcon,
  LogoutIcon,
  SettingsIcon,
} from '@/common/components/icons/MdiIcons';
import { NotificationBell } from '@/common/components/NotificationBell';
import { Button } from '@/common/components/ui';
import { SystemPulsePill } from '@/features/system/components/SystemPulsePill';

const DESKTOP_WRAPPER_CLASS_NAME = 'pointer-events-none fixed right-4 top-4 z-40 hidden lg:block';
const FLOATING_BAR_CLASS_NAME = 'pointer-events-auto flex items-center gap-1.5 rounded-full border border-pf-border/70 bg-pf-bg-1/72 p-2 shadow-[0_18px_48px_-26px_rgba(0,0,0,0.8)] ring-1 ring-black/10 backdrop-blur-sm';
const MOBILE_BAR_CLASS_NAME = 'relative z-40 flex items-center gap-1.5';
const ICON_BUTTON_CLASS_NAME = 'h-9 w-9 rounded-full p-0 text-pf-text-primary transition-colors hover:bg-pf-bg-2/80 focus-visible:ring-2 focus-visible:ring-pf-accent';
const ACCOUNT_BUTTON_CLASS_NAME = 'h-9 rounded-full px-2.5 text-pf-text-primary transition-colors hover:bg-pf-bg-2/80 focus-visible:ring-2 focus-visible:ring-pf-accent';
const MENU_PANEL_CLASS_NAME = 'absolute right-0 top-full z-10 mt-3 w-64 overflow-hidden rounded-2xl border border-pf-border/80 bg-pf-bg-1/96 shadow-[0_22px_60px_-28px_rgba(0,0,0,0.85)] backdrop-blur-sm';

interface FloatingControlBarProps {
  mobile?: boolean;
  isAuthenticated: boolean;
  userName?: string | null;
  userMenuOpen: boolean;
  onToggleUserMenu: () => void;
  onCloseUserMenu: () => void;
  onViewSystemStatus: () => void;
  onOpenPreferences: () => void;
  onOpenLogin: () => void;
  onOpenRegister: () => void;
  onLogout: () => Promise<void> | void;
}

export function FloatingControlBar({
  mobile = false,
  isAuthenticated,
  userName,
  userMenuOpen,
  onToggleUserMenu,
  onCloseUserMenu,
  onViewSystemStatus,
  onOpenPreferences,
  onOpenLogin,
  onOpenRegister,
  onLogout,
}: FloatingControlBarProps) {
  const bar = (
    <div className={mobile ? MOBILE_BAR_CLASS_NAME : FLOATING_BAR_CLASS_NAME}>
      <SystemPulsePill onClick={onViewSystemStatus} />
      {isAuthenticated && <NotificationBell buttonClassName={ICON_BUTTON_CLASS_NAME} />}

      <div className="relative">
        <Button
          type="button"
          variant="unstyled"
          className={clsx(ACCOUNT_BUTTON_CLASS_NAME, userMenuOpen && 'bg-pf-bg-2/80')}
          aria-expanded={userMenuOpen}
          aria-haspopup="menu"
          aria-label={isAuthenticated && userName ? `${userName} account menu` : 'Account menu'}
          title={isAuthenticated && userName ? `${userName} account menu` : 'Account menu'}
          onClick={onToggleUserMenu}
        >
          <span className="flex items-center gap-1.5">
            {isAuthenticated && userName ? (
              <AccountCheckIcon className="h-5 w-5 text-pf-success" />
            ) : (
              <AccountIcon className="h-5 w-5 text-pf-text-muted" />
            )}
            <ChevronDownIcon
              className={clsx(
                'h-3.5 w-3.5 text-pf-text-tertiary transition-transform',
                userMenuOpen && 'rotate-180'
              )}
            />
          </span>
        </Button>

        {userMenuOpen && (
          <div className={MENU_PANEL_CLASS_NAME} role="menu" aria-label="Account menu">
            <div className="py-1">
              {isAuthenticated && userName ? (
                <>
                  <div className="border-b border-pf-border px-4 py-2 text-sm text-pf-text-secondary">
                    Signed in as <strong>{userName}</strong>
                  </div>
                  <Button
                    type="button"
                    variant="subtle"
                    size="sm"
                    role="menuitem"
                    className="w-full justify-start!"
                    iconLeft={<SettingsIcon className="h-4 w-4" />}
                    onClick={() => {
                      onOpenPreferences();
                      onCloseUserMenu();
                    }}
                  >
                    Preferences
                  </Button>
                  <Button
                    type="button"
                    variant="subtle"
                    size="sm"
                    role="menuitem"
                    className="w-full justify-start!"
                    iconLeft={<LogoutIcon className="h-4 w-4" />}
                    onClick={async () => {
                      await onLogout();
                      onCloseUserMenu();
                    }}
                  >
                    Sign out
                  </Button>
                </>
              ) : (
                <>
                  <Button
                    type="button"
                    variant="subtle"
                    size="sm"
                    role="menuitem"
                    className="w-full justify-start!"
                    iconLeft={<LoginIcon className="h-4 w-4" />}
                    onClick={() => {
                      onOpenLogin();
                      onCloseUserMenu();
                    }}
                  >
                    Sign In
                  </Button>
                  <Button
                    type="button"
                    variant="subtle"
                    size="sm"
                    role="menuitem"
                    className="flex w-full items-center justify-start!"
                    onClick={() => {
                      onOpenRegister();
                      onCloseUserMenu();
                    }}
                  >
                    Register
                  </Button>
                </>
              )}
            </div>
          </div>
        )}
      </div>
    </div>
  );

  if (mobile) {
    return bar;
  }

  return <div className={DESKTOP_WRAPPER_CLASS_NAME}>{bar}</div>;
}

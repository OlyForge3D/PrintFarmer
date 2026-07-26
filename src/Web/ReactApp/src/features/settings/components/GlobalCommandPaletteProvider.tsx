/**
 * Global command-palette provider (#938).
 *
 * Owns the palette's open/close state, the Ctrl/Cmd+K keyboard listener, and
 * the assembly + routing of palette items sourced from:
 *
 *  - the admin destination registry (#934)
 *  - the pre-existing user-scope settings navigation
 *  - the settings-metadata backend (per property, deep-linked with a `field`
 *    URL param that {@link SettingsPage} interprets to bypass Essential mode
 *    and scroll the field into view)
 *  - a curated list of safe global actions (sign out, refresh admin overview,
 *    switch light/dark)
 *
 * The provider is mounted once from {@link Layout} so Ctrl+K works on every
 * authenticated route — the settings shell now consumes {@link useCommandPalette}
 * to open the same palette from its toolbar button.
 */
import {
  useCallback,
  useEffect,
  useMemo,
  useState,
  type ReactNode,
} from 'react';
import { useNavigate } from 'react-router';
import { useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import { CommandPalette } from '@/features/settings/components/CommandPalette';
import { ConfirmationModal } from '@/common/components/modals/ConfirmationModal';
import {
  CommandPaletteContext,
  type CommandPaletteContextValue,
} from '@/features/settings/components/commandPaletteContext';
import {
  buildAdminDestinationCommandItems,
  buildSettingCommandItems,
  buildSettingsCommandItems,
  buildSettingsPath,
  resolveSettingsNavigationTarget,
  type SettingsCommandItem,
} from '@/features/settings/settings-navigation';
import {
  ADMIN_DESTINATIONS,
  filterDestinationsByAccess,
} from '@/features/admin/registry/adminDestinations';
import { useAuth } from '@/features/auth/hooks/useAuth';
import { useTheme } from '@/common/hooks/useTheme';
import { ADMIN_OVERVIEW_QUERY_KEY } from '@/features/admin/hooks/useAdminOverview';
import {
  useSettingsGroups,
  useSettingsMetadata,
} from '@/features/settings/queries/useSettingsMetadata';
import {
  LogoutIcon,
  RefreshIcon,
  SunIcon,
} from '@/common/components/icons/MdiIcons';

export interface GlobalCommandPaletteProviderProps {
  children: ReactNode;
}

export function GlobalCommandPaletteProvider({ children }: GlobalCommandPaletteProviderProps): ReactNode {
  const [isOpen, setIsOpen] = useState(false);
  const [pendingAction, setPendingAction] = useState<SettingsCommandItem | null>(null);
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const { user, hasRole, hasPermission, logout } = useAuth();
  const { theme, setTheme } = useTheme();
  const isFarmAdmin = hasRole('farm_admin');

  const open = useCallback(() => setIsOpen(true), []);
  const close = useCallback(() => setIsOpen(false), []);

  // Ctrl/Cmd+K opens the palette from anywhere. Modelled on the pre-existing
  // handler in SettingsShell — no state reads inside the closure, no refs
  // written during render, so the React compiler keeps optimising this file.
  useEffect(() => {
    function isEditableTarget(target: EventTarget | null): boolean {
      if (!(target instanceof HTMLElement)) {
        return false;
      }
      if (target.isContentEditable) {
        return true;
      }
      return ['INPUT', 'TEXTAREA', 'SELECT'].includes(target.tagName);
    }

    function handleKeyDown(event: KeyboardEvent) {
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
      setIsOpen(true);
    }

    window.addEventListener('keydown', handleKeyDown);
    return () => window.removeEventListener('keydown', handleKeyDown);
  }, []);

  // Settings metadata is only useful once the user can actually reach admin
  // settings. Keep the query disabled for signed-out users so we don't fire
  // a request that will 401.
  const metadataQuery = useSettingsMetadata({ enabled: Boolean(user) && isOpen });
  const groupsQuery = useSettingsGroups({ enabled: Boolean(user) && isOpen });

  const accessibleDestinations = useMemo(() => {
    if (!user) {
      return [];
    }
    return filterDestinationsByAccess(ADMIN_DESTINATIONS, { hasRole, hasPermission });
  }, [user, hasRole, hasPermission]);

  const destinationItems = useMemo(
    () => buildAdminDestinationCommandItems(accessibleDestinations),
    [accessibleDestinations],
  );

  // The pre-#938 nav items also cover admin scopes (users, data, etc.) — the
  // admin-destination registry is now the source of truth for admin routes so
  // filter those out here. User-scope profile items stay because there is no
  // admin destination for user preferences.
  const settingsNavItems = useMemo(
    () => buildSettingsCommandItems().filter((item) => item.scopeId === 'user'),
    [],
  );

  const settingFieldItems = useMemo(() => {
    if (!isFarmAdmin) {
      return [] as SettingsCommandItem[];
    }
    return buildSettingCommandItems(metadataQuery.data, groupsQuery.data);
  }, [isFarmAdmin, metadataQuery.data, groupsQuery.data]);

  const actionItems = useMemo<SettingsCommandItem[]>(() => {
    const actions: SettingsCommandItem[] = [];

    if (user) {
      actions.push({
        id: 'action.sign-out',
        kind: 'action',
        scopeId: 'user',
        categoryId: 'profile',
        label: 'Sign out',
        description: 'End the current session and return to the login screen.',
        breadcrumb: 'Actions / Session',
        keywords: ['sign out', 'signout', 'log out', 'logout', 'exit', 'quit'],
        icon: LogoutIcon,
        confirmMessage: 'Sign out of PrintFarmer?',
        onExecute: async ({ close: closePalette }) => {
          closePalette();
          try {
            await logout();
          } catch {
            toast.error('Could not sign out. Try again in a moment.');
          }
        },
      });
    }

    if (isFarmAdmin) {
      actions.push({
        id: 'action.refresh-admin-overview',
        kind: 'action',
        scopeId: 'admin',
        categoryId: 'operations',
        label: 'Refresh admin overview',
        description: 'Re-fetch every tile on the Admin Control Center.',
        breadcrumb: 'Actions / Admin',
        keywords: ['refresh', 'reload', 'admin', 'overview', 'control center', 'invalidate'],
        icon: RefreshIcon,
        onExecute: ({ close: closePalette }) => {
          void queryClient.invalidateQueries({ queryKey: ADMIN_OVERVIEW_QUERY_KEY });
          toast.success('Refreshing admin overview…');
          closePalette();
        },
      });
    }

    const targetTheme = theme === 'light' ? 'dark' : 'light';
    actions.push({
      id: 'action.switch-theme',
      kind: 'action',
      scopeId: 'user',
      categoryId: 'profile',
      label: targetTheme === 'dark' ? 'Switch to dark theme' : 'Switch to light theme',
      description: 'Toggle the interface between the light and dark PrintFarmer themes.',
      breadcrumb: 'Actions / Appearance',
      keywords: ['theme', 'appearance', 'dark mode', 'light mode', 'toggle theme'],
      icon: SunIcon,
      onExecute: ({ close: closePalette }) => {
        setTheme(targetTheme);
        closePalette();
      },
    });

    return actions;
  }, [user, isFarmAdmin, theme, setTheme, logout, queryClient]);

  const items = useMemo<SettingsCommandItem[]>(
    () => [
      ...destinationItems,
      ...settingsNavItems,
      ...settingFieldItems,
      ...actionItems,
    ],
    [destinationItems, settingsNavItems, settingFieldItems, actionItems],
  );

  const handleSelect = useCallback(
    (item: SettingsCommandItem) => {
      if (item.kind === 'action' && item.onExecute) {
        if (item.confirmMessage) {
          // Defer to an in-app confirmation modal rather than `window.confirm`.
          // A native dialog is unstyled, unannounced to the palette's own live
          // region, and inconsistent with every other confirmation in the app.
          setPendingAction(item);
          close();
          return;
        }
        void item.onExecute({ close });
        return;
      }
      if (item.href) {
        navigate(item.href);
      } else {
        const resolved = resolveSettingsNavigationTarget(item.categoryId, item.subPageId, item.scopeId);
        navigate(buildSettingsPath({ ...resolved }));
      }
      close();
    },
    [navigate, close],
  );

  // Guard against a URL change that unmounts the current view leaving the
  // palette open — closing on route change is done inside `handleSelect` so
  // we can avoid a `useEffect` that reads `location` and calls `setState`
  // (both of which would break React-compiler purity rules for the file).

  const confirmPendingAction = useCallback(() => {
    const item = pendingAction;
    setPendingAction(null);
    if (item?.onExecute) {
      void item.onExecute({ close });
    }
  }, [pendingAction, close]);

  const cancelPendingAction = useCallback(() => {
    setPendingAction(null);
  }, []);

  const value = useMemo<CommandPaletteContextValue>(
    () => ({ open, close, isOpen }),
    [open, close, isOpen],
  );

  return (
    <CommandPaletteContext.Provider value={value}>
      {children}
      <CommandPalette isOpen={isOpen} items={items} onClose={close} onSelect={handleSelect} />
      <ConfirmationModal
        isOpen={pendingAction !== null}
        title={pendingAction?.label ?? 'Confirm action'}
        message={pendingAction?.confirmMessage ?? ''}
        confirmButtonText={pendingAction?.label ?? 'Confirm'}
        isDangerous
        onConfirm={confirmPendingAction}
        onCancel={cancelPendingAction}
      />
    </CommandPaletteContext.Provider>
  );
}

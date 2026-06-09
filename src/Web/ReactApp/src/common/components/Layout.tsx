import { LoginModal } from '@/features/auth/components/LoginModal';
import { RegisterModal } from '@/features/auth/components/RegisterModal';
import { EmailConfirmationBanner } from '@/features/auth/components/EmailConfirmationBanner';
import { TasksBadge } from '@/features/tasks';
import { InstallBanner } from '@/common/components/InstallBanner';
import clsx from 'clsx';
import { Button } from '@/common/components/ui';
import {
  HomeIcon,
  PrinterIcon,
  LayersIcon,
  SettingsIcon,
  MenuIcon,
  ChevronLeftIcon,
  ChevronRightIcon,
  CloseIcon,
  GearIcon,
  FolderOpenIcon,
  HistoryIcon,
  WrenchIcon,
  TrendingUpIcon,
  AlertIcon,
  ClipboardListIcon,
  PlayIcon,
  CalendarIcon,
  LocationIcon,
  KeyIcon,
  NetworkIcon,
  ShieldIcon,
} from '@/common/components/icons/MdiIcons';
import { PrintFarmerLogoIcon } from '@/common/components/icons/PrintFarmerLogoIcon';
import { useAuth } from '@/features/auth/hooks/useAuth';
import { useSlicer } from '@/hooks/useSlicer';
import { useSystemCapabilities } from '@/common/hooks/useSystemCapabilities';
import { PlatformBanner } from '@/common/components/PlatformBanner';
import { useSignalRConnection } from '@/common/hooks/useSignalR';
import { Suspense, useCallback, useEffect, useMemo, useRef, useState, type CSSProperties } from 'react';
import { useAllAutoDispatchStatuses } from '@/features/printers/hooks/useAutoDispatch';
import { requiresBedClearConfirmation } from '@/common/utils/printerStateDisplay';
import type { AutoDispatchStatus } from '@/types/api';
import { RouteErrorBoundary } from '@/common/components/ErrorBoundary';
import { NavLink, Outlet, useLocation, useNavigate } from 'react-router';
import DebugPrinterSignalRPanel from '@/features/printers/components/DebugPrinterSignalRPanel';
import { printerSignalRService } from '@/services/printer-signalr';
import { NfcPairingModal } from '@/features/nfc/components/NfcPairingModal';
import { useNfcPairingSession } from '@/features/nfc/hooks/useNfcPairingSession';
import { FloatingControlBar } from '@/common/components/FloatingControlBar';
import { BoxIcon, SpoolIcon } from 'lucide-react';
// Layout now uses <Outlet /> for nested routes

interface NavigationItem {
  name: string;
  href: string;
  icon: React.ComponentType<{ className?: string }>;
  requiredPermission?: { resource: string; action: string };
  requiredRole?: string;
  requiresSlicer?: boolean;
  /** Hide when platform-level slicing is disabled (ARM / Raspberry Pi) */
  requiresSlicingCapability?: boolean;
  /** Hide when platform-level model file support is disabled (ARM / Raspberry Pi) */
  requiresModelFiles?: boolean;
  matches?: (pathname: string) => boolean;
  isDivider?: false;
  isSectionHeader?: false;
}

interface NavigationDivider {
  name: string;
  isDivider: true;
}

interface NavigationSectionHeader {
  name: string;
  icon: React.ComponentType<{ className?: string }>;
  isSectionHeader: true;
  requiredRole?: string;
}

interface NavigationGroup {
  header: NavigationSectionHeader;
  items: NavigationItem[];
}

type NavigationElement = NavigationItem | NavigationDivider | NavigationSectionHeader;

const isDivider = (item: NavigationElement): item is NavigationDivider => 'isDivider' in item && item.isDivider === true;
const isSectionHeader = (item: NavigationElement): item is NavigationSectionHeader => 'isSectionHeader' in item && item.isSectionHeader === true;
const isNavigationItem = (item: NavigationElement): item is NavigationItem => !isDivider(item) && !isSectionHeader(item);

const navigation: NavigationElement[] = [
  { name: 'Dashboard', icon: HomeIcon, isSectionHeader: true },
  { name: 'Overview', href: '/dashboard', icon: HomeIcon, matches: (pathname) => pathname === '/' || pathname.startsWith('/dashboard') },
  {
    name: 'Print Queue',
    href: '/printQueue',
    icon: HistoryIcon,
    requiredPermission: { resource: 'printers', action: 'read' },
    matches: (pathname) => pathname.startsWith('/printQueue')
  },
  {
    name: 'Auto-Dispatch',
    href: '/auto-dispatch',
    icon: PlayIcon,
    requiredPermission: { resource: 'printers', action: 'read' },
    matches: (pathname) => pathname.startsWith('/auto-dispatch')
  },
  {
    name: 'Analytics',
    href: '/analytics',
    icon: TrendingUpIcon,
    matches: (pathname) => pathname.startsWith('/analytics') || pathname.startsWith('/statistics')
  },
  {
    name: 'Scheduling',
    href: '/scheduling',
    icon: CalendarIcon,
    matches: (pathname) => pathname.startsWith('/scheduling')
  },

  { name: 'Printers', icon: PrinterIcon, isSectionHeader: true },
  {
    name: 'Printers',
    href: '/printers',
    icon: PrinterIcon,
    requiredPermission: { resource: 'printers', action: 'read' },
    matches: (pathname) => pathname === '/printers' || /^\/printers\/[^/]+$/.test(pathname)
  },
  {
    name: 'Maintenance',
    href: '/maintenance',
    icon: WrenchIcon,
    requiredPermission: { resource: 'printers', action: 'read' },
    matches: (pathname) => pathname === '/maintenance' || pathname.endsWith('/maintenance')
  },
  {
    name: 'Filament Inventory',
    href: '/spools',
    icon: SpoolIcon,
    matches: (pathname) => pathname.startsWith('/spools')
  },
  {
    name: 'Locations',
    href: '/locations/dashboard',
    icon: LocationIcon,
    matches: (pathname) => pathname.startsWith('/locations')
  },

  { name: 'Files', icon: FolderOpenIcon, isSectionHeader: true },
  {
    name: 'Files',
    href: '/files',
    icon: FolderOpenIcon,
    requiredPermission: { resource: 'models', action: 'read' },
    matches: (pathname) => pathname.startsWith('/files')
  },
  {
    name: 'Projects',
    href: '/projects',
    icon: ClipboardListIcon,
    requiredPermission: { resource: 'models', action: 'read' },
    matches: (pathname) => pathname.startsWith('/projects')
  },

  { name: 'Slicer', icon: BoxIcon, isSectionHeader: true },
  {
    name: 'Slice Job',
    href: '/slicer',
    icon: BoxIcon,
    requiredPermission: { resource: 'models', action: 'read' },
    requiresSlicer: true,
    requiresSlicingCapability: true,
    matches: (pathname) => pathname.startsWith('/slicer') || pathname.startsWith('/profiles/import')
  },

  { name: 'Settings', icon: GearIcon, isSectionHeader: true },
  {
    name: 'Preferences',
    href: '/settings',
    icon: GearIcon,
    matches: (pathname) => pathname === '/settings' || pathname.startsWith('/preferences')
  },
  {
    name: 'API Keys',
    href: '/profile/api-keys',
    icon: KeyIcon,
    matches: (pathname) => pathname.startsWith('/profile/api-keys')
  },
  {
    name: 'Notifications',
    href: '/profile/notifications',
    icon: NetworkIcon,
    matches: (pathname) => pathname.startsWith('/profile/notifications')
  },
  {
    name: 'Passkeys',
    href: '/profile/passkeys',
    icon: ShieldIcon,
    matches: (pathname) => pathname.startsWith('/profile/passkeys')
  },

  { name: 'Admin', icon: SettingsIcon, isSectionHeader: true, requiredRole: 'farm_admin' },
  {
    name: 'Catalog',
    href: '/catalog',
    icon: LayersIcon,
    requiredRole: 'farm_admin',
    matches: (pathname) => pathname.startsWith('/catalog')
  },
  {
    name: 'System Settings',
    href: '/admin/settings',
    icon: GearIcon,
    requiredRole: 'farm_admin',
    matches: (pathname) => pathname.startsWith('/admin/settings')
  },
  {
    name: 'Admin Console',
    href: '/admin/manage',
    icon: SettingsIcon,
    requiredRole: 'farm_admin',
    matches: (pathname) => pathname.startsWith('/admin/manage') || pathname.startsWith('/admin/system') || pathname.startsWith('/slice-jobs')
  },
];

const MOBILE_TOP_BAR_HEIGHT_PX = 48;
const EXPANDED_RAIL_WIDTH_PX = 248;
const COLLAPSED_RAIL_WIDTH_PX = 64;
const NAVBAR_COLLAPSED_STORAGE_KEY = 'pf_navbar_collapsed';
const FOCUSABLE_SELECTOR = [
  'button:not([disabled])',
  '[href]',
  'input:not([disabled])',
  'select:not([disabled])',
  'textarea:not([disabled])',
  '[tabindex]:not([tabindex="-1"])',
].join(', ');

function getFocusableElements(container: HTMLElement | null): HTMLElement[] {
  if (!container) {
    return [];
  }

  return Array.from(container.querySelectorAll<HTMLElement>(FOCUSABLE_SELECTOR)).filter((element) => {
    if (element.hasAttribute('disabled') || element.getAttribute('aria-hidden') === 'true') {
      return false;
    }

    return element.tabIndex >= 0;
  });
}

function focusFirstElement(container: HTMLElement | null): boolean {
  const firstFocusable = getFocusableElements(container)[0];

  if (!firstFocusable) {
    return false;
  }

  firstFocusable.focus();
  return true;
}

export function Layout() {
  const { isConnected } = useSignalRConnection('printer');
  const { user, logout, isAuthenticated, hasRole, hasPermission } = useAuth();
  const { isSlicerAvailable } = useSlicer();
  const canRole = useCallback((role: string) => typeof hasRole === 'function' ? hasRole(role) : user?.role === role, [hasRole, user?.role]);
  const canPermission = useCallback((resource: string, action: string) => typeof hasPermission === 'function' ? hasPermission(resource, action) : true, [hasPermission]);
  const { data: capabilities } = useSystemCapabilities();

  const navigate = useNavigate();
  const { data: allAutoDispatchStatuses } = useAllAutoDispatchStatuses();
  const nfcPairingSession = useNfcPairingSession();
  const pendingAttentionCount = useMemo(
    () => ((allAutoDispatchStatuses ?? []) as AutoDispatchStatus[]).filter((status) => requiresBedClearConfirmation(status)).length,
    [allAutoDispatchStatuses]
  );
  const location = useLocation();

  // Global debug subscription to printer SignalR events (for dev verification)
  useEffect(() => {
    if (!import.meta.env.VITE_PRINTFARMER_DEBUG) return;
    let unsub: (() => void) | null = null;
    try {
      printerSignalRService.connect();
      unsub = printerSignalRService.onPrinterStatusUpdate((status) => {
        // Gate debug logging behind per-area flag so it can be enabled in-browser
        const win = window as unknown as { PrintFarmerDebug?: Record<string, unknown> };
        if (win.PrintFarmerDebug?.layout) {
          console.debug('[Layout] Received PrinterUpdated', status.id, status.state, status.isOnline);
        }
      });
    } catch (err) {
      console.warn('[Layout] Could not subscribe to printerSignalRService', err);
    }
    return () => { if (unsub) unsub(); };
  }, []);
  const [sidebarOpen, setSidebarOpen] = useState(false);
  const [userMenuOpen, setUserMenuOpen] = useState(false);
  const [showLoginModal, setShowLoginModal] = useState(false);
  const [showRegisterModal, setShowRegisterModal] = useState(false);
  const [navbarCollapsed, setNavbarCollapsed] = useState(() => {
    const saved = localStorage.getItem(NAVBAR_COLLAPSED_STORAGE_KEY);
    return saved ? JSON.parse(saved) : false;
  });
  const [openCollapsedSection, setOpenCollapsedSection] = useState<string | null>(null);
  const desktopRailRef = useRef<HTMLDivElement | null>(null);
  const mobileDrawerAnnouncementRef = useRef<HTMLDivElement | null>(null);
  const mobileMenuButtonRef = useRef<HTMLButtonElement | null>(null);
  const mobileDrawerRef = useRef<HTMLDivElement | null>(null);
  const sidebarAnnouncementTimeoutRef = useRef<number | null>(null);
  const previousSidebarOpenRef = useRef(false);
  const previousDrawerFocusRef = useRef<HTMLElement | null>(null);
  const collapsedSectionTriggerRefs = useRef<Record<string, HTMLButtonElement | null>>({});
  const collapsedSectionPopoverRefs = useRef<Record<string, HTMLDivElement | null>>({});

  useEffect(() => {
    localStorage.setItem(NAVBAR_COLLAPSED_STORAGE_KEY, JSON.stringify(navbarCollapsed));
  }, [navbarCollapsed]);

  const filteredNavigation = useMemo(() => {
    const isHiddenByCapabilities = (item: NavigationItem) => {
      if (item.requiresSlicingCapability && capabilities?.slicingEnabled === false) return true;
      if (item.requiresModelFiles && capabilities?.modelFilesEnabled === false) return true;
      return false;
    };

    if (!isAuthenticated) {
      return navigation.filter((item) => {
        if (isDivider(item)) return true;
        if (isSectionHeader(item)) {
          return !item.requiredRole;
        }

        if (isHiddenByCapabilities(item)) return false;
        if (item.requiresSlicer && !isSlicerAvailable) return false;
        return !item.requiredRole && !item.requiredPermission;
      });
    }

    return navigation.filter((item) => {
      if (isDivider(item)) return true;
      if (isSectionHeader(item)) {
        return !item.requiredRole || canRole(item.requiredRole);
      }

      if (item.requiredRole && !canRole(item.requiredRole)) return false;
      if (item.requiredPermission && !canPermission(item.requiredPermission.resource, item.requiredPermission.action)) return false;
      if (isHiddenByCapabilities(item)) return false;
      if (item.requiresSlicer && !isSlicerAvailable) return false;
      return true;
    });
  }, [isAuthenticated, canRole, canPermission, isSlicerAvailable, capabilities]);

  const navigationGroups = useMemo<NavigationGroup[]>(() => {
    const groups: NavigationGroup[] = [];

    filteredNavigation.forEach((item) => {
      if (isSectionHeader(item)) {
        groups.push({ header: item, items: [] });
        return;
      }

      if (!isNavigationItem(item) || groups.length === 0) {
        return;
      }

      groups[groups.length - 1].items.push(item);
    });

    return groups.filter((group) => group.items.length > 0);
  }, [filteredNavigation]);

  const isNavItemActive = (item: NavigationItem) => {
    if (item.matches) {
      return item.matches(location.pathname);
    }

    return location.pathname === item.href || location.pathname.startsWith(`${item.href}/`);
  };

  const handleLogout = async () => {
    await logout();
    setUserMenuOpen(false);
  };

  const closeCollapsedSection = useCallback((returnFocus = false) => {
    setOpenCollapsedSection((current) => {
      if (returnFocus && current) {
        window.requestAnimationFrame(() => {
          collapsedSectionTriggerRefs.current[current]?.focus();
        });
      }

      return null;
    });
  }, []);

  const announceMobileDrawer = useCallback((message: string) => {
    if (sidebarAnnouncementTimeoutRef.current !== null) {
      window.clearTimeout(sidebarAnnouncementTimeoutRef.current);
    }

    if (mobileDrawerAnnouncementRef.current) {
      mobileDrawerAnnouncementRef.current.textContent = message;
    }

    sidebarAnnouncementTimeoutRef.current = window.setTimeout(() => {
      if (mobileDrawerAnnouncementRef.current) {
        mobileDrawerAnnouncementRef.current.textContent = '';
      }
      sidebarAnnouncementTimeoutRef.current = null;
    }, 1_500);
  }, []);

  useEffect(() => {
    if (!openCollapsedSection) {
      return;
    }

    const handlePointerDown = (event: PointerEvent) => {
      if (desktopRailRef.current?.contains(event.target as Node)) {
        return;
      }

      closeCollapsedSection(true);
    };

    window.addEventListener('pointerdown', handlePointerDown);
    return () => window.removeEventListener('pointerdown', handlePointerDown);
  }, [closeCollapsedSection, openCollapsedSection]);

  useEffect(() => {
    if (!sidebarOpen && !userMenuOpen && !openCollapsedSection) {
      return;
    }

    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        setSidebarOpen(false);
        setUserMenuOpen(false);
        closeCollapsedSection(true);
      }
    };

    window.addEventListener('keydown', handleKeyDown);
    return () => window.removeEventListener('keydown', handleKeyDown);
  }, [closeCollapsedSection, sidebarOpen, userMenuOpen, openCollapsedSection]);

  useEffect(() => {
    if (!openCollapsedSection) {
      return;
    }

    const frame = window.requestAnimationFrame(() => {
      const popover = collapsedSectionPopoverRefs.current[openCollapsedSection];
      if (!focusFirstElement(popover)) {
        popover?.focus();
      }
    });

    return () => window.cancelAnimationFrame(frame);
  }, [openCollapsedSection]);

  useEffect(() => {
    if (sidebarOpen) {
      previousDrawerFocusRef.current = document.activeElement instanceof HTMLElement
        ? document.activeElement
        : mobileMenuButtonRef.current;

      const frame = window.requestAnimationFrame(() => {
        if (!focusFirstElement(mobileDrawerRef.current)) {
          mobileDrawerRef.current?.focus();
        }
      });

      previousSidebarOpenRef.current = true;
      return () => window.cancelAnimationFrame(frame);
    }

    if (previousSidebarOpenRef.current) {
      announceMobileDrawer('Navigation menu closed.');

      window.requestAnimationFrame(() => {
        previousDrawerFocusRef.current?.focus();
      });
    }

    previousSidebarOpenRef.current = false;

    return undefined;
  }, [announceMobileDrawer, sidebarOpen]);

  useEffect(() => {
    if (!sidebarOpen) {
      return;
    }

    announceMobileDrawer('Navigation menu opened.');

    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.key !== 'Tab') {
        return;
      }

      const drawer = mobileDrawerRef.current;
      const focusableElements = getFocusableElements(drawer);
      if (focusableElements.length === 0) {
        event.preventDefault();
        drawer?.focus();
        return;
      }

      const firstElement = focusableElements[0];
      const lastElement = focusableElements[focusableElements.length - 1];
      const activeElement = document.activeElement instanceof HTMLElement ? document.activeElement : null;
      const containsFocus = activeElement ? drawer?.contains(activeElement) : false;

      if (event.shiftKey) {
        if (!containsFocus || activeElement === firstElement) {
          event.preventDefault();
          lastElement.focus();
        }
        return;
      }

      if (!containsFocus || activeElement === lastElement) {
        event.preventDefault();
        firstElement.focus();
      }
    };

    window.addEventListener('keydown', handleKeyDown);
    return () => window.removeEventListener('keydown', handleKeyDown);
  }, [announceMobileDrawer, sidebarOpen]);

  useEffect(() => () => {
    if (sidebarAnnouncementTimeoutRef.current !== null) {
      window.clearTimeout(sidebarAnnouncementTimeoutRef.current);
    }
  }, []);

  const switchToRegister = () => {
    setShowLoginModal(false);
    setShowRegisterModal(true);
  };

  const switchToLogin = () => {
    setShowRegisterModal(false);
    setShowLoginModal(true);
  };

  const desktopRailWidth = navbarCollapsed ? COLLAPSED_RAIL_WIDTH_PX : EXPANDED_RAIL_WIDTH_PX;
  const desktopShellStyle = useMemo(() => ({
    '--pf-layout-rail-width': `${desktopRailWidth}px`,
  }) as CSSProperties, [desktopRailWidth]);

  return (
    <div className="h-screen overflow-hidden bg-pf-bg-0 text-pf-text-primary">
      <a
        href="#main-content"
        className="sr-only focus:not-sr-only focus:absolute focus:left-4 focus:top-4 focus:z-[80] focus:rounded-md focus:bg-pf-bg-1 focus:px-3 focus:py-2 focus:text-sm focus:text-pf-text-primary focus:shadow-lg focus:outline-hidden focus-visible:ring-2 focus-visible:ring-pf-accent"
      >
        Skip to main content
      </a>
      <div ref={mobileDrawerAnnouncementRef} className="sr-only" aria-live="polite" aria-atomic="true" />

      <div
        className="flex h-full min-h-0 flex-col lg:grid lg:grid-cols-[var(--pf-layout-rail-width)_minmax(0,1fr)]"
        style={desktopShellStyle}
      >
        <header
          inert={sidebarOpen || undefined}
          className="z-40 flex h-12 shrink-0 items-center justify-between border-b border-pf-border bg-pf-bg-1 px-3 lg:hidden"
        >
          <div className="flex items-center gap-2">
            <Button
              ref={mobileMenuButtonRef}
              type="button"
              aria-controls="mobile-navigation-drawer"
              aria-expanded={sidebarOpen}
              aria-label={sidebarOpen ? 'Close navigation menu' : 'Open navigation menu'}
              title={sidebarOpen ? 'Close navigation menu' : 'Open navigation menu'}
              variant="subtle"
              size="sm"
              className="h-9 w-9 justify-center px-0"
              onClick={() => {
                setSidebarOpen(prev => !prev);
                setUserMenuOpen(false);
              }}
              iconCenter={<MenuIcon className="h-5 w-5" />}
            />
            <div className="flex min-w-0 items-center gap-2">
              <PrintFarmerLogoIcon decorative className="h-7 w-7 text-pf-accent" />
              <div className="min-w-0">
                <h1 className="truncate text-lg font-bold text-pf-text-primary font-bebas uppercase">PrintFarmer</h1>
              </div>
            </div>
          </div>

          <div className="flex items-center gap-2">
            {pendingAttentionCount > 0 && (
              <Button
                type="button"
                variant="unstyled"
                onClick={() => navigate('/printers?view=collapsed')}
                className="relative flex h-9 w-9 items-center justify-center rounded-md text-pf-warning transition-colors hover:bg-pf-bg-2 focus-visible:ring-2 focus-visible:ring-pf-accent"
                title={`${pendingAttentionCount} printer${pendingAttentionCount !== 1 ? 's' : ''} need${pendingAttentionCount === 1 ? 's' : ''} attention — click to view`}
                aria-label={`${pendingAttentionCount} printer${pendingAttentionCount !== 1 ? 's' : ''} need${pendingAttentionCount === 1 ? 's' : ''} attention`}
              >
                <AlertIcon className="h-4 w-4" />
                <span className="absolute right-1 top-1 flex h-4 min-w-4 items-center justify-center rounded-full bg-pf-warning px-1 text-[9px] font-bold leading-none text-black">
                  {pendingAttentionCount}
                </span>
              </Button>
            )}

            {isAuthenticated && <TasksBadge />}
            <FloatingControlBar
              mobile
              isAuthenticated={isAuthenticated}
              userName={user?.username}
              userMenuOpen={userMenuOpen}
              onToggleUserMenu={() => setUserMenuOpen((prev) => !prev)}
              onCloseUserMenu={() => setUserMenuOpen(false)}
              onViewSystemStatus={() => navigate('/admin/manage?tab=operations&sub=status')}
              onOpenPreferences={() => navigate('/settings')}
              onOpenLogin={() => setShowLoginModal(true)}
              onOpenRegister={() => setShowRegisterModal(true)}
              onLogout={handleLogout}
            />
          </div>
        </header>

        <div
          className={clsx(
            'fixed inset-x-0 bottom-0 z-50 lg:hidden',
            sidebarOpen ? 'pointer-events-auto' : 'pointer-events-none'
          )}
          style={{ top: `${MOBILE_TOP_BAR_HEIGHT_PX}px` }}
          aria-hidden={!sidebarOpen}
          inert={!sidebarOpen}
        >
          <div
            className={clsx(
              'absolute inset-0 bg-black/60 transition-opacity duration-200',
              sidebarOpen ? 'opacity-100' : 'opacity-0'
            )}
            onClick={() => setSidebarOpen(false)}
          />

          <div
            ref={mobileDrawerRef}
            id="mobile-navigation-drawer"
            role="dialog"
            aria-modal="true"
            aria-label="Mobile navigation drawer"
            tabIndex={-1}
            className={clsx(
              'relative flex h-full w-[248px] max-w-[calc(100vw-1rem)] flex-col border-r border-pf-border bg-pf-bg-1 shadow-2xl transition-transform duration-200 ease-out',
              sidebarOpen ? 'translate-x-0' : '-translate-x-full'
            )}
          >
            <div className="flex items-center justify-between border-b border-pf-border px-4 py-3">
              <div className="flex min-w-0 items-center gap-3">
                <PrintFarmerLogoIcon decorative className="h-7 w-7 text-pf-accent" />
                <div className="min-w-0">
                  <div className="truncate text-base font-semibold text-pf-text-primary font-bebas uppercase">PrintFarmer</div>
                  <div className="text-xs text-pf-text-tertiary">Command rail</div>
                </div>
              </div>
              <Button
                type="button"
                variant="subtle"
                size="sm"
                className="h-9 w-9 justify-center px-0"
                aria-label="Close navigation menu"
                onClick={() => setSidebarOpen(false)}
                iconCenter={<CloseIcon className="h-5 w-5" />}
              />
            </div>

            <nav className="flex-1 min-h-0 space-y-3 overflow-y-auto px-3 py-3" aria-label="Main navigation">
              {navigationGroups.map((group) => {
                const SectionIcon = group.header.icon;
                const isSectionActive = group.items.some((item) => isNavItemActive(item));

                return (
                  <section
                    key={group.header.name}
                    className={clsx(
                      'rounded-2xl border p-2 shadow-sm',
                      isSectionActive
                        ? 'border-pf-accent/35 bg-pf-accent/8'
                        : 'border-pf-border bg-pf-bg-0/60'
                    )}
                  >
                    <div className={clsx('flex items-center gap-3 rounded-xl px-2 py-2', isSectionActive ? 'text-pf-accent' : 'text-pf-text-primary')}>
                      <span aria-hidden="true">
                        <SectionIcon className="h-5 w-5 shrink-0" />
                      </span>
                      <div className="min-w-0">
                        <div className="text-sm font-semibold">{group.header.name}</div>
                        <div className="text-[11px] uppercase tracking-[0.22em] text-pf-text-tertiary">Navigation</div>
                      </div>
                    </div>

                    <div className="mt-1 space-y-1 px-1 pb-1">
                      {group.items.map((item) => {
                        const ItemIcon = item.icon;
                        const isActive = isNavItemActive(item);

                        return (
                          <NavLink
                            key={item.href}
                            to={item.href}
                            onClick={() => setSidebarOpen(false)}
                            className={clsx(
                              'group flex items-center rounded-xl border-l-3 px-3 py-2 text-sm transition-colors focus-visible:outline-hidden focus-visible:ring-2 focus-visible:ring-pf-accent',
                              isActive
                                ? 'border-pf-accent bg-pf-bg-2 font-semibold text-pf-accent'
                                : 'border-transparent text-pf-text-secondary hover:bg-pf-bg-2 hover:text-pf-text-primary'
                            )}
                          >
                            <span aria-hidden="true">
                              <ItemIcon className="mr-3 h-4 w-4 shrink-0" />
                            </span>
                            <span className="flex-1 text-left">{item.name}</span>
                          </NavLink>
                        );
                      })}
                    </div>
                  </section>
                );
              })}
            </nav>
          </div>
        </div>

        <aside
          ref={desktopRailRef}
          className="hidden h-screen min-h-0 border-r border-pf-border bg-pf-bg-1 shadow-[12px_0_32px_rgba(0,0,0,0.16)] lg:flex"
          aria-label="Command rail"
        >
          <div className="flex h-full min-h-0 w-full flex-col">
            <div className={clsx('border-b border-pf-border', navbarCollapsed ? 'px-2 py-3' : 'px-4 py-4')}>
              <div className={clsx('flex items-center', navbarCollapsed ? 'justify-center' : 'justify-between gap-3')}>
                <div className={clsx('flex min-w-0 items-center', navbarCollapsed ? 'justify-center' : 'gap-3')}>
                  <PrintFarmerLogoIcon decorative className="h-8 w-8 shrink-0 text-pf-accent" />
                  {!navbarCollapsed && (
                    <div className="min-w-0">
                      <div className="truncate text-lg font-bold uppercase tracking-wide text-pf-text-primary font-bebas">PrintFarmer</div>
                      <div className="text-xs text-pf-text-tertiary">Command rail</div>
                    </div>
                  )}
                </div>
                {!navbarCollapsed && (
                  <div className="flex items-center gap-2 rounded-full border border-pf-border bg-pf-bg-2 px-2 py-1 text-xs text-pf-text-tertiary">
                    <span className={clsx('h-2 w-2 rounded-full', isConnected ? 'bg-pf-success' : 'bg-pf-error')} aria-hidden="true" />
                    {isConnected ? 'Connected' : 'Disconnected'}
                  </div>
                )}
              </div>
            </div>

            <nav
              className={clsx('relative flex-1 min-h-0 overflow-y-auto', navbarCollapsed ? 'px-2 py-3' : 'px-3 py-4')}
              aria-label="Main navigation"
            >
              {navbarCollapsed ? (
                <div className="space-y-2">
                  {navigationGroups.map((group) => {
                    const SectionIcon = group.header.icon;
                    const isSectionActive = group.items.some((item) => isNavItemActive(item));
                    const isOpen = openCollapsedSection === group.header.name;
                    const popoverId = `rail-popover-${group.header.name.toLowerCase().replace(/[^a-z0-9]+/g, '-')}`;

                    return (
                      <div key={group.header.name} className="relative">
                        <Button
                          ref={(element) => {
                            collapsedSectionTriggerRefs.current[group.header.name] = element;
                          }}
                          type="button"
                          variant="subtle"
                          size="sm"
                          className={clsx(
                            'h-11 w-11 justify-center rounded-2xl border px-0',
                            isSectionActive
                              ? 'border-pf-accent bg-pf-accent/12 text-pf-accent'
                              : 'border-pf-border text-pf-text-primary hover:bg-pf-bg-2'
                          )}
                          aria-label={group.header.name}
                          title={group.header.name}
                          aria-expanded={isOpen}
                          aria-haspopup="dialog"
                          aria-controls={isOpen ? popoverId : undefined}
                          onClick={() => setOpenCollapsedSection((prev) => prev === group.header.name ? null : group.header.name)}
                          iconCenter={<SectionIcon className="h-5 w-5" />}
                        />

                        {isOpen && (
                          <div
                            ref={(element) => {
                              collapsedSectionPopoverRefs.current[group.header.name] = element;
                            }}
                            id={popoverId}
                            role="dialog"
                            aria-label={`${group.header.name} navigation`}
                            tabIndex={-1}
                            className="absolute left-full top-0 z-50 ml-3 w-64 rounded-2xl border border-pf-border bg-pf-bg-1 p-2 shadow-[0_16px_40px_rgba(0,0,0,0.28)]"
                          >
                            <div className="mb-1 flex items-center gap-3 rounded-xl px-2 py-2 text-sm font-semibold text-pf-text-primary">
                              <span aria-hidden="true">
                                <SectionIcon className="h-5 w-5 text-pf-accent" />
                              </span>
                              <span>{group.header.name}</span>
                            </div>
                            <div className="space-y-1">
                              {group.items.map((item) => {
                                const ItemIcon = item.icon;
                                const isActive = isNavItemActive(item);

                                return (
                                  <NavLink
                                    key={item.href}
                                    to={item.href}
                                    onClick={() => closeCollapsedSection()}
                                    className={clsx(
                                      'flex items-center rounded-xl border-l-3 px-3 py-2 text-sm transition-colors focus-visible:outline-hidden focus-visible:ring-2 focus-visible:ring-pf-accent',
                                      isActive
                                        ? 'border-pf-accent bg-pf-bg-2 font-semibold text-pf-accent'
                                        : 'border-transparent text-pf-text-secondary hover:bg-pf-bg-2 hover:text-pf-text-primary'
                                    )}
                                  >
                                    <span aria-hidden="true">
                                      <ItemIcon className="mr-3 h-4 w-4 shrink-0" />
                                    </span>
                                    <span className="flex-1 text-left">{item.name}</span>
                                  </NavLink>
                                );
                              })}
                            </div>
                          </div>
                        )}
                      </div>
                    );
                  })}
                </div>
              ) : (
                <div className="space-y-3">
                  {navigationGroups.map((group) => {
                    const SectionIcon = group.header.icon;
                    const isSectionActive = group.items.some((item) => isNavItemActive(item));

                    return (
                      <section
                        key={group.header.name}
                        className={clsx(
                          'rounded-2xl border p-2 shadow-sm',
                          isSectionActive
                            ? 'border-pf-accent/35 bg-pf-accent/8'
                            : 'border-pf-border bg-pf-bg-0/60'
                        )}
                      >
                        <div className={clsx('flex items-center gap-3 rounded-xl px-2 py-2', isSectionActive ? 'text-pf-accent' : 'text-pf-text-primary')}>
                          <span aria-hidden="true">
                            <SectionIcon className="h-5 w-5 shrink-0" />
                          </span>
                          <div className="min-w-0">
                            <div className="text-sm font-semibold">{group.header.name}</div>
                            <div className="text-[11px] uppercase tracking-[0.22em] text-pf-text-tertiary">Rail section</div>
                          </div>
                        </div>

                        <div className="mt-1 space-y-1 px-1 pb-1">
                          {group.items.map((item) => {
                            const ItemIcon = item.icon;
                            const isActive = isNavItemActive(item);

                            return (
                              <NavLink
                                key={item.href}
                                to={item.href}
                                className={clsx(
                                  'group flex items-center rounded-xl border-l-3 px-3 py-2 text-sm transition-colors focus-visible:outline-hidden focus-visible:ring-2 focus-visible:ring-pf-accent',
                                  isActive
                                    ? 'border-pf-accent bg-pf-bg-2 font-semibold text-pf-accent'
                                    : 'border-transparent text-pf-text-secondary hover:bg-pf-bg-2 hover:text-pf-text-primary'
                                )}
                              >
                                <span aria-hidden="true">
                                  <ItemIcon className="mr-3 h-4 w-4 shrink-0" />
                                </span>
                                <span className="flex-1 text-left">{item.name}</span>
                              </NavLink>
                            );
                          })}
                        </div>
                      </section>
                    );
                  })}
                </div>
              )}
            </nav>

            <div className="shrink-0 border-t border-pf-border p-2">
              {!navbarCollapsed && pendingAttentionCount > 0 && (
                <div className="mb-2 flex items-center justify-end rounded-lg border border-pf-border bg-pf-bg-2 px-3 py-2">
                  <Button
                    type="button"
                    variant="unstyled"
                    onClick={() => navigate('/printers?view=collapsed')}
                    className="relative flex h-8 w-8 items-center justify-center rounded-md text-pf-warning transition-colors hover:bg-pf-bg-1 focus-visible:ring-2 focus-visible:ring-pf-accent"
                    title={`${pendingAttentionCount} printer${pendingAttentionCount !== 1 ? 's' : ''} need${pendingAttentionCount === 1 ? 's' : ''} attention — click to view`}
                    aria-label={`${pendingAttentionCount} printer${pendingAttentionCount !== 1 ? 's' : ''} need${pendingAttentionCount === 1 ? 's' : ''} attention`}
                  >
                    <AlertIcon className="h-4 w-4" />
                    <span className="absolute right-0.5 top-0.5 flex h-4 min-w-4 items-center justify-center rounded-full bg-pf-warning px-1 text-[9px] font-bold leading-none text-black">
                      {pendingAttentionCount}
                    </span>
                  </Button>
                </div>
              )}

              <div className={clsx('flex items-center gap-2', navbarCollapsed ? 'flex-col' : 'justify-between')}>
                <div className={clsx('flex items-center gap-2', navbarCollapsed && 'flex-col')}>
                  {isAuthenticated && <TasksBadge />}
                </div>

                <div className="flex items-center gap-2">
                  {navbarCollapsed && pendingAttentionCount > 0 && (
                    <Button
                      type="button"
                      variant="unstyled"
                      onClick={() => navigate('/printers?view=collapsed')}
                      className="relative flex h-9 w-9 items-center justify-center rounded-md text-pf-warning transition-colors hover:bg-pf-bg-2 focus-visible:ring-2 focus-visible:ring-pf-accent"
                      aria-label={`${pendingAttentionCount} printer${pendingAttentionCount !== 1 ? 's' : ''} need${pendingAttentionCount === 1 ? 's' : ''} attention`}
                      iconCenter={<AlertIcon className="h-4 w-4" />}
                    >
                      <span className="absolute right-0.5 top-0.5 flex h-4 min-w-4 items-center justify-center rounded-full bg-pf-warning px-1 text-[9px] font-bold leading-none text-black">
                        {pendingAttentionCount}
                      </span>
                    </Button>
                  )}
                </div>
              </div>

              <div className="mt-2">
                <Button
                  type="button"
                  aria-label={navbarCollapsed ? 'Expand navigation rail' : 'Collapse navigation rail'}
                  title={navbarCollapsed ? 'Expand navigation rail' : 'Collapse navigation rail'}
                  variant="subtle"
                  size="sm"
                  className="w-full justify-center"
                  onClick={() => {
                    setOpenCollapsedSection(null);
                    setNavbarCollapsed((prev) => !prev);
                  }}
                  iconCenter={navbarCollapsed ? <ChevronRightIcon className="h-5 w-5" /> : <ChevronLeftIcon className="h-5 w-5" />}
                />
              </div>
            </div>
          </div>
        </aside>

        <FloatingControlBar
          isAuthenticated={isAuthenticated}
          userName={user?.username}
          userMenuOpen={userMenuOpen}
          onToggleUserMenu={() => setUserMenuOpen((prev) => !prev)}
          onCloseUserMenu={() => setUserMenuOpen(false)}
          onViewSystemStatus={() => navigate('/admin/manage?tab=operations&sub=status')}
          onOpenPreferences={() => navigate('/settings')}
          onOpenLogin={() => setShowLoginModal(true)}
          onOpenRegister={() => setShowRegisterModal(true)}
          onLogout={handleLogout}
        />

        <main
          id="main-content"
          data-main-content
          inert={sidebarOpen || undefined}
          tabIndex={-1}
          className="flex-1 min-h-0 overflow-x-hidden overflow-y-auto bg-pf-bg-0 focus:outline-hidden lg:h-screen lg:scroll-pt-24"
        >
          <EmailConfirmationBanner />
          <PlatformBanner />
          <InstallBanner />
          <div className="px-2 pt-2 pb-4 lg:px-4 lg:pt-4 lg:pb-6">
            <RouteErrorBoundary>
              <Suspense
                fallback={
                  <div className="flex min-h-[40vh] items-center justify-center" role="status" aria-label="Loading page">
                    <div className="h-8 w-8 rounded-full border-b-2 border-pf-accent pf-animate-spin"></div>
                  </div>
                }
              >
                <Outlet />
              </Suspense>
            </RouteErrorBoundary>
          </div>
        </main>
      </div>

      {userMenuOpen && (
        <div
          className="fixed inset-0 z-10 pointer-events-auto lg:z-30"
          onClick={() => setUserMenuOpen(false)}
          aria-hidden="true"
        />
      )}

      <LoginModal
        isOpen={showLoginModal}
        onClose={() => setShowLoginModal(false)}
        onSwitchToRegister={switchToRegister}
      />
      <RegisterModal
        isOpen={showRegisterModal}
        onClose={() => setShowRegisterModal(false)}
        onSwitchToLogin={switchToLogin}
      />
      {import.meta.env.VITE_PRINTFARMER_DEBUG && <DebugPrinterSignalRPanel />}
      <NfcPairingModal session={nfcPairingSession} />
    </div>
  );
}

// Footer with build info now moved out of header for persistent display

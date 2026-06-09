import { LoginModal } from '@/features/auth/components/LoginModal';
import { RegisterModal } from '@/features/auth/components/RegisterModal';
import { EmailConfirmationBanner } from '@/features/auth/components/EmailConfirmationBanner';
import { TasksBadge } from '@/features/tasks';
import { NotificationBell } from '@/common/components/NotificationBell';
import { InstallBanner } from '@/common/components/InstallBanner';
import clsx from 'clsx';
import { Button } from '@/common/components/ui';
import { 
  HomeIcon,
  PrinterIcon,
  LayersIcon,
  SettingsIcon,
  MenuIcon,
  AccountCheckIcon,
  AccountIcon,
  LogoutIcon,
  LoginIcon,
  ChevronDownIcon,
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
} from '@/common/components/icons/MdiIcons';
import { PrintFarmerLogoIcon } from '@/common/components/icons/PrintFarmerLogoIcon';
import { useAuth } from '@/features/auth/hooks/useAuth';
import { useSlicer } from '@/hooks/useSlicer';
import { useSystemCapabilities } from '@/common/hooks/useSystemCapabilities';
import { PlatformBanner } from '@/common/components/PlatformBanner';
import { useSignalRConnection } from '@/common/hooks/useSignalR';
import { Suspense, useEffect, useMemo, useRef, useState, type CSSProperties } from 'react';
import { useAllAutoDispatchStatuses } from '@/features/printers/hooks/useAutoDispatch';
import { requiresBedClearConfirmation } from '@/common/utils/printerStateDisplay';
import type { AutoDispatchStatus } from '@/types/api';
import { RouteErrorBoundary } from '@/common/components/ErrorBoundary';
import { NavLink, Outlet, useLocation, useNavigate } from 'react-router';
import DebugPrinterSignalRPanel from '@/features/printers/components/DebugPrinterSignalRPanel';
import { printerSignalRService } from '@/services/printer-signalr';
import { NfcPairingModal } from '@/features/nfc/components/NfcPairingModal';
import { useNfcPairingSession } from '@/features/nfc/hooks/useNfcPairingSession';
import { SystemPulsePill } from '@/features/system/components/SystemPulsePill';
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
  children?: NavigationItem[];
  isDivider?: false;
  isSectionHeader?: false;
}

interface NavigationDivider {
  name: string;
  isDivider: true;
}

interface NavigationSectionHeader {
  name: string;
  isSectionHeader: true;
  requiredRole?: string;
}

type NavigationElement = NavigationItem | NavigationDivider | NavigationSectionHeader;

const isDivider = (item: NavigationElement): item is NavigationDivider => 'isDivider' in item && item.isDivider === true;
const isSectionHeader = (item: NavigationElement): item is NavigationSectionHeader => 'isSectionHeader' in item && item.isSectionHeader === true;
const isNavigationItem = (item: NavigationElement): item is NavigationItem => !isDivider(item) && !isSectionHeader(item);

const navigation: NavigationElement[] = [
  // — Operations —
  { name: 'Operations', isSectionHeader: true },
  { name: 'Dashboard', href: '/dashboard', icon: HomeIcon },
  {
    name: 'Printers',
    href: '/printers',
    icon: PrinterIcon,
    requiredPermission: { resource: 'printers', action: 'read' }
  },
  {
    name: 'Files',
    href: '/files',
    icon: FolderOpenIcon,
    requiredPermission: { resource: 'models', action: 'read' }
  },
  {
    name: 'Projects',
    href: '/projects',
    icon: ClipboardListIcon,
    requiredPermission: { resource: 'models', action: 'read' }
  },
  {
    name: 'Slice',
    href: '/slicer',
    icon: BoxIcon,
    requiredPermission: { resource: 'models', action: 'read' },
    requiresSlicer: true,
    requiresSlicingCapability: true
  },
  {
    name: 'Print Queue',
    href: '/printQueue',
    icon: HistoryIcon,
    requiredPermission: { resource: 'printers', action: 'read' }
  },
  {
    name: 'Auto-Dispatch',
    href: '/auto-dispatch',
    icon: PlayIcon,
    requiredPermission: { resource: 'printers', action: 'read' }
  },

  // — Hardware —
  { name: 'Hardware', isSectionHeader: true },
  {
    name: 'Filament Inventory',
    href: '/spools',
    icon: SpoolIcon
  },
  {
    name: 'Locations',
    href: '/locations/dashboard',
    icon: LocationIcon
  },
 
  // — Management —
  { name: 'Management', isSectionHeader: true },
  {
    name: 'Maintenance',
    href: '/maintenance',
    icon: WrenchIcon,
    requiredPermission: { resource: 'printers', action: 'read' }
  },
  {
    name: 'Analytics',
    href: '/analytics',
    icon: TrendingUpIcon,
  },
  {
    name: 'Scheduling',
    href: '/scheduling',
    icon: CalendarIcon,
  },

  // — Admin —
  { name: 'Admin', isSectionHeader: true, requiredRole: 'farm_admin' },
  {
    name: 'Catalog',
    href: '/catalog',
    icon: LayersIcon,
    requiredRole: 'farm_admin'
  },
  {
    name: 'Settings',
    href: '/admin/settings',
    icon: GearIcon,
    requiredRole: 'farm_admin'
  },
  {
    name: 'Admin',
    href: '/admin/manage',
    icon: SettingsIcon,
    requiredRole: 'farm_admin'
  },

];

const MOBILE_TOP_BAR_HEIGHT_PX = 48;
const EXPANDED_RAIL_WIDTH_PX = 248;
const COLLAPSED_RAIL_WIDTH_PX = 64;
const NAVBAR_COLLAPSED_STORAGE_KEY = 'pf_navbar_collapsed';
const NAV_EXPANDED_STORAGE_KEY = 'pf_nav_expanded_v1';


export function Layout() {
  const { isConnected } = useSignalRConnection('printer');
  const { user, logout, isAuthenticated, hasRole, hasPermission } = useAuth();
  const { isSlicerAvailable } = useSlicer();
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
  const [hoveredNavItem, setHoveredNavItem] = useState<string | null>(null);

  // Persist navbar collapsed state
  useEffect(() => {
    localStorage.setItem(NAVBAR_COLLAPSED_STORAGE_KEY, JSON.stringify(navbarCollapsed));
  }, [navbarCollapsed]);

  // Filter navigation based on user permissions, slicer availability, and platform capabilities (stable memoization)
  const filteredNavigation = useMemo(() => {
    // Helper: check whether a nav item is hidden by platform capabilities.
    // Uses `!== false` so items stay visible before the query resolves.
    const isHiddenByCapabilities = (item: NavigationItem) => {
      if (item.requiresSlicingCapability && capabilities?.slicingEnabled === false) return true;
      if (item.requiresModelFiles && capabilities?.modelFilesEnabled === false) return true;
      return false;
    };

    if (!isAuthenticated) {
      // For non-authenticated users, show only public navigation (including section headers)
      return navigation.filter(item => {
        if (isDivider(item)) return true;
        if (isSectionHeader(item)) {
          if (item.requiredRole) return false;
          return true;
        }
        if (isHiddenByCapabilities(item)) return false;
        if (item.requiresSlicer && !isSlicerAvailable) return false;
        return !item.requiredRole && !item.requiredPermission;
      });
    }
    
    return navigation.filter(item => {
      if (isDivider(item)) return true;
      if (isSectionHeader(item)) {
        if (item.requiredRole && !hasRole(item.requiredRole)) return false;
        return true;
      }
      if (item.requiredRole && !hasRole(item.requiredRole)) return false;
      if (item.requiredPermission && !hasPermission(item.requiredPermission.resource, item.requiredPermission.action)) return false;
      if (isHiddenByCapabilities(item)) return false;
      if (item.requiresSlicer && !isSlicerAvailable) return false;
      return true;
    });
  }, [isAuthenticated, hasRole, hasPermission, isSlicerAvailable, capabilities]); // Include all dependencies

  const handleLogout = async () => {
    await logout();
    setUserMenuOpen(false);
  };

  // Track which parent menus are expanded (with persistence)
  // Initialize from localStorage with auto-expand for active routes
  const [expanded, setExpanded] = useState<Record<string, boolean>>(() => {
    const path = location.pathname;
    try {
      const raw = localStorage.getItem(NAV_EXPANDED_STORAGE_KEY);
      let parsed: Record<string, boolean> = {};
      if (raw) {
        const storedData = JSON.parse(raw);
        if (storedData && typeof storedData === 'object') {
          parsed = storedData;
        }
      }
      
      // Auto-expand groups containing current route during initialization
      // Note: filteredNavigation not available yet, so use navigation directly
      for (const item of navigation) {
        if (isNavigationItem(item) && item.children) {
          const hasActiveChild = item.children.some(c => path.startsWith(c.href));
          if (hasActiveChild && !(item.name in parsed)) {
            parsed[item.name] = true;
          }
        }
      }
      
      return parsed;
    } catch {
      // If parsing fails, at least auto-expand current route
      const autoExpanded: Record<string, boolean> = {};
      for (const item of navigation) {
        if (isNavigationItem(item) && item.children) {
          const hasActiveChild = item.children.some(c => path.startsWith(c.href));
          if (hasActiveChild) {
            autoExpanded[item.name] = true;
          }
        }
      }
      return autoExpanded;
    }
  });
  const [announcement, setAnnouncement] = useState('');
  const announcementTimer = useRef<number | null>(null);

  useEffect(() => {
    return () => {
      if (announcementTimer.current) {
        clearTimeout(announcementTimer.current);
      }
    };
  }, []);

  useEffect(() => {
    if (!sidebarOpen && !userMenuOpen) {
      return;
    }

    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        setSidebarOpen(false);
        setUserMenuOpen(false);
      }
    };

    window.addEventListener('keydown', handleKeyDown);
    return () => window.removeEventListener('keydown', handleKeyDown);
  }, [sidebarOpen, userMenuOpen]);

  const toggleExpand = (name: string) => {
    setExpanded((prev: Record<string, boolean>) => {
      const currentValue = prev[name];
      const nextValue = !currentValue;
      
      // Only update if value actually changes
      if (currentValue === nextValue) {
        return prev; // No change, return same object to prevent re-render
      }
      
      const next = { ...prev, [name]: nextValue };
      
      // Find item to get child count (from filtered list so it's permission-safe)
      const itemDef = filteredNavigation.find(i => i.name === name);
      const childCount = itemDef && isNavigationItem(itemDef) ? itemDef.children?.length ?? 0 : 0;
      const message = nextValue
        ? `${name} section expanded. ${childCount} item${childCount === 1 ? '' : 's'}.`
        : `${name} section collapsed.`;
      
      setAnnouncement(message);
      
      // Clear previous timer
      if (announcementTimer.current) {
        clearTimeout(announcementTimer.current);
      }
      announcementTimer.current = window.setTimeout(() => {
        setAnnouncement('');
        announcementTimer.current = null;
      }, 2500);
      
      return next;
    });
  };

  const switchToRegister = () => {
    setShowLoginModal(false);
    setShowRegisterModal(true);
  };

  const switchToLogin = () => {
    setShowRegisterModal(false);
    setShowLoginModal(true);
  };

  // (Removed unused prefersReducedMotion calculation to satisfy lint)
  
  // Compute merged expanded state: user selections + auto-expand for active route
  const computedExpanded = useMemo(() => {
    const path = location.pathname;
    const result = { ...expanded };
    
    // Auto-expand groups containing current route
    for (const item of filteredNavigation) {
      if (isNavigationItem(item) && item.children) {
        const hasActiveChild = item.children.some(c => path.startsWith(c.href));
        // Only auto-expand if user hasn't explicitly set it
        if (hasActiveChild && expanded[item.name] === undefined) {
          result[item.name] = true;
        }
      }
    }
    
    return result;
  }, [location.pathname, expanded, filteredNavigation]);

  // Persist expanded changes to localStorage
  useEffect(() => {
    try {
      localStorage.setItem(NAV_EXPANDED_STORAGE_KEY, JSON.stringify(expanded));
    } catch {
      // ignore
    }
  }, [expanded]);

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
      <div className="sr-only" aria-live="polite" role="status">{announcement}</div>

      <div
        className="flex h-full min-h-0 flex-col lg:grid lg:grid-cols-[var(--pf-layout-rail-width)_minmax(0,1fr)]"
        style={desktopShellStyle}
      >
        <header className="z-20 flex h-12 shrink-0 items-center justify-between border-b border-pf-border bg-pf-bg-1 px-3 lg:hidden">
          <div className="flex items-center gap-2">
            <Button
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
            {isAuthenticated && <NotificationBell />}
            <div className="hidden sm:block">
              <SystemPulsePill />
            </div>

            <div className="flex items-center gap-2 rounded-full border border-pf-border bg-pf-bg-2 px-2 py-1">
              <span className={clsx('h-2 w-2 rounded-full', isConnected ? 'bg-pf-success' : 'bg-pf-error')} aria-hidden="true" />
              <span className="hidden text-xs text-pf-text-tertiary sm:inline">{isConnected ? 'Connected' : 'Disconnected'}</span>
            </div>

            <div className="relative z-50">
              <Button
                type="button"
                variant="subtle"
                size="sm"
                className="flex h-9 w-9 justify-center px-0"
                aria-expanded={userMenuOpen}
                aria-haspopup="menu"
                onClick={() => setUserMenuOpen(prev => !prev)}
                iconCenter={isAuthenticated && user ? (
                  <AccountCheckIcon className="h-5 w-5 text-pf-success" />
                ) : (
                  <AccountIcon className="h-5 w-5 text-pf-text-muted" />
                )}
              />

              {userMenuOpen && (
                <div className="absolute right-0 mt-2 w-64 rounded-md border border-pf-border bg-pf-bg-1 shadow-lg">
                  <div className="py-1">
                    {isAuthenticated && user ? (
                      <>
                        <div className="border-b border-pf-border px-4 py-2 text-sm text-pf-text-secondary">
                          Signed in as <strong>{user.username}</strong>
                        </div>
                        <Button
                          type="button"
                          onClick={() => {
                            navigate('/settings');
                            setUserMenuOpen(false);
                          }}
                          variant="subtle"
                          size="sm"
                          className="w-full justify-start!"
                          iconLeft={<SettingsIcon className="h-4 w-4" />}
                        >
                          Preferences
                        </Button>
                        <Button
                          type="button"
                          onClick={handleLogout}
                          variant="subtle"
                          size="sm"
                          className="w-full justify-start!"
                          iconLeft={<LogoutIcon className="h-4 w-4" />}
                        >
                          Sign out
                        </Button>
                      </>
                    ) : (
                      <>
                        <Button
                          type="button"
                          onClick={() => {
                            setShowLoginModal(true);
                            setUserMenuOpen(false);
                          }}
                          variant="subtle"
                          size="sm"
                          className="w-full justify-start!"
                          iconLeft={<LoginIcon className="h-4 w-4" />}
                        >
                          Sign In
                        </Button>
                        <Button
                          type="button"
                          onClick={() => {
                            setShowRegisterModal(true);
                            setUserMenuOpen(false);
                          }}
                          variant="subtle"
                          size="sm"
                          className="flex w-full items-center justify-start!"
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
        </header>

        <div
          className={clsx(
            'fixed inset-x-0 bottom-0 z-50 lg:hidden',
            sidebarOpen ? 'pointer-events-auto' : 'pointer-events-none'
          )}
          style={{ top: `${MOBILE_TOP_BAR_HEIGHT_PX}px` }}
          aria-hidden={!sidebarOpen}
        >
          <div
            className={clsx(
              'absolute inset-0 bg-black/60 transition-opacity duration-200',
              sidebarOpen ? 'opacity-100' : 'opacity-0'
            )}
            onClick={() => setSidebarOpen(false)}
          />

          <div
            id="mobile-navigation-drawer"
            role="dialog"
            aria-modal="true"
            aria-label="Navigation menu"
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
                  <div className="text-xs text-pf-text-tertiary">Shell scaffold</div>
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

            <nav className="flex-1 min-h-0 space-y-1 overflow-y-auto px-2 py-3" aria-label="Primary">
              {filteredNavigation.map((item, index) => {
                if (isDivider(item)) {
                  return (
                    <div key={`divider-${item.name || index}`} className="my-1.5">
                      <div className="border-t border-pf-border"></div>
                    </div>
                  );
                }

                if (isSectionHeader(item)) {
                  return (
                    <div key={`section-${item.name}`} className={`px-2 pb-1 ${index === 0 ? 'pt-0' : 'pt-4'}`}>
                      <div className="text-xs font-semibold uppercase tracking-wider text-pf-text-tertiary">{item.name}</div>
                    </div>
                  );
                }

                const navItem = item as NavigationItem;
                const Icon = navItem.icon;
                const isExpanded = !!computedExpanded[navItem.name];
                const hasChildren = !!navItem.children?.length;

                return (
                  <div key={navItem.href} className="relative flex flex-col">
                    {hasChildren ? (
                      <details open={isExpanded} className="group">
                        <summary
                          className="flex cursor-pointer list-none items-center rounded-md px-2 py-2 text-sm font-medium text-pf-text-primary focus-visible:outline-hidden focus-visible:ring-2 focus-visible:ring-pf-accent hover:bg-pf-bg-2 hover:text-pf-text-light"
                          onClick={event => {
                            event.preventDefault();
                            toggleExpand(navItem.name);
                          }}
                          tabIndex={0}
                          role="button"
                        >
                          <Icon className="h-5 w-5 shrink-0" />
                          <span className="ml-3 flex-1 text-left">{navItem.name}</span>
                          <ChevronDownIcon className={clsx('ml-2 h-4 w-4 transition-transform duration-200', isExpanded && 'rotate-90')} aria-hidden="true" />
                        </summary>
                        <div className="mt-0.5 ml-6 space-y-0.5">
                          {navItem.children!.map((child: NavigationItem) => {
                            const ChildIcon = child.icon;
                            return (
                              <NavLink
                                key={child.href}
                                to={child.href}
                                onClick={() => setSidebarOpen(false)}
                                className={({ isActive }: { isActive: boolean }) =>
                                  clsx(
                                    'group flex items-center rounded-md px-3 py-1.5 text-sm transition-colors focus-visible:outline-hidden focus-visible:ring-2 focus-visible:ring-pf-accent',
                                    isActive
                                      ? 'bg-pf-bg-2 text-pf-accent'
                                      : 'text-pf-text-secondary hover:bg-pf-bg-2 hover:text-pf-text-primary'
                                  )
                                }
                                end={child.href === '/harvest'}
                              >
                                <ChildIcon className="mr-2 h-4 w-4 shrink-0" />
                                {child.name}
                              </NavLink>
                            );
                          })}
                        </div>
                      </details>
                    ) : (
                      <NavLink
                        to={navItem.href}
                        end
                        onClick={() => setSidebarOpen(false)}
                        className={({ isActive }: { isActive: boolean }) =>
                          clsx(
                            'group flex items-center rounded-md px-2 py-2 text-sm font-medium transition-colors focus-visible:outline-hidden focus-visible:ring-2 focus-visible:ring-pf-accent',
                            isActive
                              ? 'border-l-3 border-pf-accent bg-pf-bg-2 font-semibold text-pf-accent'
                              : 'text-pf-text-primary hover:bg-pf-bg-2 hover:text-pf-text-light'
                          )
                        }
                      >
                        <Icon className="h-5 w-5 shrink-0" />
                        <span className="ml-3 flex-1 text-left">{navItem.name}</span>
                      </NavLink>
                    )}
                  </div>
                );
              })}
            </nav>
          </div>
        </div>

        <aside
          className="hidden h-screen min-h-0 border-r border-pf-border bg-pf-bg-1 shadow-[12px_0_32px_rgba(0,0,0,0.16)] lg:flex"
          aria-label="Primary navigation rail"
        >
          <div className="flex h-full min-h-0 w-full flex-col">
            <div className={clsx('border-b border-pf-border', navbarCollapsed ? 'px-2 py-3' : 'px-4 py-4')}>
              <div className={clsx('flex items-center', navbarCollapsed ? 'justify-center' : 'justify-between gap-3')}>
                <div className={clsx('flex min-w-0 items-center', navbarCollapsed ? 'justify-center' : 'gap-3')}>
                  <PrintFarmerLogoIcon decorative className="h-8 w-8 shrink-0 text-pf-accent" />
                  {!navbarCollapsed && (
                    <div className="min-w-0">
                      <div className="truncate text-lg font-bold uppercase tracking-wide text-pf-text-primary font-bebas">PrintFarmer</div>
                      <div className="text-xs text-pf-text-tertiary">Two-pane shell</div>
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

            <nav className="flex-1 min-h-0 space-y-1 overflow-y-auto px-2 py-3" aria-label="Primary">
              {filteredNavigation.map((item, index) => {
                if (isDivider(item)) {
                  return (
                    <div key={`divider-${item.name || index}`} className="my-1.5">
                      <div className="border-t border-pf-border"></div>
                    </div>
                  );
                }

                if (isSectionHeader(item)) {
                  if (navbarCollapsed) {
                    return (
                      <div key={`section-${item.name}`} className="my-1.5">
                        <div className="border-t border-pf-border"></div>
                      </div>
                    );
                  }

                  return (
                    <div key={`section-${item.name}`} className={`px-2 pb-1 ${index === 0 ? 'pt-0' : 'pt-4'}`}>
                      <div className="text-xs font-semibold uppercase tracking-wider text-pf-text-tertiary">{item.name}</div>
                    </div>
                  );
                }

                const navItem = item as NavigationItem;
                const Icon = navItem.icon;
                const isExpanded = !!computedExpanded[navItem.name];
                const isHovered = hoveredNavItem === navItem.name;
                const hasChildren = !!navItem.children?.length;

                return (
                  <div
                    key={navItem.href}
                    className="relative flex flex-col"
                    onMouseEnter={() => navbarCollapsed && hasChildren && setHoveredNavItem(navItem.name)}
                    onMouseLeave={() => setHoveredNavItem(null)}
                  >
                    {hasChildren ? (
                      <details open={isExpanded} className="group">
                        <summary
                          className={clsx(
                            'flex cursor-pointer list-none items-center rounded-md text-sm font-medium text-pf-text-primary focus-visible:outline-hidden focus-visible:ring-2 focus-visible:ring-pf-accent hover:bg-pf-bg-2 hover:text-pf-text-light',
                            navbarCollapsed ? 'justify-center px-1.5 py-2' : 'px-2 py-2'
                          )}
                          title={navbarCollapsed ? item.name : undefined}
                          onClick={event => {
                            event.preventDefault();
                            toggleExpand(item.name);
                          }}
                          tabIndex={0}
                          role="button"
                        >
                          <Icon className="h-5 w-5 shrink-0" />
                          {!navbarCollapsed && (
                            <>
                              <span className="ml-3 flex-1 text-left">{item.name}</span>
                              <ChevronDownIcon className={clsx('ml-2 h-4 w-4 transition-transform duration-200', isExpanded && 'rotate-90')} aria-hidden="true" />
                            </>
                          )}
                        </summary>
                        {!navbarCollapsed && (
                          <div className="mt-0.5 ml-6 space-y-0.5">
                            {item.children!.map(child => {
                              const ChildIcon = child.icon;
                              return (
                                <NavLink
                                  key={child.href}
                                  to={child.href}
                                  className={({ isActive }: { isActive: boolean }) =>
                                    clsx(
                                      'group flex items-center rounded-md px-3 py-1.5 text-sm transition-colors focus-visible:outline-hidden focus-visible:ring-2 focus-visible:ring-pf-accent',
                                      isActive
                                        ? 'bg-pf-bg-2 text-pf-accent'
                                        : 'text-pf-text-secondary hover:bg-pf-bg-2 hover:text-pf-text-primary'
                                    )
                                  }
                                  end={child.href === '/harvest'}
                                >
                                  <ChildIcon className="mr-2 h-4 w-4 shrink-0" />
                                  {child.name}
                                </NavLink>
                              );
                            })}
                          </div>
                        )}
                      </details>
                    ) : (
                      <NavLink
                        to={navItem.href}
                        end
                        className={({ isActive }: { isActive: boolean }) =>
                          clsx(
                            'group flex items-center rounded-md text-sm font-medium transition-colors focus-visible:outline-hidden focus-visible:ring-2 focus-visible:ring-pf-accent',
                            navbarCollapsed ? 'justify-center px-1.5 py-2' : 'px-2 py-2',
                            isActive
                              ? clsx('bg-pf-bg-2 font-semibold text-pf-accent', !navbarCollapsed && 'border-l-3 border-pf-accent')
                              : 'text-pf-text-primary hover:bg-pf-bg-2 hover:text-pf-text-light'
                          )
                        }
                        title={navbarCollapsed ? navItem.name : undefined}
                      >
                        <Icon className="h-5 w-5 shrink-0" />
                        {!navbarCollapsed && <span className="ml-3 flex-1 text-left">{navItem.name}</span>}
                      </NavLink>
                    )}

                    {navbarCollapsed && hasChildren && isHovered && (
                      <div className="absolute top-0 left-full z-50 ml-2 w-48 rounded-md border border-pf-border bg-pf-bg-1 shadow-lg">
                        <div className="py-1">
                          <div className="border-b border-pf-border px-3 py-2 text-xs font-semibold text-pf-text-tertiary">
                            {navItem.name}
                          </div>
                          {navItem.children!.map((child: NavigationItem) => {
                            const ChildIcon = child.icon;
                            return (
                              <NavLink
                                key={child.href}
                                to={child.href}
                                className={({ isActive }: { isActive: boolean }) =>
                                  clsx(
                                    'flex items-center px-3 py-2 text-sm transition-colors',
                                    isActive
                                      ? 'bg-pf-bg-2 text-pf-accent'
                                      : 'text-pf-text-secondary hover:bg-pf-bg-2 hover:text-pf-text-primary'
                                  )
                                }
                                end={child.href === '/harvest'}
                              >
                                <ChildIcon className="mr-2 h-4 w-4 shrink-0" />
                                {child.name}
                              </NavLink>
                            );
                          })}
                        </div>
                      </div>
                    )}
                  </div>
                );
              })}
            </nav>

            <div className="shrink-0 border-t border-pf-border p-2">
              {!navbarCollapsed && (
                <div className="mb-2 flex items-center justify-between gap-2 rounded-lg border border-pf-border bg-pf-bg-2 px-3 py-2">
                  <SystemPulsePill />
                  {pendingAttentionCount > 0 && (
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
                  )}
                </div>
              )}

              <div className={clsx('flex items-center gap-2', navbarCollapsed ? 'flex-col' : 'justify-between')}>
                <div className={clsx('flex items-center gap-2', navbarCollapsed && 'flex-col')}>
                  {isAuthenticated && <TasksBadge />}
                  {isAuthenticated && <NotificationBell />}
                </div>

                <div className="relative z-50 flex items-center gap-2">
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

                  <Button
                    type="button"
                    variant="subtle"
                    size="sm"
                    className={clsx(navbarCollapsed ? 'h-9 w-9 justify-center px-0' : 'h-9 px-3')}
                    aria-expanded={userMenuOpen}
                    aria-haspopup="menu"
                    onClick={() => setUserMenuOpen(prev => !prev)}
                    iconLeft={isAuthenticated && user ? (
                      <AccountCheckIcon className="h-5 w-5 text-pf-success" />
                    ) : (
                      <AccountIcon className="h-5 w-5 text-pf-text-muted" />
                    )}
                  >
                    {!navbarCollapsed && <span>{isAuthenticated && user ? user.username : 'Guest'}</span>}
                  </Button>

                  {userMenuOpen && (
                    <div className="absolute right-0 bottom-full mb-2 w-64 rounded-md border border-pf-border bg-pf-bg-1 shadow-lg">
                      <div className="py-1">
                        {isAuthenticated && user ? (
                          <>
                            <div className="border-b border-pf-border px-4 py-2 text-sm text-pf-text-secondary">
                              Signed in as <strong>{user.username}</strong>
                            </div>
                            <Button
                              type="button"
                              onClick={() => {
                                navigate('/settings');
                                setUserMenuOpen(false);
                              }}
                              variant="subtle"
                              size="sm"
                              className="w-full justify-start!"
                              iconLeft={<SettingsIcon className="h-4 w-4" />}
                            >
                              Preferences
                            </Button>
                            <Button
                              type="button"
                              onClick={handleLogout}
                              variant="subtle"
                              size="sm"
                              className="w-full justify-start!"
                              iconLeft={<LogoutIcon className="h-4 w-4" />}
                            >
                              Sign out
                            </Button>
                          </>
                        ) : (
                          <>
                            <Button
                              type="button"
                              onClick={() => {
                                setShowLoginModal(true);
                                setUserMenuOpen(false);
                              }}
                              variant="subtle"
                              size="sm"
                              className="w-full justify-start!"
                              iconLeft={<LoginIcon className="h-4 w-4" />}
                            >
                              Sign In
                            </Button>
                            <Button
                              type="button"
                              onClick={() => {
                                setShowRegisterModal(true);
                                setUserMenuOpen(false);
                              }}
                              variant="subtle"
                              size="sm"
                              className="flex w-full items-center justify-start!"
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

              <div className="mt-2">
                <Button
                  type="button"
                  aria-label={navbarCollapsed ? 'Expand navigation rail' : 'Collapse navigation rail'}
                  title={navbarCollapsed ? 'Expand navigation rail' : 'Collapse navigation rail'}
                  variant="subtle"
                  size="sm"
                  className="w-full justify-center"
                  onClick={() => setNavbarCollapsed(prev => !prev)}
                  iconCenter={navbarCollapsed ? <ChevronRightIcon className="h-5 w-5" /> : <ChevronLeftIcon className="h-5 w-5" />}
                />
              </div>
            </div>
          </div>
        </aside>

        <main
          id="main-content"
          data-main-content
          tabIndex={-1}
          className="flex-1 min-h-0 overflow-y-auto bg-pf-bg-0 focus:outline-hidden lg:h-screen"
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
          className="fixed inset-0 z-30 pointer-events-auto"
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

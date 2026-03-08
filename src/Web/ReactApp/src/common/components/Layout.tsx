import { LoginModal } from '@/features/auth/components/LoginModal';
import { RegisterModal } from '@/features/auth/components/RegisterModal';
import { EmailConfirmationBanner } from '@/features/auth/components/EmailConfirmationBanner';
import { TasksBadge } from '@/features/tasks';
import { useTheme, Theme } from '@/contexts/ThemeContext';
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
  UsersIcon,
  GearIcon,
  FolderOpenIcon,
  HistoryIcon,
  TagIcon,
  WrenchIcon,
  TrendingUpIcon,
  LocationIcon,
  KeyIcon,
  DatabaseIcon,
  CheckIcon,
  CameraIcon,
  NfcIcon,
  ChartIcon,
  ExternalLinkIcon,
} from '@/common/components/icons/MdiIcons';
import { useAuth } from '@/features/auth/hooks/useAuth';
import { useSlicer } from '@/hooks/useSlicer';
import { useSignalRConnection } from '@/common/hooks/useSignalR';
import { useEffect, useMemo, useRef, useState } from 'react';
import { NavLink, Outlet, useLocation } from 'react-router';
import DebugPrinterSignalRPanel from '@/features/printers/components/DebugPrinterSignalRPanel';
import { printerSignalRService } from '@/services/printer-signalr';
import { BoxIcon, SpoolIcon } from 'lucide-react';
// Layout now uses <Outlet /> for nested routes

interface NavigationItem {
  name: string;
  href: string;
  icon: React.ComponentType<{ className?: string }>;
  requiredPermission?: { resource: string; action: string };
  requiredRole?: string;
  requiresSlicer?: boolean;
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
    name: 'Slice',
    href: '/jobs/new',
    icon: BoxIcon,
    requiredPermission: { resource: 'models', action: 'read' },
    requiresSlicer: true
  },
  {
    name: 'Print Queue',
    href: '/printQueue',
    icon: HistoryIcon,
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
    name: 'Cameras',
    href: '/cameras',
    icon: CameraIcon
  },
  {
    name: 'NFC Devices',
    href: '/nfc-devices',
    icon: NfcIcon
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
    name: 'Statistics',
    href: '/statistics',
    icon: ChartIcon,
  },
  {
    name: 'API Keys',
    href: '/profile/api-keys',
    icon: KeyIcon
  },

  // — Admin —
  { name: 'Admin', isSectionHeader: true, requiredRole: 'farm_admin' },
  {
    name: 'Locations',
    href: '/locations',
    icon: LocationIcon,
    requiredRole: 'farm_admin'
  },
  {
    name: 'Catalog',
    href: '/catalog',
    icon: LayersIcon,
    requiredRole: 'farm_admin'
  },
  {
    name: 'User Accounts',
    href: '/users',
    icon: UsersIcon,
    requiredRole: 'farm_admin'
  },
  {
    name: 'Tags',
    href: '/admin/tags',
    icon: TagIcon,
    requiredRole: 'farm_admin'
  },
  {
    name: 'Webhooks',
    href: '/admin/webhooks',
    icon: ExternalLinkIcon,
    requiredRole: 'farm_admin'
  },
  {
    name: 'Workers',
    href: '/admin/workers',
    icon: WrenchIcon,
    requiredRole: 'farm_admin',
    requiresSlicer: true
  },
  {
    name: 'Slicer Profiles',
    href: '/admin/slicer-profiles',
    icon: SettingsIcon,
    requiredRole: 'farm_admin',
    requiresSlicer: true
  },
  {
    name: 'System',
    href: '/admin/system',
    icon: TrendingUpIcon,
    requiredRole: 'farm_admin'
  },
  {
    name: 'Data Management',
    href: '/admin/data',
    icon: DatabaseIcon,
    requiredRole: 'farm_admin'
  },
  {
    name: 'Settings',
    href: '/settings',
    icon: GearIcon,
    requiredRole: 'farm_admin'
  },
];

export function Layout() {
  const { isConnected } = useSignalRConnection('printer');
  const { user, logout, isAuthenticated, hasRole, hasPermission } = useAuth();
  const { isSlicerAvailable } = useSlicer();
  const { theme, setTheme } = useTheme();
  const location = useLocation();
  // Debug: log current pathname to ensure re-render on navigation
  useEffect(() => {
    // location change effect (debug removed)
  }, [location.pathname]);

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
    const saved = localStorage.getItem('pf_navbar_collapsed');
    return saved ? JSON.parse(saved) : false;
  });
  const [hoveredNavItem, setHoveredNavItem] = useState<string | null>(null);

  // Persist navbar collapsed state
  useEffect(() => {
    localStorage.setItem('pf_navbar_collapsed', JSON.stringify(navbarCollapsed));
  }, [navbarCollapsed]);

  // Filter navigation based on user permissions and slicer availability (stable memoization)
  const filteredNavigation = useMemo(() => {
    if (!isAuthenticated) {
      return navigation.filter(item => {
        if (isDivider(item)) return true;
        if (isSectionHeader(item)) {
          if (item.requiredRole) return false;
          return true;
        }
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
      if (item.requiresSlicer && !isSlicerAvailable) return false;
      return true;
    });
  }, [isAuthenticated, hasRole, hasPermission, isSlicerAvailable]); // Include all dependencies

  const handleLogout = async () => {
    await logout();
    setUserMenuOpen(false);
  };

  // Key for persisting expanded nav groups
  const LOCAL_STORAGE_KEY = 'pf_nav_expanded_v1';

  // Track which parent menus are expanded (with persistence)
  // Initialize from localStorage with auto-expand for active routes
  const [expanded, setExpanded] = useState<Record<string, boolean>>(() => {
    const path = location.pathname;
    try {
      const raw = localStorage.getItem(LOCAL_STORAGE_KEY);
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
        if (!isDivider(item) && !isSectionHeader(item) && item.children) {
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
        if (!isDivider(item) && !isSectionHeader(item) && item.children) {
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
      const childCount = itemDef && !isDivider(itemDef) && !isSectionHeader(itemDef) ? itemDef.children?.length ?? 0 : 0;
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
      if (!isDivider(item) && !isSectionHeader(item) && item.children) {
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
      localStorage.setItem(LOCAL_STORAGE_KEY, JSON.stringify(expanded));
    } catch {
      // ignore
    }
  }, [expanded]);

  return (
    <div className="flex flex-col h-screen bg-pf-bg-0">
      {/* Live region for accessibility announcements */}
      <div className="sr-only" aria-live="polite" role="status">{announcement}</div>
      {/* Top Header Bar */}
      <header className="bg-pf-bg-1 border-b border-pf-border h-12 shrink-0 z-50">
        <div className="flex items-center justify-between h-12 px-3">
          {/* Left side - App branding */}
          <div className="flex items-center space-x-4">
            {/* Mobile menu button - toggles sidebar */}
            <Button
              type="button"
              aria-label={sidebarOpen ? "Close navigation menu" : "Open navigation menu"}
              title={sidebarOpen ? "Close navigation menu" : "Open navigation menu"}
              variant="subtle"
              size="sm"
              className="lg:hidden"
              onClick={() => setSidebarOpen(prev => !prev)}
            >
              <MenuIcon className="h-5 w-5" />
            </Button>

            {/* App logo and name */}
            <div className="flex items-center space-x-2">
              <img 
                src="/printfarmer-logo.svg" 
                alt="PrintFarmer Logo" 
                className="w-7 h-7" 
              />
              <h1 className="text-lg font-bold text-pf-text-primary font-bebas uppercase">PrintFarmer</h1>
            </div>
          </div>

          {/* Right side - Status and user */}
          <div className="flex items-center space-x-3">
            {/* Connection status */}
            <div className="flex items-center space-x-2">
              <div 
                className={`h-2 w-2 rounded-full ${
                  isConnected ? 'bg-pf-success' : 'bg-pf-error'
                }`}
              />
              <span className="text-sm text-pf-text-tertiary">
                {isConnected ? 'Connected' : 'Disconnected'}
              </span>
            </div>

            {/* Pending Tasks Badge */}
            {isAuthenticated && <TasksBadge />}

            {/* User menu */}
            <div className="relative">
              <Button
                type="button"
                variant="subtle"
                size="sm"
                className="flex items-center space-x-2"
                onClick={() => setUserMenuOpen(!userMenuOpen)}
              >
                {isAuthenticated && user ? (
                  <>
                    <AccountCheckIcon className="h-5 w-5 text-pf-success" />
                  </>
                ) : (
                  <>
                    <AccountIcon className="h-5 w-5 text-pf-text-muted" />
                    <span className="hidden sm:block text-sm">Guest</span>
                  </>
                )}
              </Button>

              {/* User dropdown menu */}
              {userMenuOpen && (
                <div className="absolute right-0 mt-2 w-64 bg-pf-bg-1 border border-pf-border rounded-md shadow-lg z-10">
                  <div className="py-1">
                    {isAuthenticated && user ? (
                      <>
                          <div className="px-4 py-2 text-sm text-pf-text-secondary border-b border-pf-border">
                          Signed in as <strong>{user.username}</strong>
                        </div>
                        <Button
                          type="button"
                          onClick={() => setUserMenuOpen(false)}
                          variant="subtle"
                          size="sm"
                          className="flex items-center gap-2 w-full !justify-start"
                        >
                          <SettingsIcon className="h-4 w-4" />
                          <span>Profile</span>
                        </Button>
                        <Button
                          type="button"
                          onClick={handleLogout}
                          variant="subtle"
                          size="sm"
                          className="flex items-center gap-2 w-full !justify-start"
                        >
                          <LogoutIcon className="h-4 w-4" />
                          <span>Sign out</span>
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
                          className="flex items-center gap-2 w-full !justify-start"
                        >
                          <LoginIcon className="h-4 w-4" />
                          <span>Sign In</span>
                        </Button>
                        <Button
                          type="button"
                          onClick={() => {
                            setShowRegisterModal(true);
                            setUserMenuOpen(false);
                          }}
                          variant="subtle"
                          size="sm"
                          className="flex items-center w-full !justify-start"
                        >
                          Register
                        </Button>
                      </>
                    )}

                    {/* Theme Selection - available to all users */}
                    <div className="border-t border-pf-border mt-1 pt-1">
                      <div className="px-4 py-2 text-xs font-medium text-pf-text-secondary uppercase tracking-wider">
                        Theme
                      </div>
                      {([
                        { value: 'github-dark' as Theme, label: 'GitHub Dark', desc: 'Dark theme inspired by GitHub' },
                        { value: 'printfarmer-dark' as Theme, label: 'PrintFarmer Dark', desc: 'Original dark theme' },
                        { value: 'light' as Theme, label: 'Light', desc: 'Light theme for bright environments' },
                      ]).map((t) => (
                        <Button
                          key={t.value}
                          variant="subtle"
                          onClick={() => setTheme(t.value)}
                          className={`w-full text-left px-4 py-2 text-sm hover:bg-pf-bg-2 flex items-center gap-2 justify-start ${
                            theme === t.value ? 'text-pf-accent' : 'text-pf-text-primary'
                          }`}
                        >
                          <span className="flex-1">
                            <span className="block">{t.label}</span>
                            <span className="block text-xs text-pf-text-secondary">{t.desc}</span>
                          </span>
                          {theme === t.value && (
                            <CheckIcon className="h-4 w-4 text-pf-accent shrink-0" />
                          )}
                        </Button>
                      ))}
                    </div>
                  </div>
                </div>
              )}
            </div>
          </div>
        </div>
      </header>

      <div className="flex flex-1 min-h-0 overflow-hidden">
        {/* Mobile sidebar overlay */}
        {sidebarOpen && (
          <div className="fixed inset-x-0 top-12 bottom-0 z-40 lg:hidden flex">
            {/* Backdrop - starts below header so hamburger button remains clickable */}
            <div className="fixed inset-x-0 top-12 bottom-0 bg-black/75" onClick={() => setSidebarOpen(false)} />
            
            {/* Sidebar panel - matches desktop sidebar exactly */}
            <div className="relative flex flex-col w-56 bg-pf-bg-1 border-r border-pf-border z-10 h-full">
              {/* Navigation - identical to desktop */}
              <nav className="flex-1 px-2 py-3 space-y-1 overflow-y-auto min-h-0">
                {filteredNavigation.map((item, index) => {
                  // Handle section headers
                  if (isSectionHeader(item)) {
                    return (
                      <div key={`section-${item.name}`} className={index === 0 ? 'pt-0' : 'pt-4'}>
                        <span className="px-3 text-xs uppercase tracking-wider font-semibold text-pf-text-tertiary">
                          {item.name}
                        </span>
                      </div>
                    );
                  }

                  // Handle dividers
                  if (isDivider(item)) {
                    return (
                      <div key={`divider-${item.name || index}`} className="my-1.5">
                        <div className="border-t border-pf-border"></div>
                      </div>
                    );
                  }

                  const navItem = item as NavigationItem;
                  const Icon = navItem.icon;
                  const isExpanded = !!computedExpanded[navItem.name];
                  
                  const hasChildren = !!navItem.children?.length;
                  return (
                    <div key={navItem.name} className="flex flex-col relative">
                      {hasChildren ? (
                        <details open={isExpanded} className="group">
                          <summary
                            className="flex items-center px-2 py-1.5 text-sm font-medium rounded-md cursor-pointer list-none focus-visible:outline-hidden focus-visible:ring-2 focus-visible:ring-pf-accent text-pf-text-primary hover:text-pf-text-light hover:bg-pf-bg-2"
                            onClick={e => {
                              e.preventDefault();
                              toggleExpand(navItem.name);
                            }}
                            tabIndex={0}
                            role="button"
                          >
                            <Icon className="h-5 w-5 shrink-0" />
                            <span className="flex-1 text-left ml-3">{navItem.name}</span>
                            <ChevronDownIcon className={`ml-2 h-4 w-4 transition-transform duration-200 ${isExpanded ? 'rotate-90' : ''}`} aria-hidden="true" />
                          </summary>
                          <div className="ml-6 space-y-0.5 mt-0.5">
                            {navItem.children!.map((child: NavigationItem) => {
                              const ChildIcon = child.icon;
                              return (
                                <NavLink
                                  key={child.name}
                                  to={child.href}
                                  onClick={() => { setSidebarOpen(false); }}
                                  className={({ isActive }: { isActive: boolean }) =>
                                    `group flex items-center px-3 py-1.5 text-sm rounded-md transition-colors focus-visible:outline-hidden focus-visible:ring-2 focus-visible:ring-pf-accent ${isActive
                                      ? 'bg-pf-bg-2 text-pf-text-primary border-r-2 border-pf-accent'
                                      : 'text-pf-text-secondary hover:text-pf-text-primary hover:bg-pf-bg-2'
                                    }`
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
                          onClick={() => { setSidebarOpen(false); }}
                          className={({ isActive }: { isActive: boolean }) =>
                            `group flex items-center px-2 py-1.5 text-sm font-medium rounded-md transition-colors focus-visible:outline-hidden focus-visible:ring-2 focus-visible:ring-pf-accent ${isActive
                              ? 'bg-pf-bg-2 text-pf-text-primary border-r-2 border-pf-accent'
                              : 'text-pf-text-primary hover:text-pf-text-light hover:bg-pf-bg-2'
                            }`
                          }
                        >
                          <Icon className="h-5 w-5 shrink-0" />
                          <span className="flex-1 text-left ml-3">{navItem.name}</span>
                        </NavLink>
                      )}
                    </div>
                  );
                })}
              </nav>
            </div>
          </div>
        )}

        {/* Desktop sidebar (elevated z-index to avoid being covered by user menu overlay) */}
        <aside className={`hidden lg:flex lg:shrink-0 z-40 transition-all duration-300 ${navbarCollapsed ? 'w-14' : 'w-56'}`}>
          <div className={`flex flex-col ${navbarCollapsed ? 'w-14' : 'w-56'} bg-pf-bg-1 border-r border-pf-border h-full min-h-0`}>
            <nav className="flex-1 px-2 py-3 space-y-1 overflow-y-auto min-h-0">
              {filteredNavigation.map((item, index) => {
                // Handle section headers
                if (isSectionHeader(item)) {
                  if (navbarCollapsed) {
                    return (
                      <div key={`section-${item.name}`} className={index === 0 ? '' : 'my-1.5'}>
                        {index > 0 && <div className="border-t border-pf-border"></div>}
                      </div>
                    );
                  }
                  return (
                    <div key={`section-${item.name}`} className={index === 0 ? 'pt-0' : 'pt-4'}>
                      <span className="px-3 text-xs uppercase tracking-wider font-semibold text-pf-text-tertiary">
                        {item.name}
                      </span>
                    </div>
                  );
                }

                if (isDivider(item)) {
                  return (
                    <div key={`divider-${item.name || index}`} className="my-1.5">
                      <div className="border-t border-pf-border"></div>
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
                    key={navItem.name} 
                    className="flex flex-col relative"
                    onMouseEnter={() => navbarCollapsed && hasChildren && setHoveredNavItem(navItem.name)}
                    onMouseLeave={() => setHoveredNavItem(null)}
                  >
                    {hasChildren ? (
                      <details open={isExpanded} className="group">
                        <summary
                          className={`flex items-center ${navbarCollapsed ? 'px-1.5 py-1.5 justify-center' : 'px-2 py-1.5'} text-sm font-medium rounded-md cursor-pointer list-none focus-visible:outline-hidden focus-visible:ring-2 focus-visible:ring-pf-accent text-pf-text-primary hover:text-pf-text-light hover:bg-pf-bg-2`}
                          title={navbarCollapsed ? item.name : undefined}
                          onClick={e => {
                            e.preventDefault(); // Prevent native toggle
                            toggleExpand(item.name);
                          }}
                          tabIndex={0}
                          role="button"
                        >
                          <Icon className="h-5 w-5 shrink-0" />
                          {!navbarCollapsed && (
                            <>
                              <span className="flex-1 text-left ml-3">{item.name}</span>
                              <ChevronDownIcon className={`ml-2 h-4 w-4 transition-transform duration-200 ${isExpanded ? 'rotate-90' : ''}`} aria-hidden="true" />
                            </>
                          )}
                        </summary>
                        {!navbarCollapsed && (
                          <div className="ml-6 space-y-0.5 mt-0.5">
                          {item.children!.map(child => {
                            const ChildIcon = child.icon;
                            return (
                              <NavLink
                                key={child.name}
                                to={child.href}
                                onClick={() => { /* child nav */ }}
                                className={({ isActive }: { isActive: boolean }) =>
                                  `group flex items-center px-3 py-1.5 text-sm rounded-md transition-colors focus-visible:outline-hidden focus-visible:ring-2 focus-visible:ring-pf-accent ${isActive
                                    ? 'bg-pf-bg-2 text-pf-text-primary border-r-2 border-pf-accent'
                                    : 'text-pf-text-secondary hover:text-pf-text-primary hover:bg-pf-bg-2'
                                  }`
                                }
                                end={child.href === '/harvest'} // Exact match for parent routes
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
                        onClick={() => { /* top-level nav */ }}
                        className={({ isActive }: { isActive: boolean }) =>
                          `group flex items-center ${navbarCollapsed ? 'px-1.5 py-1.5 justify-center' : 'px-2 py-1.5'} text-sm font-medium rounded-md transition-colors focus-visible:outline-hidden focus-visible:ring-2 focus-visible:ring-pf-accent ${isActive
                            ? 'bg-pf-bg-2 text-pf-text-primary border-r-2 border-pf-accent'
                            : 'text-pf-text-primary hover:text-pf-text-light hover:bg-pf-bg-2'
                          }`
                        }
                        title={navbarCollapsed ? navItem.name : undefined}
                      >
                        <Icon className="h-5 w-5 shrink-0" />
                        {!navbarCollapsed && <span className="flex-1 text-left ml-3">{navItem.name}</span>}
                      </NavLink>
                    )}

                    {/* Flyout menu for collapsed nav items with children */}
                    {navbarCollapsed && hasChildren && isHovered && (
                      <div className="absolute left-full top-0 ml-2 w-48 bg-pf-bg-1 border border-pf-border rounded-md shadow-lg z-50">
                        <div className="py-1">
                          <div className="px-3 py-2 text-xs font-semibold text-pf-text-tertiary border-b border-pf-border">
                            {navItem.name}
                          </div>
                          {navItem.children!.map((child: NavigationItem) => {
                            const ChildIcon = child.icon;
                            return (
                              <NavLink
                                key={child.name}
                                to={child.href}
                                className={({ isActive }: { isActive: boolean }) =>
                                  `flex items-center px-3 py-2 text-sm transition-colors ${isActive
                                    ? 'bg-pf-bg-2 text-pf-text-primary border-r-2 border-pf-accent'
                                    : 'text-pf-text-secondary hover:text-pf-text-primary hover:bg-pf-bg-2'
                                  }`
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

            {/* Navbar collapse toggle at bottom */}
            <div className="border-t border-pf-border p-2 shrink-0">
              <Button
                type="button"
                aria-label={navbarCollapsed ? "Expand navigation" : "Collapse navigation"}
                title={navbarCollapsed ? "Expand navigation" : "Collapse navigation"}
                variant="subtle"
                size="sm"
                className="w-full flex justify-center"
                onClick={() => setNavbarCollapsed(!navbarCollapsed)}
              >
                <MenuIcon className="h-5 w-5" />
              </Button>
            </div>
          </div>
        </aside>

        {/* Main content area */}
        <main className="flex-1 overflow-y-auto min-h-0">
          <EmailConfirmationBanner />
          <div className="pt-2 pr-2 pl-2">
            <Outlet />
          </div>
        </main>
      </div>

      {/* Click outside handler for user menu */}
      {userMenuOpen && (
        <div
          className="fixed inset-0 z-30 pointer-events-auto"
          onClick={() => setUserMenuOpen(false)}
          aria-hidden="true"
        />
      )}

      {/* Authentication Modals */}
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
    </div>
  );
}

// Footer with build info now moved out of header for persistent display
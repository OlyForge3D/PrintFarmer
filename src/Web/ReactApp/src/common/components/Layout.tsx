import { LoginModal } from '@/features/auth/components/LoginModal';
import { RegisterModal } from '@/features/auth/components/RegisterModal';
import { EmailConfirmationBanner } from '@/features/auth/components/EmailConfirmationBanner';
import { BuildInfo } from '@/common/components/BuildInfo';
import { ThemeToggle } from '@/common/components/ThemeToggle';
import { Button } from '@/common/components/ui';
import { 
  CloseIcon, 
  FileIcon,
  HomeIcon,
  PrinterIcon,
  CubeIcon,
  LayersIcon,
  SettingsIcon,
  MenuIcon,
  AccountCheckIcon,
  AccountIcon,
  LogoutIcon,
  LoginIcon,
  ChevronDownIcon,
  UsersIcon,
  GearIcon
} from '@/common/components/icons/MdiIcons';
import { useAuth } from '@/features/auth/hooks/useAuth';
import { useSignalRConnection } from '@/common/hooks/useSignalR';
import { useEffect, useMemo, useRef, useState } from 'react';
import { NavLink, Outlet, useLocation } from 'react-router-dom';
import DebugPrinterSignalRPanel from '@/features/printers/components/DebugPrinterSignalRPanel';
import { printerSignalRService } from '@/services/printer-signalr';
// Layout now uses <Outlet /> for nested routes

interface NavigationItem {
  name: string;
  href: string;
  icon: React.ComponentType<{ className?: string }>;
  requiredPermission?: { resource: string; action: string };
  requiredRole?: string;
  children?: NavigationItem[];
}

const navigation: NavigationItem[] = [
  { name: 'Dashboard', href: '/dashboard', icon: HomeIcon },
  {
    name: 'Printers',
    href: '/printers',
    icon: PrinterIcon,
    requiredPermission: { resource: 'printers', action: 'read' }
  },
  {
    name: '3D Models',
    href: '/models',
    icon: CubeIcon,
    requiredPermission: { resource: 'models', action: 'read' }
  },
  {
    name: 'Slice Jobs',
    href: '/jobs',
    icon: LayersIcon,
    requiredPermission: { resource: 'models', action: 'read' },
    children: [
      { name: 'Queue', href: '/jobs', icon: LayersIcon },
      { name: 'New Job', href: '/jobs/new', icon: FileIcon }
    ]
  },
  {
    name: 'G-code',
    href: '/harvest',
    icon: GearIcon,
    requiredPermission: { resource: 'gcode_harvest', action: 'read' },
    children: [
      { name: 'Harvest', href: '/harvest', icon: GearIcon },
      { name: 'History', href: '/harvest/history', icon: FileIcon },
      { name: 'G-Code Library', href: '/files', icon: FileIcon }
    ]
  },
  {
    name: 'Admin',
    href: '#',
    icon: SettingsIcon,
    requiredRole: 'farm_admin',
    children: [
      { name: 'Printers', href: '/admin/printers', icon: PrinterIcon },
      { name: 'Locations', href: '/admin/locations', icon: LayersIcon },
      { name: 'Workers', href: '/admin/workers', icon: GearIcon },
      { name: 'File Health', href: '/admin/file-health', icon: FileIcon },
      { name: 'Catalog', href: '/catalog', icon: LayersIcon },
      { name: 'Settings', href: '/settings', icon: SettingsIcon },
      { name: 'Spools', href: '/spools', icon: CubeIcon },
      { name: 'User Management', href: '/admin/users', icon: UsersIcon },
      { name: 'Observability', href: '/admin/observability', icon: GearIcon },
      { name: 'Slicer Dry Run', href: '/admin/slicer/dry-run', icon: FileIcon },
      { name: 'Slicer Job Status', href: '/admin/slicer/job-status', icon: FileIcon }
    ]
  },
  // Direct access (admin-only) to the new Slicer Profiles management page
  {
    name: 'Slicer Profiles',
    href: '/slicer-profiles',
    icon: FileIcon,
    requiredRole: 'farm_admin'
  },
];

export function Layout() {
  const { isConnected } = useSignalRConnection('printer');
  const { user, logout, isAuthenticated, hasRole, hasPermission } = useAuth();
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

  // Filter navigation based on user permissions (stable memoization)
  const filteredNavigation = useMemo(() => {
    if (!isAuthenticated) {
      // For non-authenticated users, show only public navigation
      return navigation.filter(item => !item.requiredRole && !item.requiredPermission);
    }
    
    return navigation.filter(item => {
      if (item.requiredRole && !hasRole(item.requiredRole)) return false;
      if (item.requiredPermission && !hasPermission(item.requiredPermission.resource, item.requiredPermission.action)) return false;
      return true;
    });
  }, [isAuthenticated, hasRole, hasPermission]); // Include all dependencies

  const handleLogout = async () => {
    await logout();
    setUserMenuOpen(false);
  };

  // Track which parent menus are expanded (with persistence)
  const LOCAL_STORAGE_KEY = 'pf_nav_expanded_v1';
  const [expanded, setExpanded] = useState<Record<string, boolean>>({});
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
      const childCount = itemDef?.children?.length ?? 0;
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

  // Initialize expanded state only once on mount
  const initializedRef = useRef(false);
  
  useEffect(() => {
    if (initializedRef.current) return; // Prevent re-initialization
    
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
      for (const item of filteredNavigation) {
        if (item.children) {
          const hasActiveChild = item.children.some(c => path.startsWith(c.href));
          if (hasActiveChild && !(item.name in parsed)) {
            parsed[item.name] = true;
          }
        }
      }
      
      setExpanded(parsed);
      initializedRef.current = true;
    } catch {
      // If parsing fails, at least auto-expand current route
      const autoExpanded: Record<string, boolean> = {};
      for (const item of filteredNavigation) {
        if (item.children) {
          const hasActiveChild = item.children.some(c => path.startsWith(c.href));
          if (hasActiveChild) {
            autoExpanded[item.name] = true;
          }
        }
      }
      setExpanded(autoExpanded);
      initializedRef.current = true;
    }
  }, [isAuthenticated, filteredNavigation, location.pathname]); // Include all dependencies

  // Persist expanded changes
  useEffect(() => {
    if (!initializedRef.current) return; // Don't persist until initialized
    try {
      localStorage.setItem(LOCAL_STORAGE_KEY, JSON.stringify(expanded));
    } catch {
      // ignore
    }
  }, [expanded]);

  // Auto-expand groups containing current route on navigation (only after initialization)
  useEffect(() => {
    if (!initializedRef.current) return; // Don't auto-expand until initialized
    
    const path = location.pathname;
    setExpanded((prev: Record<string, boolean>) => {
      let hasChanges = false;
      const next = { ...prev };
      
      // Only process navigation items that have children
      for (const item of filteredNavigation) {
        if (item.children) {
          const hasActiveChild = item.children.some(c => path.startsWith(c.href));
          if (hasActiveChild && prev[item.name] === undefined) {
            // Only auto-expand if user hasn't manually set the state
            next[item.name] = true;
            hasChanges = true;
          }
        }
      }
      
      return hasChanges ? next : prev; // Prevent unnecessary re-renders
    });
  }, [location.pathname, filteredNavigation]); // Include all dependencies

  return (
    <div className="min-h-screen bg-pf-bg-0 flex flex-col">
      {/* Live region for accessibility announcements */}
      <div className="sr-only" aria-live="polite" role="status">{announcement}</div>
      {/* Top Header Bar */}
      <header className="bg-pf-bg-1 border-b border-pf-border sticky top-0 z-50">
        <div className="flex items-center justify-between h-16 px-4">
          {/* Left side - App branding */}
          <div className="flex items-center space-x-4">
            {/* Mobile menu button */}
            <Button
              type="button"
              aria-label="Open navigation menu"
              title="Open navigation menu"
              variant="subtle"
              size="sm"
              className="lg:hidden"
              onClick={() => setSidebarOpen(true)}
            >
              <MenuIcon className="h-5 w-5" />
            </Button>

            {/* App logo and name */}
            <div className="flex items-center space-x-3">
              <img 
                src="/printfarmer-logo.svg" 
                alt="PrintFarmer Logo" 
                className="w-8 h-8" 
              />
              <h1 className="text-xl font-bold text-pf-text-primary font-bebas uppercase">PrintFarmer</h1>
            </div>
          </div>

          {/* Right side - Status and user */}
          <div className="flex items-center space-x-4">
            {/* Theme toggle */}
            <ThemeToggle size="sm" />

            {/* Connection status */}
            <div className="flex items-center space-x-2">
              <div 
                className="h-2 w-2 rounded-full" 
                style={{ backgroundColor: isConnected ? 'var(--pf-success)' : 'var(--pf-error)' }}
              />
              <span className="text-sm text-pf-text-tertiary">
                {isConnected ? 'Connected' : 'Disconnected'}
              </span>
            </div>

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
                <div className="absolute right-0 mt-2 w-48 bg-pf-bg-1 border border-pf-border rounded-md shadow-lg z-10">
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
                  </div>
                </div>
              )}
            </div>
          </div>
        </div>
      </header>

      <div className="flex flex-1 h-[calc(100vh-4rem)]">
        {/* Mobile sidebar overlay */}
        {sidebarOpen && (
          <div className="fixed inset-0 z-40 lg:hidden">
            <div className="fixed inset-0 bg-black bg-opacity-75" onClick={() => setSidebarOpen(false)} />
            <div className="relative flex w-full max-w-xs flex-1 flex-col bg-pf-bg-1 border-r border-pf-border h-full">
              <div className="absolute top-0 right-0 -mr-12 pt-2">
                <Button
                  type="button"
                  aria-label="Close navigation menu"
                  title="Close navigation menu"
                  variant="subtle"
                  size="sm"
                  className="ml-1 !p-2 h-10 w-10"
                  onClick={() => setSidebarOpen(false)}
                >
                  <CloseIcon className="h-6 w-6" />
                </Button>
              </div>

              <nav className="flex-1 px-4 py-4 space-y-2 overflow-y-auto">
                {filteredNavigation.map(item => {
                  const Icon = item.icon;
                  const isExpanded = !!expanded[item.name];
                  
                  const hasChildren = !!item.children?.length;
                  return (
                    <div key={item.name} className="flex flex-col">
                      {hasChildren ? (
                        <details open={isExpanded} className="group">
                          <summary
                            className={`flex items-center px-3 py-2 text-sm font-medium rounded-md cursor-pointer list-none focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-pf-accent text-pf-text-primary hover:text-pf-text-light hover:bg-pf-bg-2`}
                            onClick={e => {
                              e.preventDefault(); // Prevent native toggle
                              toggleExpand(item.name);
                            }}
                            aria-expanded={isExpanded}
                            tabIndex={0}
                            role="button"
                          >
                            <Icon className="mr-3 h-5 w-5 flex-shrink-0" />
                            <span className="flex-1 text-left">{item.name}</span>
                            <ChevronDownIcon className={`ml-2 h-4 w-4 transition-transform duration-200 ${isExpanded ? 'rotate-90' : ''}`} aria-hidden="true" />
                          </summary>
                          <div className="ml-8 space-y-1 mt-1">
                            {item.children!.map(child => {
                              const ChildIcon = child.icon;
                              return (
                                <NavLink
                                  key={child.name}
                                  to={child.href}
                                  onClick={() => { setSidebarOpen(false); }}
                                  className={({ isActive }: { isActive: boolean }) =>
                                    `group flex items-center px-3 py-1.5 text-sm rounded-md transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-pf-accent ${isActive
                                      ? 'bg-pf-bg-2 text-pf-text-primary border-r-2 border-pf-accent'
                                      : 'text-pf-text-secondary hover:text-pf-text-primary hover:bg-pf-bg-2'
                                    }`
                                  }
                                  end={child.href === '/harvest'} // Exact match for parent routes
                                >
                                  <ChildIcon className="mr-2 h-4 w-4 flex-shrink-0" />
                                  {child.name}
                                </NavLink>
                              );
                            })}
                          </div>
                        </details>
                      ) : (
                        <NavLink
                          to={item.href}
                          onClick={() => { setSidebarOpen(false); }}
                          className={({ isActive }: { isActive: boolean }) =>
                            `group flex items-center px-3 py-2 text-sm font-medium rounded-md transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-pf-accent ${isActive
                              ? 'bg-pf-bg-2 text-pf-text-primary border-r-2 border-pf-accent'
                              : 'text-pf-text-primary hover:text-pf-text-light hover:bg-pf-bg-2'
                            }`
                          }
                        >
                          <Icon className="mr-3 h-5 w-5 flex-shrink-0" />
                          <span className="flex-1 text-left">{item.name}</span>
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
        <aside className="hidden lg:flex lg:flex-shrink-0 z-40">
          <div className="flex flex-col w-64 bg-pf-bg-1 border-r border-pf-border">
            <nav className="flex-1 px-4 py-4 space-y-2 overflow-y-auto">
              {filteredNavigation.map(item => {
                const Icon = item.icon;
                const isExpanded = !!expanded[item.name];
                
                const hasChildren = !!item.children?.length;
                return (
                  <div key={item.name} className="flex flex-col">
                    {hasChildren ? (
                      <details open={isExpanded} className="group">
                        <summary
                          className={`flex items-center px-3 py-2 text-sm font-medium rounded-md cursor-pointer list-none focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-pf-accent text-pf-text-primary hover:text-pf-text-light hover:bg-pf-bg-2`}
                          onClick={e => {
                            e.preventDefault(); // Prevent native toggle
                            toggleExpand(item.name);
                          }}
                          aria-expanded={isExpanded}
                          tabIndex={0}
                          role="button"
                        >
                          <Icon className="mr-3 h-5 w-5 flex-shrink-0" />
                          <span className="flex-1 text-left">{item.name}</span>
                          <ChevronDownIcon className={`ml-2 h-4 w-4 transition-transform duration-200 ${isExpanded ? 'rotate-90' : ''}`} aria-hidden="true" />
                        </summary>
                        <div className="ml-8 space-y-1 mt-1">
                          {item.children!.map(child => {
                            const ChildIcon = child.icon;
                            return (
                              <NavLink
                                key={child.name}
                                to={child.href}
                                onClick={() => { /* child nav */ }}
                                className={({ isActive }: { isActive: boolean }) =>
                                  `group flex items-center px-3 py-1.5 text-sm rounded-md transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-pf-accent ${isActive
                                    ? 'bg-pf-bg-2 text-pf-text-primary border-r-2 border-pf-accent'
                                    : 'text-pf-text-secondary hover:text-pf-text-primary hover:bg-pf-bg-2'
                                  }`
                                }
                                end={child.href === '/harvest'} // Exact match for parent routes
                              >
                                <ChildIcon className="mr-2 h-4 w-4 flex-shrink-0" />
                                {child.name}
                              </NavLink>
                            );
                          })}
                        </div>
                      </details>
                    ) : (
                      <NavLink
                        to={item.href}
                        onClick={() => { /* top-level nav */ }}
                        className={({ isActive }: { isActive: boolean }) =>
                          `group flex items-center px-3 py-2 text-sm font-medium rounded-md transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-pf-accent ${isActive
                            ? 'bg-pf-bg-2 text-pf-text-primary border-r-2 border-pf-accent'
                            : 'text-pf-text-primary hover:text-pf-text-light hover:bg-pf-bg-2'
                          }`
                        }
                      >
                        <Icon className="mr-3 h-5 w-5 flex-shrink-0" />
                        <span className="flex-1 text-left">{item.name}</span>
                      </NavLink>
                    )}
                  </div>
                );
              })}
            </nav>
          </div>
        </aside>

        {/* Main content area */}
        <main className="flex-1 overflow-y-auto">
          <EmailConfirmationBanner />
          <div className="p-6">
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

      <footer className="bg-pf-bg-1 border-t border-pf-border px-4 py-2 text-xs text-pf-text-tertiary flex items-center justify-between">
        <div className="flex items-center space-x-2">
          <span>&copy; {new Date().getFullYear()} PrintFarmer</span>
        </div>
        <BuildInfo />
      </footer>

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
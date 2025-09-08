import { LoginModal } from '@/components/auth/LoginModal';
import { RegisterModal } from '@/components/auth/RegisterModal';
import { ThemeToggle } from '@/components/ThemeToggle';
import { useAuth } from '@/contexts/AuthContext';
import { useSignalRConnection } from '@/hooks/useSignalR';
import {
  Box,
  ChevronRight,
  Cog,
  FileText,
  Grid3X3,
  Home,
  Layers,
  LogIn,
  LogOut,
  Menu,
  Printer,
  Settings,
  Table,
  User,
  UserCheck,
  Users,
  X
} from 'lucide-react';
import type { ReactNode } from 'react';
import { useEffect, useMemo, useRef, useState } from 'react';
import { NavLink, useLocation } from 'react-router-dom';

interface LayoutProps {
  children: ReactNode;
}

interface NavigationItem {
  name: string;
  href: string;
  icon: React.ComponentType<{ className?: string }>;
  requiredPermission?: { resource: string; action: string };
  requiredRole?: string;
  children?: NavigationItem[];
}

const navigation: NavigationItem[] = [
  { name: 'Dashboard', href: '/dashboard', icon: Home },
  {
    name: 'Printers',
    href: '/printers/dashboard',
    icon: Printer,
    requiredPermission: { resource: 'printers', action: 'read' },
    children: [
      { name: 'Dashboard', href: '/printers/dashboard', icon: Grid3X3 },
      { name: 'Table View', href: '/printers/table', icon: Table }
    ]
  },
  {
    name: '3D Models',
    href: '/models',
    icon: Box,
    requiredPermission: { resource: 'models', action: 'read' }
  },
  {
    name: 'G-code Harvest',
    href: '/harvest',
    icon: Cog,
    requiredPermission: { resource: 'gcode_harvest', action: 'read' }
  },
  {
    name: 'G-code Files',
    href: '/files',
    icon: FileText,
    requiredPermission: { resource: 'gcode_harvest', action: 'read' }
  },
  {
    name: 'Admin',
    href: '#',
    icon: Settings,
    requiredRole: 'farm_admin',
    children: [
      { name: 'Catalog', href: '/catalog', icon: Layers },
      { name: 'Settings', href: '/settings', icon: Settings },
      { name: 'Spools', href: '/spools', icon: Box }
    ]
  },
  {
    name: 'User Management',
    href: '/admin/users',
    icon: Users,
    requiredRole: 'farm_admin'
  }
];

export function Layout({ children }: LayoutProps) {
  const { isConnected } = useSignalRConnection();
  const { user, logout, isAuthenticated, hasRole, hasPermission } = useAuth();
  const location = useLocation();
  const [sidebarOpen, setSidebarOpen] = useState(false);
  const [userMenuOpen, setUserMenuOpen] = useState(false);
  const [showLoginModal, setShowLoginModal] = useState(false);
  const [showRegisterModal, setShowRegisterModal] = useState(false);

  // Filter navigation based on user permissions
  const filteredNavigation = navigation.filter(item => {
    if (item.requiredRole && !hasRole(item.requiredRole)) {
      return false;
    }
    if (item.requiredPermission && !hasPermission(item.requiredPermission.resource, item.requiredPermission.action)) {
      return false;
    }
    return true;
  });

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
    setExpanded(prev => {
      const nextValue = !prev[name];
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

  // Motion preference
  const prefersReducedMotion = useMemo(() => {
    if (typeof window === 'undefined' || !('matchMedia' in window)) return false;
    return window.matchMedia('(prefers-reduced-motion: reduce)').matches;
  }, []);

  // Hydrate expanded state
  useEffect(() => {
    try {
      const raw = localStorage.getItem(LOCAL_STORAGE_KEY);
      if (raw) {
        const parsed = JSON.parse(raw);
        if (parsed && typeof parsed === 'object') {
          setExpanded(parsed);
        }
      }
    } catch {
      // ignore
    }
  }, []);

  // Persist expanded changes
  useEffect(() => {
    try {
      localStorage.setItem(LOCAL_STORAGE_KEY, JSON.stringify(expanded));
    } catch {
      // ignore
    }
  }, [expanded]);

  // Auto-expand groups containing current route
  useEffect(() => {
    const path = location.pathname;
    setExpanded(prev => {
      const next = { ...prev };
      filteredNavigation.forEach(item => {
        if (item.children) {
          const hasActiveChild = item.children.some(c => path.startsWith(c.href));
          if (hasActiveChild) {
            next[item.name] = true;
          }
        }
      });
      return next;
    });
  }, [location.pathname, filteredNavigation]);

  return (
    <div className="min-h-screen bg-pf-bg-0">
      {/* Live region for accessibility announcements */}
      <div className="sr-only" aria-live="polite" role="status">{announcement}</div>
      {/* Top Header Bar */}
      <header className="bg-pf-bg-1 border-b border-pf-border sticky top-0 z-50">
        <div className="flex items-center justify-between h-16 px-4">
          {/* Left side - App branding */}
          <div className="flex items-center space-x-4">
            {/* Mobile menu button */}
            <button
              type="button"
              className="lg:hidden p-2 rounded-md text-pf-text-primary hover:bg-pf-bg-2 focus:outline-none focus:ring-2 focus:ring-pf-accent"
              onClick={() => setSidebarOpen(true)}
            >
              <Menu className="h-5 w-5" />
            </button>

            {/* App logo and name */}
            <div className="flex items-center space-x-3">
              <div className="flex items-center justify-center w-8 h-8 bg-pf-accent rounded-md">
                <Layers className="h-5 w-5 text-white" />
              </div>
              <h1 className="text-xl font-bold text-pf-text-primary font-bebas uppercase">PrintFarmer</h1>
            </div>
          </div>

          {/* Right side - Status and user */}
          <div className="flex items-center space-x-4">
            {/* Theme toggle */}
            <ThemeToggle size="sm" />

            {/* Connection status */}
            <div className="flex items-center space-x-2">
              <div className={`h-2 w-2 rounded-full ${isConnected ? 'bg-green-500' : 'bg-red-500'
                }`} />
              <span className="text-sm text-pf-text-tertiary">
                {isConnected ? 'Connected' : 'Disconnected'}
              </span>
            </div>

            {/* User menu */}
            <div className="relative">
              <button
                type="button"
                className="flex items-center space-x-2 p-2 rounded-md text-pf-text-primary hover:bg-pf-bg-2 focus:outline-none focus:ring-2 focus:ring-pf-accent"
                onClick={() => setUserMenuOpen(!userMenuOpen)}
              >
                {isAuthenticated && user ? (
                  <>
                    <UserCheck className="h-5 w-5 text-green-500" />
                    <span className="hidden sm:block text-sm font-medium">
                      {user.firstName || user.username}
                    </span>
                  </>
                ) : (
                  <>
                    <User className="h-5 w-5 text-gray-400" />
                    <span className="hidden sm:block text-sm">Guest</span>
                  </>
                )}
              </button>

              {/* User dropdown menu */}
              {userMenuOpen && (
                <div className="absolute right-0 mt-2 w-48 bg-pf-bg-1 border border-pf-border rounded-md shadow-lg z-10">
                  <div className="py-1">
                    {isAuthenticated && user ? (
                      <>
                        <div className="px-4 py-2 text-sm text-pf-text-secondary border-b border-pf-border">
                          Signed in as <strong>{user.username}</strong>
                        </div>
                        <button
                          onClick={() => setUserMenuOpen(false)}
                          className="flex items-center w-full px-4 py-2 text-sm text-pf-text-primary hover:bg-pf-bg-2"
                        >
                          <Settings className="h-4 w-4 mr-2" />
                          Profile
                        </button>
                        <button
                          onClick={handleLogout}
                          className="flex items-center w-full px-4 py-2 text-sm text-pf-text-primary hover:bg-pf-bg-2"
                        >
                          <LogOut className="h-4 w-4 mr-2" />
                          Sign out
                        </button>
                      </>
                    ) : (
                      <>
                        <button
                          onClick={() => {
                            setShowLoginModal(true);
                            setUserMenuOpen(false);
                          }}
                          className="flex items-center w-full px-4 py-2 text-sm text-pf-text-primary hover:bg-pf-bg-2"
                        >
                          <LogIn className="h-4 w-4 mr-2" />
                          Sign In
                        </button>
                        <button
                          onClick={() => {
                            setShowRegisterModal(true);
                            setUserMenuOpen(false);
                          }}
                          className="flex items-center w-full px-4 py-2 text-sm text-pf-text-primary hover:bg-pf-bg-2"
                        >
                          <User className="h-4 w-4 mr-2" />
                          Register
                        </button>
                      </>
                    )}
                  </div>
                </div>
              )}
            </div>
          </div>
        </div>
      </header>

      <div className="flex h-[calc(100vh-4rem)]">
        {/* Mobile sidebar overlay */}
        {sidebarOpen && (
          <div className="fixed inset-0 z-40 lg:hidden">
            <div className="fixed inset-0 bg-black bg-opacity-75" onClick={() => setSidebarOpen(false)} />
            <div className="relative flex w-full max-w-xs flex-1 flex-col bg-pf-bg-1 border-r border-pf-border h-full">
              <div className="absolute top-0 right-0 -mr-12 pt-2">
                <button
                  type="button"
                  className="ml-1 flex h-10 w-10 items-center justify-center rounded-full focus:outline-none focus:ring-2 focus:ring-inset focus:ring-pf-accent"
                  onClick={() => setSidebarOpen(false)}
                >
                  <X className="h-6 w-6 text-pf-text-primary" />
                </button>
              </div>

              <nav className="flex-1 px-4 py-4 space-y-2 overflow-y-auto">
                {filteredNavigation.map(item => {
                  const Icon = item.icon;
                  const isExpanded = !!expanded[item.name];
                  return (
                    <div key={item.name} className="flex flex-col">
                      {/* Hidden hint description for screen readers */}
                      {item.children && (
                        <span id={`desc-mobile-${item.name.replace(/\s+/g, '-').toLowerCase()}`} className="sr-only">
                          Press Enter or Space to toggle this section.
                        </span>
                      )}
                      <button
                        type="button"
                        onClick={() => item.children ? toggleExpand(item.name) : (setSidebarOpen(false))}
                        className={`group flex items-center px-3 py-2 text-sm font-medium rounded-md transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-pf-accent text-pf-text-primary hover:text-pf-text-light hover:bg-pf-bg-2`}
                        aria-expanded={item.children ? isExpanded : undefined}
                        aria-controls={item.children ? `submenu-${item.name}` : undefined}
                        aria-describedby={item.children ? `desc-mobile-${item.name.replace(/\s+/g, '-').toLowerCase()}` : undefined}
                      >
                        <Icon className="mr-3 h-5 w-5 flex-shrink-0" />
                        <span className="flex-1 text-left">{item.name}</span>
                        {item.children && (
                          <ChevronRight className={`ml-2 h-4 w-4 transition-transform duration-200 ${isExpanded ? 'rotate-90' : ''}`} aria-hidden="true" />
                        )}
                      </button>
                      {item.children && (
                        <div
                          id={`submenu-${item.name}`}
                          className={`overflow-hidden ${prefersReducedMotion ? '' : 'transition-all duration-300 ease-in-out'} ${isExpanded ? 'max-h-64 opacity-100 mt-1' : 'max-h-0 opacity-0'}`}
                        >
                          <div className="ml-8 space-y-1">
                            {item.children.map(child => {
                              const ChildIcon = child.icon;
                              return (
                                <NavLink
                                  key={child.name}
                                  to={child.href}
                                  onClick={() => setSidebarOpen(false)}
                                  className={({ isActive }) =>
                                    `group flex items-center px-3 py-1.5 text-sm rounded-md transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-pf-accent ${isActive
                                      ? 'bg-pf-bg-2 text-pf-text-primary border-r-2 border-pf-accent'
                                      : 'text-pf-text-secondary hover:text-pf-text-primary hover:bg-pf-bg-2'
                                    }`
                                  }
                                >
                                  <ChildIcon className="mr-2 h-4 w-4 flex-shrink-0" />
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
            </div>
          </div>
        )}

        {/* Desktop sidebar */}
        <aside className="hidden lg:flex lg:flex-shrink-0">
          <div className="flex flex-col w-64 bg-pf-bg-1 border-r border-pf-border">
            <nav className="flex-1 px-4 py-4 space-y-2 overflow-y-auto">
              {filteredNavigation.map((item) => {
                const Icon = item.icon;
                return (
                  <div key={item.name}>
                    <div className="flex flex-col">
                      {item.children && (
                        <span id={`desc-desktop-${item.name.replace(/\s+/g, '-').toLowerCase()}`} className="sr-only">
                          Press Enter or Space to toggle this section.
                        </span>
                      )}
                      <button
                        type="button"
                        onClick={() => item.children ? toggleExpand(item.name) : undefined}
                        className={`group flex items-center px-3 py-2 text-sm font-medium rounded-md transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-pf-accent ${'text-pf-text-primary hover:text-pf-text-light hover:bg-pf-bg-2'
                          }`}
                        aria-expanded={item.children ? !!expanded[item.name] : undefined}
                        aria-controls={item.children ? `submenu-desktop-${item.name}` : undefined}
                        aria-describedby={item.children ? `desc-desktop-${item.name.replace(/\s+/g, '-').toLowerCase()}` : undefined}
                      >
                        <Icon className="mr-3 h-5 w-5 flex-shrink-0" />
                        <span className="flex-1 text-left">{item.name}</span>
                        {item.children && (
                          <ChevronRight className={`ml-2 h-4 w-4 transition-transform duration-200 ${expanded[item.name] ? 'rotate-90' : ''}`} aria-hidden="true" />
                        )}
                      </button>
                      {item.children && (
                        <div
                          id={`submenu-desktop-${item.name}`}
                          className={`overflow-hidden ${prefersReducedMotion ? '' : 'transition-all duration-300 ease-in-out'} ${expanded[item.name] ? 'max-h-64 opacity-100 mt-1' : 'max-h-0 opacity-0'}`}
                        >
                          <div className="ml-8 space-y-1">
                            {item.children.map((child) => {
                              const ChildIcon = child.icon;
                              return (
                                <NavLink
                                  key={child.name}
                                  to={child.href}
                                  className={({ isActive }) =>
                                    `group flex items-center px-3 py-1.5 text-sm rounded-md transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-pf-accent ${isActive
                                      ? 'bg-pf-bg-2 text-pf-text-primary border-r-2 border-pf-accent'
                                      : 'text-pf-text-secondary hover:text-pf-text-primary hover:bg-pf-bg-2'
                                    }`
                                  }
                                >
                                  <ChildIcon className="mr-2 h-4 w-4 flex-shrink-0" />
                                  {child.name}
                                </NavLink>
                              );
                            })}
                          </div>
                        </div>
                      )}
                    </div>
                  </div>
                );
              })}
            </nav>
          </div>
        </aside>

        {/* Main content area */}
        <main className="flex-1 overflow-y-auto">
          <div className="p-6">
            {children}
          </div>
        </main>
      </div>

      {/* Click outside handler for user menu */}
      {userMenuOpen && (
        <div
          className="fixed inset-0 z-30"
          onClick={() => setUserMenuOpen(false)}
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
    </div>
  );
}
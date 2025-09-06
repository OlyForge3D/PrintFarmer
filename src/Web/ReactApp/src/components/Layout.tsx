import type { ReactNode } from 'react';
import { NavLink } from 'react-router-dom';
import { useSignalRConnection } from '@/hooks/useSignalR';
import { useAuth } from '@/contexts/AuthContext';
import { LoginModal } from '@/components/auth/LoginModal';
import { RegisterModal } from '@/components/auth/RegisterModal';
import { ThemeToggle } from '@/components/ThemeToggle';
import { 
  Home,
  Printer, 
  Cog, 
  Users,
  Menu,
  X,
  Box,
  FileText,
  Table,
  Grid3X3,
  User,
  UserCheck,
  LogOut,
  LogIn,
  Settings,
  Layers
} from 'lucide-react';
import { useState } from 'react';

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
    href: '/printers', 
    icon: Printer,
    requiredPermission: { resource: 'printers', action: 'read' },
    children: [
      { name: 'Dashboard', href: '/printers', icon: Grid3X3 },
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

  const handleLoginSuccess = () => {
    setShowLoginModal(false);
    setShowRegisterModal(false);
  };

  const switchToRegister = () => {
    setShowLoginModal(false);
    setShowRegisterModal(true);
  };

  const switchToLogin = () => {
    setShowRegisterModal(false);
    setShowLoginModal(true);
  };

  return (
    <div className="min-h-screen bg-pf-bg-0">
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
              <div className={`h-2 w-2 rounded-full ${
                isConnected ? 'bg-green-500' : 'bg-red-500'
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
                {filteredNavigation.map((item) => {
                  const Icon = item.icon;
                  return (
                    <div key={item.name}>
                      <NavLink
                        to={item.href}
                        onClick={() => setSidebarOpen(false)}
                        className={({ isActive }) =>
                          `group flex items-center px-3 py-2 text-sm font-medium rounded-md transition-colors ${
                            isActive
                              ? 'bg-pf-accent bg-opacity-20 text-pf-accent border-r-2 border-pf-accent'
                              : 'text-pf-text-primary hover:text-pf-text-light hover:bg-pf-bg-2'
                          }`
                        }
                      >
                        <Icon className="mr-3 h-5 w-5 flex-shrink-0" />
                        {item.name}
                      </NavLink>
                      {/* Render submenu items if they exist */}
                      {item.children && (
                        <div className="ml-8 mt-1 space-y-1">
                          {item.children.map((child) => {
                            const ChildIcon = child.icon;
                            return (
                              <NavLink
                                key={child.name}
                                to={child.href}
                                onClick={() => setSidebarOpen(false)}
                                className={({ isActive }) =>
                                  `group flex items-center px-3 py-1.5 text-sm rounded-md transition-colors ${
                                    isActive
                                      ? 'bg-pf-accent bg-opacity-15 text-pf-accent'
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
                    <NavLink
                      to={item.href}
                      className={({ isActive }) =>
                        `group flex items-center px-3 py-2 text-sm font-medium rounded-md transition-colors ${
                          isActive
                            ? 'bg-pf-accent bg-opacity-20 text-pf-accent border-r-2 border-pf-accent'
                            : 'text-pf-text-primary hover:text-pf-text-light hover:bg-pf-bg-2'
                        }`
                      }
                    >
                      <Icon className="mr-3 h-5 w-5 flex-shrink-0" />
                      {item.name}
                    </NavLink>
                    {/* Render submenu items if they exist */}
                    {item.children && (
                      <div className="ml-8 mt-1 space-y-1">
                        {item.children.map((child) => {
                          const ChildIcon = child.icon;
                          return (
                            <NavLink
                              key={child.name}
                              to={child.href}
                              className={({ isActive }) =>
                                `group flex items-center px-3 py-1.5 text-sm rounded-md transition-colors ${
                                  isActive
                                    ? 'bg-pf-accent bg-opacity-15 text-pf-accent'
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
                    )}
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
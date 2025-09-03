import type { ReactNode } from 'react';
import { NavLink, useLocation } from 'react-router-dom';
import { useSignalRConnection } from '@/hooks/useSignalR';
import { useAuth } from '@/contexts/AuthContext';
import { 
  Home,
  Printer, 
  Cog, 
  Users,
  Menu,
  X,
  Box,
  FileText
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
}

const navigation: NavigationItem[] = [
  { name: 'Dashboard', href: '/dashboard', icon: Home },
  { 
    name: 'Printers', 
    href: '/printers', 
    icon: Printer,
    requiredPermission: { resource: 'printers', action: 'read' }
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
    name: 'User Management', 
    href: '/admin/users', 
    icon: Users,
    requiredRole: 'farm_admin'
  }
];

export function Layout({ children }: LayoutProps) {
  const { isConnected, connectionState } = useSignalRConnection();
  const { user, logout, hasPermission, hasRole, isAuthenticated } = useAuth();
  const [sidebarOpen, setSidebarOpen] = useState(false);
  const location = useLocation();

  const filteredNavigation = navigation.filter(item => {
    if (!isAuthenticated) return false;
    if (item.requiredRole && !hasRole(item.requiredRole)) return false;
    if (item.requiredPermission && !hasPermission(item.requiredPermission.resource, item.requiredPermission.action)) return false;
    return true;
  });

  const handleLogout = async () => {
    await logout();
  };

  if (!isAuthenticated) {
    // For now, show a simple login prompt - in production this would be a proper login form
    return (
      <div className="min-h-screen bg-pf-bg-0 flex items-center justify-center">
        <div className="max-w-md w-full bg-pf-bg-1 border border-pf-border shadow-lg rounded-xl p-6">
          <h2 className="text-2xl font-bold text-center mb-4 text-pf-text-primary font-bebas uppercase">PrintFarmer</h2>
          <p className="text-pf-text-secondary text-center">
            Please log in to access the printer management system.
          </p>
          <div className="mt-4 p-3 bg-pf-loading bg-opacity-10 rounded border border-pf-loading-border">
            <p className="text-sm text-pf-loading">
              <strong>Development Note:</strong> Authentication is not fully implemented yet. 
              The system will work with mock authentication for now.
            </p>
          </div>
        </div>
      </div>
    );
  }

  return (
    <div className="min-h-screen bg-pf-bg-0">
      {/* Mobile sidebar overlay */}
      {sidebarOpen && (
        <div className="fixed inset-0 z-40 lg:hidden">
          <div className="fixed inset-0 bg-black bg-opacity-75" onClick={() => setSidebarOpen(false)} />
          <div className="relative flex w-full max-w-xs flex-1 flex-col bg-pf-bg-1 border-r border-pf-border">
            <div className="absolute top-0 right-0 -mr-12 pt-2">
              <button
                type="button"
                className="ml-1 flex h-10 w-10 items-center justify-center rounded-full focus:outline-none focus:ring-2 focus:ring-inset focus:ring-pf-accent"
                onClick={() => setSidebarOpen(false)}
              >
                <X className="h-6 w-6 text-pf-text-primary" />
              </button>
            </div>
            <nav className="flex-1 px-4 py-4 space-y-2">
              {filteredNavigation.map((item) => {
                const Icon = item.icon;
                return (
                  <NavLink
                    key={item.name}
                    to={item.href}
                    onClick={() => setSidebarOpen(false)}
                    className={({ isActive }) =>
                      `group flex items-center px-2 py-2 text-sm font-medium rounded-md transition-colors ${
                        isActive
                          ? 'bg-pf-accent bg-opacity-20 text-pf-accent border-r-2 border-pf-accent'
                          : 'text-pf-text-primary hover:text-pf-text-light hover:bg-pf-bg-2'
                      }`
                    }
                  >
                    <Icon className="mr-3 h-5 w-5 flex-shrink-0" />
                    {item.name}
                  </NavLink>
                );
              })}
            </nav>
          </div>
        </div>
      )}

      {/* Desktop sidebar */}
      <div className="hidden lg:flex lg:flex-shrink-0">
        <div className="flex flex-col w-64 bg-pf-bg-1 border-r border-pf-border">
          <div className="flex items-center h-16 px-4 bg-pf-accent">
            <h1 className="text-lg font-semibold text-white font-bebas uppercase">PrintFarmer</h1>
          </div>
          
          <nav className="flex-1 px-4 py-4 space-y-2">
            {filteredNavigation.map((item) => {
              const Icon = item.icon;
              return (
                <NavLink
                  key={item.name}
                  to={item.href}
                  className={({ isActive }) =>
                    `group flex items-center px-2 py-2 text-sm font-medium rounded-md transition-colors ${
                      isActive
                        ? 'bg-pf-accent bg-opacity-20 text-pf-accent border-r-2 border-pf-accent'
                        : 'text-pf-text-primary hover:text-pf-text-light hover:bg-pf-bg-2'
                    }`
                  }
                >
                  <Icon className="mr-3 h-5 w-5 flex-shrink-0" />
                  {item.name}
                </NavLink>
              );
            })}
          </nav>

          {/* User info and connection status */}
          <div className="flex-shrink-0 border-t border-pf-border p-4">
            <div className="flex items-center justify-between mb-3">
              <div className={`h-2 w-2 rounded-full ${
                isConnected ? 'bg-pf-success' : 'bg-pf-error'
              }`} />
              <span className="text-xs text-pf-text-tertiary">
                {isConnected ? 'Connected' : `Disconnected`}
              </span>
            </div>
            
            {user && (
              <div className="flex items-center justify-between">
                <div className="min-w-0 flex-1">
                  <p className="text-sm font-medium text-pf-text-primary truncate">
                    {user.firstName && user.lastName 
                      ? `${user.firstName} ${user.lastName}` 
                      : user.username}
                  </p>
                  <p className="text-xs text-pf-text-tertiary truncate">{user.email}</p>
                </div>
                <button
                  onClick={handleLogout}
                  className="ml-2 text-xs text-pf-text-tertiary hover:text-pf-accent transition-colors"
                >
                  Logout
                </button>
              </div>
            )}
          </div>
        </div>
      </div>

      {/* Main content */}
      <div className="flex-1 overflow-hidden lg:ml-0">
        {/* Mobile header */}
        <div className="lg:hidden bg-pf-bg-1 border-b border-pf-border">
          <div className="flex items-center justify-between px-4 py-2">
            <button
              type="button"
              className="text-pf-text-secondary hover:text-pf-text-primary focus:outline-none focus:ring-2 focus:ring-inset focus:ring-pf-accent"
              onClick={() => setSidebarOpen(true)}
            >
              <Menu className="h-6 w-6" />
            </button>
            
            <h1 className="text-lg font-semibold text-pf-text-primary font-bebas uppercase">PrintFarmer</h1>
            
            {/* Connection Status */}
            <div className="flex items-center">
              <div className={`h-2 w-2 rounded-full mr-2 ${
                isConnected ? 'bg-pf-success' : 'bg-pf-error'
              }`} />
              <span className="text-xs text-pf-text-tertiary">
                {isConnected ? 'Connected' : 'Offline'}
              </span>
            </div>
          </div>
        </div>

        {/* Page content */}
        <main className="flex-1 relative z-0 overflow-y-auto focus:outline-none bg-pf-bg-0">
          <div className="py-6">
            <div className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8">
              {children}
            </div>
          </div>
        </main>
      </div>
    </div>
  );
}
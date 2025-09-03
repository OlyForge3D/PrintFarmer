import type { ReactNode } from 'react';
import { NavLink } from 'react-router-dom';
import { useSignalRConnection } from '@/hooks/useSignalR';

interface LayoutProps {
  children: ReactNode;
}

export function Layout({ children }: LayoutProps) {
  const { isConnected, connectionState } = useSignalRConnection();

  return (
    <div className="min-h-screen bg-gray-50">
      {/* Header */}
      <header className="bg-white shadow">
        <div className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8">
          <div className="flex justify-between items-center py-6">
            <div className="flex items-center">
              <h1 className="text-2xl font-bold text-gray-900">
                PrintFarmer React
              </h1>
            </div>
            
            {/* Navigation */}
            <nav className="flex space-x-8">
              <NavLink
                to="/printers"
                className={({ isActive }) =>
                  `px-3 py-2 rounded-md text-sm font-medium transition-colors ${
                    isActive
                      ? 'bg-blue-100 text-blue-700'
                      : 'text-gray-500 hover:text-gray-700 hover:bg-gray-100'
                  }`
                }
              >
                Printers
              </NavLink>
            </nav>
            
            {/* Connection Status */}
            <div className="flex items-center">
              <div className={`h-2 w-2 rounded-full mr-2 ${
                isConnected ? 'bg-green-400' : 'bg-red-400'
              }`} />
              <span className="text-xs text-gray-500">
                {isConnected ? 'Connected' : `Disconnected (${connectionState})`}
              </span>
            </div>
          </div>
        </div>
      </header>

      {/* Main Content */}
      <main>
        <div className="max-w-7xl mx-auto py-6 sm:px-6 lg:px-8">
          <div className="px-4 py-6 sm:px-0">
            {children}
          </div>
        </div>
      </main>
    </div>
  );
}
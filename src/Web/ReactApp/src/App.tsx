import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ReactQueryDevtools } from '@tanstack/react-query-devtools';
import { BrowserRouter as Router, Routes, Route } from 'react-router-dom';
import { useState, useEffect } from 'react';
import { PrinterDashboard } from '@/components/PrinterDashboard';
import { PrinterTableViewPage } from '@/pages/PrinterTableViewPage';
import { ModelsPage } from '@/pages/ModelsPage';
import { HarvestPage } from '@/pages/HarvestPage';
import { FilesPage } from '@/pages/FilesPage';
import { CatalogPage } from '@/pages/CatalogPage';
import { SettingsPage } from '@/pages/SettingsPage';
import { SpoolsPage } from '@/pages/SpoolsPage';
import { UserManagementPage } from '@/pages/UserManagementPage';
import { Layout } from '@/components/Layout';
import { ErrorBoundary } from '@/components/ErrorBoundary';
import { AuthProvider } from '@/contexts/AuthContext';
import { ProtectedRoute } from '@/components/auth/ProtectedRoute';
import { SetupWizard } from '@/components/SetupWizard';
import { ThemeProvider } from '@/contexts/ThemeContext';
import './App.css';

// Create a query client for React Query
const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      retry: (failureCount, error: any) => {
        // Don't retry for 4xx errors
        if (error?.statusCode >= 400 && error?.statusCode < 500) {
          return false;
        }
        // Retry up to 3 times for other errors
        return failureCount < 3;
      },
      staleTime: 30000, // 30 seconds
      gcTime: 300000, // 5 minutes
    },
    mutations: {
      retry: false, // Don't retry mutations by default
    },
  },
});

function App() {
  const [setupComplete, setSetupComplete] = useState(false);
  const [checkingSetup, setCheckingSetup] = useState(true);

  useEffect(() => {
    const checkSetupStatus = async () => {
      try {
        const response = await fetch('/api/setup/status');
        if (response.ok) {
          const data = await response.json();
          setSetupComplete(!data.needsSetup);
        } else {
          // If we can't check setup status, assume setup is needed
          setSetupComplete(false);
        }
      } catch (error) {
        console.error('Error checking setup status:', error);
        // If there's an error, assume setup is needed
        setSetupComplete(false);
      } finally {
        setCheckingSetup(false);
      }
    };

    checkSetupStatus();
  }, []);

  const handleSetupComplete = () => {
    setSetupComplete(true);
  };

  // Show loading while checking setup status
  if (checkingSetup) {
    return (
      <div className="min-h-screen bg-pf-bg-0 flex items-center justify-center">
        <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-pf-accent"></div>
      </div>
    );
  }

  // Show setup wizard if setup is not complete
  if (!setupComplete) {
    return (
      <ErrorBoundary>
        <SetupWizard onComplete={handleSetupComplete} />
      </ErrorBoundary>
    );
  }

  // Show main application if setup is complete
  return (
    <ErrorBoundary>
      <ThemeProvider>
        <AuthProvider>
          <QueryClientProvider client={queryClient}>
            <Router>
              <Layout>
                <Routes>
                  <Route path="/" element={<PrinterDashboard />} />
                  <Route path="/dashboard" element={<PrinterDashboard />} />
                  <Route path="/printers" element={<PrinterDashboard />} />
                  <Route path="/printers/table" element={<PrinterTableViewPage />} />
                  <Route path="/models" element={<ModelsPage />} />
                  <Route path="/harvest" element={<HarvestPage />} />
                  <Route path="/files" element={<FilesPage />} />
                  <Route path="/catalog" element={<CatalogPage />} />
                  <Route path="/settings" element={<SettingsPage />} />
                  <Route path="/spools" element={<SpoolsPage />} />
                  {/* Add more routes as needed */}
                </Routes>
              </Layout>
            </Router>
            <ReactQueryDevtools initialIsOpen={false} />
          </QueryClientProvider>
        </AuthProvider>
      </ThemeProvider>
      <AuthProvider>
        <QueryClientProvider client={queryClient}>
          <Router>
            <Layout>
              <Routes>
                <Route path="/" element={<PrinterDashboard />} />
                <Route path="/dashboard" element={<PrinterDashboard />} />
                <Route path="/printers" element={<PrinterDashboard />} />
                <Route path="/printers/table" element={<PrinterTableViewPage />} />
                <Route path="/models" element={<ModelsPage />} />
                <Route path="/harvest" element={<HarvestPage />} />
                <Route path="/files" element={<FilesPage />} />
                <Route path="/catalog" element={<CatalogPage />} />
                <Route path="/settings" element={<SettingsPage />} />
                <Route path="/spools" element={<SpoolsPage />} />
                <Route 
                  path="/admin/users" 
                  element={
                    <ProtectedRoute requiredRole="farm_admin">
                      <UserManagementPage />
                    </ProtectedRoute>
                  } 
                />
                {/* Add more routes as needed */}
              </Routes>
            </Layout>
          </Router>
          <ReactQueryDevtools initialIsOpen={false} />
        </QueryClientProvider>
      </AuthProvider>
    </ErrorBoundary>
  );
}

export default App;

import { ProtectedRoute } from '@/components/auth/ProtectedRoute';
import { ErrorBoundary } from '@/components/ErrorBoundary';
import { Layout } from '@/components/Layout';
import { ObservabilityDashboard } from '@/components/ObservabilityDashboard';
import { PrinterDashboard } from '@/components/PrinterDashboard';
import { SetupWizard } from '@/components/SetupWizard';
import { AuthProvider } from '@/contexts/AuthContext';
import { ThemeProvider } from '@/contexts/ThemeContext';
import { useUnifiedLogging } from '@/hooks/useUnifiedLogging';
import { CatalogPage } from '@/pages/CatalogPage';
import { FilesPage } from '@/pages/FilesPage';
import { HarvestPage } from '@/pages/HarvestPage';
import { HarvestHistoryPage } from '@/pages/HarvestHistoryPage';
import { ModelsPage } from '@/pages/ModelsPage';
import { PrintersPage } from '@/pages/PrintersPage';
import { SettingsPage } from '@/pages/SettingsPage';
import { SlicerDryRunPage } from '@/pages/SlicerDryRunPage';
import { SlicerJobStatusPage } from '@/pages/SlicerJobStatusPage';
import LogsPage from './pages/logs/LogsPage';
import { SlicerSettingsPage } from '@/pages/SlicerSettingsPage';
import { SpoolsPage } from '@/pages/SpoolsPage';
import { UserManagementPage } from '@/pages/UserManagementPage';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ReactQueryDevtools } from '@tanstack/react-query-devtools';
import { useEffect, useState } from 'react';
import { Route, BrowserRouter as Router, Routes, Navigate, useLocation } from 'react-router-dom';
import RegistrationPendingPage from '@/pages/RegistrationPendingPage';
import { HarvestedFilesLibrary } from './pages/HarvestedFilesLibrary';
import { Toaster } from 'sonner';
import LoginPage from './pages/LoginPage';
import { useAuth } from '@/contexts/AuthHooks';
import './App.css';

// Create a query client for React Query
const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      retry: (failureCount, error: unknown) => {
        // Don't retry client (4xx) errors
        const statusCode = typeof error === 'object' && error && 'statusCode' in error
          ? (error as { statusCode?: number }).statusCode
          : undefined;
        if (typeof statusCode === 'number' && statusCode >= 400 && statusCode < 500) {
          return false;
        }
        return failureCount < 3; // retry other errors up to 3 times
      },
      staleTime: 30000, // 30 seconds
      gcTime: 300000, // 5 minutes
    },
    mutations: {
      retry: false, // Don't retry mutations by default
    },
  },
});

function AuthenticatedAppRoutes() {
  // Custom global ProtectedRoute logic for redirecting guests and unapproved users
  const { isAuthenticated, isLoading, user } = useAuth();
  const location = useLocation();
  if (isLoading) {
    return (
      <div className="flex items-center justify-center min-h-screen">
        <div className="pf-animate-spin rounded-full h-8 w-8 border-b-2 border-pf-accent"></div>
      </div>
    );
  }
  if (!isAuthenticated) {
    // Don't redirect if already on /login
    if (location.pathname !== '/login') {
      return <Navigate to="/login" state={{ from: location }} replace />;
    }
  }
  // If user is logged in but not active, force to registration pending page
  if (user && user.isActive === false && location.pathname !== '/registration-pending') {
    return <Navigate to="/registration-pending" replace />;
  }
  // If user is on registration pending page but is now active, redirect to dashboard
  if (user && user.isActive === true && location.pathname === '/registration-pending') {
    return <Navigate to="/dashboard" replace />;
  }
  return (
    <Routes>
      <Route path="/login" element={<LoginPage />} />
      <Route path="/registration-pending" element={<RegistrationPendingPage />} />
      <Route path="/*" element={<Layout />}>
        <Route index element={<PrinterDashboard />} />
        <Route path="dashboard" element={<PrinterDashboard />} />
        <Route path="printers" element={<PrintersPage />} />
        <Route path="models" element={<ModelsPage />} />
        <Route path="harvest">
          <Route index element={<HarvestPage />} />
          <Route path="history" element={<HarvestHistoryPage />} />
          <Route path="library" element={<HarvestedFilesLibrary />} />
        </Route>
        <Route path="files" element={<FilesPage />} />
        <Route path="catalog" element={<CatalogPage />} />
        <Route path="settings" element={<SettingsPage />} />
        <Route path="spools" element={<SpoolsPage />} />
        <Route
          path="admin/users"
          element={
            <ProtectedRoute requiredRole="farm_admin">
              <UserManagementPage />
            </ProtectedRoute>
          }
        />
        <Route
          path="admin/observability"
          element={
            <ProtectedRoute requiredRole="farm_admin">
              <ObservabilityDashboard />
            </ProtectedRoute>
          }
        />
        <Route
          path="admin/logs"
          element={
            <ProtectedRoute requiredRole="farm_admin">
              <LogsPage />
            </ProtectedRoute>
          }
        />
        <Route
          path="admin/slicer"
          element={
            <ProtectedRoute requiredRole="farm_admin">
              <SlicerSettingsPage />
            </ProtectedRoute>
          }
        />
        <Route
          path="admin/slicer/dry-run"
          element={
            <ProtectedRoute requiredRole="farm_admin">
              <SlicerDryRunPage />
            </ProtectedRoute>
          }
        />
        <Route
          path="admin/slicer/job-status"
          element={
            <ProtectedRoute requiredRole="farm_admin">
              <SlicerJobStatusPage />
            </ProtectedRoute>
          }
        />
      </Route>
    </Routes>
  );
}

function App() {
  const [setupComplete, setSetupComplete] = useState(false);
  const [checkingSetup, setCheckingSetup] = useState(true);
  // Initialize unified logging for the main App component
  const { logger } = useUnifiedLogging({ 
    component: 'App', 
    logLifecycle: true 
  });

  useEffect(() => {
    const checkSetupStatus = async () => {
      logger.info('Checking setup status');
      try {
        const response = await fetch('/api/setup/status');
        if (response.ok) {
          const data = await response.json();
          setSetupComplete(!data.needsSetup);
          logger.info('Setup status retrieved', { 
            needsSetup: data.needsSetup, 
            setupComplete: !data.needsSetup 
          });
        } else {
          setSetupComplete(false);
          logger.warn('Setup status check failed - assuming setup needed', { 
            status: response.status 
          });
        }
      } catch (error) {
        logger.error('Error checking setup status', { 
          error: error instanceof Error ? error.message : String(error) 
        });
        setSetupComplete(false);
      } finally {
        setCheckingSetup(false);
      }
    };
    checkSetupStatus();
  }, [logger]);

  const handleSetupComplete = () => {
    setSetupComplete(true);
    window.location.href = '/';
  };

  if (checkingSetup) {
    return (
      <div className="min-h-screen bg-pf-bg-0 flex items-center justify-center">
        <div className="pf-animate-spin rounded-full h-8 w-8 border-b-2 border-pf-accent"></div>
      </div>
    );
  }

  if (!setupComplete) {
    return (
      <ErrorBoundary>
        <ThemeProvider>
          <AuthProvider>
            <QueryClientProvider client={queryClient}>
              <SetupWizard onComplete={handleSetupComplete} />
              <Toaster position="top-right" richColors />
            </QueryClientProvider>
          </AuthProvider>
        </ThemeProvider>
      </ErrorBoundary>
    );
  }

  return (
    <ErrorBoundary>
      <ThemeProvider>
        <AuthProvider>
          <QueryClientProvider client={queryClient}>
            <Router>
              <AuthenticatedAppRoutes />
            </Router>
            <ReactQueryDevtools initialIsOpen={false} />
            <Toaster position="top-right" richColors />
          </QueryClientProvider>
        </AuthProvider>
      </ThemeProvider>
    </ErrorBoundary>
  );
}

export default App;

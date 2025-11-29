import { ProtectedRoute } from '@/components/auth/ProtectedRoute';
import { ErrorBoundary } from '@/components/ErrorBoundary';
import { Layout } from '@/components/Layout';
import { ObservabilityDashboard } from '@/components/ObservabilityDashboard';
import { PrinterDashboard } from '@/components/PrinterDashboard';
import { SetupWizard } from '@/components/SetupWizard';
import { FileHealthDashboard } from '@/components/admin/file-health/FileHealthDashboard';
import { AuthProvider } from '@/contexts/AuthContext';
import { ThemeProvider } from '@/contexts/ThemeContext';
import { SlicerUIProvider } from '@/contexts/SlicerUIContext';
import { useUnifiedLogging } from '@/hooks/useUnifiedLogging';
import { CatalogPage } from '@/pages/CatalogPage';
import { FilesPage } from '@/pages/FilesPage';
import { HarvestPage } from '@/pages/HarvestPage';
import { HarvestHistoryPage } from '@/pages/HarvestHistoryPage';
import { ModelsPage } from '@/pages/ModelsPage';
import { ModelDetailPage } from '@/pages/ModelDetailPage';
import { TagAdminPage } from '@/pages/TagAdminPage';
import { PrintersPage } from '@/pages/PrintersPage';
import { SettingsPage } from '@/pages/SettingsPage';
import { SlicerDryRunPage } from '@/pages/SlicerDryRunPage';
import { SlicerJobStatusPage } from '@/pages/SlicerJobStatusPage';
import PrintersAdminPage from '@/pages/admin/PrintersAdminPage';
import SlicersAdminPage from '@/pages/admin/SlicersAdminPage';
import LogsPage from './pages/logs/LogsPage';
import { SlicerSettingsPage } from '@/pages/SlicerSettingsPage';
import { SpoolsPage } from '@/pages/SpoolsPage';
import { UserManagementPage } from '@/pages/UserManagementPage';
import WorkerManagementPage from '@/pages/WorkerManagementPage';
import JobQueueDashboardPage from '@/pages/JobQueueDashboardPage';
import NewSliceJobPage from '@/pages/NewSliceJobPage';
import SlicerProfilesPage from '@/pages/SlicerProfilesPage';
import ImportOfficialProfilesPage from '@/pages/ImportOfficialProfilesPage';
import { OrcaImportWizard } from '@farm/slicers-orcaslicer-v2_3_1';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ReactQueryDevtools } from '@tanstack/react-query-devtools';
import { useEffect, useState } from 'react';
import { Route, BrowserRouter as Router, Routes, Navigate, useLocation } from 'react-router-dom';
import RegistrationPendingPage from '@/pages/RegistrationPendingPage';
import { HarvestedFilesLibrary } from './pages/HarvestedFilesLibrary';
import { Toaster } from 'sonner';
import LoginPage from './pages/LoginPage';
import ForgotPasswordPage from './pages/ForgotPasswordPage';
import ResetPasswordPage from './pages/ResetPasswordPage';
import { ConfirmEmailPage } from './pages/ConfirmEmailPage';
import { useAuth } from '@/contexts/AuthHooks';
import { getApiBaseUrl, getAuthHeaders } from '@/utils/apiUrlHelpers';
import { assetService } from '@/services/assetService';
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
      <Route path="/forgot-password" element={<ForgotPasswordPage />} />
      <Route path="/reset-password" element={<ResetPasswordPage />} />
      <Route path="/confirm-email" element={<ConfirmEmailPage />} />
      <Route path="/registration-pending" element={<RegistrationPendingPage />} />
      <Route path="/*" element={<Layout />}>
        <Route index element={<PrinterDashboard />} />
        <Route path="dashboard" element={<PrinterDashboard />} />
        <Route path="printers" element={<PrintersPage />} />
        <Route path="models" element={<ModelsPage />} />
        <Route path="models/:modelId" element={<ModelDetailPage />} />
        <Route path="harvest/*">
          <Route index element={<HarvestPage />} />
          <Route path="history" element={<HarvestHistoryPage />} />
          <Route path="library" element={<HarvestedFilesLibrary />} />
        </Route>
        <Route path="files" element={<FilesPage />} />
        <Route path="catalog" element={<CatalogPage />} />
        <Route path="settings" element={<SettingsPage />} />
        <Route path="spools" element={<SpoolsPage />} />
        <Route
          path="admin/tags"
          element={
            <ProtectedRoute requiredRole="farm_admin">
              <TagAdminPage />
            </ProtectedRoute>
          }
        />
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
          path="admin/printers"
          element={
            <ProtectedRoute requiredRole="farm_admin">
              <PrintersAdminPage />
            </ProtectedRoute>
          }
        />
        <Route
          path="admin/slicers"
          element={
            <ProtectedRoute requiredRole="farm_admin">
              <SlicersAdminPage />
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
          path="admin/file-health"
          element={
            <ProtectedRoute requiredRole="farm_admin">
              <FileHealthDashboard />
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
        <Route
          path="admin/workers"
          element={
            <ProtectedRoute requiredRole="farm_admin">
              <WorkerManagementPage />
            </ProtectedRoute>
          }
        />
        <Route path="jobs" element={<JobQueueDashboardPage />} />
        <Route path="jobs/new" element={<NewSliceJobPage />} />
        <Route
          path="slicer-profiles"
          element={
            <ProtectedRoute requiredRole="farm_admin">
              <SlicerProfilesPage />
            </ProtectedRoute>
          }
        />
        <Route
          path="profiles/import/orca"
          element={
            <ProtectedRoute requiredRole="farm_admin">
              <OrcaImportWizard />
            </ProtectedRoute>
          }
        />
        <Route
          path="profiles/import/official"
          element={
            <ProtectedRoute requiredRole="farm_admin">
              <ImportOfficialProfilesPage />
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

  // Initialize asset service on app startup
  useEffect(() => {
    assetService.initialize().catch(err => {
      logger.warn('Failed to initialize asset service', {
        error: err instanceof Error ? err.message : String(err)
      });
    });
  }, [logger]);

  useEffect(() => {
    const checkSetupStatus = async () => {
      logger.info('Checking setup status');
      try {
        const response = await fetch(`${getApiBaseUrl()}/setup/status`, {
          headers: getAuthHeaders()
        });
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
              <SlicerUIProvider>
                <SetupWizard onComplete={handleSetupComplete} />
                <Toaster position="top-right" richColors />
              </SlicerUIProvider>
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
            <SlicerUIProvider>
              {/*
                Enable react-router future flags to opt into upcoming behavior and silence
                development warnings about future flags. These are safe opt-ins for our
                current router version and recommended by react-router maintainers.
              */}
              <Router
                // Future flags documented by react-router to opt into v7 behaviors. See
                // https://reactrouter.com/en/main/upgrading/v6
                future={{
                  // prevents double-slash when basename and paths are combined
                  v7_preventBasepathDoubleSlash: true,
                  // use route ids in path generation where applicable
                  v7_useIdInRoutePaths: true,
                  // wrap state updates in React.startTransition (opt-in for upcoming v7)
                  v7_startTransition: true,
                  // change relative path resolution in splat routes to v7 behavior
                  v7_relativeSplatPath: true,
                }}
              >
                <AuthenticatedAppRoutes />
              </Router>
              <ReactQueryDevtools initialIsOpen={false} />
              <Toaster position="top-right" richColors />
            </SlicerUIProvider>
          </QueryClientProvider>
        </AuthProvider>
      </ThemeProvider>
    </ErrorBoundary>
  );
}

export default App;

// Common components
import { ProtectedRoute } from '@/features/auth/components/ProtectedRoute';
import { ErrorBoundary } from '@/common/components/ErrorBoundary';
import { Layout } from '@/common/components/Layout';
import { PrinterDashboard } from '@/features/printers/components/PrinterDashboard';
import { SetupWizard } from '@/features/auth/components/SetupWizard';

// Contexts & Providers
import { AuthProvider } from '@/common/contexts/AuthContext';
import { ThemeProvider } from '@/contexts/ThemeContext';
import { SlicerUIProvider } from '@/contexts/SlicerUIContext';
import { SlicerProvider } from '@/contexts/SlicerContext';
import { useAuth } from '@/features/auth/hooks/useAuth';

// Hooks & Utils
import { useUnifiedLogging } from '@/common/hooks/useUnifiedLogging';

// Services
import { assetService } from '@/services/assetService';
import { printerSignalRService } from '@/services/printer-signalr';
import { apiClient } from '@/services/api';

// Feature Pages
import { CatalogPage } from '@/features/catalog/pages/CatalogPage';
import { FilamentManagementPage } from '@/features/filamentManagement/pages/FilamentManagementPage';
import { PrintersPage } from '@/features/printers/pages/PrintersPage';
import { PrinterGroupsPage } from '@/features/printer-groups/pages/PrinterGroupsPage';
import { LocationManagementAdminPage } from '@/features/admin/pages/LocationManagementAdminPage';
import { UserManagementPage } from '@/features/admin/pages/UserManagementPage';
import { SettingsPage } from '@/features/admin/pages/SettingsPage';
import { TagAdminPage } from '@/features/admin/pages/TagAdminPage';
import { DataManagementPage } from '@/features/admin/pages/DataManagementPage';
import { SystemDashboardPage } from '@/features/admin/pages/SystemDashboardPage';
import { ApiKeysPage } from '@/features/profile/pages/ApiKeysPage';
import { PrintQueueDashboardPage } from '@/features/queue/pages/PrintQueueDashboardPage';
import { LoginPage } from '@/features/auth/pages/LoginPage';
import { ForgotPasswordPage } from '@/features/auth/pages/ForgotPasswordPage';
import { ResetPasswordPage } from '@/features/auth/pages/ResetPasswordPage';
import { ConfirmEmailPage } from '@/features/auth/pages/ConfirmEmailPage';
import { RegistrationPendingPage } from '@/features/auth/pages/RegistrationPendingPage';
import { ProfileImportWizardPage } from '@/features/tasks';
// Admin pages may be missing in some branches; use inline placeholders in routes below.
// Observability/FileHealth/Tags admin pages may be missing in this branch.
import { FilesPage } from '@/features/files/pages/FilesPage';
import SlicerJobStatus from '@/features/slicer/components/SlicerJobStatus';
import { FileHealthDashboard } from '@/features/gcode/components/file-health';
import { MaintenanceDashboardPage } from '@/features/maintenance/pages/MaintenanceDashboardPage';
import { PrinterMaintenancePage } from '@/features/maintenance/pages/PrinterMaintenancePage';
import { CamerasPage } from '@/features/cameras/pages/CamerasPage';
import { NfcDevicesPage } from '@/features/nfc/pages/NfcDevicesPage';
import { StatisticsPage } from '@/features/statistics/pages/StatisticsPage';
import { AnalyticsDashboardPage } from '@/features/analytics/pages/AnalyticsDashboardPage';
import { WebhooksAdminPage } from '@/features/webhooks/pages/WebhooksAdminPage';
import { LocationDashboardPage } from '@/features/locations/pages/LocationDashboardPage';
import { useSlicer } from '@/hooks/useSlicer';

// External packages
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ReactQueryDevtools } from '@tanstack/react-query-devtools';
import { lazy, Suspense, useEffect, useState } from 'react';
import { Route, BrowserRouter as Router, Routes, Navigate, useLocation, Outlet } from 'react-router';
import { Toaster } from 'sonner';
import { signalRService as harvestSignalRService } from '@/services/harvest-signalr';
import './App.css';

const LazyWorkerManagementPage = lazy(() =>
  import('@/features/slicer/pages/WorkerManagementPage').then(mod => ({ default: mod.WorkerManagementPage }))
);
const LazySlicerProfilesPage = lazy(() =>
  import('@/features/slicer/pages/SlicerProfilesPage').then(mod => ({ default: mod.SlicerProfilesPage }))
);
const LazyNewSliceJobPage = lazy(() =>
  import('@/features/slicer/pages/NewSliceJobPage').then(mod => ({ default: mod.NewSliceJobPage }))
);
const LazyOrcaSlicerPage = lazy(() => import('@/features/slicer/pages/OrcaSlicerPage'));

function RouteLoader() {
  return (
    <div className="flex items-center justify-center min-h-[40vh]" role="status" aria-label="Loading">
      <div className="pf-animate-spin rounded-full h-8 w-8 border-b-2 border-pf-accent"></div>
    </div>
  );
}

function RouteSuspense({ children }: { children: React.ReactNode }) {
  return <Suspense fallback={<RouteLoader />}>{children}</Suspense>;
}

function SlicerUnavailableMessage() {
  return (
    <div className="p-6 max-w-3xl">
      <h1 className="text-xl font-semibold text-pf-text-primary">Slicer is not available</h1>
      <p className="mt-2 text-pf-text-secondary">
        The 3D slicer workspace loads only when a slicer worker is enabled and registered.
      </p>
      <p className="mt-2 text-sm text-pf-text-tertiary">
        If you expect slicing to work here, enable the worker and/or register at least one slicer service.
      </p>
    </div>
  );
}

function SlicerGate({ children }: { children: React.ReactNode }) {
  const { isLoading, isSlicerAvailable } = useSlicer();
  if (isLoading) return <RouteLoader />;
  if (!isSlicerAvailable) return <SlicerUnavailableMessage />;
  return children;
}

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
        <Route path="printers/:printerId/maintenance" element={<PrinterMaintenancePage />} />
        <Route path="printer-groups" element={<ProtectedRoute requiredRole="farm_admin"><PrinterGroupsPage /></ProtectedRoute>} />
        <Route path="printQueue" element={<PrintQueueDashboardPage />} />
        <Route path="printQueue/:tabId" element={<PrintQueueDashboardPage />} />
        <Route path="files/*" element={<FilesPage />} />
        <Route path="spools" element={<FilamentManagementPage />} />
        <Route path="spools/:tabId" element={<FilamentManagementPage />} />
        <Route path="cameras" element={<CamerasPage />} />
        <Route path="cameras/:tabId" element={<CamerasPage />} />
        <Route path="nfc-devices" element={<NfcDevicesPage />} />
        <Route path="maintenance" element={<MaintenanceDashboardPage />} />
        <Route path="statistics" element={<StatisticsPage />} />
        <Route path="analytics" element={<AnalyticsDashboardPage />} />
        <Route path="locations" element={<ProtectedRoute requiredRole="farm_admin"><LocationManagementAdminPage /></ProtectedRoute>} />
        <Route path="locations/dashboard" element={<LocationDashboardPage />} />
        <Route path="catalog" element={<ProtectedRoute requiredRole="farm_admin"><CatalogPage /></ProtectedRoute>} />
        <Route path="users" element={<ProtectedRoute requiredRole="farm_admin"><UserManagementPage /></ProtectedRoute>} />
        <Route path="settings" element={<ProtectedRoute requiredRole="farm_admin"><SettingsPage /></ProtectedRoute>} />
        <Route path="profile/api-keys" element={<ApiKeysPage />} />
        <Route path="admin" element={<ProtectedRoute requiredRole="farm_admin"><Outlet /></ProtectedRoute>}>
          <Route path="slicer/job-status/:id" element={<SlicerJobStatus />} />
          <Route path="printers" element={<PrintersPage />} />
          <Route path="workers" element={<RouteSuspense><LazyWorkerManagementPage /></RouteSuspense>} />
          <Route path="file-health" element={<FileHealthDashboard />} />
          <Route path="slicer-profiles" element={<RouteSuspense><LazySlicerProfilesPage /></RouteSuspense>} />
          <Route path="tags" element={<TagAdminPage />} />
          <Route path="webhooks" element={<WebhooksAdminPage />} />
          <Route path="data" element={<DataManagementPage />} />
          <Route path="system" element={<SystemDashboardPage />} />
          <Route path="monitoring" element={<Navigate to="/admin/system?tab=monitoring" replace />} />
          <Route path="cameras" element={<Navigate to="/cameras/manage" replace />} />
        </Route>
        <Route path="jobs/new" element={<RouteSuspense><LazyNewSliceJobPage /></RouteSuspense>} />
        <Route
          path="slicer"
          element={
            <SlicerGate>
              <RouteSuspense>
                <LazyOrcaSlicerPage />
              </RouteSuspense>
            </SlicerGate>
          }
        />
        <Route path="profiles/import" element={<ProfileImportWizardPage />} />
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

  // Eagerly establish SignalR connections on app startup for faster realtime updates
  useEffect(() => {
    // Connect both SignalR services in the background
    // These will establish connections and start receiving updates immediately
    Promise.all([
      printerSignalRService.connect(),
      harvestSignalRService.connect()
    ]).catch(err => {
      logger.warn('Failed to establish SignalR connections', {
        error: err instanceof Error ? err.message : String(err)
      });
    });
  }, [logger]);

  useEffect(() => {
    const checkSetupStatus = async () => {
      logger.info('Checking setup status');
      try {
        const data = await apiClient.getSetupStatus();
        setSetupComplete(!data.needsSetup);
        logger.info('Setup status retrieved', {
          needsSetup: data.needsSetup,
          setupComplete: !data.needsSetup
        });
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
              <SlicerProvider>
                <Router>
                  <AuthenticatedAppRoutes />
                </Router>
                <ReactQueryDevtools initialIsOpen={false} />
                <Toaster position="top-right" richColors />
              </SlicerProvider>
            </SlicerUIProvider>
          </QueryClientProvider>
        </AuthProvider>
      </ThemeProvider>
    </ErrorBoundary>
  );
}

export default App;

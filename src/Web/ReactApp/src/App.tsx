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

import { Alert } from '@/common/components/ui';
import { useSystemCapabilities } from '@/common/hooks/useSystemCapabilities';

// Hooks & Utils
import { useUnifiedLogging } from '@/common/hooks/useUnifiedLogging';

// Services
import { assetService } from '@/services/assetService';
import { printerSignalRService } from '@/services/printer-signalr';
import { apiClient } from '@/services/api';

// Feature Pages
import { CatalogPage } from '@/features/catalog/pages/CatalogPage';
import { PrintersPage } from '@/features/printers/pages/PrintersPage';
import { PrinterGroupsPage } from '@/features/printer-groups/pages/PrinterGroupsPage';
import { SettingsPage } from '@/features/admin/pages/SettingsPage';
import { SettingsShell } from '@/features/settings/pages/SettingsShell';
import { SystemDashboardPage } from '@/features/admin/pages/SystemDashboardPage';
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
import { ProjectsPage } from '@/features/projects/pages/ProjectsPage';
import { FileHealthDashboard } from '@/features/gcode/components/file-health';
import { MaintenanceDashboardPage } from '@/features/maintenance/pages/MaintenanceDashboardPage';
import { PrinterMaintenancePage } from '@/features/maintenance/pages/PrinterMaintenancePage';
import { StatisticsPage } from '@/features/statistics/pages/StatisticsPage';
import { CostDashboardPage } from '@/features/statistics/pages/CostDashboardPage';
import { AnalyticsDashboardPage } from '@/features/analytics/pages/AnalyticsDashboardPage';
import { LocationDashboardPage } from '@/features/locations/pages/LocationDashboardPage';
import { AutoDispatchDashboardPage } from '@/features/auto-dispatch/pages/AutoDispatchDashboardPage';
import { SchedulingPage } from '@/features/scheduling/pages/SchedulingPage';
import { ApiKeysPage } from '@/features/profile/pages/ApiKeysPage';

// External packages
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ReactQueryDevtools } from '@tanstack/react-query-devtools';
import { lazy, Suspense, useEffect, useState } from 'react';
import { Route, BrowserRouter as Router, Routes, Navigate, useLocation, Outlet } from 'react-router';
import { Toaster, toast } from 'sonner';
import { signalRService as harvestSignalRService } from '@/services/harvest-signalr';
import './App.css';

const LazyWorkerManagementPage = lazy(() =>
  import('@/features/slicer/pages/WorkerManagementPage').then(mod => ({ default: mod.WorkerManagementPage }))
);

const LazyNewSliceJobPage = lazy(() =>
  import('@/features/slicer/pages/NewSliceJobPage').then(mod => ({ default: mod.NewSliceJobPage }))
);

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

/**
 * Route-level gate that blocks access to a feature when platform
 * capabilities report it as disabled (e.g. on ARM / Raspberry Pi).
 * While the capabilities query is loading the children render normally
 * so there is no layout flash on x64.
 */
function FeatureGate({ feature, children }: { feature: 'modelFiles' | 'slicing'; children: React.ReactNode }) {
  const { data: capabilities } = useSystemCapabilities();

  const enabledKey = `${feature}Enabled` as const;
  if (capabilities && !capabilities[enabledKey]) {
    return (
      <div className="p-6 max-w-3xl">
        <Alert type="warning" title="Feature Not Available">
          <span>
            This feature is not available on {capabilities.architecture} platforms.
          </span>
          {capabilities.platformNote && (
            <p className="mt-2 text-sm">{capabilities.platformNote}</p>
          )}
        </Alert>
      </div>
    );
  }

  return <>{children}</>;
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
        <Route path="files/projects" element={<Navigate to="/projects" replace />} />
        <Route path="files/*" element={<FilesPage />} />
        <Route path="projects" element={<ProjectsPage />} />
        <Route path="spools" element={<Navigate to="/settings?tab=filament" replace />} />
        <Route path="spools/:tabId" element={<Navigate to="/settings?tab=filament" replace />} />
        <Route path="cameras" element={<Navigate to="/settings?tab=hardware" replace />} />
        <Route path="cameras/:tabId" element={<Navigate to="/settings?tab=hardware" replace />} />
        <Route path="nfc-devices" element={<Navigate to="/settings?tab=hardware" replace />} />
        <Route path="maintenance" element={<MaintenanceDashboardPage />} />
        <Route path="auto-dispatch" element={<AutoDispatchDashboardPage />} />
        <Route path="statistics" element={<StatisticsPage />} />
        <Route path="statistics/costs" element={<CostDashboardPage />} />
        <Route path="analytics" element={<AnalyticsDashboardPage />} />
        <Route path="scheduling" element={<SchedulingPage />} />
        <Route path="locations" element={<Navigate to="/settings?tab=hardware" replace />} />
        <Route path="locations/dashboard" element={<LocationDashboardPage />} />
        <Route path="catalog" element={<ProtectedRoute requiredRole="farm_admin"><CatalogPage /></ProtectedRoute>} />
        <Route path="users" element={<Navigate to="/settings?tab=users" replace />} />
        <Route path="settings" element={<ProtectedRoute requiredRole="farm_admin"><SettingsShell /></ProtectedRoute>} />
        <Route path="admin/settings-legacy" element={<ProtectedRoute requiredRole="farm_admin"><SettingsPage /></ProtectedRoute>} />
        {/*
         * Access decision: ApiKeysPage is intentionally NOT gated behind farm_admin.
         * API key management is a per-user feature — every authenticated user needs
         * access to create/revoke their own keys. Admins can also reach ApiKeysPage
         * via the Settings shell (users tab), but the direct /profile/api-keys route
         * must remain open to all authenticated users to avoid a regression.
         */}
        <Route path="profile/api-keys" element={<ApiKeysPage />} />
        <Route path="admin" element={<ProtectedRoute requiredRole="farm_admin"><Outlet /></ProtectedRoute>}>
          <Route path="printers" element={<PrintersPage />} />
          <Route path="workers" element={<FeatureGate feature="slicing"><RouteSuspense><LazyWorkerManagementPage /></RouteSuspense></FeatureGate>} />
          <Route path="file-health" element={<FileHealthDashboard />} />
          <Route path="slicer-profiles" element={<Navigate to="/settings?tab=slicing" replace />} />
          <Route path="tags" element={<Navigate to="/settings?tab=data" replace />} />
          <Route path="bed-types" element={<Navigate to="/settings?tab=slicing" replace />} />
          <Route path="custom-fields" element={<Navigate to="/settings?tab=hardware" replace />} />
          <Route path="webhooks" element={<Navigate to="/settings?tab=integrations" replace />} />
          <Route path="quotas" element={<Navigate to="/settings?tab=data" replace />} />
          <Route path="data" element={<Navigate to="/settings?tab=data" replace />} />
          <Route path="system" element={<SystemDashboardPage />} />
          <Route path="monitoring" element={<Navigate to="/admin/system?tab=monitoring" replace />} />
          <Route path="cameras" element={<Navigate to="/cameras/manage" replace />} />
        <Route path="security/login-audit" element={<Navigate to="/settings?tab=users" replace />} />
        </Route>
        <Route path="slicer" element={<FeatureGate feature="slicing"><RouteSuspense><LazyNewSliceJobPage /></RouteSuspense></FeatureGate>} />
        <Route path="slice-jobs" element={<Navigate to="/admin/workers?tab=jobs" replace />} />
        <Route path="slicer-profiles" element={<Navigate to="/settings?tab=slicing" replace />} />
        <Route path="slicer/import-official" element={<Navigate to="/profiles/import" replace />} />
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

    // Listen for Obico ML failure detection events
    const unsubscribe = printerSignalRService.onFailureDetected((event) => {
      const confidencePercent = Math.round(event.confidence * 100);
      const action = event.snapshotUrl
        ? {
            label: 'View',
            onClick: () => window.open(event.snapshotUrl, '_blank', 'noopener,noreferrer'),
          }
        : undefined;

      if (event.autoPaused) {
        toast.error(
          `Failure detected on ${event.printerName} (${confidencePercent}% confidence). Print auto-paused.`,
          { duration: 10_000, action }
        );
        return;
      }

      toast.warning(
        `Failure detected on ${event.printerName} (${confidencePercent}% confidence). Review the printer now.`,
        { duration: 10_000, action }
      );
    });

    return () => {
      unsubscribe();
    };
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

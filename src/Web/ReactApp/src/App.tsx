// Common components
import { ProtectedRoute } from '@/features/auth/components/ProtectedRoute';
import { ErrorBoundary } from '@/common/components/ErrorBoundary';
import { NotFoundPage } from '@/common/components/NotFoundPage';
import { Layout } from '@/common/components/Layout';
import { SetupWizard } from '@/features/auth/components/SetupWizard';

// Contexts & Providers
import { AuthProvider } from '@/common/contexts/AuthContext';
import { ThemeProvider } from '@/contexts/ThemeContext';
import { SlicerUIProvider } from '@/contexts/SlicerUIContext';
import { SlicerProvider } from '@/contexts/SlicerContext';
import { useAuth } from '@/features/auth/hooks/useAuth';

import { Alert, Spinner } from '@/common/components/ui';
import { useSystemCapabilities } from '@/common/hooks/useSystemCapabilities';
import { hasResolvedQueryData } from '@/common/utils/queryState';

// Hooks & Utils
import { useUnifiedLogging } from '@/common/hooks/useUnifiedLogging';
import { QueueRealtimeBridge } from '@/common/components/QueueRealtimeBridge';

// Services
import { assetService } from '@/services/assetService';
import { printerSignalRService } from '@/services/printer-signalr';
import { apiClient } from '@/services/api';

// Feature Pages
import { LoginPage } from '@/features/auth/pages/LoginPage';
import { ForgotPasswordPage } from '@/features/auth/pages/ForgotPasswordPage';
import { ResetPasswordPage } from '@/features/auth/pages/ResetPasswordPage';
import { ConfirmEmailPage } from '@/features/auth/pages/ConfirmEmailPage';
import { RegistrationPendingPage } from '@/features/auth/pages/RegistrationPendingPage';
// Admin pages may be missing in some branches; use inline placeholders in routes below.
// Observability/FileHealth/Tags admin pages may be missing in this branch.

// External packages
import { QueryClientProvider } from '@tanstack/react-query';
import { ReactQueryDevtools } from '@tanstack/react-query-devtools';
import { queryClient } from '@/services/queryClient';
import { lazy, Suspense, useEffect, useState } from 'react';
import { Route, BrowserRouter as Router, Routes, Navigate, useLocation, Outlet } from 'react-router';
import { Toaster, toast } from 'sonner';
import { signalRService as harvestSignalRService } from '@/services/harvest-signalr';
import './App.css';

const LazyPrinterDashboard = lazy(() =>
  import('@/features/printers/components/PrinterDashboard').then(mod => ({ default: mod.PrinterDashboard }))
);
const LazyCatalogPage = lazy(() =>
  import('@/features/catalog/pages/CatalogPage').then(mod => ({ default: mod.CatalogPage }))
);
const LazyFilamentManagementPage = lazy(() =>
  import('@/features/filamentManagement/pages/FilamentManagementPage').then(mod => ({ default: mod.FilamentManagementPage }))
);
const LazyPrintersPage = lazy(() =>
  import('@/features/printers/pages/PrintersPage').then(mod => ({ default: mod.PrintersPage }))
);
const LazyPrinterGroupsPage = lazy(() =>
  import('@/features/printer-groups/pages/PrinterGroupsPage').then(mod => ({ default: mod.PrinterGroupsPage }))
);
const LazySettingsShell = lazy(() =>
  import('@/features/settings/pages/SettingsShell').then(mod => ({ default: mod.SettingsShell }))
);
const LazyApiKeysPage = lazy(() =>
  import('@/features/profile/pages/ApiKeysPage').then(mod => ({ default: mod.ApiKeysPage }))
);
const LazyPrintQueueDashboardPage = lazy(() =>
  import('@/features/queue/pages/PrintQueueDashboardPage').then(mod => ({ default: mod.PrintQueueDashboardPage }))
);
const LazyFilesPage = lazy(() =>
  import('@/features/files/pages/FilesPage').then(mod => ({ default: mod.FilesPage }))
);
const LazyProjectsPage = lazy(() =>
  import('@/features/projects/pages/ProjectsPage').then(mod => ({ default: mod.ProjectsPage }))
);
const LazyMaintenanceDashboardPage = lazy(() =>
  import('@/features/maintenance/pages/MaintenanceDashboardPage').then(mod => ({ default: mod.MaintenanceDashboardPage }))
);
const LazyPrinterMaintenancePage = lazy(() =>
  import('@/features/maintenance/pages/PrinterMaintenancePage').then(mod => ({ default: mod.PrinterMaintenancePage }))
);
const LazyNfcBindingsPage = lazy(() =>
  import('@/features/nfc/pages/NfcBindingsPage').then(mod => ({ default: mod.NfcBindingsPage }))
);
const LazyAnalyticsHubPage = lazy(() =>
  import('@/features/analytics/pages/AnalyticsHubPage').then(mod => ({ default: mod.AnalyticsHubPage }))
);
const LazyLocationDashboardPage = lazy(() =>
  import('@/features/locations/pages/LocationDashboardPage').then(mod => ({ default: mod.LocationDashboardPage }))
);
const LazyAutoDispatchDashboardPage = lazy(() =>
  import('@/features/auto-dispatch/pages/AutoDispatchDashboardPage').then(mod => ({ default: mod.AutoDispatchDashboardPage }))
);
const LazySchedulingPage = lazy(() =>
  import('@/features/scheduling/pages/SchedulingPage').then(mod => ({ default: mod.SchedulingPage }))
);
const LazyPowerMonitorSettingsPage = lazy(() =>
  import('@/features/power-monitors').then(mod => ({ default: mod.PowerMonitorSettingsPage }))
);
const LazyNotificationPreferencesPage = lazy(() =>
  import('@/features/notifications/pages/NotificationPreferencesPage').then(mod => ({ default: mod.NotificationPreferencesPage }))
);
const LazyPasskeysPage = lazy(() =>
  import('@/features/profile/pages/PasskeysPage').then(mod => ({ default: mod.PasskeysPage }))
);
const LazyPrintablesOAuthCallbackPage = lazy(() =>
  import('@/features/models3d/pages/PrintablesOAuthCallbackPage').then(mod => ({ default: mod.PrintablesOAuthCallbackPage }))
);
const LazyProfileImportWizardPage = lazy(() =>
  import('@/features/tasks').then(mod => ({ default: mod.ProfileImportWizardPage }))
);
const LazyNewSliceJobPage = lazy(() =>
  import('@/features/slicer/pages/NewSliceJobPage').then(mod => ({ default: mod.NewSliceJobPage }))
);
const LazyAdminControlCenterPage = lazy(() =>
  import('@/features/admin/pages/AdminControlCenterPage').then(mod => ({ default: mod.AdminControlCenterPage }))
);

const LazyPartsInventoryPage = lazy(() =>
  import('@/features/parts-inventory/pages/PartsInventoryPage').then(mod => ({
    default: mod.PartsInventoryPage,
  }))
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

function lazyRoute(children: React.ReactNode) {
  return <RouteSuspense>{children}</RouteSuspense>;
}

/**
 * Route-level gate that blocks access to a feature when platform
 * capabilities report it as disabled (e.g. on ARM / Raspberry Pi).
 */
function FeatureGate({ feature, children }: { feature: 'modelFiles' | 'slicing'; children: React.ReactNode }) {
  const { data: capabilities, error } = useSystemCapabilities();

  if (error) {
    return (
      <div className="p-6 max-w-3xl">
        <Alert type="error" title="Unable to Check Feature Availability">
          Platform capabilities could not be loaded.
        </Alert>
      </div>
    );
  }

  if (!hasResolvedQueryData(capabilities)) {
    return (
      <div className="flex items-center justify-center min-h-[40vh]" role="status" aria-label="Loading platform capabilities">
        <Spinner size="lg" />
      </div>
    );
  }

  const enabledKey = `${feature}Enabled` as const;
  if (!capabilities[enabledKey]) {
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

function AuthenticatedAppRoutes() {
  // Custom global ProtectedRoute logic for redirecting guests and unapproved users
  const { isAuthenticated, isLoading, user } = useAuth();
  const location = useLocation();
  const { logger } = useUnifiedLogging({
    component: 'AuthenticatedAppRoutes',
    logLifecycle: false,
  });

  useEffect(() => {
    if (!isAuthenticated) {
      return;
    }

    harvestSignalRService.connect().catch(err => {
      logger.warn('Failed to establish authenticated SignalR connection', {
        error: err instanceof Error ? err.message : String(err),
      });
    });

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
          { duration: 10_000, action },
        );
        return;
      }

      toast.warning(
        `Failure detected on ${event.printerName} (${confidencePercent}% confidence). Review the printer now.`,
        { duration: 10_000, action },
      );
    });

    return unsubscribe;
  }, [isAuthenticated, logger, user?.id]);

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
    <>
      {isAuthenticated && user?.isActive !== false && <QueueRealtimeBridge />}
      <Routes>
      <Route path="/login" element={<LoginPage />} />
      <Route path="/forgot-password" element={<ForgotPasswordPage />} />
      <Route path="/reset-password" element={<ResetPasswordPage />} />
      <Route path="/confirm-email" element={<ConfirmEmailPage />} />
      <Route path="/oauth/printables/callback" element={lazyRoute(<LazyPrintablesOAuthCallbackPage />)} />
      <Route path="/registration-pending" element={<RegistrationPendingPage />} />
      <Route path="/*" element={<Layout />}>
        <Route index element={lazyRoute(<LazyPrinterDashboard />)} />
        <Route path="dashboard" element={lazyRoute(<LazyPrinterDashboard />)} />
        <Route path="printers" element={lazyRoute(<LazyPrintersPage />)} />
        <Route path="printers/:printerId" element={lazyRoute(<LazyPrintersPage />)} />
        <Route path="printers/:printerId/maintenance" element={lazyRoute(<LazyPrinterMaintenancePage />)} />
        <Route path="printer-groups" element={<ProtectedRoute requiredPermission={{ resource: 'printers', action: 'admin' }}>{lazyRoute(<LazyPrinterGroupsPage />)}</ProtectedRoute>} />
        <Route path="printQueue" element={lazyRoute(<LazyPrintQueueDashboardPage />)} />
        <Route path="printQueue/:tabId" element={lazyRoute(<LazyPrintQueueDashboardPage />)} />
        <Route path="files/*" element={lazyRoute(<LazyFilesPage />)} />
        <Route path="projects" element={lazyRoute(<LazyProjectsPage />)} />
        <Route path="spools" element={lazyRoute(<LazyFilamentManagementPage />)} />
        <Route path="spools/:tabId" element={lazyRoute(<LazyFilamentManagementPage />)} />
        <Route path="nfc-bindings" element={lazyRoute(<LazyNfcBindingsPage />)} />
        <Route path="maintenance" element={lazyRoute(<LazyMaintenanceDashboardPage />)} />
        <Route path="parts-inventory" element={<ProtectedRoute requiredPermission={{ resource: 'parts_inventory', action: 'admin' }}>{lazyRoute(<LazyPartsInventoryPage />)}</ProtectedRoute>} />
        <Route path="parts-inventory/:tabId" element={<ProtectedRoute requiredPermission={{ resource: 'parts_inventory', action: 'admin' }}>{lazyRoute(<LazyPartsInventoryPage />)}</ProtectedRoute>} />
        <Route path="auto-dispatch" element={lazyRoute(<LazyAutoDispatchDashboardPage />)} />
        <Route path="analytics" element={lazyRoute(<LazyAnalyticsHubPage />)} />
        <Route path="scheduling" element={lazyRoute(<LazySchedulingPage />)} />
        <Route path="locations" element={<Outlet />}>
          <Route index element={lazyRoute(<LazyLocationDashboardPage />)} />
        </Route>
        <Route path="catalog" element={<ProtectedRoute requiredPermission={{ resource: 'catalog', action: 'admin' }}>{lazyRoute(<LazyCatalogPage />)}</ProtectedRoute>} />
        <Route path="settings" element={lazyRoute(<LazySettingsShell routeScope="user" />)} />
        {/*
         * Access decision: ApiKeysPage is intentionally NOT gated behind farm_admin.
         * API key management is a per-user feature — every authenticated user needs
         * access to create/revoke their own keys. Admins can also reach ApiKeysPage
         * via the User Settings profile section, but the direct /profile/api-keys route
         * must remain open to all authenticated users to avoid a regression.
         */}
        <Route path="profile/api-keys" element={lazyRoute(<LazyApiKeysPage />)} />
        <Route path="profile/notifications" element={lazyRoute(<LazyNotificationPreferencesPage />)} />
        <Route path="profile/passkeys" element={lazyRoute(<LazyPasskeysPage />)} />
        {/*
         * The `/admin` outlet itself is intentionally NOT role-gated (#1457):
         * a custom role granted a specific resource permission (e.g.
         * `printers:admin`) must be able to reach `/admin/settings?tab=...`
         * even though it can't reach every admin surface. The Control Center
         * hub, SettingsShell's scope/tab gating, and each admin destination's
         * own `requiredPermission` are what actually enforce access from here
         * down. The server remains the real enforcement point regardless.
         */}
        <Route path="admin" element={<Outlet />}>
          <Route index element={lazyRoute(<LazyAdminControlCenterPage />)} />
          <Route path="settings" element={lazyRoute(<LazySettingsShell routeScope="system" />)} />
          <Route path="manage" element={lazyRoute(<LazySettingsShell routeScope="admin" />)} />
          <Route path="power-monitors" element={lazyRoute(<LazyPowerMonitorSettingsPage />)} />
        </Route>
        <Route path="slicer" element={<FeatureGate feature="slicing"><RouteSuspense><LazyNewSliceJobPage /></RouteSuspense></FeatureGate>} />
        <Route path="profiles/import" element={lazyRoute(<LazyProfileImportWizardPage />)} />
        <Route path="*" element={<NotFoundPage />} />
      </Route>
      </Routes>
    </>
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

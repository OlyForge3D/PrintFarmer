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

function LegacySettingsRedirect({
  to,
  searchParamMap,
}: {
  to: string;
  searchParamMap?: Record<string, string>;
}) {
  const location = useLocation();
  const [pathname, search = ''] = to.split('?');
  const currentSearchParams = new URLSearchParams(location.search);
  const nextSearchParams = new URLSearchParams(search);

  Object.entries(searchParamMap ?? {}).forEach(([fromKey, toKey]) => {
    const value = currentSearchParams.get(fromKey);
    if (value) {
      nextSearchParams.set(toKey, value);
    }
  });

  const nextLocation = nextSearchParams.toString()
    ? `${pathname}?${nextSearchParams.toString()}`
    : pathname;

  return <Navigate to={nextLocation} replace />;
}

const LEGACY_SYSTEM_TAB_MAP: Record<string, string> = {
  services: '/admin/manage?tab=operations&sub=workers',
  status: '/admin/manage?tab=operations&sub=status',
  logs: '/admin/manage?tab=operations&sub=status',
  connections: '/admin/manage?tab=operations&sub=status',
  monitoring: '/admin/manage?tab=operations&sub=status',
};

function LegacySystemTabRedirect() {
  const location = useLocation();
  const tabParam = new URLSearchParams(location.search).get('tab');
  const target = (tabParam && LEGACY_SYSTEM_TAB_MAP[tabParam]) || '/admin/manage?tab=operations&sub=status';
  return <Navigate to={target} replace />;
}

function SystemSettingsRoute() {
  const location = useLocation();
  const params = new URLSearchParams(location.search);
  if (params.get('tab') === 'hardware' && params.get('sub') === 'locations') {
    return <Navigate to="/locations/dashboard" replace />;
  }

  return lazyRoute(<LazySettingsShell routeScope="system" />);
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

    Promise.all([
      printerSignalRService.connect(),
      harvestSignalRService.connect(),
    ]).catch(err => {
      logger.warn('Failed to establish authenticated SignalR connections', {
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
        <Route path="printer-groups" element={<ProtectedRoute requiredRole="farm_admin">{lazyRoute(<LazyPrinterGroupsPage />)}</ProtectedRoute>} />
        <Route path="printQueue" element={lazyRoute(<LazyPrintQueueDashboardPage />)} />
        <Route path="printQueue/:tabId" element={lazyRoute(<LazyPrintQueueDashboardPage />)} />
        <Route path="files/projects" element={<Navigate to="/projects" replace />} />
        <Route path="files/*" element={lazyRoute(<LazyFilesPage />)} />
        <Route path="projects" element={lazyRoute(<LazyProjectsPage />)} />
        <Route path="spools" element={lazyRoute(<LazyFilamentManagementPage />)} />
        <Route path="spools/:tabId" element={lazyRoute(<LazyFilamentManagementPage />)} />
        <Route path="cameras" element={<Navigate to="/admin/settings?tab=hardware&sub=cameras" replace />} />
        <Route path="cameras/:tabId" element={<Navigate to="/admin/settings?tab=hardware&sub=cameras" replace />} />
        <Route path="nfc-devices" element={<Navigate to="/admin/settings?tab=hardware&sub=nfc" replace />} />
        <Route path="nfc-bindings" element={lazyRoute(<LazyNfcBindingsPage />)} />
        <Route path="maintenance" element={lazyRoute(<LazyMaintenanceDashboardPage />)} />
        <Route path="parts-inventory" element={<ProtectedRoute requiredRole="farm_admin">{lazyRoute(<LazyPartsInventoryPage />)}</ProtectedRoute>} />
        <Route path="parts-inventory/:tabId" element={<ProtectedRoute requiredRole="farm_admin">{lazyRoute(<LazyPartsInventoryPage />)}</ProtectedRoute>} />
        <Route path="auto-dispatch" element={lazyRoute(<LazyAutoDispatchDashboardPage />)} />
        <Route path="statistics" element={<Navigate to="/analytics?lens=production" replace />} />
        <Route path="statistics/costs" element={<Navigate to="/analytics?lens=cost" replace />} />
        <Route path="analytics" element={lazyRoute(<LazyAnalyticsHubPage />)} />
        <Route path="scheduling" element={lazyRoute(<LazySchedulingPage />)} />
        <Route path="locations" element={<Navigate to="/locations/dashboard" replace />} />
        <Route path="locations/dashboard" element={lazyRoute(<LazyLocationDashboardPage />)} />
        <Route path="catalog" element={<ProtectedRoute requiredRole="farm_admin">{lazyRoute(<LazyCatalogPage />)}</ProtectedRoute>} />
        <Route path="users" element={<ProtectedRoute requiredRole="farm_admin"><Navigate to="/admin/manage?tab=users&sub=accounts" replace /></ProtectedRoute>} />
        <Route path="settings" element={lazyRoute(<LazySettingsShell routeScope="user" />)} />
        <Route path="settings/system" element={<ProtectedRoute requiredRole="farm_admin"><Navigate to="/admin/settings?tab=general" replace /></ProtectedRoute>} />
        <Route path="admin/settings-legacy" element={<ProtectedRoute requiredRole="farm_admin"><Navigate to="/admin/settings?tab=general" replace /></ProtectedRoute>} />
        {/*
         * Access decision: ApiKeysPage is intentionally NOT gated behind farm_admin.
         * API key management is a per-user feature — every authenticated user needs
         * access to create/revoke their own keys. Admins can also reach ApiKeysPage
         * via the User Settings profile section, but the direct /profile/api-keys route
         * must remain open to all authenticated users to avoid a regression.
         */}
        <Route path="preferences" element={<Navigate to="/settings" replace />} />
        <Route path="profile/api-keys" element={lazyRoute(<LazyApiKeysPage />)} />
        <Route path="profile/notifications" element={lazyRoute(<LazyNotificationPreferencesPage />)} />
        <Route path="profile/passkeys" element={lazyRoute(<LazyPasskeysPage />)} />
        <Route path="admin" element={<ProtectedRoute requiredRole="farm_admin"><Outlet /></ProtectedRoute>}>
          <Route index element={lazyRoute(<LazyAdminControlCenterPage />)} />
          <Route path="settings" element={<SystemSettingsRoute />} />
          <Route path="manage" element={lazyRoute(<LazySettingsShell routeScope="admin" />)} />
          <Route path="printers" element={<Navigate to="/printers" replace />} />
          <Route path="workers" element={<LegacySettingsRedirect to="/admin/manage?tab=operations&sub=workers" searchParamMap={{ tab: 'workerTab' }} />} />
          <Route path="file-health" element={<Navigate to="/admin/manage?tab=operations&sub=status" replace />} />
          <Route path="slicer-profiles" element={<Navigate to="/admin/settings?tab=slicing&sub=profiles" replace />} />
          <Route path="tags" element={<Navigate to="/admin/manage?tab=data&sub=tags" replace />} />
          <Route path="bed-types" element={<Navigate to="/admin/settings?tab=slicing&sub=bed-types" replace />} />
          <Route path="custom-fields" element={<Navigate to="/admin/settings?tab=hardware&sub=custom-fields" replace />} />
          <Route path="webhooks" element={<Navigate to="/admin/settings?tab=integrations" replace />} />
          <Route path="quotas" element={<Navigate to="/admin/settings?tab=quotas" replace />} />
          <Route path="power-monitors" element={lazyRoute(<LazyPowerMonitorSettingsPage />)} />
          <Route path="data" element={<Navigate to="/admin/manage?tab=data&sub=management" replace />} />
          <Route path="system" element={<LegacySystemTabRedirect />} />
          <Route path="monitoring" element={<Navigate to="/admin/manage?tab=operations&sub=status" replace />} />
          <Route path="cameras" element={<Navigate to="/admin/settings?tab=hardware&sub=cameras" replace />} />
          <Route path="security/login-audit" element={<Navigate to="/admin/manage?tab=users&sub=audit" replace />} />
        </Route>
        <Route path="slicer" element={<FeatureGate feature="slicing"><RouteSuspense><LazyNewSliceJobPage /></RouteSuspense></FeatureGate>} />
        <Route path="slice-jobs" element={<Navigate to="/admin/manage?tab=operations&sub=workers&workerTab=jobs" replace />} />
        <Route path="slicer-profiles" element={<Navigate to="/admin/settings?tab=slicing&sub=profiles" replace />} />
        <Route path="slicer/import-official" element={<Navigate to="/profiles/import" replace />} />
        <Route path="profiles/import" element={lazyRoute(<LazyProfileImportWizardPage />)} />
        <Route path="*" element={<NotFoundPage />} />
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

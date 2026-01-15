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
import { useAuth } from '@/features/auth/hooks/useAuth';

// Hooks & Utils
import { useUnifiedLogging } from '@/common/hooks/useUnifiedLogging';
// Services
import { apiClient } from '@/services/api';
import { assetService } from '@/services/assetService';
import { printerSignalRService } from '@/services/printer-signalr';

// Feature Pages
import { CatalogPage } from '@/features/catalog/pages/CatalogPage';
import { SpoolsPage } from '@/features/catalog/pages/SpoolsPage';
import { PrintersPage } from '@/features/printers/pages/PrintersPage';
import { LocationManagementAdminPage } from '@/features/admin/pages/LocationManagementAdminPage';
import { UserManagementPage } from '@/features/admin/pages/UserManagementPage';
import { SettingsPage } from '@/features/admin/pages/SettingsPage';
import { LogsPage } from '@/features/admin/pages/LogsPage';
import { TagAdminPage } from '@/features/admin/pages/TagAdminPage';
import { WorkerManagementPage } from '@/features/slicer/pages/WorkerManagementPage';
import { SlicerProfilesPage } from '@/features/slicer/pages/SlicerProfilesPage';
import { NewSliceJobPage } from '@/features/slicer/pages/NewSliceJobPage';
import { PrintQueueDashboardPage } from '@/features/queue/pages/PrintQueueDashboardPage';
import { LoginPage } from '@/features/auth/pages/LoginPage';
import { ForgotPasswordPage } from '@/features/auth/pages/ForgotPasswordPage';
import { ResetPasswordPage } from '@/features/auth/pages/ResetPasswordPage';
import { ConfirmEmailPage } from '@/features/auth/pages/ConfirmEmailPage';
import { RegistrationPendingPage } from '@/features/auth/pages/RegistrationPendingPage';
// Admin pages may be missing in some branches; use inline placeholders in routes below.
// Observability/FileHealth/Tags admin pages may be missing in this branch.
import { FilesPage } from '@/features/files/pages/FilesPage';
import SlicerJobStatus from '@/features/slicer/components/SlicerJobStatus';
import { ObservabilityDashboard } from '@/common/components/ObservabilityDashboard';
import { FileHealthDashboard } from '@/features/gcode/components/file-health';

// External packages
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ReactQueryDevtools } from '@tanstack/react-query-devtools';
import { useEffect, useState } from 'react';
import { Route, BrowserRouter as Router, Routes, Navigate, useLocation, Outlet } from 'react-router-dom';
import { Toaster } from 'sonner';
import { signalRService as harvestSignalRService } from '@/services/harvest-signalr';
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
        <Route path="printQueue" element={<PrintQueueDashboardPage />} />
        <Route path="files/*" element={<FilesPage />} />
        <Route path="spools" element={<SpoolsPage />} />
        <Route path="locations" element={<ProtectedRoute requiredRole="farm_admin"><LocationManagementAdminPage /></ProtectedRoute>} />
        <Route path="catalog" element={<ProtectedRoute requiredRole="farm_admin"><CatalogPage /></ProtectedRoute>} />
        <Route path="users" element={<ProtectedRoute requiredRole="farm_admin"><UserManagementPage /></ProtectedRoute>} />
        <Route path="settings" element={<ProtectedRoute requiredRole="farm_admin"><SettingsPage /></ProtectedRoute>} />
        <Route path="logs" element={<ProtectedRoute requiredRole="farm_admin"><LogsPage /></ProtectedRoute>} />
        <Route path="admin" element={<ProtectedRoute requiredRole="farm_admin"><Outlet /></ProtectedRoute>}>
          <Route path="slicer/job-status/:id" element={<SlicerJobStatus />} />
          <Route path="printers" element={<PrintersPage />} />
          <Route path="workers" element={<WorkerManagementPage />} />
          <Route path="observability" element={<ObservabilityDashboard />} />
          <Route path="file-health" element={<FileHealthDashboard />} />
          <Route path="slicer-profiles" element={<SlicerProfilesPage />} />
          <Route path="tags" element={<TagAdminPage />} />
        </Route>
        <Route path="jobs/new" element={<NewSliceJobPage />} />
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
      setCheckingSetup(true);
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
              <Toaster 
                position="top-right" 
                duration={3000}
                visibleToasts={2}
                theme="system"
                gap={8}
              />
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
              <Toaster 
                position="top-right" 
                duration={3000}
                visibleToasts={2}
                theme="system"
                gap={8}
              />
            </SlicerUIProvider>
          </QueryClientProvider>
        </AuthProvider>
      </ThemeProvider>
    </ErrorBoundary>
  );
}

export default App;

import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ReactQueryDevtools } from '@tanstack/react-query-devtools';
import { BrowserRouter as Router, Routes, Route } from 'react-router-dom';
import { PrinterDashboard } from '@/components/PrinterDashboard';
import { PrinterTableViewPage } from '@/pages/PrinterTableViewPage';
import { ModelsPage } from '@/pages/ModelsPage';
import { HarvestPage } from '@/pages/HarvestPage';
import { FilesPage } from '@/pages/FilesPage';
import { CatalogPage } from '@/pages/CatalogPage';
import { SettingsPage } from '@/pages/SettingsPage';
import { SpoolsPage } from '@/pages/SpoolsPage';
import { Layout } from '@/components/Layout';
import { ErrorBoundary } from '@/components/ErrorBoundary';
import { AuthProvider } from '@/contexts/AuthContext';
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
  return (
    <ErrorBoundary>
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
    </ErrorBoundary>
  );
}

export default App;

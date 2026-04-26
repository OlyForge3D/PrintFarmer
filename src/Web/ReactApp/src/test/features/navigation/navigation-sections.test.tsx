import { describe, it, expect, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { Layout } from '@/common/components/Layout';

// Create a test query client
const createTestQueryClient = () => new QueryClient({
  defaultOptions: {
    queries: { retry: false },
    mutations: { retry: false },
  },
});

// Mock contexts and hooks
vi.mock('@/features/auth/hooks/useAuth', () => ({
  useAuth: () => ({
    user: { id: '1', email: 'admin@test.com', role: 'farm_admin', isActive: true },
    logout: vi.fn(),
    isAuthenticated: true,
    hasRole: (role: string) => role === 'farm_admin',
    hasPermission: () => true,
  }),
}));

vi.mock('@/hooks/useSlicer', () => ({
  useSlicer: () => ({
    isSlicerAvailable: true,
    isLoading: false,
  }),
}));

vi.mock('@/contexts/ThemeContext', () => ({
  useTheme: () => ({
    theme: 'light',
    setTheme: vi.fn(),
  }),
}));

vi.mock('@/common/hooks/useSignalR', () => ({
  useSignalRConnection: () => ({
    isConnected: true,
  }),
}));

vi.mock('@/services/printer-signalr', () => ({
  printerSignalRService: {
    connect: vi.fn().mockResolvedValue(undefined),
    onPrinterStatusUpdate: vi.fn().mockReturnValue(() => {}),
    onAutoDispatchStateChanged: vi.fn().mockReturnValue(() => {}),
  },
}));

// Mock TasksBadge to avoid query client issues
vi.mock('@/features/tasks', () => ({
  TasksBadge: () => null,
}));

vi.mock('@/features/printers/hooks/useAutoDispatch', () => ({
  useAllAutoDispatchStatuses: () => ({
    data: [],
    isLoading: false,
  }),
}));

describe('Navigation Section Headers', () => {
  const renderLayout = () => {
    const queryClient = createTestQueryClient();
    return render(
      <QueryClientProvider client={queryClient}>
        <MemoryRouter>
          <Layout />
        </MemoryRouter>
      </QueryClientProvider>
    );
  };

  describe('Slicer profile browsing', () => {
    it('shows slice entry points including slicer profiles in the nav', () => {
      renderLayout();

      expect(screen.getByRole('link', { name: /^slice$/i })).toBeInTheDocument();
      expect(screen.getByRole('link', { name: /slicer profiles/i })).toBeInTheDocument();
    });
  });

  // NOTE: These tests validate the FUTURE implementation of section headers
  // They may fail until PFarm1-egw is merged
  describe('Section Header Rendering (Future Validation)', () => {
    it.skip('renders Operations section header with correct text', () => {
      renderLayout();

      const operationsHeader = screen.getByText('Operations', { selector: 'div.text-xs.uppercase.tracking-wider' });
      expect(operationsHeader).toBeInTheDocument();
    });

    it.skip('renders Hardware section header with correct text', () => {
      renderLayout();

      const hardwareHeader = screen.getByText('Hardware', { selector: 'div.text-xs.uppercase.tracking-wider' });
      expect(hardwareHeader).toBeInTheDocument();
    });

    it.skip('renders Management section header with correct text', () => {
      renderLayout();

      const managementHeader = screen.getByText('Management', { selector: 'div.text-xs.uppercase.tracking-wider' });
      expect(managementHeader).toBeInTheDocument();
    });

    it.skip('renders Admin section header with correct text', () => {
      renderLayout();

      const adminHeader = screen.getByText('Admin', { selector: 'div.text-xs.uppercase.tracking-wider' });
      expect(adminHeader).toBeInTheDocument();
    });
  });

  describe('Section Header Non-Interactive Behavior (Future Validation)', () => {
    it.skip('ensures section headers are not interactive (no button or link role)', () => {
      renderLayout();

      const operationsHeader = screen.getByText('Operations', { selector: 'div.text-xs.uppercase.tracking-wider' });
      
      // Section header should not have button or link role
      expect(operationsHeader.tagName).toBe('DIV');
      expect(operationsHeader).not.toHaveAttribute('role', 'button');
      expect(operationsHeader).not.toHaveAttribute('role', 'link');
      expect(operationsHeader.tagName).not.toBe('BUTTON');
      expect(operationsHeader.tagName).not.toBe('A');
    });

    it.skip('ensures section headers use proper styling classes', () => {
      renderLayout();

      const hardwareHeader = screen.getByText('Hardware', { selector: 'div.text-xs.uppercase.tracking-wider' });
      
      expect(hardwareHeader).toHaveClass('text-xs');
      expect(hardwareHeader).toHaveClass('uppercase');
      expect(hardwareHeader).toHaveClass('tracking-wider');
    });
  });

  describe('Nav Items Grouped Under Section Headers (Future Validation)', () => {
    it.skip('groups Dashboard, Printers, Files, etc. under Operations section', () => {
      renderLayout();

      const operationsHeader = screen.getByText('Operations', { selector: 'div.text-xs.uppercase.tracking-wider' });
      const operationsSection = operationsHeader.closest('.navigation-section, nav > div');
      
      expect(operationsSection).toBeInTheDocument();
      
      // Operations section should contain these nav items
      expect(screen.getByText('Dashboard')).toBeInTheDocument();
      expect(screen.getByText('Printers')).toBeInTheDocument();
      expect(screen.getByText('Files')).toBeInTheDocument();
    });

    it.skip('groups Filament Inventory, Cameras under Hardware section', () => {
      renderLayout();

      const hardwareHeader = screen.getByText('Hardware', { selector: 'div.text-xs.uppercase.tracking-wider' });
      expect(hardwareHeader).toBeInTheDocument();
      
      expect(screen.getByText('Filament Inventory')).toBeInTheDocument();
      expect(screen.getByText('Cameras')).toBeInTheDocument();
    });

    it.skip('groups Locations, Catalog, User Accounts under Management section', () => {
      renderLayout();

      const managementHeader = screen.getByText('Management', { selector: 'div.text-xs.uppercase.tracking-wider' });
      expect(managementHeader).toBeInTheDocument();
      
      expect(screen.getByText('Locations')).toBeInTheDocument();
      expect(screen.getByText('Catalog')).toBeInTheDocument();
      expect(screen.getByText('User Accounts')).toBeInTheDocument();
    });

    it.skip('groups Tags, Webhooks, Settings under Admin section', () => {
      renderLayout();

      const adminHeader = screen.getByText('Admin', { selector: 'div.text-xs.uppercase.tracking-wider' });
      expect(adminHeader).toBeInTheDocument();
      
      expect(screen.getByText('Tags')).toBeInTheDocument();
      expect(screen.getByText('Webhooks')).toBeInTheDocument();
      expect(screen.getByText('Settings')).toBeInTheDocument();
    });
  });

  describe('Existing Nav Links Accessibility', () => {
    it.skip('ensures all nav links are still rendered and accessible', () => {
      renderLayout();

      // Check that key navigation links are still present and accessible
      const dashboardLink = screen.getByRole('link', { name: /dashboard/i });
      const printersLink = screen.getByRole('link', { name: /printers/i });
      const filesLink = screen.getByRole('link', { name: /^files$/i });
      const statisticsLink = screen.getByRole('link', { name: /statistics/i });
      
      expect(dashboardLink).toBeInTheDocument();
      expect(printersLink).toBeInTheDocument();
      expect(filesLink).toBeInTheDocument();
      expect(statisticsLink).toBeInTheDocument();
    });

    it.skip('ensures admin links are accessible when user has admin role', () => {
      renderLayout();

      const locationsLink = screen.getByRole('link', { name: /locations/i });
      const catalogLink = screen.getByRole('link', { name: /catalog/i });
      const usersLink = screen.getByRole('link', { name: /user accounts/i });
      
      expect(locationsLink).toBeInTheDocument();
      expect(catalogLink).toBeInTheDocument();
      expect(usersLink).toBeInTheDocument();
    });
  });
});

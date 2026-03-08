import { describe, it, expect, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { Layout } from '@/common/components/Layout';

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
  useSlicer: () => ({ isSlicerAvailable: true, isLoading: false }),
}));

vi.mock('@/contexts/ThemeContext', () => ({
  useTheme: () => ({ theme: 'light', setTheme: vi.fn() }),
}));

vi.mock('@/common/hooks/useSignalR', () => ({
  useSignalRConnection: () => ({ isConnected: true }),
}));

vi.mock('@/services/printer-signalr', () => ({
  printerSignalRService: {
    connect: vi.fn().mockResolvedValue(undefined),
    onPrinterStatusUpdate: vi.fn().mockReturnValue(() => {}),
  },
}));

vi.mock('@/features/tasks', () => ({
  TasksBadge: () => null,
}));

function renderLayout() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter>
        <Layout />
      </MemoryRouter>
    </QueryClientProvider>
  );
}

describe('Navigation Section Headers', () => {
  describe('Section Header Rendering', () => {
    it('renders Operations section header', () => {
      renderLayout();
      expect(screen.getByText('Operations', { selector: 'span.text-xs.uppercase.tracking-wider' })).toBeInTheDocument();
    });

    it('renders Hardware section header', () => {
      renderLayout();
      expect(screen.getByText('Hardware', { selector: 'span.text-xs.uppercase.tracking-wider' })).toBeInTheDocument();
    });

    it('renders Management section header', () => {
      renderLayout();
      expect(screen.getByText('Management', { selector: 'span.text-xs.uppercase.tracking-wider' })).toBeInTheDocument();
    });

    it('renders Admin section header', () => {
      renderLayout();
      expect(screen.getByText('Admin', { selector: 'span.text-xs.uppercase.tracking-wider' })).toBeInTheDocument();
    });
  });

  describe('Section Header Non-Interactive Behavior', () => {
    it('section headers are not interactive', () => {
      renderLayout();
      const header = screen.getByText('Operations', { selector: 'span.text-xs.uppercase.tracking-wider' });
      expect(header.tagName).toBe('SPAN');
      expect(header).not.toHaveAttribute('role', 'button');
      expect(header).not.toHaveAttribute('role', 'link');
    });

    it('section headers use proper styling classes', () => {
      renderLayout();
      const header = screen.getByText('Hardware', { selector: 'span.text-xs.uppercase.tracking-wider' });
      expect(header).toHaveClass('text-xs');
      expect(header).toHaveClass('uppercase');
      expect(header).toHaveClass('tracking-wider');
      expect(header).toHaveClass('text-pf-text-tertiary');
    });
  });

  describe('Nav Items Grouped Under Sections', () => {
    it('Operations contains Dashboard, Printers, Files', () => {
      renderLayout();
      expect(screen.getByText('Operations', { selector: 'span.text-xs.uppercase.tracking-wider' })).toBeInTheDocument();
      expect(screen.getByText('Dashboard')).toBeInTheDocument();
      expect(screen.getByText('Printers')).toBeInTheDocument();
      expect(screen.getByText('Files')).toBeInTheDocument();
    });

    it('Hardware contains Filament Inventory, Cameras, NFC Devices', () => {
      renderLayout();
      expect(screen.getByText('Hardware', { selector: 'span.text-xs.uppercase.tracking-wider' })).toBeInTheDocument();
      expect(screen.getByText('Filament Inventory')).toBeInTheDocument();
      expect(screen.getByText('Cameras')).toBeInTheDocument();
      expect(screen.getByText('NFC Devices')).toBeInTheDocument();
    });

    it('Management contains Maintenance, Statistics, API Keys', () => {
      renderLayout();
      expect(screen.getByText('Management', { selector: 'span.text-xs.uppercase.tracking-wider' })).toBeInTheDocument();
      expect(screen.getByText('Maintenance')).toBeInTheDocument();
      expect(screen.getByText('Statistics')).toBeInTheDocument();
      expect(screen.getByText('API Keys')).toBeInTheDocument();
    });

    it('Admin contains Locations, Catalog, User Accounts, Tags, Settings', () => {
      renderLayout();
      expect(screen.getByText('Admin', { selector: 'span.text-xs.uppercase.tracking-wider' })).toBeInTheDocument();
      expect(screen.getByText('Locations')).toBeInTheDocument();
      expect(screen.getByText('Catalog')).toBeInTheDocument();
      expect(screen.getByText('User Accounts')).toBeInTheDocument();
      expect(screen.getByText('Tags')).toBeInTheDocument();
      expect(screen.getByText('Settings')).toBeInTheDocument();
    });
  });

  describe('Existing Nav Links Accessibility', () => {
    it('all nav links remain accessible', () => {
      renderLayout();
      expect(screen.getByRole('link', { name: /dashboard/i })).toBeInTheDocument();
      expect(screen.getByRole('link', { name: /printers/i })).toBeInTheDocument();
      expect(screen.getByRole('link', { name: /statistics/i })).toBeInTheDocument();
    });

    it('admin links are accessible for admin user', () => {
      renderLayout();
      expect(screen.getByRole('link', { name: /locations/i })).toBeInTheDocument();
      expect(screen.getByRole('link', { name: /catalog/i })).toBeInTheDocument();
      expect(screen.getByRole('link', { name: /user accounts/i })).toBeInTheDocument();
    });
  });
});

import '@testing-library/jest-dom';
import React from 'react';
import { render, screen } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { MemoryRouter } from 'react-router';

// Mock PageTemplate to simplify testing
vi.mock('@/common/components/PageTemplate', () => ({
  PageTemplate: ({ children, title }: { children: React.ReactNode; title: string }) => (
    <div data-testid="page-template" data-title={title}>{children}</div>
  ),
}));

// Mock data hooks so the page renders deterministically without hitting the API layer
vi.mock('../../hooks/useMaintenanceStats', () => ({
  useMaintenanceStats: () => ({
    stats: undefined,
    isLoading: false,
    error: null,
    refetch: vi.fn(),
  }),
}));
vi.mock('../../hooks/useMaintenanceAlerts', () => ({
  useMaintenanceAlerts: () => ({
    alerts: [],
    isLoading: false,
    error: null,
    refetch: vi.fn(),
  }),
}));
vi.mock('../../hooks/useUpcomingMaintenance', () => ({
  useUpcomingMaintenance: () => ({
    tasks: [],
    isLoading: false,
    error: null,
    refetch: vi.fn(),
    overdueCount: 0,
    dueSoonCount: 0,
  }),
}));
vi.mock('../../hooks/useComponentMaintenance', () => ({
  useComponentMaintenance: () => ({
    replacements: [],
    componentNames: [],
    isLoading: false,
    error: null,
    refetch: vi.fn(),
  }),
}));

// Mock every panel-content component with a lightweight test-id stand-in
vi.mock('../../components/FleetMaintenanceOverview', () => ({
  FleetMaintenanceOverview: () => <div data-testid="fleet-overview-content">Fleet Overview</div>,
}));
vi.mock('../../components/MaintenanceStatusGrid', () => ({
  MaintenanceStatusGrid: () => <div data-testid="status-grid-content">Status Grid</div>,
}));
vi.mock('../../components/MaintenancePriorityList', () => ({
  MaintenancePriorityList: () => <div data-testid="priority-list-content">Priority List</div>,
}));
vi.mock('../../components/UpcomingMaintenanceCalendar', () => ({
  UpcomingMaintenanceCalendar: () => <div data-testid="calendar-content">Calendar</div>,
}));
vi.mock('../../components/MaintenanceTimeline', () => ({
  MaintenanceTimeline: () => <div data-testid="timeline-content">Timeline</div>,
}));
vi.mock('../../components/FleetStatisticsTable', () => ({
  FleetStatisticsTable: () => <div data-testid="fleet-stats-content">Fleet Statistics</div>,
}));
vi.mock('../../components/MaintenancePlansTabV2', () => ({
  MaintenancePlansTab: () => <div data-testid="plans-content">Plans</div>,
}));
vi.mock('../../components/PartsInventoryTab', () => ({
  PartsInventoryTab: () => <div data-testid="parts-inventory-content">Parts Inventory</div>,
}));
vi.mock('../../components/TaskCatalogTab', () => ({
  TaskCatalogTab: () => <div data-testid="task-catalog-content">Task Catalog</div>,
}));
vi.mock('../../components/LowStockAlert', () => ({
  LowStockAlert: () => <div data-testid="low-stock-alert-content">Low Stock Alert</div>,
}));
vi.mock('../../components/ComponentReplacementHistory', () => ({
  ComponentReplacementHistory: () => <div data-testid="replacement-history-content">Replacement History</div>,
}));
vi.mock('../../components', () => ({
  MaintenanceTrendsChart: () => <div data-testid="trends-chart-content">Trends Chart</div>,
  ComponentLifespanChart: () => <div data-testid="lifespan-chart-content">Lifespan Chart</div>,
  MaintenanceCostAnalysis: () => <div data-testid="cost-analysis-content">Cost Analysis</div>,
  PrinterUptimeChart: () => <div data-testid="uptime-chart-content">Uptime Chart</div>,
}));
vi.mock('../../components/MaintenanceReport', () => ({
  MaintenanceReport: () => <div data-testid="maintenance-report-content">Maintenance Report</div>,
}));

// Import after mocks are set up
import { MaintenanceDashboardPage } from '../MaintenanceDashboardPage';

const renderWithRouter = (initialRoute = '/maintenance') => {
  return render(
    <MemoryRouter initialEntries={[initialRoute]}>
      <MaintenanceDashboardPage />
    </MemoryRouter>
  );
};

describe('MaintenanceDashboardPage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  describe('tab deep-linking', () => {
    it('defaults to the Dashboard tab and panel when no tab query parameter is present', () => {
      renderWithRouter('/maintenance');
      expect(screen.getByRole('tab', { name: /dashboard/i })).toHaveAttribute('aria-selected', 'true');
      expect(screen.getByTestId('fleet-overview-content')).toBeInTheDocument();
      expect(screen.queryByTestId('parts-inventory-content')).not.toBeInTheDocument();
    });

    it('selects the Inventory tab and renders the inventory panel for tab=inventory (dashboard low-stock deep link)', () => {
      renderWithRouter('/maintenance?tab=inventory');
      expect(screen.getByRole('tab', { name: /inventory/i })).toHaveAttribute('aria-selected', 'true');
      expect(screen.getByTestId('low-stock-alert-content')).toBeInTheDocument();
      expect(screen.getByTestId('parts-inventory-content')).toBeInTheDocument();
      expect(screen.queryByTestId('fleet-overview-content')).not.toBeInTheDocument();
    });

    it('selects the Schedule tab and panel for tab=schedule', () => {
      renderWithRouter('/maintenance?tab=schedule');
      expect(screen.getByRole('tab', { name: /schedule/i })).toHaveAttribute('aria-selected', 'true');
      expect(screen.getByTestId('calendar-content')).toBeInTheDocument();
    });

    it('selects the Library tab and panel for tab=library', () => {
      renderWithRouter('/maintenance?tab=library');
      expect(screen.getByRole('tab', { name: /library/i })).toHaveAttribute('aria-selected', 'true');
      expect(screen.getByTestId('plans-content')).toBeInTheDocument();
    });

    it('selects the Analytics tab and panel for tab=analytics', () => {
      renderWithRouter('/maintenance?tab=analytics');
      expect(screen.getByRole('tab', { name: /analytics/i })).toHaveAttribute('aria-selected', 'true');
      expect(screen.getByTestId('fleet-stats-content')).toBeInTheDocument();
    });

    it('falls back to the Dashboard tab for an unrecognized tab parameter', () => {
      renderWithRouter('/maintenance?tab=bogus');
      expect(screen.getByRole('tab', { name: /dashboard/i })).toHaveAttribute('aria-selected', 'true');
      expect(screen.getByTestId('fleet-overview-content')).toBeInTheDocument();
    });
  });
});

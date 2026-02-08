import '@testing-library/jest-dom';
import React from 'react';
import { render, screen } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { MemoryRouter } from 'react-router';

// Mock the content components
vi.mock('../../../features/admin/components/SystemLogsContent', () => ({
  SystemLogsContent: () => <div data-testid="system-logs-content">System Logs Content</div>
}));
vi.mock('../../../features/admin/components/ObservabilityContent', () => ({
  ObservabilityContent: () => <div data-testid="observability-content">Observability Content</div>
}));
vi.mock('../../../features/admin/components/FileHealthContent', () => ({
  FileHealthContent: () => <div data-testid="file-health-content">File Health Content</div>
}));

// Mock PageTemplate to simplify testing
vi.mock('../../../common/components/PageTemplate', () => ({
  PageTemplate: ({ children, title }: { children: React.ReactNode; title: string }) => (
    <div data-testid="page-template" data-title={title}>{children}</div>
  )
}));

// Import after mocks are set up
import { SystemDashboardPage } from '../../../features/admin/pages/SystemDashboardPage';

const renderWithRouter = (initialRoute = '/admin/system') => {
  return render(
    <MemoryRouter initialEntries={[initialRoute]}>
      <SystemDashboardPage />
    </MemoryRouter>
  );
};

describe('SystemDashboardPage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  describe('rendering', () => {
    it('renders with page template', () => {
      renderWithRouter();
      expect(screen.getByTestId('page-template')).toBeInTheDocument();
      expect(screen.getByTestId('page-template')).toHaveAttribute('data-title', 'System Dashboard');
    });

    it('renders all three tabs', () => {
      renderWithRouter();
      expect(screen.getByRole('tab', { name: /system logs/i })).toBeInTheDocument();
      expect(screen.getByRole('tab', { name: /observability/i })).toBeInTheDocument();
      expect(screen.getByRole('tab', { name: /file health/i })).toBeInTheDocument();
    });
  });

  describe('tab navigation', () => {
    it('defaults to logs tab when no query parameter', () => {
      renderWithRouter('/admin/system');
      expect(screen.getByTestId('system-logs-content')).toBeInTheDocument();
    });

    it('shows logs content when tab=logs', () => {
      renderWithRouter('/admin/system?tab=logs');
      expect(screen.getByTestId('system-logs-content')).toBeInTheDocument();
    });

    it('shows observability content when tab=observability', () => {
      renderWithRouter('/admin/system?tab=observability');
      expect(screen.getByTestId('observability-content')).toBeInTheDocument();
    });

    it('shows file health content when tab=file-health', () => {
      renderWithRouter('/admin/system?tab=file-health');
      expect(screen.getByTestId('file-health-content')).toBeInTheDocument();
    });

    it('defaults to logs tab for invalid tab parameter', () => {
      renderWithRouter('/admin/system?tab=invalid');
      expect(screen.getByTestId('system-logs-content')).toBeInTheDocument();
    });
  });

  describe('accessibility', () => {
    it('has accessible tab list', () => {
      renderWithRouter();
      expect(screen.getByRole('tablist')).toBeInTheDocument();
    });

    it('tabs have proper roles', () => {
      renderWithRouter();
      const tabs = screen.getAllByRole('tab');
      expect(tabs).toHaveLength(3);
    });

    it('tab panels have proper roles', () => {
      renderWithRouter();
      expect(screen.getByRole('tabpanel')).toBeInTheDocument();
    });
  });
});

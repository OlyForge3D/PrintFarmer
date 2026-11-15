import '@testing-library/jest-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { TestRouter } from '@/test/utils/TestRouter';
import { CriticalAlertsBanner } from '@/features/printers/components/CriticalAlertsBanner';
import { apiClient } from '@/services/api';
import { toast } from 'sonner';
import type { PrinterSummary } from '@/types/api';
import { MaintenanceAlertStatus, type MaintenanceAlert } from '@/types/maintenance';

interface BannerState {
  userId?: string;
  alerts: Pick<MaintenanceAlert, 'id' | 'status' | 'severity' | 'createdAt'>[];
  printers: Pick<PrinterSummary, 'id' | 'hasCatalogUpdate' | 'catalogUpdateRevision'>[];
  alertsError: Error | null;
  summariesError: Error | null;
  summariesFetching: boolean;
}

const state = vi.hoisted<BannerState>(() => ({
  userId: 'operator-a',
  alerts: [],
  printers: [],
  alertsError: null,
  summariesError: null,
  summariesFetching: false,
}));

vi.mock('@/features/auth/hooks/useAuth', () => ({
  useAuth: () => ({ user: state.userId ? { id: state.userId } : undefined, hasPermission: () => false }),
}));
vi.mock('@/features/maintenance/hooks/useMaintenanceAlerts', () => ({
  useMaintenanceAlerts: () => ({ alerts: state.alerts, isLoading: false, error: state.alertsError }),
}));
vi.mock('@/features/maintenance/hooks/useMaintenanceComponents', () => ({
  useMaintenanceComponents: () => ({ data: [], isLoading: false }),
}));
vi.mock('@/common/hooks/useApi', () => ({
  usePrinterSummary: () => ({
    data: state.printers, error: state.summariesError, isFetching: state.summariesFetching,
  }),
}));
vi.mock('@/common/hooks/useSignalR', () => ({
  usePrinterStatusUpdates: () => ({ printerStatuses: new Map() }),
}));
vi.mock('@/services/api', () => ({
  apiClient: { applyAllModelTemplates: vi.fn(), getBackgroundServicesSummary: vi.fn() },
}));
vi.mock('sonner', () => ({ toast: { error: vi.fn(), success: vi.fn() } }));

function mountBanner() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(<CriticalAlertsBanner />, {
    wrapper: ({ children }) => (
      <QueryClientProvider client={client}><TestRouter>{children}</TestRouter></QueryClientProvider>
    ),
  });
}

function dismissBoth() {
  fireEvent.click(screen.getByRole('button', { name: /Dismiss 1 overdue/ }));
  fireEvent.click(screen.getByRole('button', { name: /Dismiss 1 printer/ }));
}

function expectBothVisible() {
  expect(screen.getByText('1 overdue maintenance task needs attention')).toBeInTheDocument();
  expect(screen.getByText(/configuration update available/)).toBeInTheDocument();
}

function expectBothHidden() {
  expect(screen.queryByText(/overdue maintenance/)).not.toBeInTheDocument();
  expect(screen.queryByText(/configuration update available/)).not.toBeInTheDocument();
}

describe('CriticalAlertsBanner dismissals', () => {
  beforeEach(() => {
    localStorage.clear();
    vi.clearAllMocks();
    state.userId = 'operator-a';
    state.alerts = [{ id: 'alert-a', status: MaintenanceAlertStatus.Active, severity: 1, createdAt: '2026-11-06T12:00:00Z' }];
    state.printers = [{ id: 'printer-a', hasCatalogUpdate: true, catalogUpdateRevision: '2026-11-06T12:00:00Z' }];
    state.alertsError = null;
    state.summariesError = null;
    state.summariesFetching = false;
  });
  afterEach(() => {
    cleanup();
    vi.restoreAllMocks();
  });

  it('keeps both dismissals across remounts without applying configuration', () => {
    const first = mountBanner();
    dismissBoth();
    expectBothHidden();
    first.unmount();
    mountBanner();
    expectBothHidden();
    expect(apiClient.applyAllModelTemplates).not.toHaveBeenCalled();
  });

  it('shows different underlying items even when their count is unchanged', () => {
    const view = mountBanner();
    dismissBoth();
    state.alerts[0].id = 'alert-b';
    state.printers[0].id = 'printer-b';
    view.rerender(<CriticalAlertsBanner />);
    expectBothVisible();
  });

  it('shows a newer catalog revision for the same printer without observing resolution', () => {
    const view = mountBanner();
    fireEvent.click(screen.getByRole('button', { name: /Dismiss 1 printer/ }));
    state.printers[0].catalogUpdateRevision = '2026-11-07T12:00:00Z';
    view.rerender(<CriticalAlertsBanner />);
    expect(screen.getByText(/configuration update available/)).toBeInTheDocument();
  });

  it('shows maintenance escalation and a new occurrence with the same alert ID', () => {
    const view = mountBanner();
    fireEvent.click(screen.getByRole('button', { name: /Dismiss 1 overdue/ }));
    state.alerts[0].severity = 2;
    view.rerender(<CriticalAlertsBanner />);
    fireEvent.click(screen.getByRole('button', { name: /Dismiss 1 overdue/ }));
    state.alerts[0].createdAt = '2026-11-07T12:00:00Z';
    view.rerender(<CriticalAlertsBanner />);
    expect(screen.getByText(/overdue maintenance/)).toBeInTheDocument();
  });

  it('resets both dismissals after resolution in the same mounted session', () => {
    const view = mountBanner();
    dismissBoth();
    const alerts = state.alerts;
    state.alerts = [];
    state.printers[0].hasCatalogUpdate = false;
    view.rerender(<CriticalAlertsBanner />);
    state.alerts = alerts;
    state.printers[0].hasCatalogUpdate = true;
    view.rerender(<CriticalAlertsBanner />);
    expectBothVisible();
  });

  it('does not interpret failed empty responses as resolution', () => {
    const first = mountBanner();
    dismissBoth();
    const alerts = state.alerts;
    const printers = state.printers;
    state.alerts = [];
    state.printers = [];
    state.alertsError = new Error('Offline');
    state.summariesError = new Error('Offline');
    first.rerender(<CriticalAlertsBanner />);
    first.unmount();
    state.alerts = alerts;
    state.printers = printers;
    state.alertsError = null;
    state.summariesError = null;
    mountBanner();
    expectBothHidden();
  });

  it('does not clear a catalog dismissal while its response is still fetching', () => {
    const first = mountBanner();
    fireEvent.click(screen.getByRole('button', { name: /Dismiss 1 printer/ }));
    const printers = state.printers;
    state.printers = [];
    state.summariesFetching = true;
    first.rerender(<CriticalAlertsBanner />);
    state.printers = printers;
    state.summariesFetching = false;
    first.rerender(<CriticalAlertsBanner />);
    expect(screen.queryByText(/configuration update available/)).not.toBeInTheDocument();
  });

  it('isolates operators even when switching without a remount', () => {
    const view = mountBanner();
    dismissBoth();
    state.userId = 'operator-b';
    view.rerender(<CriticalAlertsBanner />);
    expectBothVisible();
  });

  it('never persists an anonymous dismissal', () => {
    state.userId = undefined;
    const first = mountBanner();
    dismissBoth();
    expect(localStorage.length).toBe(0);
    first.unmount();
    mountBanner();
    expectBothVisible();
  });

  it('does not persistently hide a configuration update whose revision is unavailable', () => {
    state.printers[0].catalogUpdateRevision = null;
    const first = mountBanner();
    fireEvent.click(screen.getByRole('button', { name: /Dismiss 1 printer/ }));
    expect(screen.queryByText(/configuration update available/)).not.toBeInTheDocument();
    first.unmount();
    mountBanner();
    expect(screen.getByText(/configuration update available/)).toBeInTheDocument();
  });

  it('hides for the session and reports failed persistence', () => {
    vi.spyOn(localStorage, 'setItem').mockImplementation(() => {
      throw new DOMException('Storage blocked', 'SecurityError');
    });
    vi.spyOn(console, 'warn').mockImplementation(() => {});
    mountBanner();
    dismissBoth();
    expect(toast.error).toHaveBeenCalledWith('Alert hidden for now, but the dismissal could not be saved');
    expectBothHidden();
  });
});

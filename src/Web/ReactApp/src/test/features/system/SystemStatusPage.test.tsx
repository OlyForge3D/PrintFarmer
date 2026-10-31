import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useQuery } from '@tanstack/react-query';
import { beforeEach, expect, it, vi } from 'vitest';
import { SystemStatusPage } from '@/features/system/pages/SystemStatusPage';
import { useAuth } from '@/features/auth/hooks/useAuth';
import { SystemServiceHealth } from '@/types/api';

vi.mock('@tanstack/react-query', () => ({ useQuery: vi.fn() }));
vi.mock('@/features/auth/hooks/useAuth', () => ({ useAuth: vi.fn() }));
vi.mock('@/services/api', () => ({ apiClient: { getSystemInfo: vi.fn() } }));
const useQueryMock = vi.mocked(useQuery);
const useAuthMock = vi.mocked(useAuth);

beforeEach(() => {
  vi.clearAllMocks();
  useAuthMock.mockReturnValue({ hasPermission: (resource: string, action: string) => resource === 'system_settings' && action === 'admin' } as ReturnType<typeof useAuth>);
  useQueryMock.mockReturnValue({ data: { app: { version: '1.2.3', hostname: 'test', uptime: '1h' },
    cpu: { cores: 4, usagePercent: 10 }, memory: { usedBytes: 0, totalBytes: 0 },
    disk: { usedBytes: 0, totalBytes: 0, archiveBytes: 0, databaseBytes: 0 },
    services: [
      { name: 'Backend API', version: '1.2.3', health: SystemServiceHealth.Healthy },
      { name: 'Slicer worker', version: '1.2.4', engineVersion: '2.4.2', health: SystemServiceHealth.Healthy },
    ],
    database: { engine: 'SQLite', version: '3.45', migrationHeads: ['20260912_Example'], printerCount: 0, archiveCount: 0 } },
  dataUpdatedAt: Date.parse('2026-09-12T12:00:00Z') } as ReturnType<typeof useQuery>);
});

it('shows application and worker versions, health, and database migration heads', () => {
  render(<SystemStatusPage />);
  expect(screen.getByText('System status')).toBeVisible();
  expect(screen.getByRole('columnheader', { name: 'Application version' })).toBeVisible();
  expect(screen.getByRole('columnheader', { name: 'Engine version' })).toBeVisible();
  expect(screen.getByText('2.4.2')).toBeVisible();
  expect(screen.getAllByText('Healthy')).not.toHaveLength(0);
  expect(screen.getByText('20260912_Example')).toBeVisible();
  expect(screen.queryByText(/Operational snapshot|Selected channel|Compatibility|update eligibility|provenance/i)).not.toBeInTheDocument();
});

it('blocks rendering cached detailed observations and fetching after permission loss', () => {
  useAuthMock.mockReturnValue({
    user: null,
    isAuthenticated: false,
    isLoading: false,
    login: vi.fn(),
    loginWithPasskey: vi.fn(),
    register: vi.fn(),
    logout: vi.fn(),
    hasRole: () => false,
    hasPermission: () => false,
    error: null,
  } as ReturnType<typeof useAuth>);
  render(<SystemStatusPage />);
  expect(screen.getByText('Access denied')).toBeVisible();
  expect(useQueryMock).toHaveBeenCalledWith(expect.objectContaining({ enabled: false }));
});

it('reports failed observation rather than claiming current status', () => {
  useQueryMock.mockReturnValue({ error: new Error('Unavailable') } as ReturnType<typeof useQuery>);
  render(<SystemStatusPage />);
  expect(screen.getByText('Failed to load system status')).toBeVisible();
  expect(screen.queryByText(/System status is current/)).not.toBeInTheDocument();
});

it.each(['Service', 'Application version', 'Engine version', 'Health'])('cycles service %s and leaves missing versions last', async label => {
  const services = [
    { name: 'Zeta', version: '2.0', engineVersion: '3.0', health: SystemServiceHealth.Healthy },
    { name: 'Alpha', version: '1.0', engineVersion: '2.0', health: SystemServiceHealth.Critical },
    { name: 'Missing', version: '', engineVersion: null, health: SystemServiceHealth.Degraded },
  ];
  useQueryMock.mockReturnValue({ data: {
    app: { version: '1.0', hostname: 'test', uptime: '1h' },
    cpu: { cores: 4, usagePercent: 10 }, memory: { usedBytes: 0, totalBytes: 0 },
    disk: { usedBytes: 0, totalBytes: 0, archiveBytes: 0, databaseBytes: 0 },
    services, database: { engine: 'SQLite', version: '3.45', printerCount: 0, archiveCount: 0 },
  }, dataUpdatedAt: 0 } as ReturnType<typeof useQuery>);
  const user = userEvent.setup();
  render(<SystemStatusPage />);
  const table = screen.getByRole('table');
  const order = () => within(table).getAllByRole('row').slice(1).map(row => within(row).getByRole('rowheader').textContent);
  const header = within(table).getByRole('columnheader', { name: label });
  const button = within(header).getByRole('button', { name: label });
  const nullable = label === 'Application version' || label === 'Engine version';
  expect(header).toHaveAttribute('aria-sort', 'none');
  await user.click(button);
  expect(header).toHaveAttribute('aria-sort', 'ascending');
  expect(order()).toEqual(nullable ? ['Alpha', 'Zeta', 'Missing'] : ['Alpha', 'Missing', 'Zeta']);
  button.focus();
  await user.keyboard(' ');
  expect(header).toHaveAttribute('aria-sort', 'descending');
  expect(order()).toEqual(nullable ? ['Zeta', 'Alpha', 'Missing'] : ['Zeta', 'Missing', 'Alpha']);
  await user.keyboard('{Enter}');
  expect(header).toHaveAttribute('aria-sort', 'none');
  expect(order()).toEqual(['Zeta', 'Alpha', 'Missing']);
  expect(services.map(service => service.name)).toEqual(['Zeta', 'Alpha', 'Missing']);
});

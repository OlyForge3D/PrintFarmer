import { render, screen } from '@testing-library/react';
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

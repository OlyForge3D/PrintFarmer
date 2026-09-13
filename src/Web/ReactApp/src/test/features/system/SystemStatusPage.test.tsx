import { render, screen } from '@testing-library/react';
import { useQuery } from '@tanstack/react-query';
import { beforeEach, expect, it, vi } from 'vitest';
import { SystemStatusPage } from '@/features/system/pages/SystemStatusPage';
import { useAuth } from '@/features/auth/hooks/useAuth';
import { inventory } from '@/test/features/system/serviceInventoryFixture';

vi.mock('@tanstack/react-query', () => ({ useQuery: vi.fn() }));
vi.mock('@/features/auth/hooks/useAuth', () => ({ useAuth: vi.fn() }));
vi.mock('@/services/api', () => ({ apiClient: { getSystemInfo: vi.fn() } }));
const useQueryMock = vi.mocked(useQuery);
const useAuthMock = vi.mocked(useAuth);

beforeEach(() => {
  vi.clearAllMocks();
  useAuthMock.mockReturnValue({ hasPermission: (resource: string, action: string) => resource === 'system_settings' && action === 'admin' } as ReturnType<typeof useAuth>);
  useQueryMock.mockReturnValue({ data: { inventory: inventory(), app: { version: '1.2.3', hostname: 'test', uptime: '1h' },
    cpu: { cores: 4, usagePercent: 10 }, memory: { usedBytes: 0, totalBytes: 0 },
    disk: { usedBytes: 0, totalBytes: 0, archiveBytes: 0, databaseBytes: 0 }, services: [],
    database: { engine: 'SQLite', version: '3.45', migrationHeads: ['20260912_Example'], printerCount: 0, archiveCount: 0 } },
  } as ReturnType<typeof useQuery>);
});

it('integrates inventory in the existing page and keeps migration heads separate from engine version', () => {
  render(<SystemStatusPage />);
  expect(screen.getByRole('heading', { name: 'Service and replica inventory' })).toBeVisible();
  expect(screen.getByText('Engine version')).toBeVisible();
  expect(screen.getByText('20260912_Example')).toBeVisible();
});

it('blocks rendering cached detailed observations and fetching after permission loss', () => {
  useAuthMock.mockReturnValue({ hasPermission: () => false } as ReturnType<typeof useAuth>);
  render(<SystemStatusPage />);
  expect(screen.getByText('Access denied')).toBeVisible();
  expect(screen.queryByText('Service and replica inventory')).not.toBeInTheDocument();
  expect(useQueryMock).toHaveBeenCalledWith(expect.objectContaining({ enabled: false }));
});

it('reports failed observation rather than claiming current status', () => {
  useQueryMock.mockReturnValue({ error: new Error('Unavailable') } as ReturnType<typeof useQuery>);
  render(<SystemStatusPage />);
  expect(screen.getByText('Failed to load system status')).toBeVisible();
  expect(screen.queryByText(/System status is current/)).not.toBeInTheDocument();
});

import { render, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router';
import { describe, expect, it, vi } from 'vitest';
import { Layout } from '@/common/components/Layout';
import { ADMIN_DESTINATIONS } from '@/features/admin/registry/adminDestinations';

const createTestQueryClient = () => new QueryClient({
  defaultOptions: {
    queries: { retry: false },
    mutations: { retry: false },
  },
});

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
  usePrinterStatusUpdates: () => ({
    printerStatuses: new Map(),
  }),
}));

vi.mock('@/services/printer-signalr', () => ({
  printerSignalRService: {
    connect: vi.fn().mockResolvedValue(undefined),
    onPrinterStatusUpdate: vi.fn().mockReturnValue(() => {}),
    onAutoDispatchStateChanged: vi.fn().mockReturnValue(() => {}),
  },
}));

vi.mock('@/features/tasks', () => ({
  TasksBadge: () => null,
}));

vi.mock('@/features/printers/hooks/useAutoDispatch', () => ({
  useAllAutoDispatchStatuses: () => ({
    data: [],
    isLoading: false,
  }),
}));

describe('Analytics navigation entry', () => {
  it('routes Analytics only at its canonical target, from its single default home', async () => {
    // Analytics used to be an anchored rail entry *and* an Admin Control Center
    // tile. #2526 gave it one default home — the Control Center — so the rail
    // must no longer link it. The original regression this test guards still
    // applies to the surviving home: the canonical target is `/analytics`, and
    // the retired `/statistics` routes must not come back anywhere.
    const analytics = ADMIN_DESTINATIONS.find((destination) => destination.id === 'ops-analytics');
    expect(analytics?.path).toBe('/analytics');
    expect(analytics?.isHubTile).toBe(true);
    expect(ADMIN_DESTINATIONS.filter((destination) => destination.path.startsWith('/statistics'))).toHaveLength(0);

    const queryClient = createTestQueryClient();
    const { container } = render(
      <QueryClientProvider client={queryClient}>
        <MemoryRouter>
          <Layout />
        </MemoryRouter>
      </QueryClientProvider>,
    );

    await waitFor(() => {
      expect(container.querySelector('a[href="/admin"]')).not.toBeNull();
    });

    expect(container.querySelector('a[href="/analytics"]')).toBeNull();
    expect(container.querySelector('a[href="/statistics"]')).toBeNull();
    expect(container.querySelector('a[href="/statistics/costs"]')).toBeNull();
  });
});

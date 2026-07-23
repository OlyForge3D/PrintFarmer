import { render, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router';
import { describe, expect, it, vi } from 'vitest';
import { Layout } from '@/common/components/Layout';

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
  it('shows one Analytics target and removes the old statistics targets', async () => {
    const queryClient = createTestQueryClient();
    const { container } = render(
      <QueryClientProvider client={queryClient}>
        <MemoryRouter>
          <Layout />
        </MemoryRouter>
      </QueryClientProvider>,
    );

    await waitFor(() => {
      expect(container.querySelector('a[href="/analytics"]')).not.toBeNull();
    });

    expect(container.querySelector('a[href="/statistics"]')).toBeNull();
    expect(container.querySelector('a[href="/statistics/costs"]')).toBeNull();
  });
});

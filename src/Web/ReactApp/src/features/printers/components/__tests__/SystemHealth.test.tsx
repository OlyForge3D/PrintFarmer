import { render, screen } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { DetailedSystemHealth } from '@/features/printers/components/SystemHealth';
import type { DetailedHealthStatus } from '@/types/api';

const { useHealthStatus } = vi.hoisted(() => ({ useHealthStatus: vi.fn() }));
vi.mock('@/common/hooks/useApi', () => ({
  useHealthStatus,
  useBasicHealth: vi.fn(),
}));

describe('DetailedSystemHealth provider availability', () => {
  beforeEach(() => {
    localStorage.clear();
  });

  function renderProvider(enabled: boolean, status: number, description: string) {
    const health: DetailedHealthStatus = {
      kind: 'detailed',
      status: status === 2 ? 'Healthy' : 'Degraded',
      totalChecksDuration: '00:00:00',
      results: {
        printguard: { status, duration: '00:00:00', description, data: { enabled } },
        signalr: { status: 2, duration: '00:00:00' },
      },
    };
    useHealthStatus.mockReturnValue({ data: health, isLoading: false, error: null });
    render(<DetailedSystemHealth />);
  }

  it('omits the PrintGuard card when disabled without hiding other checks', () => {
    renderProvider(false, 2, 'PrintGuard failure detection disabled');
    expect(screen.queryByText('PrintGuard Failure Detection')).not.toBeInTheDocument();
    expect(screen.queryByText('PrintGuard failure detection disabled')).not.toBeInTheDocument();
    expect(screen.getByText('signalr')).toBeInTheDocument();
  });

  it.each([
    [2, 'PrintGuard hub ready and read API accessible; classification not tested', 'Healthy'],
    [1, 'PrintGuard configuration invalid: configure a service URL', 'Degraded'],
    [1, 'PrintGuard connection failed: check the service URL and network connectivity', 'Degraded'],
  ])('shows enabled provider status %s and its safe diagnostic detail', (status, description, badge) => {
    renderProvider(true, status, description);
    const label = screen.getByText('PrintGuard Failure Detection');
    expect(label.parentElement?.parentElement).toHaveTextContent(badge);
    expect(screen.getByText(description)).toBeInTheDocument();
  });
});

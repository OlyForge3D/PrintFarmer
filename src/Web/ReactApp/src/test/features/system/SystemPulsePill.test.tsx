import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { useQuery } from '@tanstack/react-query';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { useAuth } from '@/features/auth/hooks/useAuth';
import { SystemPulsePill } from '@/features/system/components/SystemPulsePill';
import type { SystemInfo } from '@/types/api';

vi.mock('@tanstack/react-query', () => ({
  useQuery: vi.fn(),
}));

vi.mock('@/features/auth/hooks/useAuth', () => ({
  useAuth: vi.fn(),
}));

vi.mock('@/services/api', () => ({
  apiClient: {
    getSystemInfo: vi.fn(),
  },
}));

const useQueryMock = vi.mocked(useQuery);
const useAuthMock = vi.mocked(useAuth);

const systemInfo: SystemInfo = {
  app: {
    version: '10.0.0',
    uptime: '1d 2h',
    hostname: 'printfarmer-dev',
  },
  cpu: {
    cores: 16,
    usagePercent: 37.5,
  },
  memory: {
    usedBytes: 8 * 1024 * 1024 * 1024,
    totalBytes: 16 * 1024 * 1024 * 1024,
  },
  disk: {
    usedBytes: 450 * 1024 * 1024 * 1024,
    totalBytes: 1_000 * 1024 * 1024 * 1024,
    archiveBytes: 125 * 1024 * 1024 * 1024,
    databaseBytes: 3 * 1024 * 1024 * 1024,
  },
  services: [
    {
      name: 'Farm API',
      version: '10.0.0',
      health: 'Healthy',
    },
    {
      name: 'Slicer Host',
      version: '10.0.0',
      health: 'Critical',
    },
  ],
  database: {
    engine: 'sqlite',
    version: '3.45',
    printerCount: 12,
    archiveCount: 88,
  },
};

describe('SystemPulsePill', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    useAuthMock.mockReturnValue({
      hasRole: (role: string) => role === 'farm_admin',
    } as ReturnType<typeof useAuth>);
    useQueryMock.mockReturnValue({
      data: systemInfo,
      error: null,
    } as ReturnType<typeof useQuery>);
  });

  it('renders the pulse pill and opens a status panel with host meters and service versions', async () => {
    render(<SystemPulsePill />);

    const trigger = screen.getByRole('button', { name: /system/i });
    fireEvent.click(trigger);

    expect(screen.getByRole('dialog', { name: /system pulse/i })).toHaveAttribute('aria-modal', 'true');
    expect(screen.getByText('CPU')).toBeInTheDocument();
    expect(screen.getByText('Memory')).toBeInTheDocument();
    expect(screen.getByText('Disk')).toBeInTheDocument();
    expect(screen.getByText('Farm API')).toBeInTheDocument();
    expect(screen.getAllByText('10.0.0').length).toBeGreaterThan(0);

    await waitFor(() => {
      expect(screen.getByRole('button', { name: /close system pulse panel/i })).toHaveFocus();
    });
  });

  it('closes on Escape and returns focus to the pill trigger', async () => {
    render(<SystemPulsePill />);

    const trigger = screen.getByRole('button', { name: /system/i });
    fireEvent.click(trigger);
    fireEvent.keyDown(document, { key: 'Escape' });

    await waitFor(() => {
      expect(screen.queryByRole('dialog', { name: /system pulse/i })).not.toBeInTheDocument();
    });

    await waitFor(() => {
      expect(trigger).toHaveFocus();
    });
  });

  it('uses the provided action instead of opening the status panel', () => {
    const handleClick = vi.fn();

    render(<SystemPulsePill onClick={handleClick} />);

    const trigger = screen.getByRole('button', { name: /system/i });
    fireEvent.click(trigger);

    expect(handleClick).toHaveBeenCalledTimes(1);
    expect(screen.queryByRole('dialog', { name: /system pulse/i })).not.toBeInTheDocument();
  });

  it('stays hidden for non-admin users', () => {
    useAuthMock.mockReturnValue({
      hasRole: () => false,
    } as ReturnType<typeof useAuth>);

    render(<SystemPulsePill />);

    expect(screen.queryByRole('button', { name: /system/i })).not.toBeInTheDocument();
  });

  it('stays hidden when system info fails to load', () => {
    useQueryMock.mockReturnValue({
      data: undefined,
      error: new Error('boom'),
    } as ReturnType<typeof useQuery>);

    render(<SystemPulsePill />);

    expect(screen.queryByRole('button', { name: /system/i })).not.toBeInTheDocument();
  });
});

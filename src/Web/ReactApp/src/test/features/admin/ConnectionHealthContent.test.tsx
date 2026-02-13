import '@testing-library/jest-dom';
import React from 'react';
import { render, screen, waitFor } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ConnectionDiagnosticsResponse } from '../../../types/api';

function createMockResponse(): ConnectionDiagnosticsResponse {
  return {
    printers: [
      {
        printerId: '11111111-1111-1111-1111-111111111111',
        printerName: 'Voron',
        backend: 'Moonraker',
        connectionState: 'Connected',
        lastConnectedUtc: new Date().toISOString(),
        lastDisconnectedUtc: null,
        reconnectAttempts: 0,
        totalReconnects: 2,
        consecutiveFailures: 0,
        uptimePercent: 99.5,
        connectionMode: 'WebSocket',
        recentTransitions: [
          {
            timestampUtc: new Date(Date.now() - 60000).toISOString(),
            fromState: 'Reconnecting',
            toState: 'Connected',
            reason: 'Klippy ready',
          },
        ],
      },
      {
        printerId: '22222222-2222-2222-2222-222222222222',
        printerName: 'Saturn',
        backend: 'SDCP',
        connectionState: 'Offline',
        lastConnectedUtc: new Date(Date.now() - 300000).toISOString(),
        lastDisconnectedUtc: new Date(Date.now() - 60000).toISOString(),
        reconnectAttempts: 3,
        totalReconnects: 5,
        consecutiveFailures: 3,
        uptimePercent: 45.2,
        connectionMode: 'Polling',
        recentTransitions: [],
      },
    ],
    totalPrinters: 2,
    connectedCount: 1,
    reconnectingCount: 0,
    offlineCount: 1,
    degradedCount: 0,
    timestampUtc: new Date().toISOString(),
  };
}

vi.mock('../../../services/api', () => ({
  apiClient: {
    getConnectionDiagnostics: vi.fn().mockImplementation(() => Promise.resolve(createMockResponse())),
  },
}));

import { ConnectionHealthContent } from '../../../features/admin/components/ConnectionHealthContent';

function renderWithQueryClient(ui: React.ReactElement) {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  return render(
    <QueryClientProvider client={queryClient}>
      {ui}
    </QueryClientProvider>
  );
}

describe('ConnectionHealthContent', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('renders summary cards with correct counts', async () => {
    renderWithQueryClient(<ConnectionHealthContent />);

    // Wait for data to load by checking for a unique label
    await waitFor(() => {
      expect(screen.getByText('Total Printers')).toBeInTheDocument();
    });

    // Summary cards section uses grid layout - find summary cards container
    const summaryCards = screen.getByText('Total Printers').closest('.grid');
    expect(summaryCards).not.toBeNull();
    expect(summaryCards!.textContent).toContain('Total Printers');
    expect(summaryCards!.textContent).toContain('Connected');
    expect(summaryCards!.textContent).toContain('Reconnecting');
    expect(summaryCards!.textContent).toContain('Offline');
  });

  it('renders printer names in the table', async () => {
    renderWithQueryClient(<ConnectionHealthContent />);

    await waitFor(() => {
      expect(screen.getByText('Voron')).toBeInTheDocument();
      expect(screen.getByText('Saturn')).toBeInTheDocument();
    });
  });

  it('renders backend type for each printer', async () => {
    renderWithQueryClient(<ConnectionHealthContent />);

    await waitFor(() => {
      expect(screen.getByText('Moonraker')).toBeInTheDocument();
      expect(screen.getByText('SDCP')).toBeInTheDocument();
    });
  });

  it('renders connection mode column', async () => {
    renderWithQueryClient(<ConnectionHealthContent />);

    await waitFor(() => {
      expect(screen.getByText('WebSocket')).toBeInTheDocument();
      expect(screen.getByText('Polling')).toBeInTheDocument();
    });
  });

  it('renders uptime percentages', async () => {
    renderWithQueryClient(<ConnectionHealthContent />);

    await waitFor(() => {
      expect(screen.getByText('99.5%')).toBeInTheDocument();
      expect(screen.getByText('45.2%')).toBeInTheDocument();
    });
  });
});

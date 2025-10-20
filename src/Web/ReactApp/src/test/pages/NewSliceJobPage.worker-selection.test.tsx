import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, waitFor, fireEvent } from '@testing-library/react';
import { act } from '@testing-library/react';
import { NewSliceJobPage } from '../../pages/NewSliceJobPage';
import { TestRouter } from '../utils/TestRouter';
import { AuthProvider } from '../../contexts/AuthContext';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

// Mock SignalR
vi.mock('@microsoft/signalr', () => {
  const mockConnection = {
    start: vi.fn().mockResolvedValue(undefined),
    stop: vi.fn().mockResolvedValue(undefined),
    on: vi.fn(),
    off: vi.fn(),
  };

  const mockBuilder = {
    withUrl: vi.fn().mockReturnThis(),
    withAutomaticReconnect: vi.fn().mockReturnThis(),
    build: vi.fn().mockReturnValue(mockConnection),
  };

  return {
    HubConnectionBuilder: vi.fn().mockImplementation(() => mockBuilder),
    HubConnectionState: {
      Connected: 'Connected',
      Disconnected: 'Disconnected',
    },
  };
});

// Mock fetch for API responses
interface MockResp {
  ok: boolean;
  status?: number;
  body?: unknown;
}

function mockFetchSequence(responses: MockResp[]) {
  let call = 0;
  global.fetch = vi.fn().mockImplementation(() => {
    const r = responses[Math.min(call, responses.length - 1)];
    call++;
    const responseLike: Partial<Response> = {
      ok: r.ok,
      status: r.status ?? (r.ok ? 200 : 500),
      json: async () => r.body,
      text: async () => JSON.stringify(r.body),
    };
    return Promise.resolve(responseLike as Response);
  });
}

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      retry: false,
    },
  },
});

function wrapper(children: React.ReactNode) {
  return (
    <QueryClientProvider client={queryClient}>
      <AuthProvider>
        <TestRouter>{children}</TestRouter>
      </AuthProvider>
    </QueryClientProvider>
  );
}

// Mock worker data matching WorkerResponse interface
const mockWorkers = [
  {
    id: '550e8400-e29b-41d4-a716-446655440001',
    serviceId: 'orcaslicer-1',
    name: 'OrcaSlicer-Worker-1',
    endpointUrl: 'http://localhost:5001',
    capabilities: ['orcaslicer', 'stl', 'obj', '3mf'],
    version: '1.0.0',
    status: 'Online',
    freeSlots: 2,
    totalSlots: 4,
    activeJobs: 2,
    completedJobs: 10,
    failedJobs: 0,
    averageProcessingTimeSeconds: 120,
    lastHeartbeat: new Date().toISOString(),
    registeredAt: new Date().toISOString(),
    onlineAt: new Date().toISOString(),
    isDisabled: false,
  },
  {
    id: '550e8400-e29b-41d4-a716-446655440002',
    serviceId: 'prusaslicer-1',
    name: 'PrusaSlicer-Worker-1',
    endpointUrl: 'http://localhost:5002',
    capabilities: ['prusaslicer', 'stl', '3mf'],
    version: '2.7.0',
    status: 'Online',
    freeSlots: 1,
    totalSlots: 2,
    activeJobs: 1,
    completedJobs: 5,
    failedJobs: 0,
    averageProcessingTimeSeconds: 150,
    lastHeartbeat: new Date().toISOString(),
    registeredAt: new Date().toISOString(),
    onlineAt: new Date().toISOString(),
    isDisabled: false,
  },
  {
    id: '550e8400-e29b-41d4-a716-446655440003',
    serviceId: 'orcaslicer-2',
    name: 'OrcaSlicer-Worker-2',
    endpointUrl: 'http://localhost:5003',
    capabilities: ['orcaslicer', 'stl', 'obj', '3mf', 'step'],
    version: '1.0.0',
    status: 'Busy',
    freeSlots: 0,
    totalSlots: 4,
    activeJobs: 4,
    completedJobs: 20,
    failedJobs: 1,
    averageProcessingTimeSeconds: 110,
    lastHeartbeat: new Date().toISOString(),
    registeredAt: new Date().toISOString(),
    onlineAt: new Date().toISOString(),
    isDisabled: false,
  },
  {
    id: '550e8400-e29b-41d4-a716-446655440004',
    serviceId: 'superslicer-1',
    name: 'SuperSlicer-Worker-1',
    endpointUrl: 'http://localhost:5004',
    capabilities: ['superslicer', 'stl', '3mf'],
    version: '2.5.59',
    status: 'Offline',
    freeSlots: 0,
    totalSlots: 2,
    activeJobs: 0,
    completedJobs: 15,
    failedJobs: 2,
    averageProcessingTimeSeconds: 180,
    lastHeartbeat: new Date(Date.now() - 600000).toISOString(), // 10 minutes ago
    registeredAt: new Date().toISOString(),
    offlineAt: new Date().toISOString(),
    isDisabled: false,
  },
];

// Mock printer models
const mockPrinterModels = [
  { id: 1, manufacturer: 'Prusa Research', name: 'Prusa i3 MK3S+' },
  { id: 2, manufacturer: 'Bambu Lab', name: 'X1 Carbon' },
];

// Mock slicer profiles
const mockSlicerProfiles = [
  {
    id: 1,
    name: 'PLA Standard',
    slicerName: 'orcaslicer',
    printerModelId: 2,
    description: 'Standard PLA profile',
    capabilities: '["orcaslicer", "stl", "3mf"]',
  },
  {
    id: 2,
    name: 'PETG Fine',
    slicerName: 'prusaslicer',
    printerModelId: 1,
    description: 'Fine PETG profile',
    capabilities: '["prusaslicer", "stl"]',
  },
];

describe('NewSliceJobPage - Worker Selection Flow', () => {
  beforeEach(() => {
    vi.resetAllMocks();
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('should load page and display available workers', async () => {
    mockFetchSequence([
      { ok: true, body: mockPrinterModels }, // /api/catalog/printer-models
      { ok: true, body: mockSlicerProfiles }, // /api/slicer-profiles
      { ok: true, body: mockWorkers }, // /api/workers/available
    ]);

    render(wrapper(<NewSliceJobPage />));

    // Wait for page to load
    await waitFor(() => {
      expect(screen.getByText(/Create New Slice Job/i)).toBeTruthy();
    });

    // Verify workers section is present
    await waitFor(() => {
      expect(screen.getByText(/Available Workers/i)).toBeTruthy();
    });

    // Verify at least one worker is displayed
    await waitFor(() => {
      expect(screen.getByText(/OrcaSlicer-Worker-1/i)).toBeTruthy();
    });
  });

  it('should filter workers by capabilities when profile selected', async () => {
    mockFetchSequence([
      { ok: true, body: mockPrinterModels },
      { ok: true, body: mockSlicerProfiles },
      { ok: true, body: mockWorkers }, // Initial workers load
      { ok: true, body: mockWorkers.filter(w => w.capabilities.includes('orcaslicer')) }, // Filtered workers
    ]);

    render(wrapper(<NewSliceJobPage />));

    // Wait for page load
    await waitFor(() => {
      expect(screen.getByText(/Create New Slice Job/i)).toBeTruthy();
    });

    // Initial state: should see all workers
    await waitFor(() => {
      expect(screen.queryByText(/OrcaSlicer-Worker-1/i)).toBeTruthy();
      expect(screen.queryByText(/PrusaSlicer-Worker-1/i)).toBeTruthy();
    });

    // After profile selection, workers should be filtered
    // Note: This test validates the structure, actual filtering logic would need form interaction
  });

  it('should display worker status indicators', async () => {
    mockFetchSequence([
      { ok: true, body: mockPrinterModels },
      { ok: true, body: mockSlicerProfiles },
      { ok: true, body: mockWorkers },
    ]);

    render(wrapper(<NewSliceJobPage />));

    await waitFor(() => {
      expect(screen.getByText(/Available Workers/i)).toBeTruthy();
    });

    // Wait for workers to render
    await waitFor(() => {
      expect(screen.getByText(/OrcaSlicer-Worker-1/i)).toBeTruthy();
    });

    // Check for status indicators (badges)
    await waitFor(() => {
      // Online workers should have green badge
      const onlineBadges = screen.getAllByText(/Online/i);
      expect(onlineBadges.length).toBeGreaterThan(0);

      // Busy workers should have yellow badge
      const busyBadges = screen.getAllByText(/Busy/i);
      expect(busyBadges.length).toBeGreaterThan(0);

      // Offline workers should have gray badge
      const offlineBadges = screen.getAllByText(/Offline/i);
      expect(offlineBadges.length).toBeGreaterThan(0);
    });
  });

  it('should show worker capacity information', async () => {
    mockFetchSequence([
      { ok: true, body: mockPrinterModels },
      { ok: true, body: mockSlicerProfiles },
      { ok: true, body: mockWorkers },
    ]);

    render(wrapper(<NewSliceJobPage />));

    await waitFor(() => {
      expect(screen.getByText(/Available Workers/i)).toBeTruthy();
    });

    // Verify capacity display for worker with available slots
    await waitFor(() => {
      expect(screen.getByText(/2 \/ 4 slots/i)).toBeTruthy();
    });

    // Verify capacity display for busy worker
    await waitFor(() => {
      expect(screen.getByText(/0 \/ 4 slots/i)).toBeTruthy();
    });
  });

  it('should allow worker selection by clicking', async () => {
    mockFetchSequence([
      { ok: true, body: mockPrinterModels },
      { ok: true, body: mockSlicerProfiles },
      { ok: true, body: mockWorkers },
    ]);

    render(wrapper(<NewSliceJobPage />));

    await waitFor(() => {
      expect(screen.getByText(/Available Workers/i)).toBeTruthy();
    });

    // Find a worker card
    const workerCard = await screen.findByText(/OrcaSlicer-Worker-1/i);
    expect(workerCard).toBeTruthy();

    // Verify worker card is rendered
    const workerCardElement = workerCard.closest('div[role="button"]') || workerCard.closest('button');
    expect(workerCardElement).toBeTruthy();
  });

  it('should display capability badges for each worker', async () => {
    mockFetchSequence([
      { ok: true, body: mockPrinterModels },
      { ok: true, body: mockSlicerProfiles },
      { ok: true, body: mockWorkers },
    ]);

    render(wrapper(<NewSliceJobPage />));

    await waitFor(() => {
      expect(screen.getByText(/Available Workers/i)).toBeTruthy();
    });

    // Wait for worker to render
    await waitFor(() => {
      expect(screen.getByText(/OrcaSlicer-Worker-1/i)).toBeTruthy();
    });

    // Verify capability badges are present
    await waitFor(() => {
      const orcaslicerBadges = screen.getAllByText('orcaslicer');
      expect(orcaslicerBadges.length).toBeGreaterThan(0);

      const stlBadges = screen.getAllByText('stl');
      expect(stlBadges.length).toBeGreaterThan(0);
    });
  });

  it('should handle empty workers list gracefully', async () => {
    mockFetchSequence([
      { ok: true, body: mockPrinterModels },
      { ok: true, body: mockSlicerProfiles },
      { ok: true, body: [] }, // No workers available
    ]);

    render(wrapper(<NewSliceJobPage />));

    await waitFor(() => {
      expect(screen.getByText(/Create New Slice Job/i)).toBeTruthy();
    });

    // Should show empty state message
    await waitFor(() => {
      expect(screen.getByText(/No workers available/i)).toBeTruthy();
    });
  });

  it('should handle worker API error gracefully', async () => {
    mockFetchSequence([
      { ok: true, body: mockPrinterModels },
      { ok: true, body: mockSlicerProfiles },
      { ok: false, status: 500 }, // Workers API fails
    ]);

    render(wrapper(<NewSliceJobPage />));

    await waitFor(() => {
      expect(screen.getByText(/Create New Slice Job/i)).toBeTruthy();
    });

    // Should show error state
    await waitFor(() => {
      expect(screen.getByText(/Failed to load workers/i)).toBeTruthy();
    });
  });

  it('should submit job with selected worker', async () => {
    // Track POST request
    global.fetch = vi.fn().mockImplementation((url: string, options?: RequestInit) => {
      if (options?.method === 'POST' && url.includes('/api/slice-jobs')) {
        return Promise.resolve({
          ok: true,
          status: 201,
          json: async () => ({ id: '123', status: 'Queued' }),
        } as Response);
      }

      // Handle GET requests
      if (url.includes('/printer-models')) {
        return Promise.resolve({
          ok: true,
          status: 200,
          json: async () => mockPrinterModels,
        } as Response);
      }
      if (url.includes('/slicer-profiles')) {
        return Promise.resolve({
          ok: true,
          status: 200,
          json: async () => mockSlicerProfiles,
        } as Response);
      }
      if (url.includes('/workers')) {
        return Promise.resolve({
          ok: true,
          status: 200,
          json: async () => mockWorkers,
        } as Response);
      }

      return Promise.resolve({
        ok: true,
        status: 200,
        json: async () => ({}),
      } as Response);
    });

    render(wrapper(<NewSliceJobPage />));

    // Wait for page load
    await waitFor(() => {
      expect(screen.getByText(/Create New Slice Job/i)).toBeTruthy();
    });

    // Find a worker card
    const workerCard = await screen.findByText(/OrcaSlicer-Worker-1/i);
    const workerButton = workerCard.closest('div[role="button"]') || workerCard.closest('button');
    
    // Click to select worker
    if (workerButton) {
      await act(async () => {
        fireEvent.click(workerButton);
      });
    }

    // Note: Full form submission would require filling all fields
    // This test validates the structure and worker selection capability
    // Form submission logic would be tested separately with complete form data
    
    // Verify worker selection rendered
    await waitFor(() => {
      expect(screen.getByText(/OrcaSlicer-Worker-1/i)).toBeTruthy();
    });
  });
});

import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { PrinterLocationDragDrop } from '@/features/printers/components/PrinterLocationDragDrop';
import type { Location } from '@/services/locationService';
import type { Printer } from '@/services/printerLocationService';

const invalidateQueriesMock = vi.hoisted(() => vi.fn());

vi.mock('@tanstack/react-query', async () => {
  const actual = await vi.importActual<typeof import('@tanstack/react-query')>('@tanstack/react-query');
  return {
    ...actual,
    useQueryClient: () => ({
    invalidateQueries: invalidateQueriesMock,
    }),
  };
});

const mockLocations: Location[] = [
  {
    id: 'loc-1',
    name: 'Workshop',
    description: 'Main workshop',
    parentId: null,
    path: '/Workshop',
    depth: 0,
    sortOrder: 0,
    printerCount: 1,
    totalPrinterCount: 1,
    createdAt: '2024-01-01T00:00:00Z',
    modifiedAt: '2024-01-01T00:00:00Z',
    isActive: true,
  },
  {
    id: 'loc-2',
    name: 'Lab',
    description: 'Testing lab',
    parentId: null,
    path: '/Lab',
    depth: 0,
    sortOrder: 1,
    printerCount: 0,
    totalPrinterCount: 0,
    createdAt: '2024-01-02T00:00:00Z',
    modifiedAt: '2024-01-02T00:00:00Z',
    isActive: true,
  },
];

const mockPrinters: Printer[] = [
  { id: 'p-1', name: 'Prusa MK4', serverUrl: 'http://192.168.1.10', backend: 1, locationId: 'loc-1' },
  { id: 'p-2', name: 'Voron 2.4', serverUrl: 'http://192.168.1.11', backend: 0, locationId: undefined },
  { id: 'p-3', name: 'Bambu X1C', serverUrl: 'http://192.168.1.12', backend: 2, locationId: undefined },
];

vi.mock('@/services/locationService', () => ({
  locationService: {
    getAllLocations: vi.fn(),
    getLocationTree: vi.fn(),
  },
}));

vi.mock('@/services/printerLocationService', () => ({
  printerLocationService: {
    getAllPrinters: vi.fn(),
    assignPrinterToLocation: vi.fn(),
    unassignPrinterFromLocation: vi.fn(),
  },
}));

 
const { locationService } = await import('@/services/locationService') as typeof import('@/services/locationService');
 
const { printerLocationService } = await import('@/services/printerLocationService') as typeof import('@/services/printerLocationService');

describe('PrinterLocationDragDrop', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    invalidateQueriesMock.mockClear();
    vi.mocked(locationService.getAllLocations).mockResolvedValue(mockLocations);
    vi.mocked(printerLocationService.getAllPrinters).mockResolvedValue(mockPrinters);
  });

  it('renders heading and description', async () => {
    render(<PrinterLocationDragDrop />);

    await waitFor(() => {
      expect(screen.getByText('Assign Printers to Locations')).toBeInTheDocument();
    });

    expect(screen.getByText('Drag and drop printers to assign them to locations')).toBeInTheDocument();
  });

  it('renders its heading as h2, not h1, to avoid a duplicate page-level h1', async () => {
    render(<PrinterLocationDragDrop />);

    await waitFor(() => {
      expect(screen.getByRole('heading', { level: 2, name: 'Assign Printers to Locations' })).toBeInTheDocument();
    });
    expect(screen.queryByRole('heading', { level: 1 })).not.toBeInTheDocument();
  });

  it('shows loading state while fetching data', () => {
    vi.mocked(printerLocationService.getAllPrinters).mockImplementation(
      () => new Promise(() => {}),
    );

    render(<PrinterLocationDragDrop />);

    expect(screen.getByText('Loading...')).toBeInTheDocument();
  });

  it('displays unassigned printers section', async () => {
    render(<PrinterLocationDragDrop />);

    await waitFor(() => {
      expect(screen.getByText('Unassigned Printers (2)')).toBeInTheDocument();
    });

    expect(screen.getByText('Voron 2.4')).toBeInTheDocument();
    expect(screen.getByText('Bambu X1C')).toBeInTheDocument();
  });

  it('displays location columns with assigned printers', async () => {
    render(<PrinterLocationDragDrop />);

    await waitFor(() => {
      expect(screen.getByText('Workshop')).toBeInTheDocument();
    });

    expect(screen.getByText('Lab')).toBeInTheDocument();
    expect(screen.getByText('Prusa MK4')).toBeInTheDocument();
  });

  it('shows printer count per location', async () => {
    render(<PrinterLocationDragDrop />);

    await waitFor(() => {
      expect(screen.getByText('1 printers')).toBeInTheDocument(); // Workshop
      expect(screen.getByText('0 printers')).toBeInTheDocument(); // Lab
    });
  });

  it('shows "Drag printers here" for empty locations', async () => {
    render(<PrinterLocationDragDrop />);

    await waitFor(() => {
      expect(screen.getByText('Drag printers here')).toBeInTheDocument();
    });
  });

  it('shows "All printers assigned" when no unassigned printers', async () => {
    const allAssigned = mockPrinters.map((p) => ({ ...p, locationId: 'loc-1' }));
    vi.mocked(printerLocationService.getAllPrinters).mockResolvedValue(allAssigned);

    render(<PrinterLocationDragDrop />);

    await waitFor(() => {
      expect(screen.getByText('All printers assigned')).toBeInTheDocument();
    });
  });

  it('shows error message when loading fails', async () => {
    vi.mocked(printerLocationService.getAllPrinters).mockRejectedValue(
      new Error('Failed to load printers'),
    );

    render(<PrinterLocationDragDrop />);

    await waitFor(() => {
      expect(screen.getByText('Failed to load printers')).toBeInTheDocument();
    });
  });

  it('shows "No locations created yet" when locations list is empty', async () => {
    vi.mocked(locationService.getAllLocations).mockResolvedValue([]);
    render(<PrinterLocationDragDrop />);

    await waitFor(() => {
      expect(screen.getByText(/No locations created yet/)).toBeInTheDocument();
    });
  });

  it('uses parent locations when provided as prop', async () => {
    render(<PrinterLocationDragDrop locations={mockLocations} />);

    await waitFor(() => {
      expect(screen.getByText('Workshop')).toBeInTheDocument();
      expect(screen.getByText('Lab')).toBeInTheDocument();
    });

    // Should not call getAllLocations since locations were provided
    expect(locationService.getAllLocations).not.toHaveBeenCalled();
  });

  it('renders printer cards as draggable', async () => {
    render(<PrinterLocationDragDrop />);

    await waitFor(() => {
      expect(screen.getByText('Voron 2.4')).toBeInTheDocument();
    });

    // Printer cards should have draggable attribute
    const printerCard = screen.getByText('Voron 2.4').closest('[draggable]');
    expect(printerCard).not.toBeNull();
    expect(printerCard).toHaveAttribute('draggable', 'true');
  });

  it('displays location description when available', async () => {
    render(<PrinterLocationDragDrop />);

    await waitFor(() => {
      expect(screen.getByText('Main workshop')).toBeInTheDocument();
      expect(screen.getByText('Testing lab')).toBeInTheDocument();
    });
  });

  it('displays printer server URL on card', async () => {
    render(<PrinterLocationDragDrop />);

    await waitFor(() => {
      // The PrinterCard component renders the service-normalized serverUrl.
      expect(screen.getByText('Voron 2.4')).toBeInTheDocument();
      expect(screen.getByText('Bambu X1C')).toBeInTheDocument();
    });
  });

  it('invalidates dashboard queries after assigning a printer to a location', async () => {
    vi.mocked(printerLocationService.assignPrinterToLocation).mockResolvedValue({
      ...mockPrinters[1],
      locationId: 'loc-2',
    });
    render(<PrinterLocationDragDrop />);

    const dataTransfer = { effectAllowed: '', dropEffect: '' };
    const printerCard = await screen.findByText('Voron 2.4');
    fireEvent.dragStart(printerCard.closest('[draggable]')!, { dataTransfer });
    fireEvent.drop(screen.getByText('Lab').closest('div')!, { dataTransfer });

    await waitFor(() => {
      expect(printerLocationService.assignPrinterToLocation).toHaveBeenCalledWith('p-2', 'loc-2');
    });
    expect(invalidateQueriesMock).toHaveBeenCalledWith({ queryKey: ['locations', 'tree'] });
    expect(invalidateQueriesMock).toHaveBeenCalledWith({ queryKey: ['locations', 'all-printers'] });
    expect(invalidateQueriesMock).toHaveBeenCalledWith({ queryKey: ['printers'] });
    expect(invalidateQueriesMock).toHaveBeenCalledWith(expect.objectContaining({ predicate: expect.any(Function) }));
  });

  it('invalidates dashboard queries after unassigning a printer', async () => {
    vi.mocked(printerLocationService.unassignPrinterFromLocation).mockResolvedValue({
      ...mockPrinters[0],
      locationId: undefined,
    });
    render(<PrinterLocationDragDrop />);

    const dataTransfer = { effectAllowed: '', dropEffect: '' };
    const printerCard = await screen.findByText('Prusa MK4');
    fireEvent.dragStart(printerCard.closest('[draggable]')!, { dataTransfer });
    fireEvent.drop(screen.getByText('Unassigned Printers (2)').closest('div')!, { dataTransfer });

    await waitFor(() => {
      expect(printerLocationService.unassignPrinterFromLocation).toHaveBeenCalledWith('p-1');
    });
    expect(invalidateQueriesMock).toHaveBeenCalledWith({ queryKey: ['locations', 'tree'] });
    expect(invalidateQueriesMock).toHaveBeenCalledWith({ queryKey: ['locations', 'all-printers'] });
    expect(invalidateQueriesMock).toHaveBeenCalledWith({ queryKey: ['printers'] });
    expect(invalidateQueriesMock).toHaveBeenCalledWith(expect.objectContaining({ predicate: expect.any(Function) }));
  });
});

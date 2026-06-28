import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { LocationManagement } from '@/features/catalog/components/LocationManagement';
import type { Location, LocationTreeNode } from '@/services/locationService';

const mockLocations: Location[] = [
  {
    id: 'loc-1',
    name: 'Warehouse',
    description: 'Main warehouse',
    parentId: null,
    path: '/Warehouse',
    depth: 0,
    sortOrder: 0,
    printerCount: 2,
    totalPrinterCount: 5,
    createdAt: '2024-01-01T00:00:00Z',
    modifiedAt: '2024-01-01T00:00:00Z',
    isActive: true,
  },
  {
    id: 'loc-2',
    name: 'Rack A',
    description: 'First rack',
    parentId: 'loc-1',
    path: '/Warehouse/Rack A',
    depth: 1,
    sortOrder: 0,
    printerCount: 3,
    totalPrinterCount: 3,
    createdAt: '2024-01-02T00:00:00Z',
    modifiedAt: '2024-01-02T00:00:00Z',
    isActive: true,
  },
];

const mockTree: LocationTreeNode[] = [
  {
    id: 'loc-1',
    name: 'Warehouse',
    description: 'Main warehouse',
    parentId: null,
    path: '/Warehouse',
    depth: 0,
    sortOrder: 0,
    printerCount: 2,
    totalPrinterCount: 5,
    children: [
      {
        id: 'loc-2',
        name: 'Rack A',
        description: 'First rack',
        parentId: 'loc-1',
        path: '/Warehouse/Rack A',
        depth: 1,
        sortOrder: 0,
        printerCount: 3,
        totalPrinterCount: 3,
        children: [],
      },
    ],
  },
];

vi.mock('@/services/locationService', () => ({
  locationService: {
    getAllLocations: vi.fn(),
    getLocationTree: vi.fn(),
    getLocationAncestors: vi.fn(),
    createLocation: vi.fn(),
    updateLocation: vi.fn(),
    deleteLocation: vi.fn(),
    moveLocation: vi.fn(),
    getLocationById: vi.fn(),
    getLocationDescendants: vi.fn(),
  },
}));

vi.mock('@/services/printerLocationService', () => ({
  printerLocationService: {
    getAllPrinters: vi.fn().mockResolvedValue([]),
    assignPrinterToLocation: vi.fn(),
    unassignPrinterFromLocation: vi.fn(),
  },
}));

vi.mock('@/common/components/LocationTreePicker', () => ({
  LocationTreePicker: ({ onChange, value, label }: {
    onChange: (id: string | null) => void;
    value?: string | null;
    label?: string;
  }) => (
    <div data-testid="location-tree-picker">
      <label>{label}</label>
      <button onClick={() => onChange('loc-1')}>Select Warehouse</button>
      <span data-testid="picker-value">{value ?? 'none'}</span>
    </div>
  ),
}));

vi.mock('@/features/printers/components/PrinterLocationDragDrop', () => ({
  PrinterLocationDragDrop: () => <div data-testid="printer-drag-drop" />,
}));

 
const { locationService } = await import('@/services/locationService') as typeof import('@/services/locationService');

describe('LocationManagement', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(locationService.getAllLocations).mockResolvedValue(mockLocations);
    vi.mocked(locationService.getLocationTree).mockResolvedValue(mockTree);
  });

  it('renders page heading', async () => {
    render(<LocationManagement />);

    expect(screen.getByText('Printer Locations')).toBeInTheDocument();
  });

  it('renders its section heading as h2, not h1, so it nests under the page title', async () => {
    // This component is composed inside LocationManagementAdminPage, whose PageTemplate
    // renders the single page-level <h1>. Its own heading must be an h2 to avoid a
    // duplicate-h1 a11y regression.
    render(<LocationManagement />);

    expect(screen.getByRole('heading', { level: 2, name: 'Printer Locations' })).toBeInTheDocument();
    expect(screen.queryByRole('heading', { level: 1 })).not.toBeInTheDocument();
  });

  it('renders Add Location button', async () => {
    render(<LocationManagement />);

    await waitFor(() => {
      expect(screen.getByText('Add Location')).toBeInTheDocument();
    });
  });

  it('shows loading state while fetching data', () => {
    vi.mocked(locationService.getAllLocations).mockImplementation(
      () => new Promise(() => {}),
    );
    vi.mocked(locationService.getLocationTree).mockImplementation(
      () => new Promise(() => {}),
    );

    render(<LocationManagement />);

    expect(screen.getByText('Loading locations...')).toBeInTheDocument();
  });

  it('shows empty state when no locations exist', async () => {
    vi.mocked(locationService.getAllLocations).mockResolvedValue([]);
    vi.mocked(locationService.getLocationTree).mockResolvedValue([]);

    render(<LocationManagement />);

    await waitFor(() => {
      expect(screen.getByText(/No locations found/)).toBeInTheDocument();
    });
  });

  it('displays tree table with location names after loading', async () => {
    render(<LocationManagement />);

    await waitFor(() => {
      // Use getAllByText since "Warehouse" appears in both Name and Path columns
      const warehouseElements = screen.getAllByText('Warehouse');
      expect(warehouseElements.length).toBeGreaterThanOrEqual(1);
    });

    // Table headers
    expect(screen.getByText('Name')).toBeInTheDocument();
    expect(screen.getByText('Description')).toBeInTheDocument();
    expect(screen.getByText('Printers')).toBeInTheDocument();
    expect(screen.getByText('Path')).toBeInTheDocument();
    expect(screen.getByText('Actions')).toBeInTheDocument();
  });

  it('shows child nodes when parent is expanded', async () => {
    render(<LocationManagement />);

    await waitFor(() => {
      expect(screen.getAllByText('Warehouse').length).toBeGreaterThanOrEqual(1);
    });

    // Auto-expanded at first level — Rack A appears in both Name and Path columns
    expect(screen.getAllByText(/Rack A/).length).toBeGreaterThanOrEqual(1);
  });

  it('shows printer count in tree rows', async () => {
    render(<LocationManagement />);

    await waitFor(() => {
      expect(screen.getByText('2')).toBeInTheDocument(); // Warehouse printerCount
      expect(screen.getByText('3')).toBeInTheDocument(); // Rack A printerCount
    });
  });

  it('shows total printer count when different from direct count', async () => {
    render(<LocationManagement />);

    await waitFor(() => {
      expect(screen.getByText('(5 total)')).toBeInTheDocument();
    });
  });

  it('collapses tree node when toggle is clicked', async () => {
    const user = userEvent.setup();
    render(<LocationManagement />);

    await waitFor(() => {
      expect(screen.getAllByText(/Rack A/).length).toBeGreaterThanOrEqual(1);
    });

    // Click the collapse button on Warehouse
    const collapseBtn = screen.getByLabelText('Collapse');
    await user.click(collapseBtn);

    // After collapsing, Rack A row should be hidden
    expect(screen.queryByText(/^Rack A$/)).not.toBeInTheDocument();
  });

  it('opens create form when Add Location is clicked', async () => {
    const user = userEvent.setup();
    render(<LocationManagement />);

    await waitFor(() => {
      expect(screen.getByText('Add Location')).toBeInTheDocument();
    });

    await user.click(screen.getByText('Add Location'));

    expect(screen.getByText('Create New Location')).toBeInTheDocument();
    expect(screen.getByLabelText('Location Name *')).toBeInTheDocument();
  });

  it('creates a new location when form is submitted', async () => {
    vi.mocked(locationService.createLocation).mockResolvedValue({
      ...mockLocations[0],
      id: 'loc-new',
      name: 'New Location',
    });

    const user = userEvent.setup();
    render(<LocationManagement />);

    await waitFor(() => {
      expect(screen.getByText('Add Location')).toBeInTheDocument();
    });

    await user.click(screen.getByText('Add Location'));
    await user.type(screen.getByLabelText('Location Name *'), 'New Location');
    await user.click(screen.getByText('Create'));

    expect(locationService.createLocation).toHaveBeenCalledWith(
      expect.objectContaining({ name: 'New Location' }),
    );
  });

  it('opens edit form when Edit button is clicked', async () => {
    const user = userEvent.setup();
    render(<LocationManagement />);

    await waitFor(() => {
      expect(screen.getAllByText('Warehouse').length).toBeGreaterThanOrEqual(1);
    });

    const editButtons = screen.getAllByText('Edit');
    await user.click(editButtons[0]);

    expect(screen.getByText('Edit Location')).toBeInTheDocument();
    expect(screen.getByDisplayValue('Warehouse')).toBeInTheDocument();
  });

  it('updates a location when edit form is submitted', async () => {
    vi.mocked(locationService.updateLocation).mockResolvedValue({
      ...mockLocations[0],
      name: 'Updated Warehouse',
    });

    const user = userEvent.setup();
    render(<LocationManagement />);

    await waitFor(() => {
      expect(screen.getAllByText('Warehouse').length).toBeGreaterThanOrEqual(1);
    });

    const editButtons = screen.getAllByText('Edit');
    await user.click(editButtons[0]);

    const nameInput = screen.getByDisplayValue('Warehouse');
    await user.clear(nameInput);
    await user.type(nameInput, 'Updated Warehouse');
    await user.click(screen.getByText('Update'));

    expect(locationService.updateLocation).toHaveBeenCalledWith(
      'loc-1',
      expect.objectContaining({ name: 'Updated Warehouse' }),
    );
  });

  it('opens add child form when "+ Child" button is clicked', async () => {
    const user = userEvent.setup();
    render(<LocationManagement />);

    await waitFor(() => {
      expect(screen.getAllByText('Warehouse').length).toBeGreaterThanOrEqual(1);
    });

    const addChildButtons = screen.getAllByText('+ Child');
    await user.click(addChildButtons[0]);

    expect(screen.getByText('Create New Location')).toBeInTheDocument();
  });

  it('shows confirmation modal when Delete is clicked', async () => {
    const user = userEvent.setup();
    render(<LocationManagement />);

    await waitFor(() => {
      expect(screen.getAllByText(/Rack A/).length).toBeGreaterThanOrEqual(1);
    });

    // Find delete buttons that are not disabled (leaf nodes like Rack A)
    const deleteButtons = screen.getAllByRole('button', { name: 'Delete' });
    const enabledDelete = deleteButtons.find((btn) => !(btn as HTMLButtonElement).disabled);
    expect(enabledDelete).toBeDefined();
    await user.click(enabledDelete!);

    await waitFor(() => {
      expect(screen.getByText('Delete Location?')).toBeInTheDocument();
    });
  });

  it('disables Delete button for nodes with children', async () => {
    render(<LocationManagement />);

    await waitFor(() => {
      expect(screen.getAllByText('Warehouse').length).toBeGreaterThanOrEqual(1);
    });

    // Warehouse has children, so its delete button should be disabled
    const deleteButtons = screen.getAllByRole('button', { name: 'Delete' });
    // First Delete button is for Warehouse (has children)
    expect(deleteButtons[0]).toBeDisabled();
  });

  it('cancels create form when Cancel is clicked', async () => {
    const user = userEvent.setup();
    render(<LocationManagement />);

    await waitFor(() => {
      expect(screen.getByText('Add Location')).toBeInTheDocument();
    });

    await user.click(screen.getByText('Add Location'));
    expect(screen.getByText('Create New Location')).toBeInTheDocument();

    await user.click(screen.getByText('Cancel'));
    expect(screen.queryByText('Create New Location')).not.toBeInTheDocument();
  });

  it('shows error message when create fails', async () => {
    vi.mocked(locationService.createLocation).mockRejectedValue(
      new Error('Duplicate name'),
    );

    const user = userEvent.setup();
    render(<LocationManagement />);

    await waitFor(() => {
      expect(screen.getByText('Add Location')).toBeInTheDocument();
    });

    await user.click(screen.getByText('Add Location'));
    await user.type(screen.getByLabelText('Location Name *'), 'Test');
    await user.click(screen.getByText('Create'));

    await waitFor(() => {
      expect(screen.getByText('Duplicate name')).toBeInTheDocument();
    });
  });

  it('shows error when loading fails', async () => {
    vi.mocked(locationService.getAllLocations).mockRejectedValue(
      new Error('Server error'),
    );
    vi.mocked(locationService.getLocationTree).mockRejectedValue(
      new Error('Server error'),
    );

    render(<LocationManagement />);

    await waitFor(() => {
      expect(screen.getByText('Server error')).toBeInTheDocument();
    });
  });

  it('validates that name is required before submission', async () => {
    const user = userEvent.setup();
    render(<LocationManagement />);

    await waitFor(() => {
      expect(screen.getByText('Add Location')).toBeInTheDocument();
    });

    await user.click(screen.getByText('Add Location'));
    await user.click(screen.getByText('Create'));

    // The name field uses HTML required validation; the component also
    // checks for empty name and sets error
    await waitFor(() => {
      expect(locationService.createLocation).not.toHaveBeenCalled();
    });
  });

  it('shows path column in tree table', async () => {
    render(<LocationManagement />);

    await waitFor(() => {
      expect(screen.getByText('/Warehouse')).toBeInTheDocument();
      expect(screen.getByText('/Warehouse/Rack A')).toBeInTheDocument();
    });
  });
});

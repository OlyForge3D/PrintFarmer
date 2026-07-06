import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { LocationManagement } from '@/features/locations/components/LocationManagement';
import type { Location, LocationTreeNode } from '@/services/locationService';

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

vi.mock('@/features/locations/components/LocationTreePicker', () => ({
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
    invalidateQueriesMock.mockClear();
    vi.mocked(locationService.getAllLocations).mockResolvedValue(mockLocations);
    vi.mocked(locationService.getLocationTree).mockResolvedValue(mockTree);
  });

  it('renders page heading', async () => {
    render(<LocationManagement />);

    expect(screen.getByText('Printer Locations')).toBeInTheDocument();
    expect(await screen.findByText('Add Location')).toBeInTheDocument();
  });

  it('renders its section heading as h2, not h1, so it nests under the page title', async () => {
    // This component is composed inside LocationManagementAdminPage, whose PageTemplate
    // renders the single page-level <h1>. Its own heading must be an h2 to avoid a
    // duplicate-h1 a11y regression.
    render(<LocationManagement />);

    expect(screen.getByRole('heading', { level: 2, name: 'Printer Locations' })).toBeInTheDocument();
    expect(screen.queryByRole('heading', { level: 1 })).not.toBeInTheDocument();
    expect(await screen.findByText('Add Location')).toBeInTheDocument();
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
    const collapseBtn = screen.getByLabelText('Collapse Warehouse');
    expect(collapseBtn).toHaveAttribute('aria-expanded', 'true');
    await user.click(collapseBtn);
    expect(collapseBtn).toHaveAttribute('aria-expanded', 'false');

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
    expect(invalidateQueriesMock).toHaveBeenCalledWith({ queryKey: ['locations', 'tree'] });
    expect(invalidateQueriesMock).toHaveBeenCalledWith({ queryKey: ['locations', 'all-printers'] });
    expect(invalidateQueriesMock).toHaveBeenCalledWith({ queryKey: ['printers'] });
    expect(invalidateQueriesMock).toHaveBeenCalledWith(expect.objectContaining({ predicate: expect.any(Function) }));
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
    expect(invalidateQueriesMock).toHaveBeenCalledWith({ queryKey: ['locations', 'tree'] });
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

  it('invalidates dashboard queries after delete is confirmed', async () => {
    vi.mocked(locationService.deleteLocation).mockResolvedValue(undefined);

    const user = userEvent.setup();
    render(<LocationManagement />);

    await waitFor(() => {
      expect(screen.getAllByText(/Rack A/).length).toBeGreaterThanOrEqual(1);
    });

    const enabledDelete = screen
      .getAllByRole('button', { name: 'Delete' })
      .find((btn) => !(btn as HTMLButtonElement).disabled);
    expect(enabledDelete).toBeDefined();
    await user.click(enabledDelete!);
    const confirmDeleteButton = screen.getAllByRole('button', { name: 'Delete' }).at(-1);
    expect(confirmDeleteButton).toBeDefined();
    await user.click(confirmDeleteButton!);

    await waitFor(() => {
      expect(locationService.deleteLocation).toHaveBeenCalledWith('loc-2');
    });
    expect(invalidateQueriesMock).toHaveBeenCalledWith({ queryKey: ['locations', 'tree'] });
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

    await waitFor(() => {
      expect(locationService.createLocation).not.toHaveBeenCalled();
    });
    expect(screen.getAllByRole('alert')[0]).toHaveTextContent('Location name is required');
    expect(screen.getByLabelText('Location Name *')).toHaveAttribute('aria-invalid', 'true');
    expect(screen.getAllByText('Location name is required').length).toBeGreaterThanOrEqual(1);
  });

  it('does not reopen or reset create form when only the initial parent changes', async () => {
    const user = userEvent.setup();
    const { rerender } = render(
      <LocationManagement embedded autoOpenCreateToken={1} initialParentId="loc-1" />,
    );

    expect(await screen.findByText('Create New Location')).toBeInTheDocument();
    await user.type(screen.getByLabelText('Location Name *'), 'Draft location');

    rerender(<LocationManagement embedded autoOpenCreateToken={1} initialParentId="loc-2" />);

    expect(screen.getByDisplayValue('Draft location')).toBeInTheDocument();
    expect(screen.getByTestId('picker-value')).toHaveTextContent('loc-1');
  });

  it('opens a fresh create form when the auto-open token changes', async () => {
    const user = userEvent.setup();
    const { rerender } = render(
      <LocationManagement embedded autoOpenCreateToken={1} initialParentId="loc-1" />,
    );

    expect(await screen.findByText('Create New Location')).toBeInTheDocument();
    await user.type(screen.getByLabelText('Location Name *'), 'Draft location');

    rerender(<LocationManagement embedded autoOpenCreateToken={2} initialParentId="loc-2" />);

    expect(screen.getByLabelText('Location Name *')).toHaveValue('');
    expect(screen.getByTestId('picker-value')).toHaveTextContent('loc-2');
  });

  it('invalidates dashboard queries after moving a location', async () => {
    vi.mocked(locationService.moveLocation).mockResolvedValue(mockLocations[1]);

    const user = userEvent.setup();
    render(<LocationManagement />);

    await waitFor(() => {
      expect(screen.getAllByText(/Rack A/).length).toBeGreaterThanOrEqual(1);
    });

    const moveButtons = screen.getAllByRole('button', { name: 'Move' });
    await user.click(moveButtons[1]);
    await user.click(screen.getByRole('button', { name: 'Move location' }));

    await waitFor(() => {
      expect(locationService.moveLocation).toHaveBeenCalledWith('loc-2', { newParentId: 'loc-1' });
    });
    expect(invalidateQueriesMock).toHaveBeenCalledWith({ queryKey: ['locations', 'tree'] });
    expect(invalidateQueriesMock).toHaveBeenCalledWith({ queryKey: ['locations', 'all-printers'] });
    expect(invalidateQueriesMock).toHaveBeenCalledWith({ queryKey: ['printers'] });
    expect(invalidateQueriesMock).toHaveBeenCalledWith(expect.objectContaining({ predicate: expect.any(Function) }));
  });

  it('shows path column in tree table', async () => {
    render(<LocationManagement />);

    await waitFor(() => {
      expect(screen.getByText('/Warehouse')).toBeInTheDocument();
      expect(screen.getByText('/Warehouse/Rack A')).toBeInTheDocument();
    });
  });
});

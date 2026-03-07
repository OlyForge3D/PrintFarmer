import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { LocationTreePicker } from '@/common/components/LocationTreePicker';
import type { LocationTreeNode } from '@/services/locationService';

const mockTree: LocationTreeNode[] = [
  {
    id: 'loc-1',
    name: 'Building A',
    description: 'Main building',
    parentId: null,
    path: '/Building A',
    depth: 0,
    sortOrder: 0,
    printerCount: 2,
    totalPrinterCount: 5,
    children: [
      {
        id: 'loc-2',
        name: 'Floor 1',
        description: 'First floor',
        parentId: 'loc-1',
        path: '/Building A/Floor 1',
        depth: 1,
        sortOrder: 0,
        printerCount: 3,
        totalPrinterCount: 3,
        children: [],
      },
      {
        id: 'loc-3',
        name: 'Floor 2',
        description: 'Second floor',
        parentId: 'loc-1',
        path: '/Building A/Floor 2',
        depth: 1,
        sortOrder: 1,
        printerCount: 0,
        totalPrinterCount: 0,
        children: [],
      },
    ],
  },
  {
    id: 'loc-4',
    name: 'Building B',
    description: '',
    parentId: null,
    path: '/Building B',
    depth: 0,
    sortOrder: 1,
    printerCount: 0,
    totalPrinterCount: 0,
    children: [],
  },
];

vi.mock('@/services/locationService', () => ({
  locationService: {
    getLocationTree: vi.fn(),
    getAllLocations: vi.fn(),
    getLocationAncestors: vi.fn(),
    getLocationDescendants: vi.fn(),
    createLocation: vi.fn(),
    updateLocation: vi.fn(),
    moveLocation: vi.fn(),
    deleteLocation: vi.fn(),
  },
  // Re-export the type so imports don't break
}));

 
const { locationService } = await import('@/services/locationService') as typeof import('@/services/locationService');

describe('LocationTreePicker', () => {
  const defaultProps = {
    onChange: vi.fn(),
  };

  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(locationService.getLocationTree).mockResolvedValue(mockTree);
  });

  it('renders with default label and placeholder', async () => {
    render(<LocationTreePicker {...defaultProps} />);

    expect(screen.getByText('Location')).toBeInTheDocument();
    await waitFor(() => {
      expect(screen.getByText('Select a location...')).toBeInTheDocument();
    });
  });

  it('renders custom label and placeholder', async () => {
    render(
      <LocationTreePicker
        {...defaultProps}
        label="Parent Location"
        placeholder="Choose parent..."
      />,
    );

    expect(screen.getByText('Parent Location')).toBeInTheDocument();
    await waitFor(() => {
      expect(screen.getByText('Choose parent...')).toBeInTheDocument();
    });
  });

  it('shows required indicator when required is true', () => {
    render(<LocationTreePicker {...defaultProps} required />);

    expect(screen.getByText('*')).toBeInTheDocument();
  });

  it('shows loading state while fetching tree', () => {
    vi.mocked(locationService.getLocationTree).mockImplementation(
      () => new Promise(() => {}), // never resolves
    );

    render(<LocationTreePicker {...defaultProps} />);

    expect(screen.getByText('Loading...')).toBeInTheDocument();
  });

  it('opens dropdown on click and shows tree nodes', async () => {
    const user = userEvent.setup();
    render(<LocationTreePicker {...defaultProps} />);

    await waitFor(() => {
      expect(screen.getByText('Select a location...')).toBeInTheDocument();
    });

    await user.click(screen.getByText('Select a location...'));

    expect(screen.getByText('Building A')).toBeInTheDocument();
    expect(screen.getByText('Building B')).toBeInTheDocument();
  });

  it('shows search input when dropdown is open', async () => {
    const user = userEvent.setup();
    render(<LocationTreePicker {...defaultProps} />);

    await waitFor(() => {
      expect(screen.getByText('Select a location...')).toBeInTheDocument();
    });

    await user.click(screen.getByText('Select a location...'));

    expect(screen.getByPlaceholderText('Search locations...')).toBeInTheDocument();
  });

  it('selects a location and calls onChange', async () => {
    const onChange = vi.fn();
    const user = userEvent.setup();
    render(<LocationTreePicker onChange={onChange} />);

    await waitFor(() => {
      expect(screen.getByText('Select a location...')).toBeInTheDocument();
    });

    await user.click(screen.getByText('Select a location...'));
    await user.click(screen.getByText('Building A'));

    expect(onChange).toHaveBeenCalledWith('loc-1');
  });

  it('displays selected location name when value is set', async () => {
    render(<LocationTreePicker {...defaultProps} value="loc-1" />);

    await waitFor(() => {
      expect(screen.getByText('Building A')).toBeInTheDocument();
    });
  });

  it('displays full path for nested selected location', async () => {
    render(<LocationTreePicker {...defaultProps} value="loc-2" />);

    await waitFor(() => {
      expect(screen.getByText('Building A / Floor 1')).toBeInTheDocument();
    });
  });

  it('shows clear button when a value is selected', async () => {
    render(<LocationTreePicker {...defaultProps} value="loc-1" />);

    await waitFor(() => {
      expect(screen.getByLabelText('Clear selection')).toBeInTheDocument();
    });
  });

  it('clears selection when clear button is clicked', async () => {
    const onChange = vi.fn();
    const user = userEvent.setup();
    render(<LocationTreePicker onChange={onChange} value="loc-1" />);

    await waitFor(() => {
      expect(screen.getByLabelText('Clear selection')).toBeInTheDocument();
    });

    await user.click(screen.getByLabelText('Clear selection'));

    expect(onChange).toHaveBeenCalledWith(null);
  });

  it('shows "No location (unassigned)" option when not required', async () => {
    const user = userEvent.setup();
    render(<LocationTreePicker {...defaultProps} required={false} />);

    await waitFor(() => {
      expect(screen.getByText('Select a location...')).toBeInTheDocument();
    });

    await user.click(screen.getByText('Select a location...'));

    expect(screen.getByText('No location (unassigned)')).toBeInTheDocument();
  });

  it('hides "No location" option when required', async () => {
    const user = userEvent.setup();
    render(<LocationTreePicker {...defaultProps} required />);

    await waitFor(() => {
      expect(screen.getByText('Select a location...')).toBeInTheDocument();
    });

    await user.click(screen.getByText('Select a location...'));

    expect(screen.queryByText('No location (unassigned)')).not.toBeInTheDocument();
  });

  it('expands children when toggle arrow is clicked', async () => {
    const user = userEvent.setup();
    render(<LocationTreePicker {...defaultProps} />);

    await waitFor(() => {
      expect(screen.getByText('Select a location...')).toBeInTheDocument();
    });

    await user.click(screen.getByText('Select a location...'));

    // Building A auto-expands at first level, so children should be visible
    expect(screen.getByText('Floor 1')).toBeInTheDocument();
    expect(screen.getByText('Floor 2')).toBeInTheDocument();
  });

  it('collapses children when toggle arrow is clicked on expanded node', async () => {
    const user = userEvent.setup();
    render(<LocationTreePicker {...defaultProps} />);

    await waitFor(() => {
      expect(screen.getByText('Select a location...')).toBeInTheDocument();
    });

    await user.click(screen.getByText('Select a location...'));

    // Find the collapse button for Building A (which is auto-expanded)
    const collapseButtons = screen.getAllByLabelText('Collapse');
    await user.click(collapseButtons[0]);

    expect(screen.queryByText('Floor 1')).not.toBeInTheDocument();
  });

  it('filters tree nodes by search term', async () => {
    const user = userEvent.setup();
    render(<LocationTreePicker {...defaultProps} />);

    await waitFor(() => {
      expect(screen.getByText('Select a location...')).toBeInTheDocument();
    });

    await user.click(screen.getByText('Select a location...'));
    await user.type(screen.getByPlaceholderText('Search locations...'), 'Floor 1');

    // Building A still visible because it has a matching child
    expect(screen.getByText('Building A')).toBeInTheDocument();
    expect(screen.getByText('Floor 1')).toBeInTheDocument();
    // Building B should be hidden (no match)
    expect(screen.queryByText('Building B')).not.toBeInTheDocument();
  });

  it('shows "No locations found" when tree is empty', async () => {
    vi.mocked(locationService.getLocationTree).mockResolvedValue([]);
    const user = userEvent.setup();
    render(<LocationTreePicker {...defaultProps} />);

    await waitFor(() => {
      expect(screen.getByText('Select a location...')).toBeInTheDocument();
    });

    await user.click(screen.getByText('Select a location...'));

    expect(screen.getByText('No locations found')).toBeInTheDocument();
  });

  it('excludes a node by excludeId', async () => {
    const user = userEvent.setup();
    render(<LocationTreePicker {...defaultProps} excludeId="loc-4" />);

    await waitFor(() => {
      expect(screen.getByText('Select a location...')).toBeInTheDocument();
    });

    await user.click(screen.getByText('Select a location...'));

    expect(screen.getByText('Building A')).toBeInTheDocument();
    expect(screen.queryByText('Building B')).not.toBeInTheDocument();
  });

  it('renders disabled state', async () => {
    render(<LocationTreePicker {...defaultProps} disabled />);

    await waitFor(() => {
      const trigger = screen.getByRole('button', { name: /select a location/i });
      expect(trigger).toBeDisabled();
    });
  });

  it('shows printer count badge for nodes with printers', async () => {
    const user = userEvent.setup();
    render(<LocationTreePicker {...defaultProps} />);

    await waitFor(() => {
      expect(screen.getByText('Select a location...')).toBeInTheDocument();
    });

    await user.click(screen.getByText('Select a location...'));

    // Building A has totalPrinterCount of 5
    expect(screen.getByText('5')).toBeInTheDocument();
  });

  it('handles API error gracefully with empty tree', async () => {
    vi.mocked(locationService.getLocationTree).mockRejectedValue(new Error('Network error'));
    const user = userEvent.setup();
    render(<LocationTreePicker {...defaultProps} />);

    await waitFor(() => {
      expect(screen.getByText('Select a location...')).toBeInTheDocument();
    });

    await user.click(screen.getByText('Select a location...'));

    expect(screen.getByText('No locations found')).toBeInTheDocument();
  });
});

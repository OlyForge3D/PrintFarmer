import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import type { LocationTreeNode } from '@/services/locationService';

/**
 * Tests for the enhanced LocationTreePicker in features/locations/.
 * Ripley is building this component concurrently — tests target the expected interface.
 * Once the component lands, adjust the import path if needed.
 */

// Use common component until feature-level one exists
import { LocationTreePicker } from '@/common/components/LocationTreePicker';

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
        children: [
          {
            id: 'loc-5',
            name: 'Room 101',
            description: 'Corner office',
            parentId: 'loc-2',
            path: '/Building A/Floor 1/Room 101',
            depth: 2,
            sortOrder: 0,
            printerCount: 1,
            totalPrinterCount: 1,
            children: [],
          },
        ],
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
    description: 'Storage building',
    parentId: null,
    path: '/Building B',
    depth: 0,
    sortOrder: 1,
    printerCount: 1,
    totalPrinterCount: 1,
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
}));

// eslint-disable-next-line @typescript-eslint/consistent-type-imports
const { locationService } = await import('@/services/locationService') as typeof import('@/services/locationService');

describe('LocationTreePicker — tree rendering and indentation', () => {
  const defaultProps = {
    onChange: vi.fn(),
  };

  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(locationService.getLocationTree).mockResolvedValue(mockTree);
  });

  it('renders root-level tree nodes after loading', async () => {
    const user = userEvent.setup();
    render(<LocationTreePicker {...defaultProps} />);

    await waitFor(() => {
      expect(screen.getByText('Select a location...')).toBeInTheDocument();
    });

    await user.click(screen.getByText('Select a location...'));

    expect(screen.getByText('Building A')).toBeInTheDocument();
    expect(screen.getByText('Building B')).toBeInTheDocument();
  });

  it('indents child nodes deeper than root nodes', async () => {
    const user = userEvent.setup();
    render(<LocationTreePicker {...defaultProps} />);

    await waitFor(() => {
      expect(screen.getByText('Select a location...')).toBeInTheDocument();
    });

    await user.click(screen.getByText('Select a location...'));

    // First level auto-expanded, Floor 1 should be visible
    const floor1Button = screen.getByText('Floor 1').closest('button');
    const buildingAButton = screen.getByText('Building A').closest('button');

    expect(floor1Button).not.toBeNull();
    expect(buildingAButton).not.toBeNull();

    // Child nodes should have greater padding (indentation)
    const floor1Padding = floor1Button?.style.paddingLeft;
    const buildingAPadding = buildingAButton?.style.paddingLeft;
    expect(parseInt(floor1Padding ?? '0')).toBeGreaterThan(parseInt(buildingAPadding ?? '0'));
  });

  it('shows three levels of depth when all expanded', async () => {
    const user = userEvent.setup();
    render(<LocationTreePicker {...defaultProps} />);

    await waitFor(() => {
      expect(screen.getByText('Select a location...')).toBeInTheDocument();
    });

    await user.click(screen.getByText('Select a location...'));

    // First level auto-expanded — Floor 1 visible
    expect(screen.getByText('Floor 1')).toBeInTheDocument();

    // Expand Floor 1 to see Room 101
    const expandButtons = screen.getAllByLabelText('Expand');
    await user.click(expandButtons[0]); // First "Expand" is Floor 1 (the only expandable child)

    expect(screen.getByText('Room 101')).toBeInTheDocument();
  });
});

describe('LocationTreePicker — expand/collapse behavior', () => {
  const defaultProps = {
    onChange: vi.fn(),
  };

  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(locationService.getLocationTree).mockResolvedValue(mockTree);
  });

  it('auto-expands first level nodes on load', async () => {
    const user = userEvent.setup();
    render(<LocationTreePicker {...defaultProps} />);

    await waitFor(() => {
      expect(screen.getByText('Select a location...')).toBeInTheDocument();
    });

    await user.click(screen.getByText('Select a location...'));

    // Children of root nodes should be visible (auto-expanded)
    expect(screen.getByText('Floor 1')).toBeInTheDocument();
    expect(screen.getByText('Floor 2')).toBeInTheDocument();
  });

  it('collapses a node when collapse toggle is clicked', async () => {
    const user = userEvent.setup();
    render(<LocationTreePicker {...defaultProps} />);

    await waitFor(() => {
      expect(screen.getByText('Select a location...')).toBeInTheDocument();
    });

    await user.click(screen.getByText('Select a location...'));

    // Verify children visible
    expect(screen.getByText('Floor 1')).toBeInTheDocument();

    // Click collapse on Building A
    const collapseButtons = screen.getAllByLabelText('Collapse');
    await user.click(collapseButtons[0]);

    // Floor 1 and Floor 2 should disappear
    expect(screen.queryByText('Floor 1')).not.toBeInTheDocument();
    expect(screen.queryByText('Floor 2')).not.toBeInTheDocument();
  });

  it('re-expands a previously collapsed node', async () => {
    const user = userEvent.setup();
    render(<LocationTreePicker {...defaultProps} />);

    await waitFor(() => {
      expect(screen.getByText('Select a location...')).toBeInTheDocument();
    });

    await user.click(screen.getByText('Select a location...'));

    // Collapse
    const collapseButtons = screen.getAllByLabelText('Collapse');
    await user.click(collapseButtons[0]);
    expect(screen.queryByText('Floor 1')).not.toBeInTheDocument();

    // Re-expand
    const expandButtons = screen.getAllByLabelText('Expand');
    const buildingAExpand = expandButtons[0];
    await user.click(buildingAExpand);

    expect(screen.getByText('Floor 1')).toBeInTheDocument();
  });

  it('leaf nodes do not show expand/collapse controls', async () => {
    const user = userEvent.setup();
    render(<LocationTreePicker {...defaultProps} />);

    await waitFor(() => {
      expect(screen.getByText('Select a location...')).toBeInTheDocument();
    });

    await user.click(screen.getByText('Select a location...'));

    // Floor 2 has no children — should not have expand/collapse
    const floor2Button = screen.getByText('Floor 2').closest('button');
    expect(floor2Button?.getAttribute('aria-expanded')).toBeNull();
  });

  it('sets aria-expanded correctly on parent nodes', async () => {
    const user = userEvent.setup();
    render(<LocationTreePicker {...defaultProps} />);

    await waitFor(() => {
      expect(screen.getByText('Select a location...')).toBeInTheDocument();
    });

    await user.click(screen.getByText('Select a location...'));

    // Building A is auto-expanded
    const buildingAItem = screen.getByRole('treeitem', { name: /Building A/i });
    expect(buildingAItem).toHaveAttribute('aria-expanded', 'true');
  });
});

describe('LocationTreePicker — search filtering', () => {
  const defaultProps = {
    onChange: vi.fn(),
  };

  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(locationService.getLocationTree).mockResolvedValue(mockTree);
  });

  it('filters tree nodes by search term', async () => {
    const user = userEvent.setup();
    render(<LocationTreePicker {...defaultProps} />);

    await waitFor(() => {
      expect(screen.getByText('Select a location...')).toBeInTheDocument();
    });

    await user.click(screen.getByText('Select a location...'));
    await user.type(screen.getByPlaceholderText('Search locations...'), 'Floor 1');

    // Building A visible (ancestor of match), Floor 1 visible (match)
    expect(screen.getByText('Building A')).toBeInTheDocument();
    expect(screen.getByText('Floor 1')).toBeInTheDocument();
    // Building B filtered out (no match)
    expect(screen.queryByText('Building B')).not.toBeInTheDocument();
  });

  it('shows parent nodes when child matches search', async () => {
    const user = userEvent.setup();
    render(<LocationTreePicker {...defaultProps} />);

    await waitFor(() => {
      expect(screen.getByText('Select a location...')).toBeInTheDocument();
    });

    await user.click(screen.getByText('Select a location...'));
    await user.type(screen.getByPlaceholderText('Search locations...'), 'Building B');

    expect(screen.getByText('Building B')).toBeInTheDocument();
    // Building A should be hidden (doesn't match and no children match "Building B")
    expect(screen.queryByText('Building A')).not.toBeInTheDocument();
  });

  it('is case-insensitive when filtering', async () => {
    const user = userEvent.setup();
    render(<LocationTreePicker {...defaultProps} />);

    await waitFor(() => {
      expect(screen.getByText('Select a location...')).toBeInTheDocument();
    });

    await user.click(screen.getByText('Select a location...'));
    await user.type(screen.getByPlaceholderText('Search locations...'), 'building b');

    expect(screen.getByText('Building B')).toBeInTheDocument();
  });

  it('shows empty state when search matches nothing', async () => {
    const user = userEvent.setup();
    render(<LocationTreePicker {...defaultProps} />);

    await waitFor(() => {
      expect(screen.getByText('Select a location...')).toBeInTheDocument();
    });

    await user.click(screen.getByText('Select a location...'));
    await user.type(screen.getByPlaceholderText('Search locations...'), 'nonexistent location xyz');

    expect(screen.queryByText('Building A')).not.toBeInTheDocument();
    expect(screen.queryByText('Building B')).not.toBeInTheDocument();
  });
});

describe('LocationTreePicker — selection and printer counts', () => {
  const defaultProps = {
    onChange: vi.fn(),
  };

  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(locationService.getLocationTree).mockResolvedValue(mockTree);
  });

  it('calls onChange with node id when a location is selected', async () => {
    const onChange = vi.fn();
    const user = userEvent.setup();
    render(<LocationTreePicker onChange={onChange} />);

    await waitFor(() => {
      expect(screen.getByText('Select a location...')).toBeInTheDocument();
    });

    await user.click(screen.getByText('Select a location...'));
    await user.click(screen.getByText('Floor 1'));

    expect(onChange).toHaveBeenCalledWith('loc-2');
  });

  it('closes dropdown after selecting a location', async () => {
    const user = userEvent.setup();
    render(<LocationTreePicker {...defaultProps} />);

    await waitFor(() => {
      expect(screen.getByText('Select a location...')).toBeInTheDocument();
    });

    await user.click(screen.getByText('Select a location...'));
    await user.click(screen.getByText('Building B'));

    // Dropdown should close — tree role should disappear
    expect(screen.queryByRole('tree')).not.toBeInTheDocument();
  });

  it('displays selected location path in the trigger button', async () => {
    render(<LocationTreePicker {...defaultProps} value="loc-2" />);

    await waitFor(() => {
      expect(screen.getByText('Building A / Floor 1')).toBeInTheDocument();
    });
  });

  it('shows totalPrinterCount badge for nodes with printers', async () => {
    const user = userEvent.setup();
    render(<LocationTreePicker {...defaultProps} />);

    await waitFor(() => {
      expect(screen.getByText('Select a location...')).toBeInTheDocument();
    });

    await user.click(screen.getByText('Select a location...'));

    // Building A: totalPrinterCount = 5
    expect(screen.getByText('5')).toBeInTheDocument();
    // Floor 1: totalPrinterCount = 3
    expect(screen.getByText('3')).toBeInTheDocument();
  });

  it('does not show printer count badge when totalPrinterCount is 0', async () => {
    const user = userEvent.setup();
    render(<LocationTreePicker {...defaultProps} />);

    await waitFor(() => {
      expect(screen.getByText('Select a location...')).toBeInTheDocument();
    });

    await user.click(screen.getByText('Select a location...'));

    // Floor 2 has 0 printers — no count should be visible
    // Building B has totalPrinterCount=1, Floor 2 has 0
    // Verify "0" is NOT rendered as a badge
    const floor2Button = screen.getByText('Floor 2').closest('button');
    expect(floor2Button).not.toBeNull();
    expect(within(floor2Button!).queryByText('0')).not.toBeInTheDocument();
  });

  it('marks selected treeitem with aria-selected', async () => {
    const user = userEvent.setup();
    render(<LocationTreePicker {...defaultProps} value="loc-2" />);

    await waitFor(() => {
      expect(screen.getByText('Building A / Floor 1')).toBeInTheDocument();
    });

    await user.click(screen.getByText('Building A / Floor 1'));

    const selectedItem = screen.getByRole('treeitem', { selected: true });
    expect(selectedItem).toBeInTheDocument();
  });

  it('excludes subtree when excludeId is set', async () => {
    const user = userEvent.setup();
    render(<LocationTreePicker {...defaultProps} excludeId="loc-1" />);

    await waitFor(() => {
      expect(screen.getByText('Select a location...')).toBeInTheDocument();
    });

    await user.click(screen.getByText('Select a location...'));

    // Building A and all children should be excluded
    expect(screen.queryByText('Building A')).not.toBeInTheDocument();
    expect(screen.queryByText('Floor 1')).not.toBeInTheDocument();
    // Building B should still be present
    expect(screen.getByText('Building B')).toBeInTheDocument();
  });
});

describe('LocationTreePicker — edge cases and error handling', () => {
  const defaultProps = {
    onChange: vi.fn(),
  };

  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('shows loading state while tree is being fetched', () => {
    vi.mocked(locationService.getLocationTree).mockImplementation(
      () => new Promise(() => {}),
    );

    render(<LocationTreePicker {...defaultProps} />);

    expect(screen.getByText('Loading...')).toBeInTheDocument();
  });

  it('shows empty state when tree has no locations', async () => {
    vi.mocked(locationService.getLocationTree).mockResolvedValue([]);
    const user = userEvent.setup();
    render(<LocationTreePicker {...defaultProps} />);

    await waitFor(() => {
      expect(screen.getByText('Select a location...')).toBeInTheDocument();
    });

    await user.click(screen.getByText('Select a location...'));

    expect(screen.getByText('No locations found')).toBeInTheDocument();
  });

  it('handles API error and shows empty tree', async () => {
    vi.mocked(locationService.getLocationTree).mockRejectedValue(new Error('Server error'));
    const user = userEvent.setup();
    render(<LocationTreePicker {...defaultProps} />);

    await waitFor(() => {
      expect(screen.getByText('Select a location...')).toBeInTheDocument();
    });

    await user.click(screen.getByText('Select a location...'));

    expect(screen.getByText('No locations found')).toBeInTheDocument();
  });

  it('disables trigger button when disabled prop is true', async () => {
    vi.mocked(locationService.getLocationTree).mockResolvedValue(mockTree);
    render(<LocationTreePicker {...defaultProps} disabled />);

    await waitFor(() => {
      const trigger = screen.getByRole('button', { name: /select a location/i });
      expect(trigger).toBeDisabled();
    });
  });

  it('clears selection and calls onChange with null', async () => {
    vi.mocked(locationService.getLocationTree).mockResolvedValue(mockTree);
    const onChange = vi.fn();
    const user = userEvent.setup();
    render(<LocationTreePicker onChange={onChange} value="loc-1" />);

    await waitFor(() => {
      expect(screen.getByLabelText('Clear selection')).toBeInTheDocument();
    });

    await user.click(screen.getByLabelText('Clear selection'));

    expect(onChange).toHaveBeenCalledWith(null);
  });

  it('has tree role on the dropdown container', async () => {
    vi.mocked(locationService.getLocationTree).mockResolvedValue(mockTree);
    const user = userEvent.setup();
    render(<LocationTreePicker {...defaultProps} />);

    await waitFor(() => {
      expect(screen.getByText('Select a location...')).toBeInTheDocument();
    });

    await user.click(screen.getByText('Select a location...'));

    expect(screen.getByRole('tree')).toBeInTheDocument();
  });

  it('trigger button has aria-haspopup="tree"', async () => {
    vi.mocked(locationService.getLocationTree).mockResolvedValue(mockTree);
    render(<LocationTreePicker {...defaultProps} />);

    await waitFor(() => {
      const trigger = screen.getByRole('button', { name: /select a location/i });
      expect(trigger).toHaveAttribute('aria-haspopup', 'tree');
    });
  });
});

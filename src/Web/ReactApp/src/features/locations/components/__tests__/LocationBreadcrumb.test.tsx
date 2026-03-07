import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import type { LocationBreadcrumbItem } from '@/services/locationService';

/**
 * Tests for LocationBreadcrumb component in the locations feature.
 * Ripley is building enhanced versions concurrently.
 * Uses common component until feature-level one exists.
 */
import { LocationBreadcrumb } from '@/common/components/LocationBreadcrumb';

const mockThreeSegmentPath: LocationBreadcrumbItem[] = [
  { id: 'loc-1', name: 'Building A', depth: 0 },
  { id: 'loc-2', name: 'Floor 1', depth: 1 },
  { id: 'loc-3', name: 'Room 101', depth: 2 },
];

const mockSingleSegmentPath: LocationBreadcrumbItem[] = [
  { id: 'loc-root', name: 'Warehouse', depth: 0 },
];

const mockDeepPath: LocationBreadcrumbItem[] = [
  { id: 'l1', name: 'Campus', depth: 0 },
  { id: 'l2', name: 'East Wing', depth: 1 },
  { id: 'l3', name: 'Lab 3', depth: 2 },
  { id: 'l4', name: 'Rack 7', depth: 3 },
  { id: 'l5', name: 'Shelf B', depth: 4 },
];

vi.mock('@/services/locationService', () => ({
  locationService: {
    getLocationAncestors: vi.fn(),
    getLocationTree: vi.fn(),
    getAllLocations: vi.fn(),
  },
}));

// eslint-disable-next-line @typescript-eslint/consistent-type-imports
const { locationService } = await import('@/services/locationService') as typeof import('@/services/locationService');

describe('LocationBreadcrumb — path rendering', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('renders loading indicator while fetching ancestors', () => {
    vi.mocked(locationService.getLocationAncestors).mockImplementation(
      () => new Promise(() => {}),
    );

    render(<LocationBreadcrumb locationId="loc-3" />);

    expect(screen.getByText('…')).toBeInTheDocument();
  });

  it('renders all segments of a multi-level path', async () => {
    vi.mocked(locationService.getLocationAncestors).mockResolvedValue(mockThreeSegmentPath);

    render(<LocationBreadcrumb locationId="loc-3" />);

    await waitFor(() => {
      expect(screen.getByText('Building A')).toBeInTheDocument();
    });

    expect(screen.getByText('Floor 1')).toBeInTheDocument();
    expect(screen.getByText('Room 101')).toBeInTheDocument();
  });

  it('renders separators between path segments', async () => {
    vi.mocked(locationService.getLocationAncestors).mockResolvedValue(mockThreeSegmentPath);

    render(<LocationBreadcrumb locationId="loc-3" />);

    await waitFor(() => {
      expect(screen.getByText('Building A')).toBeInTheDocument();
    });

    const separators = screen.getAllByText('/');
    // 3 segments = 2 separators
    expect(separators).toHaveLength(2);
  });

  it('renders a single-node path without separators', async () => {
    vi.mocked(locationService.getLocationAncestors).mockResolvedValue(mockSingleSegmentPath);

    render(<LocationBreadcrumb locationId="loc-root" />);

    await waitFor(() => {
      expect(screen.getByText('Warehouse')).toBeInTheDocument();
    });

    expect(screen.queryByText('/')).not.toBeInTheDocument();
  });

  it('renders a deep path with all 5 segments', async () => {
    vi.mocked(locationService.getLocationAncestors).mockResolvedValue(mockDeepPath);

    render(<LocationBreadcrumb locationId="l5" />);

    await waitFor(() => {
      expect(screen.getByText('Campus')).toBeInTheDocument();
    });

    expect(screen.getByText('East Wing')).toBeInTheDocument();
    expect(screen.getByText('Lab 3')).toBeInTheDocument();
    expect(screen.getByText('Rack 7')).toBeInTheDocument();
    expect(screen.getByText('Shelf B')).toBeInTheDocument();

    // 5 segments = 4 separators
    const separators = screen.getAllByText('/');
    expect(separators).toHaveLength(4);
  });
});

describe('LocationBreadcrumb — navigation behavior', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(locationService.getLocationAncestors).mockResolvedValue(mockThreeSegmentPath);
  });

  it('renders ancestor segments as clickable buttons when onNavigate is provided', async () => {
    const onNavigate = vi.fn();
    render(<LocationBreadcrumb locationId="loc-3" onNavigate={onNavigate} />);

    await waitFor(() => {
      expect(screen.getByText('Building A')).toBeInTheDocument();
    });

    // Building A and Floor 1 should be clickable buttons
    expect(screen.getByText('Building A').closest('button')).not.toBeNull();
    expect(screen.getByText('Floor 1').closest('button')).not.toBeNull();
  });

  it('renders last segment as non-clickable text', async () => {
    const onNavigate = vi.fn();
    render(<LocationBreadcrumb locationId="loc-3" onNavigate={onNavigate} />);

    await waitFor(() => {
      expect(screen.getByText('Room 101')).toBeInTheDocument();
    });

    // Last segment (Room 101) should NOT be a button
    expect(screen.getByText('Room 101').closest('button')).toBeNull();
  });

  it('calls onNavigate with correct id when ancestor segment is clicked', async () => {
    const onNavigate = vi.fn();
    const user = userEvent.setup();
    render(<LocationBreadcrumb locationId="loc-3" onNavigate={onNavigate} />);

    await waitFor(() => {
      expect(screen.getByText('Building A')).toBeInTheDocument();
    });

    await user.click(screen.getByText('Building A'));
    expect(onNavigate).toHaveBeenCalledWith('loc-1');

    await user.click(screen.getByText('Floor 1'));
    expect(onNavigate).toHaveBeenCalledWith('loc-2');
  });

  it('does not make segments clickable when onNavigate is not provided', async () => {
    render(<LocationBreadcrumb locationId="loc-3" />);

    await waitFor(() => {
      expect(screen.getByText('Building A')).toBeInTheDocument();
    });

    // All segments should be plain text, not buttons
    expect(screen.getByText('Building A').closest('button')).toBeNull();
    expect(screen.getByText('Floor 1').closest('button')).toBeNull();
    expect(screen.getByText('Room 101').closest('button')).toBeNull();
  });

  it('single-node path renders as non-clickable when it is the last item', async () => {
    vi.mocked(locationService.getLocationAncestors).mockResolvedValue(mockSingleSegmentPath);
    const onNavigate = vi.fn();

    render(<LocationBreadcrumb locationId="loc-root" onNavigate={onNavigate} />);

    await waitFor(() => {
      expect(screen.getByText('Warehouse')).toBeInTheDocument();
    });

    // Single item IS the last item, so it should not be clickable
    expect(screen.getByText('Warehouse').closest('button')).toBeNull();
  });
});

describe('LocationBreadcrumb — empty and error states', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('renders nothing when ancestors list is empty', async () => {
    vi.mocked(locationService.getLocationAncestors).mockResolvedValue([]);

    const { container } = render(<LocationBreadcrumb locationId="loc-unknown" />);

    await waitFor(() => {
      expect(screen.queryByText('…')).not.toBeInTheDocument();
    });

    expect(container.querySelector('nav')).toBeNull();
  });

  it('renders nothing on API error', async () => {
    vi.mocked(locationService.getLocationAncestors).mockRejectedValue(
      new Error('Network error'),
    );

    const { container } = render(<LocationBreadcrumb locationId="loc-3" />);

    await waitFor(() => {
      expect(screen.queryByText('…')).not.toBeInTheDocument();
    });

    expect(container.querySelector('nav')).toBeNull();
  });

  it('has accessible breadcrumb navigation landmark', async () => {
    vi.mocked(locationService.getLocationAncestors).mockResolvedValue(mockThreeSegmentPath);

    render(<LocationBreadcrumb locationId="loc-3" />);

    await waitFor(() => {
      expect(screen.getByLabelText('Location breadcrumb')).toBeInTheDocument();
    });
  });

  it('applies custom className to the nav element', async () => {
    vi.mocked(locationService.getLocationAncestors).mockResolvedValue(mockThreeSegmentPath);

    render(<LocationBreadcrumb locationId="loc-3" className="mt-4 text-lg" />);

    await waitFor(() => {
      const nav = screen.getByLabelText('Location breadcrumb');
      expect(nav).toHaveClass('mt-4');
      expect(nav).toHaveClass('text-lg');
    });
  });

  it('re-fetches ancestors when locationId prop changes', async () => {
    vi.mocked(locationService.getLocationAncestors).mockResolvedValue(mockThreeSegmentPath);

    const { rerender } = render(<LocationBreadcrumb locationId="loc-3" />);

    await waitFor(() => {
      expect(locationService.getLocationAncestors).toHaveBeenCalledWith('loc-3');
    });

    const newAncestors: LocationBreadcrumbItem[] = [
      { id: 'loc-10', name: 'New Location', depth: 0 },
    ];
    vi.mocked(locationService.getLocationAncestors).mockResolvedValue(newAncestors);

    rerender(<LocationBreadcrumb locationId="loc-10" />);

    await waitFor(() => {
      expect(locationService.getLocationAncestors).toHaveBeenCalledWith('loc-10');
      expect(screen.getByText('New Location')).toBeInTheDocument();
    });
  });
});

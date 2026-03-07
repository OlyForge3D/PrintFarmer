import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { LocationBreadcrumb } from '@/common/components/LocationBreadcrumb';
import type { LocationBreadcrumbItem } from '@/services/locationService';

const mockAncestors: LocationBreadcrumbItem[] = [
  { id: 'loc-1', name: 'Building A', depth: 0 },
  { id: 'loc-2', name: 'Floor 1', depth: 1 },
  { id: 'loc-3', name: 'Room 101', depth: 2 },
];

vi.mock('@/services/locationService', () => ({
  locationService: {
    getLocationAncestors: vi.fn(),
    getLocationTree: vi.fn(),
    getAllLocations: vi.fn(),
  },
}));

 
const { locationService } = await import('@/services/locationService') as typeof import('@/services/locationService');

describe('LocationBreadcrumb', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(locationService.getLocationAncestors).mockResolvedValue(mockAncestors);
  });

  it('renders loading indicator while fetching ancestors', () => {
    vi.mocked(locationService.getLocationAncestors).mockImplementation(
      () => new Promise(() => {}),
    );

    render(<LocationBreadcrumb locationId="loc-3" />);

    expect(screen.getByText('…')).toBeInTheDocument();
  });

  it('renders full breadcrumb path after loading', async () => {
    render(<LocationBreadcrumb locationId="loc-3" />);

    await waitFor(() => {
      expect(screen.getByText('Building A')).toBeInTheDocument();
    });

    expect(screen.getByText('Floor 1')).toBeInTheDocument();
    expect(screen.getByText('Room 101')).toBeInTheDocument();
  });

  it('renders separators between breadcrumb items', async () => {
    render(<LocationBreadcrumb locationId="loc-3" />);

    await waitFor(() => {
      expect(screen.getByText('Building A')).toBeInTheDocument();
    });

    const separators = screen.getAllByText('/');
    expect(separators).toHaveLength(2);
  });

  it('renders last item as non-clickable text', async () => {
    const onNavigate = vi.fn();
    render(<LocationBreadcrumb locationId="loc-3" onNavigate={onNavigate} />);

    await waitFor(() => {
      expect(screen.getByText('Room 101')).toBeInTheDocument();
    });

    // Last item should be a span, not a button
    const lastItem = screen.getByText('Room 101');
    expect(lastItem.tagName).not.toBe('BUTTON');
  });

  it('renders ancestor items as clickable buttons when onNavigate is provided', async () => {
    const onNavigate = vi.fn();
    render(<LocationBreadcrumb locationId="loc-3" onNavigate={onNavigate} />);

    await waitFor(() => {
      expect(screen.getByText('Building A')).toBeInTheDocument();
    });

    // First and second items should be clickable
    const buildingA = screen.getByText('Building A');
    expect(buildingA.closest('button')).not.toBeNull();
  });

  it('calls onNavigate when an ancestor is clicked', async () => {
    const onNavigate = vi.fn();
    const user = userEvent.setup();
    render(<LocationBreadcrumb locationId="loc-3" onNavigate={onNavigate} />);

    await waitFor(() => {
      expect(screen.getByText('Building A')).toBeInTheDocument();
    });

    await user.click(screen.getByText('Building A'));

    expect(onNavigate).toHaveBeenCalledWith('loc-1');
  });

  it('renders all items as plain text when onNavigate is not provided', async () => {
    render(<LocationBreadcrumb locationId="loc-3" />);

    await waitFor(() => {
      expect(screen.getByText('Building A')).toBeInTheDocument();
    });

    const buildingA = screen.getByText('Building A');
    expect(buildingA.closest('button')).toBeNull();
  });

  it('renders nothing when ancestors list is empty', async () => {
    vi.mocked(locationService.getLocationAncestors).mockResolvedValue([]);

    const { container } = render(<LocationBreadcrumb locationId="loc-unknown" />);

    await waitFor(() => {
      expect(screen.queryByText('…')).not.toBeInTheDocument();
    });

    // Should render null (empty)
    expect(container.querySelector('nav')).toBeNull();
  });

  it('has accessible breadcrumb navigation landmark', async () => {
    render(<LocationBreadcrumb locationId="loc-3" />);

    await waitFor(() => {
      expect(screen.getByLabelText('Location breadcrumb')).toBeInTheDocument();
    });
  });

  it('handles API error gracefully by rendering nothing', async () => {
    vi.mocked(locationService.getLocationAncestors).mockRejectedValue(
      new Error('Network error'),
    );

    const { container } = render(<LocationBreadcrumb locationId="loc-3" />);

    await waitFor(() => {
      expect(screen.queryByText('…')).not.toBeInTheDocument();
    });

    expect(container.querySelector('nav')).toBeNull();
  });

  it('applies custom className', async () => {
    render(<LocationBreadcrumb locationId="loc-3" className="my-custom-class" />);

    await waitFor(() => {
      const nav = screen.getByLabelText('Location breadcrumb');
      expect(nav).toHaveClass('my-custom-class');
    });
  });

  it('re-fetches ancestors when locationId changes', async () => {
    const { rerender } = render(<LocationBreadcrumb locationId="loc-3" />);

    await waitFor(() => {
      expect(locationService.getLocationAncestors).toHaveBeenCalledWith('loc-3');
    });

    const newAncestors: LocationBreadcrumbItem[] = [
      { id: 'loc-10', name: 'Warehouse', depth: 0 },
    ];
    vi.mocked(locationService.getLocationAncestors).mockResolvedValue(newAncestors);

    rerender(<LocationBreadcrumb locationId="loc-10" />);

    await waitFor(() => {
      expect(locationService.getLocationAncestors).toHaveBeenCalledWith('loc-10');
      expect(screen.getByText('Warehouse')).toBeInTheDocument();
    });
  });
});

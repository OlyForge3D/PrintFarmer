import { render, screen, waitFor } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { LocationSelector } from '@/features/catalog/components/LocationSelector';

vi.mock('@/services/locationService', () => ({
  locationService: {
    getLocationTree: vi.fn().mockResolvedValue([
      {
        id: 'loc-1',
        name: 'Workshop',
        description: '',
        parentId: null,
        path: '/Workshop',
        depth: 0,
        sortOrder: 0,
        printerCount: 3,
        totalPrinterCount: 3,
        children: [],
      },
    ]),
  },
}));

describe('LocationSelector', () => {
  const defaultProps = {
    onChange: vi.fn(),
  };

  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('renders with default label "Location"', async () => {
    render(<LocationSelector {...defaultProps} />);

    expect(screen.getByText('Location')).toBeInTheDocument();
    expect(await screen.findByText('No location (unassigned)')).toBeInTheDocument();
  });

  it('passes custom label to LocationTreePicker', async () => {
    render(<LocationSelector {...defaultProps} label="Assign To" />);

    expect(screen.getByText('Assign To')).toBeInTheDocument();
    expect(await screen.findByText('No location (unassigned)')).toBeInTheDocument();
  });

  it('renders required indicator when required', async () => {
    render(<LocationSelector {...defaultProps} required />);

    expect(screen.getByText('*')).toBeInTheDocument();
    expect(await screen.findByText('Select a location')).toBeInTheDocument();
  });

  it('shows "Select a location" placeholder when required', async () => {
    render(<LocationSelector {...defaultProps} required />);

    await waitFor(() => {
      expect(screen.getByText('Select a location')).toBeInTheDocument();
    });
  });

  it('shows "No location (unassigned)" placeholder when not required', async () => {
    render(<LocationSelector {...defaultProps} required={false} />);

    await waitFor(() => {
      expect(screen.getByText('No location (unassigned)')).toBeInTheDocument();
    });
  });

  it('renders disabled state', async () => {
    render(<LocationSelector {...defaultProps} disabled />);

    await waitFor(() => {
      const btn = screen.getByRole('button');
      expect(btn).toBeDisabled();
    });
  });

  it('passes value to LocationTreePicker', async () => {
    render(<LocationSelector {...defaultProps} value="loc-1" />);

    await waitFor(() => {
      expect(screen.getByText('Workshop')).toBeInTheDocument();
    });
  });

  it('converts undefined value to null for LocationTreePicker', async () => {
    render(<LocationSelector {...defaultProps} value={undefined} />);

    await waitFor(() => {
      // Should show placeholder, not "Unknown"
      expect(screen.queryByText('Unknown')).not.toBeInTheDocument();
    });
  });
});

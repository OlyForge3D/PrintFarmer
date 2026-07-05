import '@testing-library/jest-dom';
import React from 'react';
import { render, screen, fireEvent } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { MemoryRouter } from 'react-router';
import { locationService } from '@/services/locationService';

let isFarmAdmin = false;

const mockLocationTree = [
  {
    id: 'loc-1',
    name: 'Warehouse A',
    description: 'Main warehouse',
    parentId: null,
    path: '/Warehouse A',
    depth: 0,
    sortOrder: 0,
    printerCount: 2,
    totalPrinterCount: 5,
    children: [
      {
        id: 'loc-2',
        name: 'Room 1',
        description: 'Packaging room',
        parentId: 'loc-1',
        path: '/Warehouse A/Room 1',
        depth: 1,
        sortOrder: 0,
        printerCount: 3,
        totalPrinterCount: 3,
        children: [],
      },
    ],
  },
];

const mockPrinters = [
  {
    printerId: 'p1',
    printerName: 'Printer One',
    locationId: 'loc-1',
    locationName: 'Warehouse A',
    isOnline: true,
    status: 'Printing',
    currentJobName: 'gearbox.gcode',
  },
  {
    printerId: 'p2',
    printerName: 'Printer Two',
    locationId: 'loc-2',
    locationName: 'Room 1',
    isOnline: true,
    status: 'Printing',
    currentJobName: null,
  },
  {
    printerId: 'p3',
    printerName: 'Printer Three',
    locationId: 'loc-2',
    locationName: 'Room 1',
    isOnline: true,
    status: 'Paused',
    currentJobName: 'paused.gcode',
  },
  {
    printerId: 'p4',
    printerName: 'Printer Four',
    locationId: 'loc-2',
    locationName: 'Room 1',
    isOnline: false,
    status: 'Offline',
    currentJobName: null,
  },
];

const mockStats = {
  totalPrinters: 4,
  online: 3,
  offline: 1,
  attention: 2,
  printing: 2,
  idle: 0,
  activeJobs: 3,
};

vi.mock('@/features/auth/hooks/useAuth', () => ({
  useAuth: () => ({
    hasRole: (role: string) => role === 'farm_admin' && isFarmAdmin,
  }),
}));

vi.mock('@/services/locationService', () => ({
  locationService: {
    createLocation: vi.fn(),
    updateLocation: vi.fn(),
    deleteLocation: vi.fn(),
    moveLocation: vi.fn(),
  },
}));

vi.mock('@/features/locations/hooks/useLocationDashboard', () => ({
  useLocationTree: () => ({
    data: mockLocationTree,
    isLoading: false,
    error: null,
  }),
  useLocationPrinters: () => ({
    data: mockPrinters,
    isLoading: false,
    error: null,
  }),
  useLocationStats: () => ({
    stats: mockStats,
    isLoading: false,
    error: null,
  }),
  useSignalRPrinterUpdates: vi.fn(),
  isActiveJob: (printer: { status: string; currentJobName?: string | null }) =>
    printer.status === 'Printing' || Boolean(printer.currentJobName?.trim()),
  findNode: vi.fn((nodes: typeof mockLocationTree, id: string) => {
    if (id === 'loc-1') return nodes[0];
    if (id === 'loc-2') return nodes[0].children[0];
    return undefined;
  }),
}));

vi.mock('@/common/components/PageTemplate', () => ({
  PageTemplate: ({ children, title, subtitle, actions }: {
    children: React.ReactNode;
    title: string;
    subtitle?: string;
    actions?: React.ReactNode;
  }) => (
    <div data-testid="page-template" data-title={title}>
      <h1>{title}</h1>
      {subtitle && <p>{subtitle}</p>}
      {actions}
      {children}
    </div>
  ),
}));

vi.mock('@/features/locations/components/LocationManagement', () => ({
  LocationManagement: ({ showAssignments, autoOpenCreateToken, initialParentId }: {
    showAssignments?: boolean;
    autoOpenCreateToken?: number;
    initialParentId?: string | null;
  }) => (
    <div data-testid="location-management" data-show-assignments={String(showAssignments)} data-create-token={autoOpenCreateToken} data-parent-id={initialParentId ?? ''}>
      <button onClick={() => locationService.createLocation({ name: 'New location', parentId: initialParentId })}>Create location</button>
      <button onClick={() => locationService.updateLocation('loc-1', { name: 'Warehouse Prime' })}>Edit location</button>
      <button onClick={() => locationService.deleteLocation('loc-2')}>Delete location</button>
      <button onClick={() => locationService.moveLocation('loc-2', { newParentId: 'loc-1' })}>Move location</button>
    </div>
  ),
}));

vi.mock('@/features/printers/components/PrinterLocationDragDrop', () => ({
  PrinterLocationDragDrop: ({ locations }: { locations: unknown[] }) => (
    <div data-testid="printer-location-drag-drop">Assignments for {locations.length} locations</div>
  ),
}));

import { LocationDashboardPage } from '../pages/LocationDashboardPage';

const renderPage = () => render(
  <MemoryRouter>
    <LocationDashboardPage />
  </MemoryRouter>,
);

describe('LocationDashboardPage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    isFarmAdmin = false;
  });

  it('renders the unified page structure for authenticated non-admin viewers', () => {
    renderPage();

    expect(screen.getByTestId('page-template')).toHaveAttribute('data-title', 'Locations');
    expect(screen.getByText('Hierarchy navigator')).toBeInTheDocument();
    expect(screen.getByText('Fleet health')).toBeInTheDocument();
    expect(screen.getByText('Activity')).toBeInTheDocument();
    expect(screen.getByText('Placement')).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Child locations' })).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Active jobs' })).toBeInTheDocument();
    expect(screen.getAllByText('Printer One').length).toBeGreaterThan(0);
  });

  it('normalizes subtree printer DTOs for the printer list', () => {
    renderPage();

    expect(screen.getAllByText('Printer One').length).toBeGreaterThan(0);
    expect(screen.getByText(/Printing — gearbox\.gcode/)).toBeInTheDocument();
    expect(screen.queryByText(/0%/)).not.toBeInTheDocument();
    expect(screen.queryByText(/42%/)).not.toBeInTheDocument();
    expect(screen.queryByText('undefined')).not.toBeInTheDocument();
  });

  it('renders the same number of active-job rows as the activeJobs summary count', () => {
    const { container } = renderPage();
    const activeJobDetails = Array.from(container.querySelectorAll('p')).filter((element) =>
      element.textContent?.includes(' · '),
    );

    expect(activeJobDetails).toHaveLength(mockStats.activeJobs);
  });

  it('lets location selection drive the detail panel', () => {
    renderPage();

    fireEvent.click(screen.getAllByText('Warehouse A')[0]);

    expect(screen.getByRole('heading', { name: 'Warehouse A' })).toBeInTheDocument();
    expect(screen.getByText('Main warehouse')).toBeInTheDocument();
  });

  it('hides mutating controls from non-admin users', () => {
    renderPage();

    expect(screen.queryByRole('button', { name: /Manage/i })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Add location/i })).not.toBeInTheDocument();
    expect(screen.queryByTestId('location-management')).not.toBeInTheDocument();
  });

  it('shows admin management controls for farm_admin users', () => {
    isFarmAdmin = true;
    renderPage();

    expect(screen.getByRole('button', { name: /Manage/i })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /Add location/i })).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: /Manage/i }));

    expect(screen.getByTestId('location-management')).toHaveAttribute('data-show-assignments', 'false');
  });

  it('preserves create, edit, delete, and move management actions behind admin mode', () => {
    isFarmAdmin = true;
    renderPage();

    fireEvent.click(screen.getByRole('button', { name: /Manage/i }));
    fireEvent.click(screen.getByRole('button', { name: 'Create location' }));
    fireEvent.click(screen.getByRole('button', { name: 'Edit location' }));
    fireEvent.click(screen.getByRole('button', { name: 'Delete location' }));
    fireEvent.click(screen.getByRole('button', { name: 'Move location' }));

    expect(locationService.createLocation).toHaveBeenCalledWith({ name: 'New location', parentId: null });
    expect(locationService.updateLocation).toHaveBeenCalledWith('loc-1', { name: 'Warehouse Prime' });
    expect(locationService.deleteLocation).toHaveBeenCalledWith('loc-2');
    expect(locationService.moveLocation).toHaveBeenCalledWith('loc-2', { newParentId: 'loc-1' });
  });

  it('opens management in create mode from the primary Add location action', () => {
    isFarmAdmin = true;
    renderPage();

    fireEvent.click(screen.getByRole('button', { name: /Add location/i }));

    expect(screen.getByTestId('location-management')).toHaveAttribute('data-create-token', '1');
  });

  it('keeps printer assignment in an admin-only assignments tab', () => {
    isFarmAdmin = true;
    renderPage();

    fireEvent.click(screen.getByRole('button', { name: /Manage/i }));
    expect(screen.queryByTestId('printer-location-drag-drop')).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole('tab', { name: /Assignments/i }));

    expect(screen.getByTestId('printer-location-drag-drop')).toHaveTextContent('Assignments for 2 locations');
  });
});

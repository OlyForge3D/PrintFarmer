import '@testing-library/jest-dom';
import React from 'react';
import { render, screen, fireEvent } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { MemoryRouter } from 'react-router';

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
        description: '',
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
    id: 'p1',
    name: 'Printer One',
    backend: 'Moonraker',
    isOnline: true,
    state: 'Idle',
    backendUrl: 'http://test:7125',
    isReachable: true,
    location: { id: 'loc-2', name: 'Room 1' },
  },
];

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
    stats: {
      totalPrinters: 1,
      online: 1,
      offline: 0,
      printing: 0,
      idle: 1,
      activeJobs: 0,
    },
    isLoading: false,
    error: null,
  }),
  useSignalRPrinterUpdates: vi.fn(),
  findNode: vi.fn((nodes: typeof mockLocationTree, id: string) => {
    if (id === 'loc-1') return mockLocationTree[0];
    if (id === 'loc-2') return mockLocationTree[0].children[0];
    return undefined;
  }),
}));

vi.mock('@/common/components/PageTemplate', () => ({
  PageTemplate: ({ children, title }: { children: React.ReactNode; title: string }) => (
    <div data-testid="page-template" data-title={title}>{children}</div>
  ),
}));

vi.mock('@/common/components/ui', () => ({
  Spinner: ({ size }: { size?: string }) => <div data-testid="spinner" data-size={size}>Loading...</div>,
  Button: ({ children, className, onClick, ...rest }: React.ButtonHTMLAttributes<HTMLButtonElement> & { variant?: string }) => (
    <button className={className} onClick={onClick} {...rest}>{children}</button>
  ),
  Card: Object.assign(
    ({ children, className }: { children: React.ReactNode; className?: string }) => (
      <div data-testid="card" className={className}>{children}</div>
    ),
    {
      Header: ({ children }: { children: React.ReactNode }) => <div data-testid="card-header">{children}</div>,
      Body: ({ children, className }: { children: React.ReactNode; className?: string }) => (
        <div data-testid="card-body" className={className}>{children}</div>
      ),
      Footer: ({ children }: { children: React.ReactNode }) => <div>{children}</div>,
    },
  ),
}));

vi.mock('@/common/components/icons/MdiIcons', () => ({
  LocationIcon: ({ className }: { className?: string }) => <span data-testid="location-icon" className={className} />,
}));

vi.mock('@/features/locations/components/LocationStats', () => ({
  LocationStats: ({ stats, locationName }: { stats: Record<string, number>; locationName: string }) => (
    <div data-testid="location-stats" data-location={locationName}>
      Stats: {stats.totalPrinters} printers
    </div>
  ),
}));

vi.mock('@/features/locations/components/LocationPrinterList', () => ({
  LocationPrinterList: ({ printers }: { printers: unknown[] }) => (
    <div data-testid="location-printer-list">
      {printers.length} printers listed
    </div>
  ),
}));

import { LocationDashboardPage } from '../pages/LocationDashboardPage';

const renderPage = () => {
  return render(
    <MemoryRouter>
      <LocationDashboardPage />
    </MemoryRouter>,
  );
};

describe('LocationDashboardPage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('renders with page template', () => {
    renderPage();
    const template = screen.getByTestId('page-template');
    expect(template).toBeInTheDocument();
    expect(template).toHaveAttribute('data-title', 'Location Dashboard');
  });

  it('renders location tree with All Locations option', () => {
    renderPage();
    expect(screen.getByText('All Locations')).toBeInTheDocument();
    expect(screen.getByText('Warehouse A')).toBeInTheDocument();
  });

  it('renders LocationStats component', () => {
    renderPage();
    expect(screen.getByTestId('location-stats')).toBeInTheDocument();
  });

  it('renders LocationPrinterList component', () => {
    renderPage();
    expect(screen.getByTestId('location-printer-list')).toBeInTheDocument();
  });

  it('selects a location when clicked', () => {
    renderPage();
    fireEvent.click(screen.getByText('Warehouse A'));
    expect(screen.getByText('Warehouse A')).toBeInTheDocument();
  });

  it('shows child location Room 1', () => {
    renderPage();
    expect(screen.getByText('Room 1')).toBeInTheDocument();
  });

  it('All Locations is selected by default', () => {
    renderPage();
    const allBtn = screen.getByText('All Locations');
    expect(allBtn).toHaveAttribute('aria-current', 'true');
  });

  it('renders Locations sidebar heading', () => {
    renderPage();
    expect(screen.getByText('Locations')).toBeInTheDocument();
  });

  it('renders Printers heading', () => {
    renderPage();
    expect(screen.getByText('Printers')).toBeInTheDocument();
  });
});

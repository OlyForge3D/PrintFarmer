import '@testing-library/jest-dom';
import React from 'react';
import { render, screen, fireEvent } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import type { Printer } from '@/types/api';

vi.mock('@/common/components/ui', () => ({
  Card: Object.assign(
    ({ children }: { children: React.ReactNode }) => <div data-testid="card">{children}</div>,
    {
      Header: ({ children }: { children: React.ReactNode }) => <div>{children}</div>,
      Body: ({ children, className, onClick, role, tabIndex, onKeyDown }: {
        children: React.ReactNode;
        className?: string;
        onClick?: () => void;
        role?: string;
        tabIndex?: number;
        onKeyDown?: (e: React.KeyboardEvent) => void;
      }) => (
        <div data-testid="card-body" className={className} onClick={onClick} role={role} tabIndex={tabIndex} onKeyDown={onKeyDown}>
          {children}
        </div>
      ),
      Footer: ({ children }: { children: React.ReactNode }) => <div>{children}</div>,
    },
  ),
  Input: ({ value, onChange, placeholder, className, ...rest }: React.InputHTMLAttributes<HTMLInputElement>) => (
    <input value={value} onChange={onChange} placeholder={placeholder} className={className} {...rest} />
  ),
  Select: ({ value, onChange, children, ...rest }: React.SelectHTMLAttributes<HTMLSelectElement> & { containerClassName?: string }) => (
    <select value={value} onChange={onChange} {...rest}>{children}</select>
  ),
  Badge: ({ children, variant, size }: { children: React.ReactNode; variant?: string; size?: string }) => (
    <span data-testid="badge" data-variant={variant} data-size={size}>{children}</span>
  ),
}));

vi.mock('@/common/components/icons/MdiIcons', () => ({
  SearchIcon: ({ className }: { className?: string }) => <span data-testid="search-icon" className={className} />,
}));

import { LocationPrinterList } from '../components/LocationPrinterList';

const makePrinter = (overrides: Partial<Printer> = {}): Printer =>
  ({
    id: 'p1',
    name: 'Test Printer',
    backend: 'Moonraker',
    isOnline: true,
    state: 'Idle',
    backendUrl: 'http://test:7125',
    isReachable: true,
    hotendTemp: 200,
    bedTemp: 60,
    progress: 0,
    ...overrides,
  }) as Printer;

describe('LocationPrinterList', () => {
  const printers: Printer[] = [
    makePrinter({ id: 'p1', name: 'Prusa MK4', isOnline: true, state: 'Printing', progress: 75 }),
    makePrinter({ id: 'p2', name: 'Voron 2.4', isOnline: true, state: 'Idle' }),
    makePrinter({ id: 'p3', name: 'Ender 3', isOnline: false, state: 'Disconnected' }),
  ];

  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('renders all printers', () => {
    render(<LocationPrinterList printers={printers} />);
    expect(screen.getByText('Prusa MK4')).toBeInTheDocument();
    expect(screen.getByText('Voron 2.4')).toBeInTheDocument();
    expect(screen.getByText('Ender 3')).toBeInTheDocument();
  });

  it('shows print progress for printing printers', () => {
    render(<LocationPrinterList printers={printers} />);
    expect(screen.getByText(/75%/)).toBeInTheDocument();
  });

  it('filters by search text', () => {
    render(<LocationPrinterList printers={printers} />);
    const searchInput = screen.getByPlaceholderText('Search printers...');
    fireEvent.change(searchInput, { target: { value: 'voron' } });
    expect(screen.getByText('Voron 2.4')).toBeInTheDocument();
    expect(screen.queryByText('Prusa MK4')).not.toBeInTheDocument();
    expect(screen.queryByText('Ender 3')).not.toBeInTheDocument();
  });

  it('filters by status', () => {
    render(<LocationPrinterList printers={printers} />);
    const statusFilter = screen.getByLabelText('Filter by status');
    fireEvent.change(statusFilter, { target: { value: 'offline' } });
    expect(screen.getByText('Ender 3')).toBeInTheDocument();
    expect(screen.queryByText('Prusa MK4')).not.toBeInTheDocument();
  });

  it('shows empty message when no printers', () => {
    render(<LocationPrinterList printers={[]} />);
    expect(screen.getByText('No printers at this location.')).toBeInTheDocument();
  });

  it('shows filter-no-match message when filters exclude all', () => {
    render(<LocationPrinterList printers={printers} />);
    const searchInput = screen.getByPlaceholderText('Search printers...');
    fireEvent.change(searchInput, { target: { value: 'nonexistent' } });
    expect(screen.getByText('No printers match the current filters.')).toBeInTheDocument();
  });

  it('calls onPrinterClick when a printer card is clicked', () => {
    const onClick = vi.fn();
    render(<LocationPrinterList printers={printers} onPrinterClick={onClick} />);
    const cards = screen.getAllByTestId('card-body');
    fireEvent.click(cards[0]);
    expect(onClick).toHaveBeenCalledWith('p1');
  });

  it('renders loading skeleton when isLoading', () => {
    render(<LocationPrinterList printers={[]} isLoading />);
    const cards = screen.getAllByTestId('card');
    expect(cards.length).toBe(3);
  });

  it('shows temperature info for online printers', () => {
    render(<LocationPrinterList printers={printers} />);
    const hotendTemps = screen.getAllByText(/200°C/);
    expect(hotendTemps.length).toBeGreaterThanOrEqual(1);
    const bedTemps = screen.getAllByText(/60°C/);
    expect(bedTemps.length).toBeGreaterThanOrEqual(1);
  });

  it('shows status badges', () => {
    render(<LocationPrinterList printers={printers} />);
    const badges = screen.getAllByTestId('badge');
    expect(badges.length).toBe(3);
    expect(badges[0]).toHaveTextContent('printing');
    expect(badges[1]).toHaveTextContent('idle');
    expect(badges[2]).toHaveTextContent('offline');
  });
});

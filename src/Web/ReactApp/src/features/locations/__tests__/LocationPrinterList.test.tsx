import '@testing-library/jest-dom';
import React from 'react';
import { render, screen, fireEvent } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';

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
  Select: ({ value, onChange, children, containerClassName, ...rest }: React.SelectHTMLAttributes<HTMLSelectElement> & { containerClassName?: string }) => (
    <select value={value} onChange={onChange} data-container-class-name={containerClassName} {...rest}>{children}</select>
  ),
  Toolbar: ({ children, ...rest }: React.HTMLAttributes<HTMLDivElement>) => <div role="group" {...rest}>{children}</div>,
  ToolbarField: ({ label, htmlFor, children }: { label: string; htmlFor: string; children: React.ReactNode }) => (
    <div><label htmlFor={htmlFor}>{label}</label>{children}</div>
  ),
  FiltersDisclosure: ({ activeCount, onReset, children }: {
    activeCount: number;
    onReset: () => void;
    children: React.ReactNode;
  }) => {
    const [expanded, setExpanded] = React.useState(false);
    return (
      <>
        <button type="button" aria-expanded={expanded} onClick={() => setExpanded(!expanded)}>
          Filters{activeCount > 0 ? ` (${activeCount})` : ''}
        </button>
        <div role="region" aria-label="Filters" hidden={!expanded}>
          {children}
          <button type="button" disabled={activeCount === 0} onClick={onReset}>Reset filters</button>
        </div>
      </>
    );
  },
  SearchField: ({ value, onChange, onClear, label, id, placeholder }: {
    value: string;
    onChange: React.ChangeEventHandler<HTMLInputElement>;
    onClear: () => void;
    label?: string;
    id?: string;
    placeholder?: string;
  }) => (
    <span>
      <input id={id} type="search" aria-label={label} value={value} onChange={onChange} placeholder={placeholder} />
      {value && <button type="button" aria-label="Clear search" onClick={onClear}>Clear</button>}
    </span>
  ),
  Badge: ({ children, variant, size, className }: { children: React.ReactNode; variant?: string; size?: string; className?: string }) => (
    <span data-testid="badge" data-variant={variant} data-size={size} className={className}>{children}</span>
  ),
  Button: ({ children, onClick, className, ...rest }: React.ButtonHTMLAttributes<HTMLButtonElement> & { variant?: string }) => (
    <button className={className} onClick={onClick} {...rest}>{children}</button>
  ),
}));

vi.mock('@/common/components/icons/MdiIcons', () => ({
  SearchIcon: ({ className }: { className?: string }) => <span data-testid="search-icon" className={className} />,
}));

import { LocationPrinterList, type LocationPrinterListPrinter } from '../components/LocationPrinterList';

const makePrinter = (overrides: Partial<LocationPrinterListPrinter> = {}): LocationPrinterListPrinter => ({
  id: 'p1',
  name: 'Test Printer',
  isOnline: true,
  status: 'Idle',
  currentJobName: null,
  ...overrides,
});

describe('LocationPrinterList', () => {
  const printers: LocationPrinterListPrinter[] = [
    makePrinter({ id: 'p1', name: 'Prusa MK4', isOnline: true, status: 'Printing', currentJobName: 'gearbox.gcode' }),
    makePrinter({ id: 'p2', name: 'Voron 2.4', isOnline: true, status: 'Idle' }),
    makePrinter({ id: 'p3', name: 'Ender 3', isOnline: false, status: 'Disconnected' }),
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

  it('shows current job names without dead progress or temperature placeholders', () => {
    render(<LocationPrinterList printers={printers} />);
    expect(screen.getByText(/Printing — gearbox\.gcode/)).toBeInTheDocument();
    expect(screen.queryByText(/75%/)).not.toBeInTheDocument();
    expect(screen.queryByText(/🔥/)).not.toBeInTheDocument();
    expect(screen.queryByText(/🛏️/)).not.toBeInTheDocument();
  });

  it('filters by search text', () => {
    render(<LocationPrinterList printers={printers} />);
    const searchInput = screen.getByPlaceholderText('Search printers…');
    fireEvent.change(searchInput, { target: { value: 'voron' } });
    expect(screen.getByText('Voron 2.4')).toBeInTheDocument();
    expect(screen.queryByText('Prusa MK4')).not.toBeInTheDocument();
    expect(screen.queryByText('Ender 3')).not.toBeInTheDocument();
  });

  it('filters by status', () => {
    render(<LocationPrinterList printers={printers} />);
    fireEvent.click(screen.getByRole('button', { name: 'Filters' }));
    const statusFilter = screen.getByLabelText('Filter by status');
    fireEvent.change(statusFilter, { target: { value: 'offline' } });
    expect(screen.getByText('Ender 3')).toBeInTheDocument();
    expect(screen.queryByText('Prusa MK4')).not.toBeInTheDocument();
  });

  it('keeps status filters collapsed, counts active values, and resets without clearing search', () => {
    render(<LocationPrinterList printers={printers} />);
    const searchInput = screen.getByPlaceholderText('Search printers…');
    fireEvent.change(searchInput, { target: { value: 'prusa' } });

    const filters = screen.getByRole('button', { name: 'Filters' });
    expect(filters).toHaveAttribute('aria-expanded', 'false');
    expect(screen.getByLabelText('Filter by status').closest('[role="region"]')).not.toBeVisible();
    expect(searchInput).toBeVisible();

    fireEvent.click(filters);
    fireEvent.change(screen.getByLabelText('Filter by status'), { target: { value: 'online' } });
    expect(screen.getByRole('button', { name: 'Filters (1)' })).toHaveAttribute('aria-expanded', 'true');

    fireEvent.click(screen.getByRole('button', { name: 'Reset filters' }));
    expect(screen.getByRole('button', { name: 'Filters' })).toHaveAttribute('aria-expanded', 'true');
    expect(screen.getByLabelText('Filter by status')).toHaveValue('all');
    expect(searchInput).toHaveValue('prusa');
  });

  it('shows empty message when no printers', () => {
    render(<LocationPrinterList printers={[]} />);
    expect(screen.getByText('No printers at this location.')).toBeInTheDocument();
  });

  it('shows filter-no-match message when filters exclude all', () => {
    render(<LocationPrinterList printers={printers} />);
    const searchInput = screen.getByPlaceholderText('Search printers…');
    fireEvent.change(searchInput, { target: { value: 'nonexistent' } });
    expect(screen.getByText('No printers match the current filters.')).toBeInTheDocument();
  });

  it('calls onPrinterClick when a printer card is clicked', () => {
    const onClick = vi.fn();
    render(<LocationPrinterList printers={printers} onPrinterClick={onClick} />);
    fireEvent.click(screen.getByRole('button', { name: /Prusa MK4/i }));
    expect(onClick).toHaveBeenCalledWith('p1');
  });

  it('renders loading skeleton when isLoading', () => {
    render(<LocationPrinterList printers={[]} isLoading />);
    const cards = screen.getAllByTestId('card');
    expect(cards.length).toBe(3);
  });

  it('shows current state for online printers', () => {
    render(<LocationPrinterList printers={printers} />);
    expect(screen.getAllByText(/Printing/).length).toBeGreaterThanOrEqual(1);
    expect(screen.getAllByText(/Idle/).length).toBeGreaterThanOrEqual(1);
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

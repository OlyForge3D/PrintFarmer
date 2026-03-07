import '@testing-library/jest-dom';
import React from 'react';
import { render, screen } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';

vi.mock('@/common/components/ui', () => ({
  Card: Object.assign(
    ({ children, className }: { children: React.ReactNode; className?: string }) => (
      <div data-testid="card" className={className}>{children}</div>
    ),
    {
      Header: ({ children }: { children: React.ReactNode }) => <div data-testid="card-header">{children}</div>,
      Body: ({ children, className }: { children: React.ReactNode; className?: string }) => (
        <div data-testid="card-body" className={className}>{children}</div>
      ),
      Footer: ({ children }: { children: React.ReactNode }) => <div data-testid="card-footer">{children}</div>,
    },
  ),
}));

import { LocationStats } from '../components/LocationStats';

describe('LocationStats', () => {
  const defaultStats = {
    totalPrinters: 10,
    online: 7,
    offline: 3,
    printing: 4,
    idle: 3,
    activeJobs: 4,
  };

  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('renders all stat cards with correct values', () => {
    render(<LocationStats stats={defaultStats} locationName="Warehouse A" />);

    expect(screen.getByText('10')).toBeInTheDocument();
    expect(screen.getByText('7')).toBeInTheDocument();
    const threes = screen.getAllByText('3');
    expect(threes.length).toBe(2); // offline + idle
    const fours = screen.getAllByText('4');
    expect(fours.length).toBe(2); // printing + active jobs
    expect(screen.getByText('Total Printers')).toBeInTheDocument();
    expect(screen.getByText('Online')).toBeInTheDocument();
    expect(screen.getByText('Offline')).toBeInTheDocument();
    expect(screen.getByText('Printing')).toBeInTheDocument();
    expect(screen.getByText('Idle')).toBeInTheDocument();
    expect(screen.getByText('Active Jobs')).toBeInTheDocument();
  });

  it('displays the location name in heading', () => {
    render(<LocationStats stats={defaultStats} locationName="Room B" />);
    expect(screen.getByText('Room B — Overview')).toBeInTheDocument();
  });

  it('renders loading skeleton when isLoading', () => {
    render(<LocationStats stats={defaultStats} locationName="Test" isLoading />);
    const cards = screen.getAllByTestId('card');
    expect(cards.length).toBe(6);
    expect(screen.queryByText('Total Printers')).not.toBeInTheDocument();
  });

  it('renders zero values correctly', () => {
    const emptyStats = {
      totalPrinters: 0,
      online: 0,
      offline: 0,
      printing: 0,
      idle: 0,
      activeJobs: 0,
    };
    render(<LocationStats stats={emptyStats} locationName="Empty Room" />);
    const zeros = screen.getAllByText('0');
    expect(zeros.length).toBe(6);
  });
});

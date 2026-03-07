import '@testing-library/jest-dom';
import React from 'react';
import { render, screen, fireEvent } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import type { PrinterGroup } from '@/types/api';

vi.mock('@/common/components/ui', () => ({
  Card: Object.assign(
    ({ children, className, onClick }: { children: React.ReactNode; className?: string; onClick?: () => void }) => (
      <div data-testid="card" className={className} onClick={onClick}>{children}</div>
    ),
    {
      Body: ({ children }: { children: React.ReactNode }) => <div data-testid="card-body">{children}</div>,
      Header: ({ children }: { children: React.ReactNode }) => <div>{children}</div>,
      Footer: ({ children }: { children: React.ReactNode }) => <div>{children}</div>,
    },
  ),
  Button: ({ children, onClick, ...rest }: React.ButtonHTMLAttributes<HTMLButtonElement> & { variant?: string; size?: string; iconLeft?: React.ReactNode }) => (
    <button onClick={onClick} {...rest}>{rest.iconLeft}{children}</button>
  ),
  Badge: ({ children }: { children: React.ReactNode; variant?: string; size?: string }) => (
    <span data-testid="badge">{children}</span>
  ),
}));

vi.mock('@/common/components/icons/MdiIcons', () => ({
  EditIcon: () => <span data-testid="edit-icon" />,
  DeleteIcon: () => <span data-testid="delete-icon" />,
  PrinterIcon: ({ className }: { className?: string }) => <span data-testid="printer-icon" className={className} />,
}));

vi.mock('date-fns', () => ({
  formatDistanceToNow: () => '2 hours ago',
}));

import { PrinterGroupCard } from '../components/PrinterGroupCard';

const mockGroup: PrinterGroup = {
  id: 'g1',
  name: 'Test Group',
  description: 'A test group description',
  createdDate: '2025-01-01T00:00:00Z',
  updatedDate: '2025-01-02T00:00:00Z',
  printerCount: 3,
};

describe('PrinterGroupCard', () => {
  const onEdit = vi.fn();
  const onDelete = vi.fn();
  const onSelect = vi.fn();

  beforeEach(() => {
    vi.clearAllMocks();
  });

  const renderCard = (group = mockGroup) =>
    render(<PrinterGroupCard group={group} onEdit={onEdit} onDelete={onDelete} onSelect={onSelect} />);

  it('renders group name', () => {
    renderCard();
    expect(screen.getByText('Test Group')).toBeInTheDocument();
  });

  it('renders group description when provided', () => {
    renderCard();
    expect(screen.getByText('A test group description')).toBeInTheDocument();
  });

  it('does not render description when not provided', () => {
    renderCard({ ...mockGroup, description: undefined });
    expect(screen.queryByText('A test group description')).not.toBeInTheDocument();
  });

  it('renders printer count with correct plural form', () => {
    renderCard();
    expect(screen.getByText('3 printers')).toBeInTheDocument();
  });

  it('renders singular printer text for count of 1', () => {
    renderCard({ ...mockGroup, printerCount: 1 });
    expect(screen.getByText('1 printer')).toBeInTheDocument();
  });

  it('renders updated time badge', () => {
    renderCard();
    expect(screen.getByText(/Updated 2 hours ago/)).toBeInTheDocument();
  });

  it('calls onSelect when card is clicked', () => {
    renderCard();
    fireEvent.click(screen.getByTestId('card'));
    expect(onSelect).toHaveBeenCalledWith(mockGroup);
  });

  it('calls onEdit when edit button is clicked without selecting', () => {
    renderCard();
    fireEvent.click(screen.getByLabelText('Edit group'));
    expect(onEdit).toHaveBeenCalledWith(mockGroup);
    expect(onSelect).not.toHaveBeenCalled();
  });

  it('calls onDelete when delete button is clicked without selecting', () => {
    renderCard();
    fireEvent.click(screen.getByLabelText('Delete group'));
    expect(onDelete).toHaveBeenCalledWith(mockGroup);
    expect(onSelect).not.toHaveBeenCalled();
  });

  it('renders zero printers correctly', () => {
    renderCard({ ...mockGroup, printerCount: 0 });
    expect(screen.getByText('0 printers')).toBeInTheDocument();
  });
});

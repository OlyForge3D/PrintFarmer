import { render, screen, fireEvent } from '@testing-library/react';
import { describe, it, expect, vi } from 'vitest';
import { DeleteConfirmationModal } from '../DeleteConfirmationModal';
import { Printer } from '@/types/api';

const mockPrinter: Printer = {
  id: '1',
  name: 'Test Printer',
  manufacturerName: 'Prusa',
  modelName: 'i3 MK3S+',
  ipAddress: '192.168.1.100',
  backend: 'Moonraker' as never,
  isOnline: true,
};

const mockMultiplePrinters: Printer[] = [
  { ...mockPrinter, id: '1', name: 'Printer 1' },
  { ...mockPrinter, id: '2', name: 'Printer 2' },
  { ...mockPrinter, id: '3', name: 'Printer 3' },
];

describe('DeleteConfirmationModal', () => {
  const mockOnConfirm = vi.fn();
  const mockOnCancel = vi.fn();

  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('should render when isOpen is true', () => {
    render(
      <DeleteConfirmationModal
        isOpen={true}
        printers={[mockPrinter]}
        onConfirm={mockOnConfirm}
        onCancel={mockOnCancel}
      />
    );

    expect(screen.getByRole('heading', { name: /Delete Printer/i })).toBeInTheDocument();
  });

  it('should not render when isOpen is false', () => {
    render(
      <DeleteConfirmationModal
        isOpen={false}
        printers={[mockPrinter]}
        onConfirm={mockOnConfirm}
        onCancel={mockOnCancel}
      />
    );

    expect(screen.queryByText('Delete Printer')).not.toBeInTheDocument();
  });

  it('should show single printer confirmation message', () => {
    render(
      <DeleteConfirmationModal
        isOpen={true}
        printers={[mockPrinter]}
        onConfirm={mockOnConfirm}
        onCancel={mockOnCancel}
      />
    );

    expect(screen.getByText(/Are you sure you want to delete "Test Printer"/)).toBeInTheDocument();
    expect(screen.getByText(/This action cannot be undone/)).toBeInTheDocument();
  });

  it('should show multiple printers confirmation message', () => {
    render(
      <DeleteConfirmationModal
        isOpen={true}
        printers={mockMultiplePrinters}
        onConfirm={mockOnConfirm}
        onCancel={mockOnCancel}
      />
    );

    expect(screen.getByText(/Are you sure you want to delete 3 printers/)).toBeInTheDocument();
    expect(screen.getByText('Printers to be deleted:')).toBeInTheDocument();
  });

  it('should list all printers to be deleted', () => {
    render(
      <DeleteConfirmationModal
        isOpen={true}
        printers={mockMultiplePrinters}
        onConfirm={mockOnConfirm}
        onCancel={mockOnCancel}
      />
    );

    expect(screen.getByText(/Printer 1/)).toBeInTheDocument();
    expect(screen.getByText(/Printer 2/)).toBeInTheDocument();
    expect(screen.getByText(/Printer 3/)).toBeInTheDocument();
  });

  it('should call onConfirm when delete button is clicked', () => {
    render(
      <DeleteConfirmationModal
        isOpen={true}
        printers={[mockPrinter]}
        onConfirm={mockOnConfirm}
        onCancel={mockOnCancel}
      />
    );

    const deleteButton = screen.getByRole('button', { name: /Delete Printer/i });
    fireEvent.click(deleteButton);
    expect(mockOnConfirm).toHaveBeenCalled();
  });

  it('should call onCancel when cancel button is clicked', () => {
    render(
      <DeleteConfirmationModal
        isOpen={true}
        printers={[mockPrinter]}
        onConfirm={mockOnConfirm}
        onCancel={mockOnCancel}
      />
    );

    fireEvent.click(screen.getByText('Cancel'));
    expect(mockOnCancel).toHaveBeenCalled();
  });

  it('should show correct button text for single printer', () => {
    render(
      <DeleteConfirmationModal
        isOpen={true}
        printers={[mockPrinter]}
        onConfirm={mockOnConfirm}
        onCancel={mockOnCancel}
      />
    );

    expect(screen.getByRole('button', { name: /Delete Printer/i })).toBeInTheDocument();
  });

  it('should show correct button text for multiple printers', () => {
    render(
      <DeleteConfirmationModal
        isOpen={true}
        printers={mockMultiplePrinters}
        onConfirm={mockOnConfirm}
        onCancel={mockOnCancel}
      />
    );

    expect(screen.getByText('Delete 3 Printers')).toBeInTheDocument();
  });

  it('should display alert icon', () => {
    render(
      <DeleteConfirmationModal
        isOpen={true}
        printers={[mockPrinter]}
        onConfirm={mockOnConfirm}
        onCancel={mockOnCancel}
      />
    );

    // Modal renders via portal, so query the document
    expect(document.querySelector('svg')).toBeInTheDocument();
  });

  it('should show manufacturer and model info in list', () => {
    render(
      <DeleteConfirmationModal
        isOpen={true}
        printers={mockMultiplePrinters}
        onConfirm={mockOnConfirm}
        onCancel={mockOnCancel}
      />
    );

    const printerItems = screen.getAllByText(/Prusa i3 MK3S\+/);
    expect(printerItems.length).toBeGreaterThan(0);
  });

  it('should handle empty printers array gracefully', () => {
    render(
      <DeleteConfirmationModal
        isOpen={true}
        printers={[]}
        onConfirm={mockOnConfirm}
        onCancel={mockOnCancel}
      />
    );

    // Should still render the modal
    const modal = screen.getByRole('dialog');
    expect(modal).toBeInTheDocument();
  });
});

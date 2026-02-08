import { render, screen } from '@testing-library/react';
import { describe, it, expect, vi } from 'vitest';
import userEvent from '@testing-library/user-event';
import { BrowserRouter } from 'react-router';
import { AddPrinterButton } from '../AddPrinterButton';

vi.mock('../AddPrinterModal', () => ({
  AddPrinterModal: ({ isOpen, onClose, onSuccess }: { isOpen: boolean; onClose: () => void; onSuccess: () => void }) => (
    isOpen ? (
      <div data-testid="add-printer-modal">
        <button onClick={onClose}>Close</button>
        <button onClick={onSuccess}>Save</button>
      </div>
    ) : null
  ),
}));

describe('AddPrinterButton', () => {
  const renderWithRouter = (ui: React.ReactElement) => {
    return render(<BrowserRouter>{ui}</BrowserRouter>);
  };

  it('should render Add Printer button', () => {
    renderWithRouter(<AddPrinterButton />);

    expect(screen.getByText('Add Printer')).toBeInTheDocument();
  });

  it('should open modal when clicked', async () => {
    const user = userEvent.setup();
    renderWithRouter(<AddPrinterButton />);

    await user.click(screen.getByText('Add Printer'));

    expect(screen.getByTestId('add-printer-modal')).toBeInTheDocument();
  });

  it('should close modal when Close is clicked', async () => {
    const user = userEvent.setup();
    renderWithRouter(<AddPrinterButton />);

    await user.click(screen.getByText('Add Printer'));
    await user.click(screen.getByText('Close'));

    expect(screen.queryByTestId('add-printer-modal')).not.toBeInTheDocument();
  });

  it('should call onSuccess and close modal when Save is clicked', async () => {
    const handleSuccess = vi.fn();
    const user = userEvent.setup();
    renderWithRouter(<AddPrinterButton onSuccess={handleSuccess} />);

    await user.click(screen.getByText('Add Printer'));
    await user.click(screen.getByText('Save'));

    expect(handleSuccess).toHaveBeenCalledTimes(1);
    expect(screen.queryByTestId('add-printer-modal')).not.toBeInTheDocument();
  });

  it('should work without onSuccess callback', async () => {
    const user = userEvent.setup();
    renderWithRouter(<AddPrinterButton />);

    await user.click(screen.getByText('Add Printer'));
    await user.click(screen.getByText('Save'));

    expect(screen.queryByTestId('add-printer-modal')).not.toBeInTheDocument();
  });
});

import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { AddPrinterModal } from '../AddPrinterModal';

const createPrinter = vi.fn();

vi.mock('@/services/api', () => ({
  apiClient: {
    createPrinter: (...args: unknown[]) => createPrinter(...args),
  },
}));

vi.mock('@/common/hooks/useApi', () => ({
  useManufacturers: () => ({ data: [], isLoading: false, error: null }),
  useModels: () => ({ data: [], isLoading: false, error: null }),
}));

vi.mock('@/common/components/modals/Modal', () => ({
  Modal: ({ isOpen, title, footer, children }: { isOpen: boolean; title: string; footer?: React.ReactNode; children: React.ReactNode }) => (
    isOpen ? (
      <div>
        <h1>{title}</h1>
        {children}
        {footer}
      </div>
    ) : null
  ),
}));

describe('AddPrinterModal', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  const fillRequiredFields = async (user: ReturnType<typeof userEvent.setup>, name: string) => {
    await user.type(screen.getByLabelText('Printer name'), name);
    await user.type(screen.getByLabelText('Server URL'), 'http://printer.local');
  };

  it('caps the printer name input at 100 characters', () => {
    render(<AddPrinterModal isOpen onClose={vi.fn()} onSuccess={vi.fn()} />);

    expect(screen.getByLabelText('Printer name')).toHaveAttribute('maxLength', '100');
  });

  it('shows a client-side validation error when the name exceeds 100 characters and does not submit', async () => {
    const user = userEvent.setup();
    render(<AddPrinterModal isOpen onClose={vi.fn()} onSuccess={vi.fn()} />);

    // Bypass the maxLength attribute (which only blocks user typing) to simulate a
    // name that reaches submit, e.g. via paste, autofill, or a stale draft.
    const nameInput = screen.getByLabelText('Printer name');
    fireEvent.change(nameInput, { target: { value: 'a'.repeat(101) } });
    await user.type(screen.getByLabelText('Server URL'), 'http://printer.local');

    await user.click(screen.getByRole('button', { name: /add printer/i }));

    expect(await screen.findByText('Printer name must be between 1 and 100 characters')).toBeInTheDocument();
    expect(createPrinter).not.toHaveBeenCalled();
  });

  it('submits successfully with a 100-character printer name (boundary)', async () => {
    const user = userEvent.setup();
    const onSuccess = vi.fn();
    createPrinter.mockResolvedValue({ id: 'printer-1' });

    render(<AddPrinterModal isOpen onClose={vi.fn()} onSuccess={onSuccess} />);

    const boundaryName = 'a'.repeat(100);
    await fillRequiredFields(user, boundaryName);
    await user.click(screen.getByRole('button', { name: /add printer/i }));

    await waitFor(() => {
      expect(createPrinter).toHaveBeenCalledWith(expect.objectContaining({ name: boundaryName }));
    });
    expect(onSuccess).toHaveBeenCalled();
  });

  it('surfaces the backend field validation message instead of a generic failure', async () => {
    const user = userEvent.setup();
    createPrinter.mockRejectedValue({
      response: {
        status: 400,
        data: { Name: ['Printer name must be between 1 and 100 characters'] },
      },
    });

    render(<AddPrinterModal isOpen onClose={vi.fn()} onSuccess={vi.fn()} />);

    await fillRequiredFields(user, 'Valid Name');
    await user.click(screen.getByRole('button', { name: /add printer/i }));

    expect(await screen.findByText('Printer name must be between 1 and 100 characters')).toBeInTheDocument();
    expect(screen.queryByText('Failed to add printer')).not.toBeInTheDocument();
  });

  it('falls back to a generic message when the 400 response has no field errors', async () => {
    const user = userEvent.setup();
    createPrinter.mockRejectedValue({
      response: {
        status: 400,
        data: {},
      },
    });

    render(<AddPrinterModal isOpen onClose={vi.fn()} onSuccess={vi.fn()} />);

    await fillRequiredFields(user, 'Valid Name');
    await user.click(screen.getByRole('button', { name: /add printer/i }));

    expect(await screen.findByText('Failed to add printer')).toBeInTheDocument();
  });
});

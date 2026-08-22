import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { AddPrinterModal } from '../AddPrinterModal';

const createPrinter = vi.fn();
const testConnection = vi.fn();

const { mockToast } = vi.hoisted(() => ({
  mockToast: { success: vi.fn(), error: vi.fn(), dismiss: vi.fn() },
}));

vi.mock('sonner', () => ({ toast: mockToast }));

vi.mock('@/services/api', () => ({
  apiClient: {
    createPrinter: (...args: unknown[]) => createPrinter(...args),
    testConnection: (...args: unknown[]) => testConnection(...args),
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

  it('rejects a name whose raw (untrimmed) length exceeds 100, matching the backend\'s Length(1,100) check', async () => {
    const user = userEvent.setup();
    render(<AddPrinterModal isOpen onClose={vi.fn()} onSuccess={vi.fn()} />);

    // 3 leading + 98 + 3 trailing = 104 raw characters, but only 98 once trimmed.
    // The backend's FluentValidation rule measures the raw string, so the client
    // must reject this too rather than trim-then-measure and let it through.
    const paddedName = `   ${'a'.repeat(98)}   `;
    const nameInput = screen.getByLabelText('Printer name');
    fireEvent.change(nameInput, { target: { value: paddedName } });
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
    // apiClient rejects with the shared ApiError shape (statusCode/data), not a raw
    // AxiosError — see src/services/api.ts's response interceptor.
    createPrinter.mockRejectedValue({
      message: 'Bad Request',
      statusCode: 400,
      data: { Name: ['Printer name must be between 1 and 100 characters'] },
      isAxiosError: true,
    });

    render(<AddPrinterModal isOpen onClose={vi.fn()} onSuccess={vi.fn()} />);

    await fillRequiredFields(user, 'Valid Name');
    await user.click(screen.getByRole('button', { name: /add printer/i }));

    expect(await screen.findByText('Printer name must be between 1 and 100 characters')).toBeInTheDocument();
    expect(screen.queryByText('Failed to add printer')).not.toBeInTheDocument();
  });

  it('falls back to the ApiError message when the 400 response has no field errors', async () => {
    const user = userEvent.setup();
    createPrinter.mockRejectedValue({
      message: 'Something went wrong creating the printer',
      statusCode: 400,
      data: {},
      isAxiosError: true,
    });

    render(<AddPrinterModal isOpen onClose={vi.fn()} onSuccess={vi.fn()} />);

    await fillRequiredFields(user, 'Valid Name');
    await user.click(screen.getByRole('button', { name: /add printer/i }));

    expect(await screen.findByText('Something went wrong creating the printer')).toBeInTheDocument();
  });

  it('falls back to a generic message when the rejection carries no usable message', async () => {
    const user = userEvent.setup();
    createPrinter.mockRejectedValue({
      message: '',
      statusCode: 500,
      isAxiosError: true,
    });

    render(<AddPrinterModal isOpen onClose={vi.fn()} onSuccess={vi.fn()} />);

    await fillRequiredFields(user, 'Valid Name');
    await user.click(screen.getByRole('button', { name: /add printer/i }));

    expect(await screen.findByText('Failed to add printer')).toBeInTheDocument();
  });

  // #1865: a failed connection test (e.g. an unreachable/rejected Moonraker URL) must
  // surface the backend's actual rejection reason to the user, and must keep doing so
  // on every retry rather than silently no-op'ing or collapsing to a generic message.
  describe('Test connection feedback (#1865)', () => {
    it('surfaces the backend rejection message via an error toast on test failure', async () => {
      const user = userEvent.setup();
      // apiClient rejects with the shared ApiError shape built by the Axios response
      // interceptor (see src/services/api.ts) — not a raw Error instance.
      testConnection.mockRejectedValue({
        message: 'The requested server address is not allowed.',
        statusCode: 400,
        data: { success: false, message: 'The requested server address is not allowed.' },
        isAxiosError: true,
      });

      render(<AddPrinterModal isOpen onClose={vi.fn()} onSuccess={vi.fn()} />);
      await fillRequiredFields(user, 'Valid Name');

      await user.click(screen.getByRole('button', { name: /test/i }));

      await waitFor(() => {
        expect(mockToast.error).toHaveBeenCalledWith(
          'The requested server address is not allowed.',
          expect.objectContaining({ duration: 8000 })
        );
      });
    });

    it('continues to surface feedback on a repeated test attempt (no silent no-op on retry)', async () => {
      const user = userEvent.setup();
      testConnection.mockRejectedValue({
        message: 'The requested server address is not allowed.',
        statusCode: 400,
        data: { success: false, message: 'The requested server address is not allowed.' },
        isAxiosError: true,
      });

      render(<AddPrinterModal isOpen onClose={vi.fn()} onSuccess={vi.fn()} />);
      await fillRequiredFields(user, 'Valid Name');

      const testButton = screen.getByRole('button', { name: /test/i });
      await user.click(testButton);
      await waitFor(() => expect(mockToast.error).toHaveBeenCalledTimes(1));

      await user.click(testButton);
      await waitFor(() => expect(mockToast.error).toHaveBeenCalledTimes(2));

      expect(mockToast.error).toHaveBeenNthCalledWith(
        2,
        'The requested server address is not allowed.',
        expect.objectContaining({ duration: 8000 })
      );
    });

    it('shows a success toast when the connection test succeeds', async () => {
      const user = userEvent.setup();
      testConnection.mockResolvedValue({ success: true, message: 'Connected successfully' });

      render(<AddPrinterModal isOpen onClose={vi.fn()} onSuccess={vi.fn()} />);
      await fillRequiredFields(user, 'Valid Name');

      await user.click(screen.getByRole('button', { name: /test/i }));

      await waitFor(() => {
        expect(mockToast.success).toHaveBeenCalledWith(
          'Connected successfully',
          expect.objectContaining({ duration: 5000 })
        );
      });
    });
  });
});

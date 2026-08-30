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

  // #2216: out-of-range backend/frontend ports (e.g. -1, 70000) reached the server and
  // returned an HTTP 400 with no visible feedback. Client-side validation must block
  // Test/submit and show an inline message next to the offending field instead.
  describe('Port range validation (#2216)', () => {
    it('blocks Test and shows an inline error for an out-of-range backend port', async () => {
      const user = userEvent.setup();
      render(<AddPrinterModal isOpen onClose={vi.fn()} onSuccess={vi.fn()} />);
      await fillRequiredFields(user, 'Valid Name');

      const backendPortInput = screen.getByLabelText('Backend port');
      fireEvent.change(backendPortInput, { target: { value: '-1' } });

      await user.click(screen.getByRole('button', { name: /test/i }));

      expect(await screen.findByText('Backend port must be a whole number between 1 and 65535')).toBeInTheDocument();
      expect(testConnection).not.toHaveBeenCalled();
    });

    it('blocks Test and shows an inline error for an out-of-range frontend port', async () => {
      const user = userEvent.setup();
      render(<AddPrinterModal isOpen onClose={vi.fn()} onSuccess={vi.fn()} />);
      await fillRequiredFields(user, 'Valid Name');

      const frontendPortInput = screen.getByLabelText('Frontend port');
      fireEvent.change(frontendPortInput, { target: { value: '70000' } });

      await user.click(screen.getByRole('button', { name: /test/i }));

      expect(await screen.findByText('Frontend port must be a whole number between 1 and 65535')).toBeInTheDocument();
      expect(testConnection).not.toHaveBeenCalled();
    });

    it('reproduces the reported bug: -1 backend port and 70000 frontend port both block Test with inline errors', async () => {
      // Exact repro from the issue: /printers -> Add Printer, backend port -1,
      // frontend port 70000, click Test. Previously this reached the server and
      // came back as an HTTP 400 with no visible feedback.
      const user = userEvent.setup();
      render(<AddPrinterModal isOpen onClose={vi.fn()} onSuccess={vi.fn()} />);
      await fillRequiredFields(user, 'Valid Name');

      fireEvent.change(screen.getByLabelText('Backend port'), { target: { value: '-1' } });
      fireEvent.change(screen.getByLabelText('Frontend port'), { target: { value: '70000' } });

      await user.click(screen.getByRole('button', { name: /test/i }));

      expect(await screen.findByText('Backend port must be a whole number between 1 and 65535')).toBeInTheDocument();
      expect(screen.getByText('Frontend port must be a whole number between 1 and 65535')).toBeInTheDocument();
      expect(testConnection).not.toHaveBeenCalled();
    });

    it('blocks Add Printer submission and shows an inline error for out-of-range ports', async () => {
      const user = userEvent.setup();
      render(<AddPrinterModal isOpen onClose={vi.fn()} onSuccess={vi.fn()} />);
      await fillRequiredFields(user, 'Valid Name');

      fireEvent.change(screen.getByLabelText('Backend port'), { target: { value: '-1' } });
      fireEvent.change(screen.getByLabelText('Frontend port'), { target: { value: '70000' } });

      await user.click(screen.getByRole('button', { name: /add printer/i }));

      expect(await screen.findByText('Backend port must be a whole number between 1 and 65535')).toBeInTheDocument();
      expect(screen.getByText('Frontend port must be a whole number between 1 and 65535')).toBeInTheDocument();
      expect(createPrinter).not.toHaveBeenCalled();
    });

    it('rejects a port of 0 (below the valid range)', async () => {
      const user = userEvent.setup();
      render(<AddPrinterModal isOpen onClose={vi.fn()} onSuccess={vi.fn()} />);
      await fillRequiredFields(user, 'Valid Name');

      fireEvent.change(screen.getByLabelText('Backend port'), { target: { value: '0' } });
      await user.click(screen.getByRole('button', { name: /test/i }));

      expect(await screen.findByText('Backend port must be a whole number between 1 and 65535')).toBeInTheDocument();
      expect(testConnection).not.toHaveBeenCalled();
    });

    it('rejects a fractional port instead of silently truncating it to an integer', async () => {
      const user = userEvent.setup();
      render(<AddPrinterModal isOpen onClose={vi.fn()} onSuccess={vi.fn()} />);
      await fillRequiredFields(user, 'Valid Name');

      // parseInt('1.5', 10) === 1, which would silently accept a value the user
      // never entered. Parsing via valueAsNumber preserves the fraction so it is
      // correctly rejected instead.
      fireEvent.change(screen.getByLabelText('Backend port'), { target: { value: '1.5' } });
      await user.click(screen.getByRole('button', { name: /test/i }));

      expect(await screen.findByText('Backend port must be a whole number between 1 and 65535')).toBeInTheDocument();
      expect(testConnection).not.toHaveBeenCalled();
    });

    it('accepts the boundary values 1 and 65535', async () => {
      const user = userEvent.setup();
      testConnection.mockResolvedValue({ success: true, message: 'Connected successfully' });

      render(<AddPrinterModal isOpen onClose={vi.fn()} onSuccess={vi.fn()} />);
      await fillRequiredFields(user, 'Valid Name');

      fireEvent.change(screen.getByLabelText('Backend port'), { target: { value: '1' } });
      fireEvent.change(screen.getByLabelText('Frontend port'), { target: { value: '65535' } });
      await user.click(screen.getByRole('button', { name: /test/i }));

      await waitFor(() => expect(testConnection).toHaveBeenCalled());
      expect(screen.queryByText(/must be a whole number between 1 and 65535/)).not.toBeInTheDocument();
    });

    it('clears the port validation error once the user enters a valid value', async () => {
      const user = userEvent.setup();
      testConnection.mockResolvedValue({ success: true, message: 'Connected successfully' });

      render(<AddPrinterModal isOpen onClose={vi.fn()} onSuccess={vi.fn()} />);
      await fillRequiredFields(user, 'Valid Name');

      const backendPortInput = screen.getByLabelText('Backend port');
      fireEvent.change(backendPortInput, { target: { value: '-1' } });
      await user.click(screen.getByRole('button', { name: /test/i }));
      expect(await screen.findByText('Backend port must be a whole number between 1 and 65535')).toBeInTheDocument();

      fireEvent.change(backendPortInput, { target: { value: '7125' } });
      await user.click(screen.getByRole('button', { name: /test/i }));

      await waitFor(() => expect(testConnection).toHaveBeenCalled());
      expect(screen.queryByText('Backend port must be a whole number between 1 and 65535')).not.toBeInTheDocument();
    });

    it('clears a stale backend port error when the backend type changes and resets the port programmatically', async () => {
      const user = userEvent.setup();
      testConnection.mockResolvedValue({ success: true, message: 'Connected successfully' });

      render(<AddPrinterModal isOpen onClose={vi.fn()} onSuccess={vi.fn()} />);
      await fillRequiredFields(user, 'Valid Name');

      fireEvent.change(screen.getByLabelText('Backend port'), { target: { value: '-1' } });
      await user.click(screen.getByRole('button', { name: /test/i }));
      expect(await screen.findByText('Backend port must be a whole number between 1 and 65535')).toBeInTheDocument();

      // Switching backend type auto-resets `backendPort` to a valid default
      // programmatically (not via the port input's own onChange), which must
      // also clear the now-stale error rather than leaving it on screen.
      await user.selectOptions(screen.getByLabelText('Backend type'), 'FlashForge');

      expect(screen.queryByText('Backend port must be a whole number between 1 and 65535')).not.toBeInTheDocument();
    });

    it('surfaces a server-side 400 rejection for the Test action even when client-side port validation passes', async () => {
      const user = userEvent.setup();
      testConnection.mockRejectedValue({
        message: 'Backend port is not reachable',
        statusCode: 400,
        data: { success: false, message: 'Backend port is not reachable' },
        isAxiosError: true,
      });

      render(<AddPrinterModal isOpen onClose={vi.fn()} onSuccess={vi.fn()} />);
      await fillRequiredFields(user, 'Valid Name');
      // Use a valid, non-default port to prove this is exercising client-side port
      // validation (which passes) followed by a genuine server-side rejection,
      // rather than accidentally passing due to the field being left untouched.
      fireEvent.change(screen.getByLabelText('Backend port'), { target: { value: '9999' } });

      await user.click(screen.getByRole('button', { name: /test/i }));

      await waitFor(() => {
        expect(mockToast.error).toHaveBeenCalledWith(
          'Backend port is not reachable',
          expect.objectContaining({ duration: 8000 })
        );
      });
      expect(screen.queryByText(/must be a whole number between 1 and 65535/)).not.toBeInTheDocument();
    });
  });

  // #2216 collateral: adding `noValidate` to the form removes the native `min={0}`
  // guard on Wattage/Machine Hourly Rate, so these must now be enforced in JS.
  describe('Wattage / Machine Hourly Rate validation (#2216 noValidate collateral)', () => {
    it('blocks submission and shows an inline error for a negative wattage', async () => {
      const user = userEvent.setup();
      render(<AddPrinterModal isOpen onClose={vi.fn()} onSuccess={vi.fn()} />);
      await fillRequiredFields(user, 'Valid Name');

      fireEvent.change(screen.getByLabelText('Wattage'), { target: { value: '-50' } });
      await user.click(screen.getByRole('button', { name: /add printer/i }));

      expect(await screen.findByText('Wattage must be zero or greater')).toBeInTheDocument();
      expect(createPrinter).not.toHaveBeenCalled();
    });

    it('blocks submission and shows an inline error for a negative machine hourly rate', async () => {
      const user = userEvent.setup();
      render(<AddPrinterModal isOpen onClose={vi.fn()} onSuccess={vi.fn()} />);
      await fillRequiredFields(user, 'Valid Name');

      fireEvent.change(screen.getByLabelText('Machine hourly rate'), { target: { value: '-0.5' } });
      await user.click(screen.getByRole('button', { name: /add printer/i }));

      expect(await screen.findByText('Machine hourly rate must be zero or greater')).toBeInTheDocument();
      expect(createPrinter).not.toHaveBeenCalled();
    });
  });
});

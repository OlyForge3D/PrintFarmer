import { fireEvent, render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { PrinterFilesModal } from '@/features/printers/components/PrinterFilesModal';
import { apiClient } from '@/services/api';
import { PrinterBackend, type Printer } from '@/types/api';

vi.mock('@/services/api', () => ({
  apiClient: {
    getPrinterFileList: vi.fn(),
    startPrintFromFile: vi.fn(),
  },
}));

const printer: Printer = {
  id: 'printer-1',
  name: 'Workshop printer',
  backend: PrinterBackend.Moonraker,
  isOnline: true,
};

describe('PrinterFilesModal catalog navigation', () => {
  beforeEach(() => {
    vi.mocked(apiClient.getPrinterFileList).mockResolvedValue([{ fileName: 'part.gcode' }]);
  });

  it('replaces harvest controls with the printer-scoped catalog link', async () => {
    const onClose = vi.fn();
    render(
      <MemoryRouter>
        <PrinterFilesModal isOpen onClose={onClose} printer={printer} />
      </MemoryRouter>,
    );

    expect(await screen.findByText('part.gcode')).toBeInTheDocument();
    expect(apiClient.getPrinterFileList).toHaveBeenCalledWith(printer.id);
    expect(screen.queryByRole('button', { name: /harvest/i })).not.toBeInTheDocument();
    const link = screen.getByRole('link', { name: 'Files > On printers' });
    expect(link).toHaveAttribute('href', '/files?source=printers&printer=printer-1');
    fireEvent.click(link);
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('preserves immediate print confirmation and non-harvest file controls', async () => {
    render(
      <MemoryRouter>
        <PrinterFilesModal isOpen onClose={vi.fn()} printer={printer} />
      </MemoryRouter>,
    );
    await screen.findByText('part.gcode');

    expect(screen.getByRole('button', { name: 'Copy filename' })).toBeEnabled();
    expect(screen.getByRole('button', { name: 'Download file' })).toBeEnabled();
    expect(screen.getByRole('button', { name: 'Delete requires the printer-file catalog' })).toBeDisabled();
    fireEvent.click(screen.getByRole('button', { name: 'Queue for printing' }));
    expect(screen.getByRole('dialog', { name: 'Start Printing?' })).toBeInTheDocument();
    expect(apiClient.startPrintFromFile).not.toHaveBeenCalled();
  });
});

import '@testing-library/jest-dom';
import { act, fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { PrinterBackend, type Printer } from '@/types/api';
import { PrinterServiceStatus } from '../PrinterServiceStatus';

const printer: Printer = {
  id: 'service-fixture', name: 'Workshop', backend: PrinterBackend.Moonraker,
  serverUrl: 'http://printer.example.test', isEnabled: true, isOnline: false,
};

describe('PrinterServiceStatus', () => {
  it('shows status without offering a mutation to a read-only user', () => {
    render(<PrinterServiceStatus printer={{ ...printer, isEnabled: false }} />);
    expect(screen.getByText('Out of Service')).toBeVisible();
    expect(screen.queryByRole('button')).not.toBeInTheDocument();
  });

  it('uses the reviewed printer and prevents repeat submissions while saving', async () => {
    let finish!: () => void;
    const onToggleEnabled = vi.fn(() => new Promise<void>(resolve => { finish = resolve; }));
    render(<PrinterServiceStatus printer={printer} onToggleEnabled={onToggleEnabled} />);
    const button = screen.getByRole('button', { name: 'In Service: Mark Workshop Out of Service' });
    fireEvent.click(button);
    expect(button).toBeDisabled();
    expect(button).toHaveAccessibleName('Please wait…: Mark Workshop Out of Service');
    fireEvent.click(button);
    expect(onToggleEnabled).toHaveBeenCalledExactlyOnceWith(printer);
    await act(async () => finish());
    expect(button).toBeEnabled();
    expect(button).toHaveTextContent('In Service');
  });

  it('reflects confirmed server state rather than optimistically flipping service status', () => {
    const onToggleEnabled = vi.fn();
    const { rerender } = render(<PrinterServiceStatus printer={printer} onToggleEnabled={onToggleEnabled} />);
    rerender(<PrinterServiceStatus printer={{ ...printer, isEnabled: false }} onToggleEnabled={onToggleEnabled} />);
    expect(screen.getByRole('button', { name: 'Out of Service: Mark Workshop In Service' })).toHaveTextContent('Out of Service');
  });
});

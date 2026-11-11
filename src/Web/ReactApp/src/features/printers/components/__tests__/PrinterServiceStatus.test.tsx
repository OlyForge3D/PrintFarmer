import '@testing-library/jest-dom';
import { act, fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { mdiPowerPlug, mdiPowerPlugOutline } from '@mdi/js';
import { PrinterBackend, type Printer } from '@/types/api';
import { PrinterServiceStatus } from '../PrinterServiceStatus';

const printer: Printer = {
  id: 'service-fixture', name: 'Workshop', backend: PrinterBackend.Moonraker,
  serverUrl: 'http://printer.example.test', isEnabled: true, isOnline: false,
};

describe('PrinterServiceStatus', () => {
  it.each([true, false])('renders the table glyph for isEnabled=%s without a read-only toggle', (isEnabled) => {
    render(<PrinterServiceStatus printer={{ ...printer, isEnabled }} iconOnly />);
    const label = isEnabled ? 'In Service' : 'Out of Service';
    const glyph = screen.getByRole('img', { name: label });
    expect(glyph.querySelector('path')).toHaveAttribute('d', isEnabled ? mdiPowerPlug : mdiPowerPlugOutline);
    expect(glyph).toHaveClass(isEnabled ? 'text-pf-success' : 'text-pf-text-secondary');
    expect(glyph.parentElement).toHaveAttribute('title', label);
    expect(screen.queryByRole('button')).not.toBeInTheDocument();
    expect(screen.queryByText(label)).not.toBeInTheDocument();
  });

  it('keeps the glyph state until confirmed and disables repeat toggles while pending', async () => {
    let finish!: () => void;
    const onToggleEnabled = vi.fn(() => new Promise<void>(resolve => { finish = resolve; }));
    const { rerender } = render(<PrinterServiceStatus printer={printer} iconOnly onToggleEnabled={onToggleEnabled} />);
    const button = screen.getByRole('button', { name: 'In Service: Mark Workshop Out of Service' });
    expect(button).toHaveAttribute('aria-pressed', 'true');
    expect(button).toHaveAttribute('title', 'In Service: Mark Workshop Out of Service');
    fireEvent.click(button);
    expect(button).toBeDisabled();
    expect(button).toHaveAttribute('aria-busy', 'true');
    fireEvent.click(button);
    expect(onToggleEnabled).toHaveBeenCalledExactlyOnceWith(printer);
    await act(async () => finish());
    expect(button).toBeEnabled();
    expect(button).toHaveAttribute('aria-pressed', 'true');
    rerender(<PrinterServiceStatus printer={{ ...printer, isEnabled: false }} iconOnly onToggleEnabled={onToggleEnabled} />);
    expect(button).toHaveAttribute('aria-pressed', 'false');
    expect(button).toHaveAccessibleName('Out of Service: Mark Workshop In Service');
    expect(button.querySelector('path')).toHaveAttribute('d', mdiPowerPlugOutline);
  });

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

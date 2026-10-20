vi.mock('@/features/printers/hooks/use-printer-controls-mode', () => ({ usePrinterControlsMode: () => ({ mode: 'guided' }) }));
vi.mock('@/features/auth/hooks/useAuth', () => ({ useAuth: () => ({ hasPermission: () => true }) }));
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { MovementControlSection } from '@/features/printers/components/MovementControlSection';
import type { ComponentProps } from 'react';

function props(): ComponentProps<typeof MovementControlSection> {
  return {
    moveX: 100, moveY: 110, moveZ: 10, homedAxes: 'xyz', step: 5, extrudeStep: 5, extrudeSpeed: 5, extrudeMinTemp: 170,
    movementActionPending: false, canMove: true, canDisableMotors: true, canSetStep: true, canManualMove: true, canExtrude: true,
    onMoveXChange: vi.fn(), onMoveYChange: vi.fn(), onMoveZChange: vi.fn(), onStepChange: vi.fn(),
    onExtrudeStepChange: vi.fn(), onExtrudeSpeedChange: vi.fn(), onMove: vi.fn(), onHome: vi.fn(),
    onDisableMotors: vi.fn(), onExtrude: vi.fn(),
  };
}
describe('detailed-card movement entry points', () => {
  it('requires full XYZ homing and has no GO', () => {
    render(<MovementControlSection {...props()} homedAxes="xy" onMoveTo={vi.fn()} />);
    expect(screen.queryByText('GO')).not.toBeInTheDocument();
    expect(screen.getByLabelText('X absolute target')).toBeDisabled();
    expect(screen.getByLabelText('Z absolute target')).toBeDisabled();
  });
  it('defaults to relative-only entry if no absolute API handler exists', () => {
    render(<MovementControlSection {...props()} />);
    expect(screen.getByRole('button', { name: 'Absolute' })).toBeDisabled();
    expect(screen.getByLabelText('X movement amount')).toBeEnabled();
  });
  it('preserves home and jog callbacks', async () => {
    const callbacks = props();
    render(<MovementControlSection {...callbacks} />);
    fireEvent.click(screen.getByTitle('Home X/Y'));
    expect(callbacks.onHome).toHaveBeenCalledExactlyOnceWith('xy');
    await waitFor(() => expect(screen.getByTitle('Home X/Y')).toBeEnabled());
    fireEvent.click(screen.getByRole('button', { name: 'Jog Y positive' }));
    expect(callbacks.onMove).toHaveBeenCalledExactlyOnceWith('Y', 5);
    await waitFor(() => expect(screen.getByRole('button', { name: 'Jog Y positive' })).toBeEnabled());
  });
  it.each([{ movementActionPending: true }, { canManualMove: false }])('locks coordinate entry when %j', gates => {
    render(<MovementControlSection {...props()} {...gates} onMoveTo={vi.fn()} />);
    for (const field of screen.getAllByRole('spinbutton', { name: /absolute target/ })) expect(field).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Relative' })).toBeDisabled();
  });
});

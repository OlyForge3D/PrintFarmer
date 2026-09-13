import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { MovementControlSection } from '@/features/printers/components/MovementControlSection';
import type { ComponentProps } from 'react';

function props(): ComponentProps<typeof MovementControlSection> {
  return {
    moveX: 100, moveY: 110, moveZ: 10, step: 5, extrudeStep: 5, extrudeSpeed: 5, extrudeMinTemp: 170,
    movementActionPending: false, canMove: true, canDisableMotors: true, canSetStep: true, canManualMove: true, canExtrude: true,
    onMoveXChange: vi.fn(), onMoveYChange: vi.fn(), onMoveZChange: vi.fn(), onStepChange: vi.fn(),
    onExtrudeStepChange: vi.fn(), onExtrudeSpeedChange: vi.fn(), onMove: vi.fn(), onHome: vi.fn(),
    onDisableMotors: vi.fn(), onExtrude: vi.fn(),
  };
}
describe('detailed-card movement entry points', () => {
  it('submits one absolute intent for Moonraker GO rather than three relative jogs', () => {
    const callbacks = props();
    const absolute = vi.fn();
    render(<MovementControlSection {...callbacks} onMoveTo={absolute} />);
    fireEvent.click(screen.getByTitle('Go to position'));
    expect(absolute).toHaveBeenCalledExactlyOnceWith({ x: 100, y: 110, z: 10 });
    expect(callbacks.onMove).not.toHaveBeenCalled();
  });
  it('preserves existing non-Moonraker GO behavior when no durable absolute handler is supplied', async () => {
    const callbacks = props();
    render(<MovementControlSection {...callbacks} />);
    fireEvent.click(screen.getByTitle('Go to position'));
    await waitFor(() => expect(callbacks.onMove).toHaveBeenCalledTimes(3));
    expect(callbacks.onMove).toHaveBeenNthCalledWith(1, 'X', 100);
    expect(callbacks.onMove).toHaveBeenNthCalledWith(2, 'Y', 110);
    expect(callbacks.onMove).toHaveBeenNthCalledWith(3, 'Z', 10);
  });
  it.each([
    { moveX: '' as const }, { moveY: '' as const }, { moveZ: '' as const },
    { moveY: Number.POSITIVE_INFINITY },
  ])('requires all finite XYZ values for durable GO: %j', invalid => {
    const absolute = vi.fn();
    render(<MovementControlSection {...props()} {...invalid} onMoveTo={absolute} />);
    expect(screen.getByTitle('Go to position')).toBeDisabled();
    fireEvent.click(screen.getByTitle('Go to position'));
    expect(absolute).not.toHaveBeenCalled();
    expect(screen.getByText(/Enter valid X, Y, and Z/)).toBeInTheDocument();
  });
  it('preserves partial-axis GO for non-Moonraker printers', async () => {
    const callbacks = props();
    render(<MovementControlSection {...callbacks} moveY="" moveZ="" />);
    fireEvent.click(screen.getByTitle('Go to position'));
    await waitFor(() => expect(callbacks.onMove).toHaveBeenCalledExactlyOnceWith('X', 100));
  });
  it('disables Home, jogs, and absolute positioning while a durable operation is unresolved', () => {
    const callbacks = props();
    render(<MovementControlSection {...callbacks} movementActionPending />);
    expect(screen.getByTitle('Home all axes')).toBeDisabled();
    expect(screen.getByTitle('Home X/Y')).toBeDisabled();
    expect(screen.getByTitle('Home Z')).toBeDisabled();
    expect(screen.getByTitle('Go to position')).toBeDisabled();
    for (const button of screen.getAllByRole('button', { name: /^Jog/ })) expect(button).toBeDisabled();
  });
});

import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import '@testing-library/jest-dom';
import { PlateTabBar } from '../PlateTabBar';
import type { BuildPlate } from '@/features/slicer/utils/plateManager';

function plate(overrides: Partial<BuildPlate> & { id: string; name: string }): BuildPlate {
  return { modelIds: [], locked: false, ...overrides };
}

function setup(props: Partial<React.ComponentProps<typeof PlateTabBar>> = {}) {
  const handlers = {
    onActivePlateChange: vi.fn(),
    onArrangePlate: vi.fn(),
    onOrientPlate: vi.fn(),
    onDeletePlate: vi.fn(),
    onRenamePlate: vi.fn(),
    onDuplicatePlate: vi.fn(),
  };
  const plates = props.plates ?? [
    plate({ id: 'p1', name: 'Plate 1', modelIds: ['m1', 'm2'] }),
    plate({ id: 'p2', name: 'Plate 2' }),
  ];
  render(
    <PlateTabBar
      plates={plates}
      activePlateId={props.activePlateId ?? 'p1'}
      {...handlers}
      {...props}
    />,
  );
  return handlers;
}

describe('PlateTabBar', () => {
  it('does not render an add ("+") control', () => {
    setup();
    expect(screen.queryByLabelText(/add plate/i)).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: '+' })).not.toBeInTheDocument();
  });

  it('shows arrange / orient / delete inline actions on the ACTIVE tab only', () => {
    setup({ activePlateId: 'p1' });
    // Active (Plate 1) has all three inline actions.
    expect(screen.getByLabelText('Auto-arrange Plate 1')).toBeInTheDocument();
    expect(screen.getByLabelText('Auto-orient Plate 1')).toBeInTheDocument();
    expect(screen.getByLabelText('Delete Plate 1')).toBeInTheDocument();
    // Inactive (Plate 2) does not expose inline actions.
    expect(screen.queryByLabelText('Auto-arrange Plate 2')).not.toBeInTheDocument();
    expect(screen.queryByLabelText('Auto-orient Plate 2')).not.toBeInTheDocument();
    expect(screen.queryByLabelText('Delete Plate 2')).not.toBeInTheDocument();
  });

  it('fires the explicit-plate callbacks for the active tab actions', () => {
    const h = setup({ activePlateId: 'p2' });
    fireEvent.click(screen.getByLabelText('Auto-arrange Plate 2'));
    fireEvent.click(screen.getByLabelText('Auto-orient Plate 2'));
    expect(h.onArrangePlate).toHaveBeenCalledWith('p2');
    expect(h.onOrientPlate).toHaveBeenCalledWith('p2');
  });

  it('switches plates when a tab select button is clicked', () => {
    const h = setup({ activePlateId: 'p1' });
    fireEvent.click(screen.getByLabelText(/^Select Plate 2/));
    expect(h.onActivePlateChange).toHaveBeenCalledWith('p2');
  });

  it('disables delete when only one plate exists', () => {
    setup({
      plates: [plate({ id: 'p1', name: 'Plate 1', modelIds: ['m1'] })],
      activePlateId: 'p1',
    });
    const del = screen.getByLabelText('Delete Plate 1');
    expect(del).toBeDisabled();
  });

  it('exposes descriptive aria-labels including model counts', () => {
    setup({ activePlateId: 'p1' });
    expect(screen.getByLabelText('Select Plate 1 (2 models)')).toBeInTheDocument();
    expect(screen.getByLabelText('Select Plate 2')).toBeInTheDocument();
  });
});

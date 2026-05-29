import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { AmsSlotVisualization } from '../AmsSlotVisualization';
import type { ToolheadDto } from '@/types/api';

// ── Test data factories ──

function makeMmuGate(index: number, overrides: Partial<ToolheadDto> = {}): ToolheadDto {
  return {
    id: `gate-${index}`,
    index,
    isPrimary: index === 0,
    toolheadType: 'MmuGate',
    ...overrides,
  };
}

function makePhysical(index: number, overrides: Partial<ToolheadDto> = {}): ToolheadDto {
  return {
    id: `phys-${index}`,
    index,
    isPrimary: index === 0,
    toolheadType: 'Physical',
    nozzleDiameter: 0.4,
    ...overrides,
  };
}

// ── Tests ──

describe('AmsSlotVisualization', () => {
  it('renders nothing when toolheads array is empty', () => {
    const { container } = render(<AmsSlotVisualization toolheads={[]} />);
    expect(container.firstChild).toBeNull();
  });

  it('renders correct number of slots for Bambu AMS (4 slots)', () => {
    const toolheads: ToolheadDto[] = [
      makeMmuGate(0, { currentMaterial: 'PLA', currentFilamentColor: '#FF0000' }),
      makeMmuGate(1, { currentMaterial: 'PETG', currentFilamentColor: '#00FF00' }),
      makeMmuGate(2),
      makeMmuGate(3, { currentMaterial: 'ABS', currentFilamentColor: '#0000FF' }),
    ];

    render(<AmsSlotVisualization toolheads={toolheads} />);

    expect(screen.getByTestId('ams-unit-0')).toBeDefined();
    expect(screen.getByTestId('ams-slot-0')).toBeDefined();
    expect(screen.getByTestId('ams-slot-1')).toBeDefined();
    expect(screen.getByTestId('ams-slot-2')).toBeDefined();
    expect(screen.getByTestId('ams-slot-3')).toBeDefined();
  });

  it('renders correct number of slots for Prusa MMU (5 slots)', () => {
    const toolheads: ToolheadDto[] = Array.from({ length: 5 }, (_, i) =>
      makeMmuGate(i, { currentMaterial: 'PLA', currentFilamentColor: '#AABBCC' }),
    );

    render(<AmsSlotVisualization toolheads={toolheads} />);

    // 5 slots should be treated as a single MMU unit
    expect(screen.getByTestId('ams-unit-0')).toBeDefined();
    expect(screen.getByTestId('ams-slot-4')).toBeDefined();
    // Should show "AMS 1" badge label
    expect(screen.getByText('AMS 1')).toBeDefined();
  });

  it('shows color swatches matching filament colors', () => {
    const toolheads: ToolheadDto[] = [
      makeMmuGate(0, { currentMaterial: 'PLA', currentFilamentColor: '#FF5733' }),
      makeMmuGate(1, { currentMaterial: 'PETG', currentFilamentColor: '#33FF57' }),
    ];

    render(<AmsSlotVisualization toolheads={toolheads} />);

    const swatch0 = screen.getByTestId('slot-color-0');
    expect(swatch0.style.backgroundColor).toContain('rgb');
    const swatch1 = screen.getByTestId('slot-color-1');
    expect(swatch1.style.backgroundColor).toContain('rgb');
  });

  it('shows "Empty" for unloaded slots', () => {
    const toolheads: ToolheadDto[] = [
      makeMmuGate(0, { currentMaterial: 'PLA', currentFilamentColor: '#FF0000' }),
      makeMmuGate(1), // empty
      makeMmuGate(2), // empty
      makeMmuGate(3, { currentMaterial: 'PETG', currentFilamentColor: '#00FF00' }),
    ];

    render(<AmsSlotVisualization toolheads={toolheads} />);

    const emptyLabels = screen.getAllByText('Empty');
    expect(emptyLabels.length).toBe(2);
  });

  it('groups multiple AMS units correctly (8 slots = 2 units of 4)', () => {
    const toolheads: ToolheadDto[] = Array.from({ length: 8 }, (_, i) =>
      makeMmuGate(i, { currentMaterial: `Material ${i}`, currentFilamentColor: '#AABBCC' }),
    );

    render(<AmsSlotVisualization toolheads={toolheads} />);

    expect(screen.getByTestId('ams-unit-0')).toBeDefined();
    expect(screen.getByTestId('ams-unit-1')).toBeDefined();
    expect(screen.getByText('AMS 1')).toBeDefined();
    expect(screen.getByText('AMS 2')).toBeDefined();
  });

  it('shows physical toolhead indicator', () => {
    const toolheads: ToolheadDto[] = [
      makePhysical(0, { currentMaterial: 'PLA', currentFilamentColor: '#FF0000' }),
    ];

    render(<AmsSlotVisualization toolheads={toolheads} />);

    expect(screen.getByTestId('nozzle-indicator-0')).toBeDefined();
    expect(screen.getByText('T0')).toBeDefined();
  });

  it('shows physical toolhead with nozzle diameter', () => {
    const toolheads: ToolheadDto[] = [
      makePhysical(0, { nozzleDiameter: 0.6 }),
    ];

    render(<AmsSlotVisualization toolheads={toolheads} />);

    expect(screen.getByText('0.6mm')).toBeDefined();
  });

  it('handles mixed physical and MmuGate toolheads', () => {
    const toolheads: ToolheadDto[] = [
      makePhysical(0, { currentMaterial: 'PLA', currentFilamentColor: '#FFFFFF', currentSpoolId: 42 }),
      makeMmuGate(0, { currentMaterial: 'PLA', currentFilamentColor: '#FF0000' }),
      makeMmuGate(1, { currentMaterial: 'PETG', currentFilamentColor: '#00FF00' }),
      makeMmuGate(2),
      makeMmuGate(3, { currentMaterial: 'ABS', currentFilamentColor: '#0000FF' }),
    ];

    render(<AmsSlotVisualization toolheads={toolheads} />);

    // Physical toolhead with spool alongside MMU gates shows only in external section
    expect(screen.getByTestId('nozzle-indicator-0')).toBeDefined();
    expect(screen.getByTestId('ams-unit-0')).toBeDefined();
    // Physical with spool alongside MMU shows in external section
    expect(screen.getByTestId('external-spool-section')).toBeDefined();
  });

  it('shows loaded count in unit header', () => {
    const toolheads: ToolheadDto[] = [
      makeMmuGate(0, { currentMaterial: 'PLA' }),
      makeMmuGate(1),
      makeMmuGate(2, { currentMaterial: 'PETG' }),
      makeMmuGate(3),
    ];

    render(<AmsSlotVisualization toolheads={toolheads} />);

    expect(screen.getByText('2/4 loaded')).toBeDefined();
  });

  it('renders in compact mode with smaller slots', () => {
    const toolheads: ToolheadDto[] = [
      makeMmuGate(0, { currentMaterial: 'PLA', currentFilamentColor: '#FF0000' }),
      makeMmuGate(1),
    ];

    render(<AmsSlotVisualization toolheads={toolheads} compact />);

    // In compact mode, material labels are hidden
    expect(screen.queryByText('PLA')).toBeNull();
    // But the slot still renders
    expect(screen.getByTestId('ams-slot-0')).toBeDefined();
  });

  it('renders tooltip content with material details on hover', async () => {
    const user = userEvent.setup();
    const toolheads: ToolheadDto[] = [
      makeMmuGate(0, {
        name: 'Bambu PLA Basic',
        currentMaterial: 'PLA',
        currentFilamentColor: '#FF5733',
        currentSpoolId: 101,
      }),
    ];

    render(<AmsSlotVisualization toolheads={toolheads} />);

    const slot = screen.getByTestId('ams-slot-0');
    await user.hover(slot.parentElement!);

    // Tooltip should appear with material details
    expect(await screen.findByText('Bambu PLA Basic')).toBeDefined();
    expect(screen.getByText('#FF5733')).toBeDefined();
    expect(screen.getByText('Spool #101')).toBeDefined();
  });

  it('handles 12 slots as 3 AMS units', () => {
    const toolheads: ToolheadDto[] = Array.from({ length: 12 }, (_, i) =>
      makeMmuGate(i, { currentMaterial: `M${i}`, currentFilamentColor: '#AABBCC' }),
    );

    render(<AmsSlotVisualization toolheads={toolheads} />);

    expect(screen.getByText('AMS 1')).toBeDefined();
    expect(screen.getByText('AMS 2')).toBeDefined();
    expect(screen.getByText('AMS 3')).toBeDefined();
  });

  it('adds visible border to light-colored filaments', () => {
    const toolheads: ToolheadDto[] = [
      makeMmuGate(0, { currentMaterial: 'White PLA', currentFilamentColor: '#FFFFFF' }),
    ];

    render(<AmsSlotVisualization toolheads={toolheads} />);

    const swatch = screen.getByTestId('slot-color-0');
    expect(swatch.className).toContain('border');
  });
});

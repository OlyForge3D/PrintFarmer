import '@testing-library/jest-dom';
import { render, screen, fireEvent } from '@testing-library/react';
import { describe, it, expect, vi } from 'vitest';
import { ToolheadScopePicker } from '../ToolheadScopePicker';
import {
  PRINTER_WIDE_SCOPE,
  toolheadIdFromScope,
  scopeFromToolheadId,
} from '../toolheadScope';
import type { MaintenanceEligibleToolhead } from '@/features/printers/utils/isEligibleMaintenanceToolhead';

function th(
  id: string,
  overrides: Partial<MaintenanceEligibleToolhead> = {}
): MaintenanceEligibleToolhead {
  return {
    id,
    index: overrides.index ?? 0,
    isPrimary: overrides.isPrimary ?? false,
    toolheadType: overrides.toolheadType ?? 'Physical',
    name: overrides.name,
    supportsMaintenanceScope: overrides.supportsMaintenanceScope,
  };
}

describe('toolheadIdFromScope / scopeFromToolheadId', () => {
  it('round-trips printer-wide via null', () => {
    expect(toolheadIdFromScope(PRINTER_WIDE_SCOPE)).toBeNull();
    expect(scopeFromToolheadId(null)).toBe(PRINTER_WIDE_SCOPE);
    expect(scopeFromToolheadId(undefined)).toBe(PRINTER_WIDE_SCOPE);
  });

  it('round-trips a specific toolhead id', () => {
    expect(toolheadIdFromScope('t-1')).toBe('t-1');
    expect(scopeFromToolheadId('t-1')).toBe('t-1');
  });
});

describe('ToolheadScopePicker', () => {
  it('collapses to static printer-wide text when the printer has one eligible toolhead', () => {
    render(
      <ToolheadScopePicker
        toolheads={[th('t-0', { toolheadType: 'Physical' })]}
        value={PRINTER_WIDE_SCOPE}
        onChange={() => {}}
      />
    );
    expect(screen.queryByRole('group')).not.toBeInTheDocument();
    expect(screen.getByText(/only one maintenance target/i)).toBeInTheDocument();
  });

  it('collapses to static printer-wide text when the printer has no toolheads', () => {
    render(<ToolheadScopePicker toolheads={[]} value={PRINTER_WIDE_SCOPE} onChange={() => {}} />);
    expect(screen.queryByRole('radiogroup')).not.toBeInTheDocument();
  });

  it('renders Printer-wide + one radio per eligible physical toolhead', () => {
    render(
      <ToolheadScopePicker
        toolheads={[
          th('t-0', { index: 0, name: 'Left' }),
          th('t-1', { index: 1, name: 'Right' }),
        ]}
        value={PRINTER_WIDE_SCOPE}
        onChange={() => {}}
      />
    );

    const radios = screen.getAllByRole('radio');
    expect(radios).toHaveLength(3);
    expect(screen.getByLabelText('Printer-wide')).toBeChecked();
    expect(screen.getByLabelText(/T0 · Left/)).toBeInTheDocument();
    expect(screen.getByLabelText(/T1 · Right/)).toBeInTheDocument();
  });

  it('excludes MMU/AMS gate toolheads by default (#719 acceptance)', () => {
    render(
      <ToolheadScopePicker
        toolheads={[
          th('t-0', { index: 0, name: 'Left', toolheadType: 'Physical' }),
          th('t-1', { index: 1, name: 'Right', toolheadType: 'Physical' }),
          th('g-0', { index: 2, name: 'Gate 0', toolheadType: 'MmuGate' }),
          th('g-1', { index: 3, name: 'Gate 1', toolheadType: 'MmuGate' }),
        ]}
        value={PRINTER_WIDE_SCOPE}
        onChange={() => {}}
      />
    );
    expect(screen.getAllByRole('radio')).toHaveLength(3);
    expect(screen.queryByLabelText(/Gate 0/)).not.toBeInTheDocument();
    expect(screen.queryByLabelText(/Gate 1/)).not.toBeInTheDocument();
  });

  it('includes MMU gate when API explicitly marks it eligible', () => {
    render(
      <ToolheadScopePicker
        toolheads={[
          th('t-0', { index: 0, name: 'Left' }),
          th('t-1', { index: 1, name: 'Right' }),
          th('g-0', {
            index: 2,
            name: 'Feeder',
            toolheadType: 'MmuGate',
            supportsMaintenanceScope: true,
          }),
        ]}
        value={PRINTER_WIDE_SCOPE}
        onChange={() => {}}
      />
    );
    expect(screen.getByLabelText(/Feeder/)).toBeInTheDocument();
  });

  it('emits the toolhead id when a specific toolhead is selected', () => {
    const onChange = vi.fn();
    render(
      <ToolheadScopePicker
        toolheads={[th('t-0', { index: 0, name: 'Left' }), th('t-1', { index: 1, name: 'Right' })]}
        value={PRINTER_WIDE_SCOPE}
        onChange={onChange}
      />
    );
    fireEvent.click(screen.getByLabelText(/T1 · Right/));
    expect(onChange).toHaveBeenCalledWith('t-1');
    expect(toolheadIdFromScope(onChange.mock.calls[0][0])).toBe('t-1');
  });

  it('emits the printer-wide sentinel when Printer-wide is chosen', () => {
    const onChange = vi.fn();
    render(
      <ToolheadScopePicker
        toolheads={[th('t-0', { index: 0, name: 'Left' }), th('t-1', { index: 1, name: 'Right' })]}
        value="t-1"
        onChange={onChange}
      />
    );
    fireEvent.click(screen.getByLabelText('Printer-wide'));
    expect(onChange).toHaveBeenCalledWith(PRINTER_WIDE_SCOPE);
    expect(toolheadIdFromScope(onChange.mock.calls[0][0])).toBeNull();
  });

  it('associates a visible label and helper text with the radiogroup for a11y', () => {
    render(
      <ToolheadScopePicker
        toolheads={[th('t-0', { index: 0 }), th('t-1', { index: 1 })]}
        value={PRINTER_WIDE_SCOPE}
        onChange={() => {}}
        label="Log scope"
        helperText="Choose which toolhead this maintenance applies to."
      />
    );
    const group = screen.getByRole('group', { name: 'Log scope' });
    expect(group).toBeInTheDocument();
    expect(group).toHaveAccessibleDescription(/Choose which toolhead/);
  });

  it('honors disabled prop', () => {
    render(
      <ToolheadScopePicker
        toolheads={[th('t-0', { index: 0 }), th('t-1', { index: 1 })]}
        value={PRINTER_WIDE_SCOPE}
        onChange={() => {}}
        disabled
      />
    );
    for (const radio of screen.getAllByRole('radio')) {
      expect(radio).toBeDisabled();
    }
  });
});

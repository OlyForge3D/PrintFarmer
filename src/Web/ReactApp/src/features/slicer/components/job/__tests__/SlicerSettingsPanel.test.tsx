/**
 * Unit tests for SlicerSettingsPanel (Simple mode controls).
 * Covers: infill %, infill pattern, top/bottom layers, perimeters,
 * support toggle + type, and bed adhesion radio buttons.
 */
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import '@testing-library/jest-dom';
import React from 'react';
import { SlicerSettingsPanel, type SlicerSettings } from '../SlicerSettingsPanel';

vi.mock('@/features/slicer/components/settings/metadataTypes', () => ({
  INFILL_PATTERNS: [
    { value: 'grid', label: 'Grid' },
    { value: 'gyroid', label: 'Gyroid' },
    { value: 'cubic', label: 'Cubic' },
    { value: 'honeycomb', label: 'Honeycomb' },
  ],
}));

vi.mock('@/common/components/ui', () => ({
  Checkbox: ({ id, checked, onChange }: { id: string; checked: boolean; onChange: (e: React.ChangeEvent<HTMLInputElement>) => void }) => (
    <input type="checkbox" id={id} checked={checked} onChange={onChange} />
  ),
  Select: ({ children, ...rest }: React.SelectHTMLAttributes<HTMLSelectElement> & { children: React.ReactNode }) => (
    <select {...rest}>{children}</select>
  ),
}));

const DEFAULT_SETTINGS: SlicerSettings = {
  layerHeight: 0.2,
  infillPercent: 15,
  infillPattern: 'grid',
  topShellLayers: 4,
  bottomShellLayers: 3,
  wallLoops: 2,
  supportEnabled: false,
  supportType: 'normal(auto)',
  bedAdhesionType: 'none',
};

function renderPanel(partial?: Partial<SlicerSettings>, onChange = vi.fn()) {
  const settings = { ...DEFAULT_SETTINGS, ...partial };
  return { onChange, ...render(<SlicerSettingsPanel settings={settings} onSettingsChange={onChange} simpleMode />)};
}

describe('SlicerSettingsPanel — perimeters / shell layers', () => {
  it('renders perimeters (wall loops) input with current value', () => {
    renderPanel({ wallLoops: 3 });
    const input = screen.getByLabelText(/perimeters.*wall loops/i) as HTMLInputElement;
    expect(input.value).toBe('3');
  });

  it('calls onSettingsChange with updated wallLoops', () => {
    const { onChange } = renderPanel({ wallLoops: 2 });
    fireEvent.change(screen.getByLabelText(/perimeters.*wall loops/i), { target: { value: '4' } });
    expect(onChange).toHaveBeenCalledWith(expect.objectContaining({ wallLoops: 4 }));
  });

  it('renders top shell layers input with current value', () => {
    renderPanel({ topShellLayers: 5 });
    const input = screen.getByLabelText(/top solid layers/i) as HTMLInputElement;
    expect(input.value).toBe('5');
  });

  it('calls onSettingsChange with updated topShellLayers', () => {
    const { onChange } = renderPanel({ topShellLayers: 4 });
    fireEvent.change(screen.getByLabelText(/top solid layers/i), { target: { value: '6' } });
    expect(onChange).toHaveBeenCalledWith(expect.objectContaining({ topShellLayers: 6 }));
  });

  it('renders bottom shell layers input with current value', () => {
    renderPanel({ bottomShellLayers: 2 });
    const input = screen.getByLabelText(/bottom solid layers/i) as HTMLInputElement;
    expect(input.value).toBe('2');
  });

  it('calls onSettingsChange with updated bottomShellLayers', () => {
    const { onChange } = renderPanel({ bottomShellLayers: 3 });
    fireEvent.change(screen.getByLabelText(/bottom solid layers/i), { target: { value: '5' } });
    expect(onChange).toHaveBeenCalledWith(expect.objectContaining({ bottomShellLayers: 5 }));
  });
});

describe('SlicerSettingsPanel — infill', () => {
  it('renders infill percentage slider with current value', () => {
    renderPanel({ infillPercent: 20 });
    const slider = screen.getByLabelText(/infill percentage$/i) as HTMLInputElement;
    expect(slider.value).toBe('20');
  });

  it('calls onSettingsChange with updated infillPercent via slider', () => {
    const { onChange } = renderPanel({ infillPercent: 15 });
    fireEvent.change(screen.getByLabelText(/infill percentage$/i), { target: { value: '40' } });
    expect(onChange).toHaveBeenCalledWith(expect.objectContaining({ infillPercent: 40 }));
  });

  it('renders the infill pattern select and preview icon', () => {
    renderPanel({ infillPattern: 'grid' });
    const trigger = screen.getByRole('combobox', { name: /infill pattern grid/i });
    expect(trigger).toBeInTheDocument();
    expect(trigger.querySelector('img')?.getAttribute('src')).toContain('/icons/orca/param_grid.svg');
  });

  it('opens an icon-backed listbox of infill patterns', () => {
    renderPanel();
    fireEvent.click(screen.getByRole('combobox', { name: /infill pattern grid/i }));
    const options = screen.getAllByRole('option');
    expect(options).toHaveLength(4);
    expect(options[1].querySelector('img')?.getAttribute('src')).toContain('/icons/orca/param_gyroid.svg');
    expect(screen.queryByRole('button', { name: /gyroid/i })).not.toBeInTheDocument();
  });

  it('calls onSettingsChange with updated infillPattern', () => {
    const { onChange } = renderPanel({ infillPattern: 'grid' });
    fireEvent.click(screen.getByRole('combobox', { name: /infill pattern grid/i }));
    fireEvent.click(screen.getByRole('option', { name: /gyroid/i }));
    expect(onChange).toHaveBeenCalledWith(expect.objectContaining({ infillPattern: 'gyroid' }));
  });

  it('supports keyboard selection with arrow keys and enter', () => {
    const { onChange } = renderPanel({ infillPattern: 'grid' });
    const trigger = screen.getByRole('combobox', { name: /infill pattern grid/i });
    fireEvent.keyDown(trigger, { key: 'ArrowDown' });
    expect(screen.getByRole('listbox')).toBeInTheDocument();
    fireEvent.keyDown(trigger, { key: 'ArrowDown' });
    fireEvent.keyDown(trigger, { key: 'Enter' });
    expect(onChange).toHaveBeenCalledWith(expect.objectContaining({ infillPattern: 'gyroid' }));
  });

  it('closes the listbox on escape and tab', () => {
    renderPanel();
    const trigger = screen.getByRole('combobox', { name: /infill pattern grid/i });
    fireEvent.keyDown(trigger, { key: 'ArrowDown' });
    expect(screen.getByRole('listbox')).toBeInTheDocument();
    fireEvent.keyDown(trigger, { key: 'Escape' });
    expect(screen.queryByRole('listbox')).not.toBeInTheDocument();
    fireEvent.keyDown(trigger, { key: 'ArrowDown' });
    expect(screen.getByRole('listbox')).toBeInTheDocument();
    fireEvent.keyDown(trigger, { key: 'Tab' });
    expect(screen.queryByRole('listbox')).not.toBeInTheDocument();
  });
});

describe('SlicerSettingsPanel — supports', () => {
  it('renders support enabled checkbox unchecked by default', () => {
    renderPanel({ supportEnabled: false });
    const checkbox = screen.getByRole('checkbox');
    expect(checkbox).not.toBeChecked();
  });

  it('does not render support type selector when supports are disabled', () => {
    renderPanel({ supportEnabled: false });
    expect(screen.queryByLabelText(/support type/i)).not.toBeInTheDocument();
  });

  it('renders support type selector when supports are enabled', () => {
    renderPanel({ supportEnabled: true, supportType: 'tree(auto)' });
    const select = screen.getByLabelText(/support type/i) as HTMLSelectElement;
    expect(select.value).toBe('tree(auto)');
    expect(Array.from(select.options).map((option) => option.textContent)).toEqual(['Normal', 'Tree']);
  });

  it('calls onSettingsChange with updated supportType', () => {
    const { onChange } = renderPanel({ supportEnabled: true, supportType: 'normal(auto)' });
    fireEvent.change(screen.getByLabelText(/support type/i), { target: { value: 'tree(auto)' } });
    expect(onChange).toHaveBeenCalledWith(expect.objectContaining({ supportType: 'tree(auto)' }));
  });

  it('calls onSettingsChange with supportEnabled=true when checkbox is checked', () => {
    const { onChange } = renderPanel({ supportEnabled: false });
    fireEvent.click(screen.getByRole('checkbox'));
    expect(onChange).toHaveBeenCalledWith(expect.objectContaining({ supportEnabled: true }));
  });
});

describe('SlicerSettingsPanel — bed adhesion radio buttons', () => {
  beforeEach(() => vi.clearAllMocks());

  it('renders four bed adhesion radio buttons', () => {
    renderPanel({ bedAdhesionType: 'none' });
    const radios = screen.getAllByRole('radio');
    expect(radios).toHaveLength(4);
  });

  it('has the correct radio checked for current value', () => {
    renderPanel({ bedAdhesionType: 'brim' });
    const brimRadio = screen.getByRole('radio', { name: /^brim$/i }) as HTMLInputElement;
    expect(brimRadio.checked).toBe(true);
    const skirtRadio = screen.getByRole('radio', { name: /^skirt$/i }) as HTMLInputElement;
    expect(skirtRadio.checked).toBe(false);
  });

  it('calls onSettingsChange with bedAdhesionType=skirt when skirt radio is clicked', () => {
    const { onChange } = renderPanel({ bedAdhesionType: 'none' });
    fireEvent.click(screen.getByRole('radio', { name: /^skirt$/i }));
    expect(onChange).toHaveBeenCalledWith(expect.objectContaining({ bedAdhesionType: 'skirt' }));
  });

  it('calls onSettingsChange with bedAdhesionType=brim when brim radio is clicked', () => {
    const { onChange } = renderPanel({ bedAdhesionType: 'none' });
    fireEvent.click(screen.getByRole('radio', { name: /^brim$/i }));
    expect(onChange).toHaveBeenCalledWith(expect.objectContaining({ bedAdhesionType: 'brim' }));
  });

  it('calls onSettingsChange with bedAdhesionType=raft when raft radio is clicked', () => {
    const { onChange } = renderPanel({ bedAdhesionType: 'none' });
    fireEvent.click(screen.getByRole('radio', { name: /^raft$/i }));
    expect(onChange).toHaveBeenCalledWith(expect.objectContaining({ bedAdhesionType: 'raft' }));
  });

  it('calls onSettingsChange with bedAdhesionType=none when none radio is clicked', () => {
    const { onChange } = renderPanel({ bedAdhesionType: 'brim' });
    fireEvent.click(screen.getByRole('radio', { name: /^none$/i }));
    expect(onChange).toHaveBeenCalledWith(expect.objectContaining({ bedAdhesionType: 'none' }));
  });
});

describe('SlicerSettingsPanel — simpleMode hides layer height', () => {
  it('hides layer height input in simpleMode', () => {
    render(
      <SlicerSettingsPanel
        settings={DEFAULT_SETTINGS}
        onSettingsChange={vi.fn()}
        simpleMode
      />
    );
    expect(screen.queryByLabelText(/layer height in mm/i)).not.toBeInTheDocument();
  });

  it('shows layer height input when simpleMode is false', () => {
    render(
      <SlicerSettingsPanel
        settings={DEFAULT_SETTINGS}
        onSettingsChange={vi.fn()}
        simpleMode={false}
      />
    );
    expect(screen.getByLabelText(/layer height in mm/i)).toBeInTheDocument();
  });
});

describe('SlicerSettingsPanel — input clamping', () => {
  it('clamps wallLoops to minimum 1 when 0 is entered', () => {
    const { onChange } = renderPanel({ wallLoops: 2 });
    fireEvent.change(screen.getByLabelText(/perimeters.*wall loops/i), { target: { value: '0' } });
    expect(onChange).toHaveBeenCalledWith(expect.objectContaining({ wallLoops: 1 }));
  });

  it('clamps wallLoops to minimum 1 when negative value is entered', () => {
    const { onChange } = renderPanel({ wallLoops: 2 });
    fireEvent.change(screen.getByLabelText(/perimeters.*wall loops/i), { target: { value: '-3' } });
    expect(onChange).toHaveBeenCalledWith(expect.objectContaining({ wallLoops: 1 }));
  });

  it('clamps topShellLayers to minimum 0 when negative value is entered', () => {
    const { onChange } = renderPanel({ topShellLayers: 4 });
    fireEvent.change(screen.getByLabelText(/top solid layers/i), { target: { value: '-1' } });
    expect(onChange).toHaveBeenCalledWith(expect.objectContaining({ topShellLayers: 0 }));
  });

  it('clamps bottomShellLayers to minimum 0 when negative value is entered', () => {
    const { onChange } = renderPanel({ bottomShellLayers: 3 });
    fireEvent.change(screen.getByLabelText(/bottom solid layers/i), { target: { value: '-5' } });
    expect(onChange).toHaveBeenCalledWith(expect.objectContaining({ bottomShellLayers: 0 }));
  });

  it('clamps infillPercent to 100 when value above max is entered via number input', () => {
    const { onChange } = renderPanel({ infillPercent: 50 });
    fireEvent.change(screen.getByLabelText(/infill percentage value/i), { target: { value: '150' } });
    expect(onChange).toHaveBeenCalledWith(expect.objectContaining({ infillPercent: 100 }));
  });

  it('clamps infillPercent to 0 when negative value is entered via number input', () => {
    const { onChange } = renderPanel({ infillPercent: 50 });
    fireEvent.change(screen.getByLabelText(/infill percentage value/i), { target: { value: '-10' } });
    expect(onChange).toHaveBeenCalledWith(expect.objectContaining({ infillPercent: 0 }));
  });
});

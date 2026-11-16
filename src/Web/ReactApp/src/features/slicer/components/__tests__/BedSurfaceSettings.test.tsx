import '@testing-library/jest-dom';
import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { BedSurfaceSettings } from '@/features/slicer/components/BedSurfaceSettings';

describe('BedSurfaceSettings', () => {
  it('shows distinct first/later-layer profile values, zero warning and per-extruder data without aggregation', () => {
    const props = {
      id: 'test-bed',
      selectedBedType: '',
      onChange: vi.fn(),
      machineSettings: { curr_bed_type: 'Textured PEI Plate', support_multi_bed_types: '0', bed_temperature_formula: 'by_highest_temp' },
      filaments: [
        { name: 'Micron PLA', settings: { hot_plate_temp: ['0'], textured_plate_temp_initial_layer: ['65'], textured_plate_temp: ['60'] } },
        { name: 'Unheated', settings: { textured_plate_temp_initial_layer: ['0'], textured_plate_temp: ['0'] } },
      ],
    };
    const { rerender } = render(<BedSurfaceSettings {...props} />);
    expect(screen.getByText('Bed first layer: 65°C; other layers: 60°C')).toBeInTheDocument();
    expect(screen.getByText('Bed first layer: 0°C; other layers: 0°C')).toBeInTheDocument();
    expect(screen.getByText(/Profile specifies 0°C/)).toBeInTheDocument();
    expect(screen.getByText('Extruder 2: Unheated')).toBeInTheDocument();
    fireEvent.change(screen.getByLabelText('Bed Type'), { target: { value: 'High Temp Plate' } });
    expect(props.onChange).toHaveBeenCalledWith('High Temp Plate');
    rerender(<BedSurfaceSettings {...props} selectedBedType="High Temp Plate" />);
    expect(screen.getByText('Bed first layer: Unknown; other layers: 0°C')).toBeInTheDocument();
    expect(screen.queryByText('Bed first layer: 65°C; other layers: 60°C')).not.toBeInTheDocument();
  });

  it('is honest about unavailable machine and filament context', () => {
    render(<BedSurfaceSettings id="unknown-bed" selectedBedType="" onChange={vi.fn()} filaments={[undefined]} />);
    expect(screen.getByText('Bed surface unknown until resolved by OrcaSlicer.')).toBeInTheDocument();
    expect(screen.getByText('Bed first layer: Unknown; other layers: Unknown')).toBeInTheDocument();
    expect(screen.queryByText(/60°C/)).not.toBeInTheDocument();
  });
});

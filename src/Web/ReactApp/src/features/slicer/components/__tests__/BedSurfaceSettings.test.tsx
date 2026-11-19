import '@testing-library/jest-dom';
import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { BedSurfaceSettings } from '@/features/slicer/components/BedSurfaceSettings';

describe('BedSurfaceSettings', () => {
  it('keeps the labeled selector and shows profile values for multi-bed profiles', () => {
    const props = {
      id: 'test-bed',
      selectedBedType: '',
      onChange: vi.fn(),
      context: { bedType: 'Textured PEI Plate', supportMultiBedTypes: true },
      filaments: [{
        name: 'PLA',
        settings: {
          textured_plate_temp_initial_layer: ['65'],
          textured_plate_temp: ['60'],
        },
      }],
    };
    render(<BedSurfaceSettings {...props} />);
    expect(screen.getByRole('option', { name: 'Inherit from profile (Textured PEI Plate)' })).toBeInTheDocument();
    expect(screen.getByText('Bed first layer: 65°C; other layers: 60°C')).toBeInTheDocument();
    expect(screen.getByText(/Profile values only/)).toBeInTheDocument();
    fireEvent.change(screen.getByLabelText('Bed Type'), { target: { value: 'High Temp Plate' } });
    expect(props.onChange).toHaveBeenCalledWith('High Temp Plate');
  });

  it('hides the selector for single-bed profiles but retains the temperature summary and warning', () => {
    render(
      <BedSurfaceSettings
        id="single-bed"
        selectedBedType=""
        onChange={vi.fn()}
        context={{ bedType: 'Textured PEI Plate', supportMultiBedTypes: false }}
        filaments={[{ name: 'PLA', settings: {
          textured_plate_temp_initial_layer: ['0'],
          textured_plate_temp: ['0'],
        } }]}
      />,
    );
    expect(screen.queryByLabelText('Bed Type')).not.toBeInTheDocument();
    expect(screen.getByText(/Bed first layer: 0°C; other layers: 0°C/)).toBeInTheDocument();
    expect(screen.getByText(/Profile specifies 0°C/)).toBeInTheDocument();
    expect(screen.getByText(/unknown values are not replaced/)).toBeInTheDocument();
  });
});

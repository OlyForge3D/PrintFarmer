import '@testing-library/jest-dom';
import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { BedSurfaceSettings } from '@/features/slicer/components/BedSurfaceSettings';

describe('BedSurfaceSettings', () => {
  it('shows only the labeled selector for multi-bed profiles, with no temperature text', () => {
    const onChange = vi.fn();
    const { container } = render(
      <BedSurfaceSettings
        id="test-bed"
        selectedBedType=""
        onChange={onChange}
        context={{ bedType: 'Textured PEI Plate', supportMultiBedTypes: true }}
      />,
    );
    expect(screen.getByRole('option', { name: 'Inherit from profile (Textured PEI Plate)' })).toBeInTheDocument();
    expect(container).not.toHaveTextContent(/°C/);
    expect(screen.queryByRole('button', { name: 'About bed temperatures' })).not.toBeInTheDocument();
    fireEvent.change(screen.getByLabelText('Bed Type'), { target: { value: 'High Temp Plate' } });
    expect(onChange).toHaveBeenCalledWith('High Temp Plate');
  });

  it('renders nothing for single-bed profiles', () => {
    const { container } = render(
      <BedSurfaceSettings
        id="single-bed"
        selectedBedType=""
        onChange={vi.fn()}
        context={{ bedType: 'Textured PEI Plate', supportMultiBedTypes: false }}
      />,
    );
    expect(container).toBeEmptyDOMElement();
  });
});

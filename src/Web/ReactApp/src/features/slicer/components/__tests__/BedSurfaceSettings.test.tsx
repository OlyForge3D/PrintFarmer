import '@testing-library/jest-dom';
import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { BedSurfaceSettings } from '@/features/slicer/components/BedSurfaceSettings';

describe('BedSurfaceSettings', () => {
  it('keeps the labeled selector and inherited option for multi-bed profiles without helper text', () => {
    const props = {
      id: 'test-bed',
      selectedBedType: '',
      onChange: vi.fn(),
      context: { bedType: 'Textured PEI Plate', supportMultiBedTypes: true },
    };
    const { container } = render(<BedSurfaceSettings {...props} />);
    expect(screen.getByRole('option', { name: 'Inherit from profile (Textured PEI Plate)' })).toBeInTheDocument();
    expect(container.querySelector('p')).toBeNull();
    fireEvent.change(screen.getByLabelText('Bed Type'), { target: { value: 'High Temp Plate' } });
    expect(props.onChange).toHaveBeenCalledWith('High Temp Plate');
  });

  it('does not render a label or control for a single-bed profile', () => {
    const { container } = render(<BedSurfaceSettings id="single-bed" selectedBedType="" onChange={vi.fn()} context={{ supportMultiBedTypes: false }} />);
    expect(container).toBeEmptyDOMElement();
  });
});

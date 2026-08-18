import { render } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { SpoolCard } from '@/features/filamentManagement/components/SpoolCard';
import type { SpoolmanSpoolDto } from '@/features/filamentManagement/types';

function makeSpool(overrides: Partial<SpoolmanSpoolDto> = {}): SpoolmanSpoolDto {
  return {
    id: 1,
    name: 'Test Spool',
    material: 'PLA',
    inUse: false,
    ...overrides,
  };
}

describe('SpoolCard', () => {
  it('renders the swatch with the spool\'s own colorHex, not a generic family color', () => {
    // Regression test for #1695: the swatch previously rendered
    // getRepresentativeHex(classifyColor(colorHex)) — a fixed color per family — instead
    // of the spool's actual DB color value. Two dark-red spools with different exact hex
    // values were both collapsed into the same generic "Red" swatch (#ef4444).
    const spool = makeSpool({ colorHex: '#7f1d1d' });

    render(
      <SpoolCard
        spool={spool}
        isSelected={false}
        onToggleSelect={vi.fn()}
        onEdit={vi.fn()}
        onClone={vi.fn()}
        onDelete={vi.fn()}
        onPrintLabel={vi.fn()}
      />
    );

    const swatch = document.querySelector<HTMLElement>('.color-swatch');
    expect(swatch).not.toBeNull();
    expect(swatch!.style.getPropertyValue('--swatch-color')).toBe('#7f1d1d');
    // Must not have been coerced to the generic family-representative red.
    expect(swatch!.style.getPropertyValue('--swatch-color')).not.toBe('#ef4444');
  });

  it('falls back to a neutral color when colorHex is missing', () => {
    const spool = makeSpool({ colorHex: null });

    render(
      <SpoolCard
        spool={spool}
        isSelected={false}
        onToggleSelect={vi.fn()}
        onEdit={vi.fn()}
        onClone={vi.fn()}
        onDelete={vi.fn()}
        onPrintLabel={vi.fn()}
      />
    );

    const swatch = document.querySelector<HTMLElement>('.color-swatch');
    expect(swatch).not.toBeNull();
    expect(swatch!.style.getPropertyValue('--swatch-color')).toBe('#888888');
  });
});

import { describe, expect, it } from 'vitest';
import { colorDistance, hexToLab, INVALID_COLOR_DISTANCE } from '../colorDistance';

describe('colorDistance', () => {
  it('returns zero for exact color matches', () => {
    expect(colorDistance('#FF0000', '#ff0000')).toBeCloseTo(0, 6);
  });

  it('uses perceptual Lab distance for nearby colors', () => {
    const nearRed = colorDistance('#FF0000', '#F90000');
    const blue = colorDistance('#FF0000', '#0000FF');

    expect(nearRed).toBeGreaterThan(0);
    expect(nearRed).toBeLessThan(blue);
  });

  it('converts valid hex values to Lab', () => {
    const lab = hexToLab('#FFFFFF');

    expect(lab).not.toBeNull();
    expect(lab?.l).toBeCloseTo(100, 1);
  });

  it('returns a sentinel distance for invalid or empty colors', () => {
    expect(hexToLab('')).toBeNull();
    expect(hexToLab('not-a-color')).toBeNull();
    expect(colorDistance('', '#FFFFFF')).toBe(INVALID_COLOR_DISTANCE);
    expect(colorDistance('#FFFFFF', undefined)).toBe(INVALID_COLOR_DISTANCE);
  });
});

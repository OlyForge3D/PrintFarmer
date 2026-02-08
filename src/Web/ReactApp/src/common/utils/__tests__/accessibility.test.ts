import { describe, it, expect } from 'vitest';
import {
  getLuminance,
  getContrastRatio,
  checkWCAGCompliance,
  ColorBlindnessSimulation,
  generateAccessiblePalette,
} from '../accessibility';

describe('accessibility utilities', () => {
  describe('getLuminance', () => {
    it('should calculate luminance for black', () => {
      expect(getLuminance('#000000')).toBe(0);
    });

    it('should calculate luminance for white', () => {
      expect(getLuminance('#ffffff')).toBeCloseTo(1, 1);
    });

    it('should handle hex without hash', () => {
      const result = getLuminance('ff0000');
      expect(result).toBeGreaterThan(0);
    });

    it('should throw error for invalid hex', () => {
      expect(() => getLuminance('#fff')).toThrow('Invalid hex color format');
      expect(() => getLuminance('#12345')).toThrow('Invalid hex color format');
    });
  });

  describe('getContrastRatio', () => {
    it('should return 21 for black on white', () => {
      expect(getContrastRatio('#000000', '#ffffff')).toBeCloseTo(21, 0);
    });

    it('should return 1 for same colors', () => {
      expect(getContrastRatio('#808080', '#808080')).toBeCloseTo(1, 1);
    });

    it('should be symmetric', () => {
      const ratio1 = getContrastRatio('#ff0000', '#00ff00');
      const ratio2 = getContrastRatio('#00ff00', '#ff0000');
      expect(ratio1).toBeCloseTo(ratio2, 2);
    });
  });

  describe('checkWCAGCompliance', () => {
    it('should pass AA for black on white', () => {
      const result = checkWCAGCompliance('#000000', '#ffffff');
      expect(result.passes).toBe(true);
      expect(result.ratio).toBeGreaterThan(4.5);
    });

    it('should fail AA for low contrast', () => {
      const result = checkWCAGCompliance('#888888', '#999999');
      expect(result.passes).toBe(false);
    });

    it('should have different requirements for large text', () => {
      const normal = checkWCAGCompliance('#767676', '#ffffff', 'AA', 'normal');
      const large = checkWCAGCompliance('#767676', '#ffffff', 'AA', 'large');
      expect(large.required).toBeLessThan(normal.required);
    });

    it('should have stricter requirements for AAA', () => {
      const aa = checkWCAGCompliance('#767676', '#ffffff', 'AA', 'normal');
      const aaa = checkWCAGCompliance('#767676', '#ffffff', 'AAA', 'normal');
      expect(aaa.required).toBeGreaterThan(aa.required);
    });
  });

  describe('ColorBlindnessSimulation', () => {
    it('should simulate protanopia', () => {
      const result = ColorBlindnessSimulation.protanopia('#ff0000');
      expect(result).toMatch(/^#[0-9a-f]{6}$/i);
    });

    it('should simulate deuteranopia', () => {
      const result = ColorBlindnessSimulation.deuteranopia('#00ff00');
      expect(result).toMatch(/^#[0-9a-f]{6}$/i);
    });

    it('should simulate tritanopia', () => {
      const result = ColorBlindnessSimulation.tritanopia('#0000ff');
      expect(result).toMatch(/^#[0-9a-f]{6}$/i);
    });
  });

  describe('generateAccessiblePalette', () => {
    it('should generate suggestions for low contrast', () => {
      const suggestions = generateAccessiblePalette('#888888', '#999999');
      expect(Array.isArray(suggestions)).toBe(true);
    });

    it('should return at most 5 suggestions', () => {
      const suggestions = generateAccessiblePalette('#ff0000', '#ffffff');
      expect(suggestions.length).toBeLessThanOrEqual(5);
    });

    it('should only suggest colors meeting 4.5:1 ratio', () => {
      const suggestions = generateAccessiblePalette('#888888', '#999999');
      suggestions.forEach(s => {
        expect(s.ratio).toBeGreaterThanOrEqual(4.5);
      });
    });
  });
});

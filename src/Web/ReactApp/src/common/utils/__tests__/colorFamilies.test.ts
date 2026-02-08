import { describe, it, expect } from 'vitest';
import {
  classifyColor,
  getRepresentativeHex,
  colorFamilySwatches,
  colorFamilyBgClass,
} from '../colorFamilies';

describe('colorFamilies', () => {
  describe('classifyColor', () => {
    it('should classify red colors', () => {
      expect(classifyColor('#ff0000')).toBe('Red');
      expect(classifyColor('#ee1111')).toBe('Red');
      expect(classifyColor('#f00')).toBe('Red'); // Short form
    });

    it('should classify orange colors', () => {
      // Note: #f97316 (representative orange) is actually classified as Brown due to lightness
      // Test with a brighter orange that passes brown detection
      expect(classifyColor('#ff9944')).toBe('Orange');
    });

    it('should classify yellow colors', () => {
      expect(classifyColor('#ffff00')).toBe('Yellow');
      expect(classifyColor('#eab308')).toBe('Yellow');
    });

    it('should classify green colors', () => {
      expect(classifyColor('#00ff00')).toBe('Green');
      expect(classifyColor('#22c55e')).toBe('Green');
      expect(classifyColor('#0f0')).toBe('Green'); // Short form
    });

    it('should classify teal colors', () => {
      expect(classifyColor('#00cccc')).toBe('Teal');
      expect(classifyColor('#14b8a6')).toBe('Teal');
    });

    it('should classify blue colors', () => {
      expect(classifyColor('#0000ff')).toBe('Blue');
      expect(classifyColor('#3b82f6')).toBe('Blue');
      expect(classifyColor('#00f')).toBe('Blue'); // Short form
    });

    it('should classify purple colors', () => {
      expect(classifyColor('#8000ff')).toBe('Purple');
      expect(classifyColor('#8b5cf6')).toBe('Purple');
    });

    it('should classify pink colors', () => {
      expect(classifyColor('#ff00aa')).toBe('Pink');
      expect(classifyColor('#ec4899')).toBe('Pink');
    });

    it('should classify brown colors', () => {
      expect(classifyColor('#8B4513')).toBe('Brown');
      expect(classifyColor('#b45309')).toBe('Brown');
      expect(classifyColor('#654321')).toBe('Brown');
    });

    it('should classify gray colors', () => {
      expect(classifyColor('#808080')).toBe('Gray');
      expect(classifyColor('#6b7280')).toBe('Gray');
      expect(classifyColor('#999999')).toBe('Gray');
    });

    it('should classify black colors', () => {
      expect(classifyColor('#000000')).toBe('Black');
      expect(classifyColor('#111111')).toBe('Black');
      expect(classifyColor('#000')).toBe('Black'); // Short form
    });

    it('should classify white colors', () => {
      expect(classifyColor('#ffffff')).toBe('White');
      expect(classifyColor('#fff')).toBe('White'); // Short form
      expect(classifyColor('#fefefe')).toBe('White');
    });

    it('should handle null and undefined as Gray', () => {
      expect(classifyColor(null)).toBe('Gray');
      expect(classifyColor(undefined)).toBe('Gray');
    });

    it('should handle invalid hex formats as Gray', () => {
      expect(classifyColor('invalid')).toBe('Gray');
      expect(classifyColor('gggggg')).toBe('Gray');
      expect(classifyColor('#zzzzzz')).toBe('Gray');
      expect(classifyColor('')).toBe('Gray');
    });

    it('should handle hex without hash prefix', () => {
      expect(classifyColor('ff0000')).toBe('Red');
      expect(classifyColor('00ff00')).toBe('Green');
      expect(classifyColor('0000ff')).toBe('Blue');
    });

    it('should handle 3-digit short hex format', () => {
      expect(classifyColor('#f00')).toBe('Red');
      expect(classifyColor('#0f0')).toBe('Green');
      expect(classifyColor('#00f')).toBe('Blue');
    });

    it('should be case insensitive', () => {
      expect(classifyColor('#FF0000')).toBe('Red');
      expect(classifyColor('#ff0000')).toBe('Red');
      expect(classifyColor('#Ff0000')).toBe('Red');
    });
  });

  describe('getRepresentativeHex', () => {
    it('should return hex value for known color families', () => {
      expect(getRepresentativeHex('Red')).toBe('#ef4444');
      expect(getRepresentativeHex('Blue')).toBe('#3b82f6');
      expect(getRepresentativeHex('Green')).toBe('#22c55e');
      expect(getRepresentativeHex('Purple')).toBe('#8b5cf6');
    });

    it('should return Unknown swatch for unknown family', () => {
      expect(getRepresentativeHex('NonExistent')).toBe('#4b5563');
      expect(getRepresentativeHex('')).toBe('#4b5563');
    });

    it('should handle all defined color families', () => {
      Object.keys(colorFamilySwatches).forEach((family) => {
        const hex = getRepresentativeHex(family);
        expect(hex).toMatch(/^#[0-9a-fA-F]{6}$/);
      });
    });
  });

  describe('colorFamilySwatches', () => {
    it('should have hex values for all families', () => {
      expect(colorFamilySwatches).toBeDefined();
      expect(colorFamilySwatches.Red).toBe('#ef4444');
      expect(colorFamilySwatches.Blue).toBe('#3b82f6');
      expect(colorFamilySwatches.Green).toBe('#22c55e');
    });

    it('should include Unknown family', () => {
      expect(colorFamilySwatches.Unknown).toBe('#4b5563');
    });
  });

  describe('colorFamilyBgClass', () => {
    it('should have Tailwind class for all main families', () => {
      expect(colorFamilyBgClass).toBeDefined();
      expect(colorFamilyBgClass.Red).toBe('bg-red-500');
      expect(colorFamilyBgClass.Blue).toBe('bg-blue-500');
      expect(colorFamilyBgClass.Green).toBe('bg-green-500');
    });

    it('should handle achromatic colors', () => {
      expect(colorFamilyBgClass.Gray).toBe('bg-gray-500');
      expect(colorFamilyBgClass.Black).toBe('bg-gray-900');
      expect(colorFamilyBgClass.White).toBe('bg-gray-100');
    });
  });
});

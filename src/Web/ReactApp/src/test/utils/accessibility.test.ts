import { describe, it, expect } from 'vitest';
import { 
  getLuminance, 
  getContrastRatio, 
  checkWCAGCompliance,
  ColorBlindnessSimulation 
} from '@/common/utils/accessibility';

describe('Accessibility Utils', () => {
  describe('getLuminance', () => {
    it('calculates luminance for white', () => {
      expect(getLuminance('#ffffff')).toBeCloseTo(1, 2);
    });

    it('calculates luminance for black', () => {
      expect(getLuminance('#000000')).toBeCloseTo(0, 2);
    });

    it('handles hex without hash', () => {
      expect(getLuminance('ffffff')).toBeCloseTo(1, 2);
    });

    it('throws error for invalid hex', () => {
      expect(() => getLuminance('#invalid')).toThrow('Invalid hex color format');
    });
  });

  describe('getContrastRatio', () => {
    it('calculates maximum contrast (white vs black)', () => {
      const ratio = getContrastRatio('#ffffff', '#000000');
      expect(ratio).toBeCloseTo(21, 1);
    });

    it('calculates minimum contrast (same colors)', () => {
      const ratio = getContrastRatio('#ffffff', '#ffffff');
      expect(ratio).toBe(1);
    });

    it('calculates PrintFarmer primary text contrast', () => {
      // Primary text (#e5e7eb) on dark background (#0b1020)
      const ratio = getContrastRatio('#e5e7eb', '#0b1020');
      expect(ratio).toBeGreaterThan(4.5); // Should meet WCAG AA
    });
  });

  describe('checkWCAGCompliance', () => {
    it('passes for high contrast combination', () => {
      const result = checkWCAGCompliance('#ffffff', '#000000');
      expect(result.passes).toBe(true);
      expect(result.ratio).toBeCloseTo(21, 1);
      expect(result.required).toBe(4.5);
    });

    it('fails for low contrast combination', () => {
      const result = checkWCAGCompliance('#888888', '#999999');
      expect(result.passes).toBe(false);
      expect(result.ratio).toBeLessThan(4.5);
    });

    it('applies different thresholds for large text', () => {
      const normal = checkWCAGCompliance('#888888', '#ffffff', 'AA', 'normal');
      const large = checkWCAGCompliance('#888888', '#ffffff', 'AA', 'large');
      
      expect(normal.required).toBe(4.5);
      expect(large.required).toBe(3);
    });

    it('applies AAA standards', () => {
      const aa = checkWCAGCompliance('#666666', '#ffffff', 'AA');
      const aaa = checkWCAGCompliance('#666666', '#ffffff', 'AAA');
      
      expect(aa.required).toBe(4.5);
      expect(aaa.required).toBe(7);
    });
  });

  describe('ColorBlindnessSimulation', () => {
    it('simulates protanopia', () => {
      const original = '#ff0000'; // Red
      const simulated = ColorBlindnessSimulation.protanopia(original);
      expect(simulated).toMatch(/^#[0-9a-f]{6}$/i);
      expect(simulated).not.toBe(original);
    });

    it('simulates deuteranopia', () => {
      const original = '#00ff00'; // Green
      const simulated = ColorBlindnessSimulation.deuteranopia(original);
      expect(simulated).toMatch(/^#[0-9a-f]{6}$/i);
      expect(simulated).not.toBe(original);
    });

    it('simulates tritanopia', () => {
      const original = '#0000ff'; // Blue
      const simulated = ColorBlindnessSimulation.tritanopia(original);
      expect(simulated).toMatch(/^#[0-9a-f]{6}$/i);
      expect(simulated).not.toBe(original);
    });
  });

  describe('PrintFarmer Color Compliance', () => {
    // Test core PrintFarmer color combinations for accessibility
    const darkThemeColors = {
      'pf-bg-0': '#0b1020',
      'pf-bg-1': '#0f172a',
      'pf-text-primary': '#e5e7eb',
      'pf-text-secondary': '#9ca3af',
      'pf-text-tertiary': '#6b7280',
      'pf-accent': '#10b981',          // Bright accent for text usage
      'pf-accent-bg': '#047857',       // Dark accent for button backgrounds
      'pf-success': '#10b981',         // Bright success for text usage
      'pf-success-bg': '#047857',      // Dark success for button backgrounds
      'pf-error': '#dc2626',           // Updated for accessibility
      'pf-status-online-bg': '#064e3b',
      'pf-status-online-text': '#d1fae5',
      'pf-status-offline-bg': '#450a0a',
      'pf-status-offline-text': '#fee2e2',
    };

    it('primary text meets WCAG AA on dark backgrounds', () => {
      const primaryTextOnMain = checkWCAGCompliance(
        darkThemeColors['pf-text-primary'], 
        darkThemeColors['pf-bg-0']
      );
      expect(primaryTextOnMain.passes).toBe(true);

      const primaryTextOnCard = checkWCAGCompliance(
        darkThemeColors['pf-text-primary'], 
        darkThemeColors['pf-bg-1']
      );
      expect(primaryTextOnCard.passes).toBe(true);
    });

    it('secondary text meets WCAG AA on dark backgrounds', () => {
      const secondaryTextOnMain = checkWCAGCompliance(
        darkThemeColors['pf-text-secondary'], 
        darkThemeColors['pf-bg-0']
      );
      expect(secondaryTextOnMain.passes).toBe(true);
    });

    it('status indicators meet WCAG AA', () => {
      const onlineStatus = checkWCAGCompliance(
        darkThemeColors['pf-status-online-text'], 
        darkThemeColors['pf-status-online-bg']
      );
      expect(onlineStatus.passes).toBe(true);

      const offlineStatus = checkWCAGCompliance(
        darkThemeColors['pf-status-offline-text'], 
        darkThemeColors['pf-status-offline-bg']
      );
      expect(offlineStatus.passes).toBe(true);
    });

    it('accent colors are visible on backgrounds', () => {
      const accentOnMain = checkWCAGCompliance(
        darkThemeColors['pf-accent'], 
        darkThemeColors['pf-bg-0']
      );
      expect(accentOnMain.passes).toBe(true);
    });

    it('white text on colored buttons meets standards', () => {
      const whiteOnAccentBg = checkWCAGCompliance('#ffffff', darkThemeColors['pf-accent-bg']);
      expect(whiteOnAccentBg.passes).toBe(true);

      const whiteOnSuccessBg = checkWCAGCompliance('#ffffff', darkThemeColors['pf-success-bg']);
      expect(whiteOnSuccessBg.passes).toBe(true);

      const whiteOnError = checkWCAGCompliance('#ffffff', darkThemeColors['pf-error']);
      expect(whiteOnError.passes).toBe(true);
    });
  });
});
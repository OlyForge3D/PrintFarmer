/**
 * Accessibility utilities for PrintFarmer theme system
 * WCAG 2.1 AA compliance helpers and testing functions
 */

/**
 * Calculate the relative luminance of a color
 * @param hex - Hex color code (with or without #)
 * @returns Relative luminance value (0-1)
 */
export function getLuminance(hex: string): number {
  const hexClean = hex.replace('#', '');
  
  if (hexClean.length !== 6) {
    throw new Error('Invalid hex color format');
  }

  const r = parseInt(hexClean.substring(0, 2), 16) / 255;
  const g = parseInt(hexClean.substring(2, 4), 16) / 255;
  const b = parseInt(hexClean.substring(4, 6), 16) / 255;

  // Apply gamma correction
  const gammaCorrect = (c: number) => 
    c <= 0.03928 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4);

  const rLin = gammaCorrect(r);
  const gLin = gammaCorrect(g);
  const bLin = gammaCorrect(b);

  // Calculate relative luminance
  return 0.2126 * rLin + 0.7152 * gLin + 0.0722 * bLin;
}

/**
 * Calculate contrast ratio between two colors
 * @param color1 - First hex color
 * @param color2 - Second hex color
 * @returns Contrast ratio (1-21)
 */
export function getContrastRatio(color1: string, color2: string): number {
  const lum1 = getLuminance(color1);
  const lum2 = getLuminance(color2);
  
  const lighter = Math.max(lum1, lum2);
  const darker = Math.min(lum1, lum2);
  
  return (lighter + 0.05) / (darker + 0.05);
}

/**
 * Check if a color combination meets WCAG contrast requirements
 * @param foreground - Foreground (text) color hex
 * @param background - Background color hex
 * @param level - 'AA' or 'AAA'
 * @param size - 'normal' or 'large' text
 * @returns Object with pass/fail status and ratio
 */
export function checkWCAGCompliance(
  foreground: string, 
  background: string, 
  level: 'AA' | 'AAA' = 'AA',
  size: 'normal' | 'large' = 'normal'
): { passes: boolean; ratio: number; required: number } {
  const ratio = getContrastRatio(foreground, background);
  
  let required: number;
  if (level === 'AAA') {
    required = size === 'large' ? 4.5 : 7;
  } else {
    required = size === 'large' ? 3 : 4.5;
  }
  
  return {
    passes: ratio >= required,
    ratio: Math.round(ratio * 100) / 100,
    required
  };
}

/**
 * Extract CSS custom property value
 * @param property - CSS custom property name (with or without --)
 * @returns Hex color value or null if not found
 */
export function getCSSCustomProperty(property: string): string | null {
  const propName = property.startsWith('--') ? property : `--${property}`;
  const value = getComputedStyle(document.documentElement).getPropertyValue(propName).trim();
  
  if (!value) return null;
  
  // Convert rgb() values to hex if needed
  if (value.startsWith('rgb')) {
    const match = value.match(/rgb\((\d+),?\s*(\d+),?\s*(\d+)\)/);
    if (match) {
      const [, r, g, b] = match;
      return `#${Number(r).toString(16).padStart(2, '0')}${Number(g).toString(16).padStart(2, '0')}${Number(b).toString(16).padStart(2, '0')}`;
    }
  }
  
  return value.startsWith('#') ? value : `#${value}`;
}

/**
 * Test all PrintFarmer theme color combinations for WCAG compliance
 * @returns Array of test results
 */
export function testThemeCompliance(): Array<{
  name: string;
  foreground: string;
  background: string;
  result: ReturnType<typeof checkWCAGCompliance>;
}> {
  const tests = [
    // Primary text combinations
    { name: 'Primary text on main background', fg: 'pf-text-primary', bg: 'pf-bg-0' },
    { name: 'Primary text on card background', fg: 'pf-text-primary', bg: 'pf-bg-1' },
    { name: 'Primary text on secondary background', fg: 'pf-text-primary', bg: 'pf-bg-2' },
    { name: 'Primary text on panel background', fg: 'pf-text-primary', bg: 'pf-panel' },
    
    // Secondary text combinations
    { name: 'Secondary text on main background', fg: 'pf-text-secondary', bg: 'pf-bg-0' },
    { name: 'Secondary text on card background', fg: 'pf-text-secondary', bg: 'pf-bg-1' },
    
    // Tertiary text combinations
    { name: 'Tertiary text on main background', fg: 'pf-text-tertiary', bg: 'pf-bg-0' },
    { name: 'Tertiary text on card background', fg: 'pf-text-tertiary', bg: 'pf-bg-1' },
    
    // Status indicators
    { name: 'Online status text', fg: 'pf-status-online-text', bg: 'pf-status-online-bg' },
    { name: 'Offline status text', fg: 'pf-status-offline-text', bg: 'pf-status-offline-bg' },
    { name: 'Error text', fg: 'pf-error-text', bg: 'pf-error-bg' },
    
    // Interactive elements
    { name: 'Accent on main background', fg: 'pf-accent', bg: 'pf-bg-0' },
    { name: 'Success on main background', fg: 'pf-success', bg: 'pf-bg-0' },
    { name: 'Link on main background', fg: 'pf-link', bg: 'pf-bg-0' },
    
    // Button combinations (assuming white text on colored backgrounds)
    { name: 'White text on accent button', fg: '#ffffff', bg: 'pf-accent' },
    { name: 'White text on success button', fg: '#ffffff', bg: 'pf-success' },
    { name: 'White text on error button', fg: '#ffffff', bg: 'pf-error' },
  ];

  return tests.map(test => {
    const fgColor = test.fg.startsWith('#') ? test.fg : getCSSCustomProperty(test.fg);
    const bgColor = test.bg.startsWith('#') ? test.bg : getCSSCustomProperty(test.bg);
    
    if (!fgColor || !bgColor) {
      return {
        name: test.name,
        foreground: test.fg,
        background: test.bg,
        result: { passes: false, ratio: 0, required: 4.5 }
      };
    }
    
    return {
      name: test.name,
      foreground: fgColor,
      background: bgColor,
      result: checkWCAGCompliance(fgColor, bgColor)
    };
  });
}

/**
 * Color blindness simulation utilities
 */
export const ColorBlindnessSimulation = {
  /**
   * Simulate protanopia (red-blind) color perception
   */
  protanopia(hex: string): string {
    const { r, g, b } = hexToRgb(hex);
    // Simplified protanopia transformation
    const newR = 0.567 * r + 0.433 * g;
    const newG = 0.558 * r + 0.442 * g;
    const newB = 0.242 * g + 0.758 * b;
    return rgbToHex(Math.round(newR), Math.round(newG), Math.round(newB));
  },

  /**
   * Simulate deuteranopia (green-blind) color perception
   */
  deuteranopia(hex: string): string {
    const { r, g, b } = hexToRgb(hex);
    // Simplified deuteranopia transformation
    const newR = 0.625 * r + 0.375 * g;
    const newG = 0.7 * r + 0.3 * g;
    const newB = 0.3 * g + 0.7 * b;
    return rgbToHex(Math.round(newR), Math.round(newG), Math.round(newB));
  },

  /**
   * Simulate tritanopia (blue-blind) color perception
   */
  tritanopia(hex: string): string {
    const { r, g, b } = hexToRgb(hex);
    // Simplified tritanopia transformation
    const newR = 0.95 * r + 0.05 * g;
    const newG = 0.433 * g + 0.567 * b;
    const newB = 0.475 * g + 0.525 * b;
    return rgbToHex(Math.round(newR), Math.round(newG), Math.round(newB));
  }
};

/**
 * Helper function to convert hex to RGB
 */
function hexToRgb(hex: string): { r: number; g: number; b: number } {
  const cleanHex = hex.replace('#', '');
  return {
    r: parseInt(cleanHex.substring(0, 2), 16),
    g: parseInt(cleanHex.substring(2, 4), 16),
    b: parseInt(cleanHex.substring(4, 6), 16)
  };
}

/**
 * Helper function to convert RGB to hex
 */
function rgbToHex(r: number, g: number, b: number): string {
  return `#${r.toString(16).padStart(2, '0')}${g.toString(16).padStart(2, '0')}${b.toString(16).padStart(2, '0')}`;
}

/**
 * Generate accessible color palette suggestions
 */
export function generateAccessiblePalette(baseColor: string, backgroundColor: string): {
  original: string;
  suggestion: string;
  ratio: number;
}[] {
  const suggestions = [];
  // (Lum values calculated on demand in contrast attempts; removed unused pre-calculations.)
  
  // Try different brightness levels to achieve WCAG AA compliance
  for (let adjustment = -0.8; adjustment <= 0.8; adjustment += 0.1) {
    const adjustedColor = adjustBrightness(baseColor, adjustment);
    const ratio = getContrastRatio(adjustedColor, backgroundColor);
    
    if (ratio >= 4.5) {
      suggestions.push({
        original: baseColor,
        suggestion: adjustedColor,
        ratio: Math.round(ratio * 100) / 100
      });
    }
  }
  
  return suggestions.slice(0, 5); // Return top 5 suggestions
}

/**
 * Adjust brightness of a color
 */
function adjustBrightness(hex: string, factor: number): string {
  const { r, g, b } = hexToRgb(hex);
  
  const adjust = (value: number) => {
    if (factor > 0) {
      return Math.min(255, value + (255 - value) * factor);
    } else {
      return Math.max(0, value * (1 + factor));
    }
  };
  
  return rgbToHex(
    Math.round(adjust(r)),
    Math.round(adjust(g)),
    Math.round(adjust(b))
  );
}
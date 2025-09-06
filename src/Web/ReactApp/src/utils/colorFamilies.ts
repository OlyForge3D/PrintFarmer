// Utility for mapping hex colors to human-friendly color families.
// Provides classifyColor and a representative color swatch map.

export const colorFamilySwatches: Record<string, string> = {
  Red: '#ef4444',
  Orange: '#f97316',
  Brown: '#b45309',
  Yellow: '#eab308',
  Green: '#22c55e',
  Teal: '#14b8a6',
  Blue: '#3b82f6',
  Purple: '#8b5cf6',
  Pink: '#ec4899',
  Gray: '#6b7280',
  Black: '#111827',
  White: '#f9fafb',
  Unknown: '#4b5563'
};

// Tailwind-friendly background classes approximating representative colors.
export const colorFamilyBgClass: Record<string, string> = {
  Red: 'bg-red-500',
  Orange: 'bg-orange-500',
  Brown: 'bg-amber-700',
  Yellow: 'bg-yellow-400',
  Green: 'bg-green-500',
  Teal: 'bg-teal-500',
  Blue: 'bg-blue-500',
  Purple: 'bg-purple-500',
  Pink: 'bg-pink-500',
  Gray: 'bg-gray-500',
  Black: 'bg-gray-900',
  White: 'bg-gray-100'
};

function hexToRgb(hex: string): { r: number; g: number; b: number } | null {
  const cleaned = hex.replace('#', '').trim();
  if (!/^([0-9a-fA-F]{3}|[0-9a-fA-F]{6})$/.test(cleaned)) return null;
  const full = cleaned.length === 3
    ? cleaned.split('').map(c => c + c).join('')
    : cleaned;
  const num = parseInt(full, 16);
  return {
    r: (num >> 16) & 255,
    g: (num >> 8) & 255,
    b: num & 255
  };
}

function rgbToHsl(r: number, g: number, b: number): { h: number; s: number; l: number } {
  r /= 255; g /= 255; b /= 255;
  const max = Math.max(r, g, b), min = Math.min(r, g, b);
  let h = 0, s = 0;
  const l = (max + min) / 2;
  const d = max - min;
  if (d !== 0) {
    s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
    switch (max) {
      case r: h = (g - b) / d + (g < b ? 6 : 0); break;
      case g: h = (b - r) / d + 2; break;
      case b: h = (r - g) / d + 4; break;
    }
    h *= 60;
  }
  return { h, s, l };
}

export function classifyColor(hex: string | null | undefined): string {
  // Treat missing/invalid colors as Gray to collapse 'Unknown' into a nearby neutral family
  if (!hex) return 'Gray';
  const rgb = hexToRgb(hex);
  if (!rgb) return 'Gray';
  const { h, s, l } = rgbToHsl(rgb.r, rgb.g, rgb.b);

  // Achromatic / extremes
  if (l <= 0.08) return 'Black';
  if (l >= 0.92 && s < 0.2) return 'White';
  if (s < 0.15) return 'Gray';

  // Brown detection: medium-low lightness + orange hue
  if (l >= 0.18 && l <= 0.55 && h >= 15 && h < 45 && s > 0.25) return 'Brown';

  // Hue-based families
  if ((h >= 0 && h < 15) || (h >= 345 && h <= 360)) return 'Red';
  if (h >= 15 && h < 45) return 'Orange';
  if (h >= 45 && h < 65) return 'Yellow';
  if (h >= 65 && h < 170) return 'Green';
  if (h >= 170 && h < 190) return 'Teal';
  if (h >= 190 && h < 250) return 'Blue';
  if (h >= 250 && h < 290) return 'Purple';
  if (h >= 290 && h < 345) return 'Pink';
  // Fallback (should be unreachable) -> Gray
  return 'Gray';
}

export function getRepresentativeHex(family: string): string {
  return colorFamilySwatches[family] || colorFamilySwatches.Unknown;
}

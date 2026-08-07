import { getContrastRatio } from '@/common/utils/accessibility';

function normalizeHexColor(color: string): string | null {
  if (/^#[\da-f]{6}$/i.test(color)) {
    return color;
  }

  const shortHex = color.match(/^#([\da-f])([\da-f])([\da-f])$/i);
  return shortHex
    ? `#${shortHex[1]}${shortHex[1]}${shortHex[2]}${shortHex[2]}${shortHex[3]}${shortHex[3]}`
    : null;
}

export function getTagChipForeground(color: string): '#000000' | '#ffffff' {
  const normalizedColor = normalizeHexColor(color);
  if (!normalizedColor) {
    return '#ffffff';
  }

  return getContrastRatio('#000000', normalizedColor) >= getContrastRatio('#ffffff', normalizedColor)
    ? '#000000'
    : '#ffffff';
}

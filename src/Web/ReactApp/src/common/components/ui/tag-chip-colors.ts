import { getContrastRatio } from '@/common/utils/accessibility';

export function normalizeTagChipColor(color: string): string | null {
  if (/^#[\da-f]{6}$/i.test(color)) {
    return color;
  }

  const shortHex = color.match(/^#([\da-f])([\da-f])([\da-f])$/i);
  return shortHex
    ? `#${shortHex[1]}${shortHex[1]}${shortHex[2]}${shortHex[2]}${shortHex[3]}${shortHex[3]}`
    : null;
}

export function getTagChipForeground(color: string): '#000000' | '#ffffff' {
  const normalizedColor = normalizeTagChipColor(color);
  if (!normalizedColor) {
    return '#000000';
  }

  return getContrastRatio('#000000', normalizedColor) >= getContrastRatio('#ffffff', normalizedColor)
    ? '#000000'
    : '#ffffff';
}

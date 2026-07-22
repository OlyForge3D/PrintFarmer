export interface LabColor {
  l: number;
  a: number;
  b: number;
}

const INVALID_COLOR_DISTANCE = 1_000_000;

function hexToRgb(hex: string | null | undefined): { r: number; g: number; b: number } | null {
  if (!hex) return null;

  const cleaned = hex.trim().replace(/^#/, '');
  if (!/^([0-9a-fA-F]{3}|[0-9a-fA-F]{6})$/.test(cleaned)) return null;

  const full = cleaned.length === 3
    ? cleaned.split('').map(char => `${char}${char}`).join('')
    : cleaned;
  const value = Number.parseInt(full, 16);

  return {
    r: (value >> 16) & 255,
    g: (value >> 8) & 255,
    b: value & 255,
  };
}

function srgbToLinear(channel: number): number {
  const normalized = channel / 255;
  return normalized <= 0.04045
    ? normalized / 12.92
    : ((normalized + 0.055) / 1.055) ** 2.4;
}

function pivotXyz(value: number): number {
  return value > 0.008856
    ? Math.cbrt(value)
    : (7.787 * value) + (16 / 116);
}

export function hexToLab(hex: string | null | undefined): LabColor | null {
  const rgb = hexToRgb(hex);
  if (!rgb) return null;

  const r = srgbToLinear(rgb.r);
  const g = srgbToLinear(rgb.g);
  const b = srgbToLinear(rgb.b);

  const x = (r * 0.4124564) + (g * 0.3575761) + (b * 0.1804375);
  const y = (r * 0.2126729) + (g * 0.7151522) + (b * 0.0721750);
  const z = (r * 0.0193339) + (g * 0.1191920) + (b * 0.9503041);

  const fx = pivotXyz(x / 0.95047);
  const fy = pivotXyz(y);
  const fz = pivotXyz(z / 1.08883);

  return {
    l: (116 * fy) - 16,
    a: 500 * (fx - fy),
    b: 200 * (fy - fz),
  };
}

function degreesToRadians(degrees: number): number {
  return (degrees * Math.PI) / 180;
}

function radiansToDegrees(radians: number): number {
  return (radians * 180) / Math.PI;
}

function normalizeHueDegrees(degrees: number): number {
  return (degrees + 360) % 360;
}

function labDistanceCiede2000(left: LabColor, right: LabColor): number {
  const c1 = Math.sqrt((left.a ** 2) + (left.b ** 2));
  const c2 = Math.sqrt((right.a ** 2) + (right.b ** 2));
  const cMean = (c1 + c2) / 2;
  const cMean7 = cMean ** 7;
  const g = 0.5 * (1 - Math.sqrt(cMean7 / (cMean7 + (25 ** 7))));

  const a1Prime = (1 + g) * left.a;
  const a2Prime = (1 + g) * right.a;
  const c1Prime = Math.sqrt((a1Prime ** 2) + (left.b ** 2));
  const c2Prime = Math.sqrt((a2Prime ** 2) + (right.b ** 2));
  const h1Prime = c1Prime === 0 ? 0 : normalizeHueDegrees(radiansToDegrees(Math.atan2(left.b, a1Prime)));
  const h2Prime = c2Prime === 0 ? 0 : normalizeHueDegrees(radiansToDegrees(Math.atan2(right.b, a2Prime)));

  const deltaLPrime = right.l - left.l;
  const deltaCPrime = c2Prime - c1Prime;
  const rawDeltaHue = h2Prime - h1Prime;
  const deltaHPrimeDegrees = c1Prime * c2Prime === 0
    ? 0
    : rawDeltaHue > 180
      ? rawDeltaHue - 360
      : rawDeltaHue < -180
        ? rawDeltaHue + 360
        : rawDeltaHue;
  const deltaHPrime = 2 * Math.sqrt(c1Prime * c2Prime) * Math.sin(degreesToRadians(deltaHPrimeDegrees / 2));

  const lMeanPrime = (left.l + right.l) / 2;
  const cMeanPrime = (c1Prime + c2Prime) / 2;
  const hMeanPrime = (() => {
    if (c1Prime * c2Prime === 0) return h1Prime + h2Prime;
    const diff = Math.abs(h1Prime - h2Prime);
    if (diff <= 180) return (h1Prime + h2Prime) / 2;
    return h1Prime + h2Prime < 360
      ? (h1Prime + h2Prime + 360) / 2
      : (h1Prime + h2Prime - 360) / 2;
  })();

  const t = 1
    - (0.17 * Math.cos(degreesToRadians(hMeanPrime - 30)))
    + (0.24 * Math.cos(degreesToRadians(2 * hMeanPrime)))
    + (0.32 * Math.cos(degreesToRadians((3 * hMeanPrime) + 6)))
    - (0.20 * Math.cos(degreesToRadians((4 * hMeanPrime) - 63)));
  const deltaTheta = 30 * Math.exp(-(((hMeanPrime - 275) / 25) ** 2));
  const cMeanPrime7 = cMeanPrime ** 7;
  const rC = 2 * Math.sqrt(cMeanPrime7 / (cMeanPrime7 + (25 ** 7)));
  const sL = 1 + ((0.015 * ((lMeanPrime - 50) ** 2)) / Math.sqrt(20 + ((lMeanPrime - 50) ** 2)));
  const sC = 1 + (0.045 * cMeanPrime);
  const sH = 1 + (0.015 * cMeanPrime * t);
  const rT = -Math.sin(degreesToRadians(2 * deltaTheta)) * rC;

  return Math.sqrt(
    ((deltaLPrime / sL) ** 2)
    + ((deltaCPrime / sC) ** 2)
    + ((deltaHPrime / sH) ** 2)
    + (rT * (deltaCPrime / sC) * (deltaHPrime / sH)),
  );
}

export function colorDistance(hexA: string | null | undefined, hexB: string | null | undefined): number {
  const left = hexToLab(hexA);
  const right = hexToLab(hexB);

  if (!left || !right) return INVALID_COLOR_DISTANCE;

  return labDistanceCiede2000(left, right);
}

export { INVALID_COLOR_DISTANCE };

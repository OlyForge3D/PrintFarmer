/**
 * 3D Text Geometry Generator
 *
 * Generates extruded 3D text geometry using Three.js TextGeometry + Droid
 * typeface fonts bundled in @/assets/fonts/droid (Apache-2.0, copied from
 * three.js r182; three 0.186 no longer publishes them to npm, see
 * THIRD-PARTY-NOTICES.md).  Fonts are lazy-loaded on first use
 * so the ~200-500 KB JSON payloads are only fetched when the text tool is
 * activated.
 */
import * as THREE from 'three';
import { Font, TextGeometry as ThreeTextGeometry } from 'three-stdlib';
import { STLExporter } from 'three/examples/jsm/exporters/STLExporter.js';

export type FontFamily = 'sans-serif' | 'serif' | 'monospace';

export interface TextGeometryOptions {
  text: string;
  fontSize: number;
  extrusionDepth: number;
  fontFamily: FontFamily;
}

export interface TextGeometryResult {
  geometry: THREE.BufferGeometry;
  width: number;
  height: number;
}

// ── Font cache ──────────────────────────────────────────────────────────
type FontData = ConstructorParameters<typeof Font>[0];

interface RawTypeface {
  boundingBox: { yMax: number; yMin: number };
  familyName: string;
  glyphs: Record<string, { ha: number; o?: string }>;
  resolution: number;
  underlineThickness: number;
}

function toFontData(raw: RawTypeface): FontData {
  return {
    boundingBox: { yMax: raw.boundingBox.yMax, yMin: raw.boundingBox.yMin },
    familyName: raw.familyName,
    glyphs: Object.fromEntries(
      Object.entries(raw.glyphs).map(([key, glyph]) => {
        const outline = glyph.o ?? '';
        // three-stdlib reuses a truthy _cachedOutline instead of parsing o, so it
        // must hold the split outline; an empty array would yield empty geometry.
        return [key, { _cachedOutline: outline ? outline.split(' ') : [], ha: glyph.ha, o: outline }];
      }),
    ),
    resolution: raw.resolution,
    underlineThickness: raw.underlineThickness,
  };
}

async function importTypeface(family: FontFamily): Promise<RawTypeface> {
  switch (family) {
    case 'serif':
      return (await import('@/assets/fonts/droid/droid_serif_regular.typeface.json')).default;
    case 'monospace':
      return (await import('@/assets/fonts/droid/droid_sans_mono_regular.typeface.json')).default;
    default:
      return (await import('@/assets/fonts/droid/droid_sans_regular.typeface.json')).default;
  }
}

const fontCache = new Map<FontFamily, Font>();

async function loadFont(family: FontFamily): Promise<Font> {
  const cached = fontCache.get(family);
  if (cached) return cached;

  const font = new Font(toFontData(await importTypeface(family)));
  fontCache.set(family, font);
  return font;
}

// ── Public API ──────────────────────────────────────────────────────────

/**
 * Generate extruded 3D text geometry.
 *
 * @returns A {@link TextGeometryResult} containing the BufferGeometry and its
 *   bounding dimensions (width × height) in the same unit as `fontSize`.
 */
export async function generateTextGeometry(
  options: TextGeometryOptions,
): Promise<TextGeometryResult> {
  const { text, fontSize, extrusionDepth, fontFamily } = options;

  const font = await loadFont(fontFamily);

  const geometry = new ThreeTextGeometry(text, {
    font,
    size: fontSize,
    height: extrusionDepth,
    curveSegments: 4,
    bevelEnabled: false,
  });

  geometry.computeBoundingBox();
  const box = geometry.boundingBox!;
  const width = box.max.x - box.min.x;
  const height = box.max.y - box.min.y;

  // Center the geometry around its local origin for easier placement
  geometry.translate(-width / 2, -height / 2, 0);

  return { geometry, width, height };
}

/**
 * Serialise a BufferGeometry to a binary STL Blob URL.
 *
 * The caller is responsible for calling `URL.revokeObjectURL` when the URL
 * is no longer needed.
 */
export function geometryToStlBlobUrl(geometry: THREE.BufferGeometry): string {
  const exporter = new STLExporter();
  const mesh = new THREE.Mesh(geometry);
  const buffer = exporter.parse(mesh, { binary: true });
  const blob = new Blob([buffer], { type: 'application/octet-stream' });
  return URL.createObjectURL(blob);
}

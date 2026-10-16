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
const fontCache = new Map<FontFamily, Font>();

async function loadFont(family: FontFamily): Promise<Font> {
  const cached = fontCache.get(family);
  if (cached) return cached;

  // three-stdlib reuses a truthy _cachedOutline instead of parsing o, so it
  // must hold the split outline; an empty array would yield empty geometry.
  let data: ConstructorParameters<typeof Font>[0];
  switch (family) {
    case 'serif': {
      const mod = await import('@/assets/fonts/droid/droid_serif_regular.typeface.json');
      const raw = mod.default ?? mod;
      data = {
        boundingBox: { yMax: raw.boundingBox.yMax, yMin: raw.boundingBox.yMin },
        familyName: raw.familyName,
        glyphs: Object.fromEntries(Object.entries(raw.glyphs).map(([key, glyph]) => [key, { _cachedOutline: 'o' in glyph ? glyph.o.split(' ') : [], ha: glyph.ha, o: 'o' in glyph ? glyph.o : '' }])),
        resolution: raw.resolution,
        underlineThickness: raw.underlineThickness,
      };
      break;
    }
    case 'monospace': {
      const mod = await import('@/assets/fonts/droid/droid_sans_mono_regular.typeface.json');
      const raw = mod.default ?? mod;
      data = {
        boundingBox: { yMax: raw.boundingBox.yMax, yMin: raw.boundingBox.yMin },
        familyName: raw.familyName,
        glyphs: Object.fromEntries(Object.entries(raw.glyphs).map(([key, glyph]) => [key, { _cachedOutline: 'o' in glyph ? glyph.o.split(' ') : [], ha: glyph.ha, o: 'o' in glyph ? glyph.o : '' }])),
        resolution: raw.resolution,
        underlineThickness: raw.underlineThickness,
      };
      break;
    }
    default: {
      const mod = await import('@/assets/fonts/droid/droid_sans_regular.typeface.json');
      const raw = mod.default ?? mod;
      data = {
        boundingBox: { yMax: raw.boundingBox.yMax, yMin: raw.boundingBox.yMin },
        familyName: raw.familyName,
        glyphs: Object.fromEntries(Object.entries(raw.glyphs).map(([key, glyph]) => [key, { _cachedOutline: 'o' in glyph ? glyph.o.split(' ') : [], ha: glyph.ha, o: 'o' in glyph ? glyph.o : '' }])),
        resolution: raw.resolution,
        underlineThickness: raw.underlineThickness,
      };
      break;
    }
  }

  const font = new Font(data);
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

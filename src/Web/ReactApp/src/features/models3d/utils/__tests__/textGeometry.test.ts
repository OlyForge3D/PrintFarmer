import type { BufferGeometry } from 'three';
import { Font, TextGeometry } from 'three-stdlib';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { generateTextGeometry, geometryToStlBlobUrl, type FontFamily } from '@/features/models3d/utils/textGeometry';

const TEXT = 'PrintFarmer 0123 AaGg@#&?';
const FONT_SIZE = 10;
const EXTRUSION_DEPTH = 2;

// Measured from the Droid typefaces previously shipped in three 0.182.0
// (byte-identical to the bundled copies); identical under three 0.186.1.
const expected: Record<FontFamily, { file: string; vertices: number; width: number; height: number }> = {
  serif: { file: 'droid_serif_regular', vertices: 44412, width: 192.75, height: 13.89 },
  'sans-serif': { file: 'droid_sans_regular', vertices: 30696, width: 180.44, height: 13.57 },
  monospace: { file: 'droid_sans_mono_regular', vertices: 14028, width: 206.16, height: 13.85 },
};

function sortedVertices(geometry: BufferGeometry): string[] {
  const positions = geometry.getAttribute('position').array;
  const vertices: string[] = [];
  for (let index = 0; index < positions.length; index += 3) {
    vertices.push(`${positions[index]},${positions[index + 1]},${positions[index + 2]}`);
  }
  return vertices.sort();
}

describe('generateTextGeometry', () => {
  it.each(Object.keys(expected) as FontFamily[])('extrudes the bundled %s Droid typeface', async (fontFamily) => {
    const { file, vertices, width: expectedWidth, height: expectedHeight } = expected[fontFamily];

    const { geometry, width, height } = await generateTextGeometry({
      text: TEXT,
      fontSize: FONT_SIZE,
      extrusionDepth: EXTRUSION_DEPTH,
      fontFamily,
    });

    expect(geometry.getAttribute('position').count).toBe(vertices);
    expect(width).toBeCloseTo(expectedWidth, 2);
    expect(height).toBeCloseTo(expectedHeight, 2);

    geometry.computeBoundingBox();
    const box = geometry.boundingBox!;
    expect(box.max.z - box.min.z).toBeCloseTo(EXTRUSION_DEPTH, 5);

    // Independent reference: three-stdlib's own parsing of the raw typeface JSON.
    const raw = (await import(`@/assets/fonts/droid/${file}.typeface.json`)).default;
    const reference = new TextGeometry(TEXT, {
      font: new Font(raw),
      size: FONT_SIZE,
      height: EXTRUSION_DEPTH,
      curveSegments: 4,
      bevelEnabled: false,
    });
    reference.computeBoundingBox();
    const referenceBox = reference.boundingBox!;
    reference.translate(
      -(referenceBox.max.x - referenceBox.min.x) / 2,
      -(referenceBox.max.y - referenceBox.min.y) / 2,
      0,
    );
    expect(sortedVertices(geometry)).toEqual(sortedVertices(reference));
  });
});

describe('geometryToStlBlobUrl', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('exports every generated triangle as a binary STL', async () => {
    const { geometry } = await generateTextGeometry({
      text: TEXT,
      fontSize: FONT_SIZE,
      extrusionDepth: EXTRUSION_DEPTH,
      fontFamily: 'sans-serif',
    });
    const triangles = geometry.getAttribute('position').count / 3;
    let exported: Blob | undefined;
    const createObjectURL = vi.fn((blob: Blob) => {
      exported = blob;
      return 'blob:text-stl';
    });
    vi.stubGlobal('URL', Object.assign(Object.create(URL) as typeof URL, { createObjectURL }));

    expect(geometryToStlBlobUrl(geometry)).toBe('blob:text-stl');

    expect(createObjectURL).toHaveBeenCalledTimes(1);
    expect(triangles).toBe(expected['sans-serif'].vertices / 3);
    // Binary STL: 80-byte header, uint32 triangle count, then 50 bytes per triangle.
    expect(exported!.size).toBe(84 + 50 * triangles);
    const view = new DataView(await exported!.arrayBuffer());
    expect(view.getUint32(80, true)).toBe(triangles);
  });
});
import { describe, it, expect } from 'vitest';
import type { DetailedLayer } from '@/features/slicer/services';
import { buildGcodeLineBuffers, COLOR_MODE_EXTRUSION, COLOR_MODE_LAYER, COLOR_MODE_SPEED, layerHueColor } from '../gcodeLineBuffers';

function layer(index: number, pts: Array<[number, number, number, 0 | 1, number?, number?]>): DetailedLayer {
  return {
    index,
    z: pts[0]?.[2] ?? 0,
    count: pts.length,
    x: Float32Array.from(pts.map(p => p[0])),
    y: Float32Array.from(pts.map(p => p[1])),
    pz: Float32Array.from(pts.map(p => p[2])),
    e: new Float32Array(pts.length),
    feedRate: Float32Array.from(pts.map(p => p[4] ?? 1500)),
    type: Uint8Array.from(pts.map(p => p[3])),
    tool: Int32Array.from(pts.map(p => p[5] ?? 0)),
  };
}

const opts = { colorMode: COLOR_MODE_EXTRUSION, minFeedColor: '#0000FF', maxFeedColor: '#FF0000' };

describe('buildGcodeLineBuffers', () => {
  it('connects each point to its predecessor and classifies by the end point type', () => {
    const buffers = buildGcodeLineBuffers([
      layer(0, [[0, 0, 0.2, 0], [10, 0, 0.2, 1], [10, 10, 0.2, 1], [0, 10, 0.2, 0]]),
    ], opts);

    expect(Array.from(buffers.extrude.positions)).toEqual([
      0, 0, 0.2, 10, 0, 0.2,
      10, 0, 0.2, 10, 10, 0.2,
    ].map(v => Math.fround(v)));
    expect(Array.from(buffers.travel.positions)).toEqual([10, 10, 0.2, 0, 10, 0.2].map(v => Math.fround(v)));
    expect(Array.from(buffers.extrude.colors.slice(0, 3))).toEqual([0xff, 0x6b, 0x35]);
  });

  it('records cumulative per-layer vertex ends and bridges the layer boundary', () => {
    const buffers = buildGcodeLineBuffers([
      layer(0, [[0, 0, 0.2, 0], [1, 0, 0.2, 1]]),
      layer(1, [[1, 0, 0.4, 1], [2, 0, 0.4, 1]]),
    ], opts);

    expect(Array.from(buffers.extrude.layerEnds)).toEqual([2, 6]);
    expect(Array.from(buffers.travel.layerEnds)).toEqual([0, 0]);
  });

  it('allocates exactly two vertices per segment, independent of run structure', () => {
    // Alternating types produce many tiny runs; buffer size must still be 2 vertices/segment.
    const pts = Array.from({ length: 1001 }, (_, i) => [i, 0, 0.2, (i % 2) as 0 | 1] as [number, number, number, 0 | 1]);
    const buffers = buildGcodeLineBuffers([layer(0, pts)], opts);

    expect(buffers.extrude.positions.length / 3).toBe(500 * 2);
    expect(buffers.travel.positions.length / 3).toBe(500 * 2);
  });

  it('omits segments that end on a disabled tool', () => {
    const buffers = buildGcodeLineBuffers([
      layer(0, [[0, 0, 0.2, 0, 1500, 0], [1, 0, 0.2, 1, 1500, 0], [2, 0, 0.2, 1, 1500, 1]]),
    ], { ...opts, enabledTools: new Set([0]) });

    expect(buffers.extrude.positions.length / 3).toBe(2);
  });

  it('colors by speed (mm/min converted to mm/s) and deterministically by layer', () => {
    const slow = buildGcodeLineBuffers([layer(0, [[0, 0, 0.2, 0], [1, 0, 0.2, 1, 600]])], { ...opts, colorMode: COLOR_MODE_SPEED });
    const fast = buildGcodeLineBuffers([layer(0, [[0, 0, 0.2, 0], [1, 0, 0.2, 1, 9000]])], { ...opts, colorMode: COLOR_MODE_SPEED });
    expect(Array.from(slow.extrude.colors.slice(0, 3))).toEqual([0, 0, 255]);
    expect(Array.from(fast.extrude.colors.slice(0, 3))).toEqual([255, 0, 0]);

    const byLayer = buildGcodeLineBuffers([layer(0, [[0, 0, 0.2, 0], [1, 0, 0.2, 1]])], { ...opts, colorMode: COLOR_MODE_LAYER });
    expect(Array.from(byLayer.extrude.colors.slice(0, 3))).toEqual(layerHueColor(0));
  });
});

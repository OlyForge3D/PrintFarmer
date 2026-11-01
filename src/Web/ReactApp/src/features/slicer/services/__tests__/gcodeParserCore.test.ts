import { describe, it, expect } from 'vitest';
import { parseLayersCore, parseDetailedLayersCore, detailedParseBuffersTransferList } from '../gcodeParserCore';

const THREE_LAYER_GCODE = `; generated test fixture
G28 ; home
G1 Z0.2 F3000
G1 X10 Y10 E1 F1500
G1 X20 Y10 E2
G1 X20 Y20 E3
G1 Z0.4
G1 X10 Y10 E4
G1 X20 Y10 E5
G1 X20 Y20 E6
G1 Z0.6
G1 X10 Y10 E7
G1 X20 Y10 E8
G1 X20 Y20 E9
`;

describe('parseLayersCore', () => {
  it('parses a 3-layer fixture and returns correct layer count', () => {
    const layers = parseLayersCore(THREE_LAYER_GCODE);
    expect(layers).toHaveLength(3);
    expect(layers.map(l => l.z)).toEqual([0.2, 0.4, 0.6]);
  });
});

describe('parseDetailedLayersCore', () => {
  it('groups points into typed-array buffers keyed by ascending layer Z', () => {
    const buffers = parseDetailedLayersCore(THREE_LAYER_GCODE);

    expect(buffers.layerCount).toBe(3);
    Array.from(buffers.layerZ).forEach((z, i) => expect(z).toBeCloseTo([0.2, 0.4, 0.6][i], 5));
    // Each layer has 4 points: the Z-lift move plus 3 extrusion moves.
    expect(buffers.layerStart[0]).toBe(0);
    expect(buffers.layerStart[1]).toBe(4);
    expect(buffers.layerStart[2]).toBe(8);
    expect(buffers.pointCount).toBe(12);
  });

  it('produces typed arrays (not plain arrays) for point data', () => {
    const buffers = parseDetailedLayersCore(THREE_LAYER_GCODE);

    expect(buffers.x).toBeInstanceOf(Float32Array);
    expect(buffers.y).toBeInstanceOf(Float32Array);
    expect(buffers.z).toBeInstanceOf(Float32Array);
    expect(buffers.e).toBeInstanceOf(Float32Array);
    expect(buffers.feedRate).toBeInstanceOf(Float32Array);
    expect(buffers.tool).toBeInstanceOf(Int32Array);
    expect(buffers.type).toBeInstanceOf(Uint8Array);
    expect(buffers.layerStart).toBeInstanceOf(Int32Array);
    expect(buffers.layerZ).toBeInstanceOf(Float32Array);
    expect(buffers.tools).toBeInstanceOf(Int32Array);
  });

  it('marks the initial Z-lift move as move (type=0) and subsequent E-increases as extrude (type=1)', () => {
    const buffers = parseDetailedLayersCore(THREE_LAYER_GCODE);
    // Each layer: [Z-lift move, extrude, extrude, extrude]
    expect(Array.from(buffers.type)).toEqual([0, 1, 1, 1, 0, 1, 1, 1, 0, 1, 1, 1]);
  });

  it('tracks per-point tool index across a tool change', () => {
    const gcode = `T0\nG1 X1 Y1 Z0.2 E1 F1500\nT1\nG1 X2 Y2 E2\n`;
    const buffers = parseDetailedLayersCore(gcode);

    expect(Array.from(buffers.tools)).toEqual([0, 1]);
    expect(Array.from(buffers.tool)).toEqual([0, 1]);
  });

  it('always includes tool 0 when no T command appears', () => {
    const buffers = parseDetailedLayersCore(THREE_LAYER_GCODE);
    expect(Array.from(buffers.tools)).toEqual([0]);
  });

  it('handles empty G-code with zero layers and zero points', () => {
    const buffers = parseDetailedLayersCore('');
    expect(buffers.layerCount).toBe(0);
    expect(buffers.pointCount).toBe(0);
    expect(Array.from(buffers.tools)).toEqual([0]);
  });
});

// OrcaSlicer-style output: relative E (M83), per-layer markers, and Z-hop travel.
const ORCA_ZHOP_GCODE = `M83
G92 E0
;LAYER_CHANGE
;Z:0.2
;HEIGHT:0.2
G1 Z0.2 F600
G1 X10 Y10 E0.5 F1500
G1 X20 Y10 E0.5
G1 Z0.6 F600
G1 X50 Y50 F9000
G1 Z0.2 F600
G1 X60 Y50 E0.5
;LAYER_CHANGE
;Z:0.4
;HEIGHT:0.2
G1 Z0.8 F600
G1 X10 Y10 F9000
G1 Z0.4 F600
G1 X20 Y10 E0.5
G1 X20 Y20 E0.5
`;

describe('layer detection (#gcode-layer-count)', () => {
  it('uses ;LAYER_CHANGE markers so Z-hop heights never become layers', () => {
    const buffers = parseDetailedLayersCore(ORCA_ZHOP_GCODE);

    expect(buffers.layerCount).toBe(2);
    Array.from(buffers.layerZ).forEach((z, i) => expect(z).toBeCloseTo([0.2, 0.4][i], 5));
    expect(Array.from(buffers.layerStart)).toEqual([0, 7]);
    expect(buffers.pointCount).toBe(12);
    expect(parseLayersCore(ORCA_ZHOP_GCODE).map(l => l.z)).toEqual([0.2, 0.4]);
  });

  it('does not create layers for Z-hops when the file has no markers', () => {
    const noMarkers = ORCA_ZHOP_GCODE.split('\n').filter(l => !l.startsWith(';')).join('\n');
    const buffers = parseDetailedLayersCore(noMarkers);

    expect(buffers.layerCount).toBe(2);
    Array.from(buffers.layerZ).forEach((z, i) => expect(z).toBeCloseTo([0.2, 0.4][i], 5));
    expect(parseLayersCore(noMarkers).map(l => l.z)).toEqual([0.2, 0.4]);
  });

  it('classifies relative-E (M83) moves with positive E as extrusion and travel as moves', () => {
    const buffers = parseDetailedLayersCore(ORCA_ZHOP_GCODE);
    expect(Array.from(buffers.type)).toEqual([0, 1, 1, 0, 0, 0, 1, 0, 0, 0, 1, 1]);
  });

  it('treats retractions (negative relative E) as non-extrusion', () => {
    const buffers = parseDetailedLayersCore('M83\nG1 Z0.2\nG1 X1 Y1 E1\nG1 X1 Y1 E-0.8\nG1 X2 Y2 E1\n');
    expect(Array.from(buffers.type)).toEqual([0, 1, 0, 1]);
  });

  it('honors G92 E resets in absolute mode (M82)', () => {
    const gcode = 'M82\nG1 Z0.2\nG1 X1 Y1 E5\nG92 E0\nG1 X2 Y2 E1\nG1 X3 Y3 E1\n';
    const buffers = parseDetailedLayersCore(gcode);
    expect(Array.from(buffers.type)).toEqual([0, 1, 1, 0]);
  });
});

describe('detailedParseBuffersTransferList', () => {
  it('returns every buffer required to reconstruct a DetailedParseBuffers, in a stable order', () => {
    const buffers = parseDetailedLayersCore(THREE_LAYER_GCODE);
    const transferList = detailedParseBuffersTransferList(buffers);

    expect(transferList).toHaveLength(10);
    expect(transferList).toContain(buffers.x.buffer);
    expect(transferList).toContain(buffers.tools.buffer);
  });
});

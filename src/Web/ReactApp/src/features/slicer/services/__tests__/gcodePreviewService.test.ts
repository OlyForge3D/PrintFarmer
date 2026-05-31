import { describe, it, expect, afterEach } from 'vitest';
import { createGcodePreviewService } from '../gcodePreviewService';

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

// Z-hop to a height above the next layer (e.g. Z0.8) then drop to the real layer (Z0.4).
// The transient Z0.8 moves must NOT be counted as a layer boundary.
const ZHOP_ABOVE_NEXT_LAYER_GCODE = `; Z-hop that overshoots to height above next layer
G28 ; home
G1 Z0.2 F3000
G1 X10 Y10 E1 F1500
G1 X20 Y10 E2
; Z-hop up past the next layer height
G1 Z0.8 F5000
G1 X30 Y30
; drop to the real next layer
G1 Z0.4 F3000
G1 X10 Y10 E3 F1500
G1 X20 Y10 E4
G1 X20 Y20 E5
; another Z-hop
G1 Z0.8 F5000
G1 X0 Y0
G1 Z0.6 F3000
G1 X10 Y10 E6 F1500
G1 X20 Y10 E7
`;

// Z-hop that returns to the same layer height (retract + restore).
// Zero new layers should be created during the hop.
const ZHOP_RETURN_TO_SAME_Z_GCODE = `; Z-hop that returns to same layer height
G28 ; home
G1 Z0.2 F3000
G1 X10 Y10 E1 F1500
G1 X20 Y10 E2
; Z-hop up
G1 Z0.8 F5000
G1 X30 Y30
; Z-hop back to original layer height
G1 Z0.2 F3000
G1 X10 Y10 E3 F1500
G1 X20 Y10 E4
`;

describe('GcodePreviewService', () => {
  const service = createGcodePreviewService();

  afterEach(() => {
    service.dispose();
  });

  it('parses a 3-layer fixture and returns correct layer count', async () => {
    const result = await service.parseGCode(THREE_LAYER_GCODE);

    expect(result.layerCount).toBe(3);
    expect(result.layers).toHaveLength(3);
  });

  it('returns layer metadata for each layer', async () => {
    const result = await service.parseGCode(THREE_LAYER_GCODE);

    for (const layer of result.layers) {
      expect(layer).toHaveProperty('index');
      expect(layer).toHaveProperty('commandCount');
      expect(layer).toHaveProperty('z');
      expect(layer.commandCount).toBeGreaterThan(0);
    }
  });

  it('returns increasing Z heights across layers', async () => {
    const result = await service.parseGCode(THREE_LAYER_GCODE);

    for (let i = 1; i < result.layers.length; i++) {
      expect(result.layers[i].z).toBeGreaterThan(result.layers[i - 1].z);
    }
  });

  it('returns a Promise (async-ready for v2 worker swap)', async () => {
    const resultPromise = service.parseGCode(THREE_LAYER_GCODE);
    expect(resultPromise).toBeInstanceOf(Promise);
  });

  it('does not count Z-hop travel moves as layer boundaries', async () => {
    const result = await service.parseGCode(ZHOP_ABOVE_NEXT_LAYER_GCODE);

    // 3 real printed layers at Z=0.2, Z=0.4, Z=0.6 — the two hops to Z=0.8 must not be counted
    expect(result.layerCount).toBe(3);
    expect(result.layers.map((l) => l.z)).toEqual([0.2, 0.4, 0.6]);
  });

  it('preserves layer extrusion counts when Z-hops are present', async () => {
    const result = await service.parseGCode(ZHOP_ABOVE_NEXT_LAYER_GCODE);

    for (const layer of result.layers) {
      expect(layer.commandCount).toBeGreaterThan(0);
    }
  });

  it('does not create a new layer when a Z-hop returns to the same height', async () => {
    const result = await service.parseGCode(ZHOP_RETURN_TO_SAME_Z_GCODE);

    // All extrusion happens at Z=0.2 — one layer only
    expect(result.layerCount).toBe(1);
    expect(result.layers[0].z).toBe(0.2);
    expect(result.layers[0].commandCount).toBe(4);
  });
});

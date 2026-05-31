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
});

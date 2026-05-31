/**
 * GcodePreviewService — abstraction over G-code parsing.
 *
 * v1: lightweight synchronous layer parser (no WebGL dependency).
 * v2: will swap to gcode-preview WebGLPreview in a Web Worker with OffscreenCanvas.
 *
 * The gcode-preview package (v2.18+) is installed as the intended rendering engine
 * for v2. Its WebGLPreview class requires a real WebGL context, so v1 uses a
 * standalone parser to avoid DOM/GPU coupling in service code and tests.
 */

export interface ParsedLayer {
  index: number;
  commandCount: number;
  lineNumber: number;
  z: number;
}

export interface ParsedGCode {
  layers: ParsedLayer[];
  layerCount: number;
}

export interface IGcodePreviewService {
  parseGCode(gcodeText: string): Promise<ParsedGCode>;
  dispose(): void;
}

interface LayerAccumulator {
  z: number;
  commandCount: number;
  lineNumber: number;
}

function parseLayersFromGCode(gcodeText: string): ParsedLayer[] {
  const lines = gcodeText.split('\n');
  const layerAccumulators: LayerAccumulator[] = [];
  let currentZ = -Infinity;

  for (let i = 0; i < lines.length; i++) {
    const line = lines[i].trim();
    if (!line || line.startsWith(';')) continue;

    const isMove = line.startsWith('G0 ') || line.startsWith('G1 ');
    if (!isMove) continue;

    const zMatch = line.match(/Z([\d.]+)/);
    if (zMatch) {
      const z = parseFloat(zMatch[1]);
      if (z > currentZ) {
        currentZ = z;
        layerAccumulators.push({ z, commandCount: 0, lineNumber: i + 1 });
      }
    }

    if (layerAccumulators.length > 0 && line.match(/E[\d.]+/)) {
      layerAccumulators[layerAccumulators.length - 1].commandCount++;
    }
  }

  return layerAccumulators.map((acc, idx) => ({
    index: idx,
    commandCount: acc.commandCount,
    lineNumber: acc.lineNumber,
    z: acc.z,
  }));
}

/**
 * v1 implementation: synchronous parser wrapped in a resolved Promise.
 * v2 swap will replace this body with a Web Worker + gcode-preview WebGLPreview —
 * zero component changes needed.
 */
export function createGcodePreviewService(): IGcodePreviewService {
  return {
    async parseGCode(gcodeText: string): Promise<ParsedGCode> {
      const layers = parseLayersFromGCode(gcodeText);
      return { layers, layerCount: layers.length };
    },

    dispose(): void {
      // v1: no resources to release
      // v2: will terminate Web Worker here
    },
  };
}

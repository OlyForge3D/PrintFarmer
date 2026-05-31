/**
 * GcodePreviewService — abstraction over G-code parsing.
 *
 * v1: lightweight synchronous layer parser (no WebGL dependency).
 * v2: will swap to gcode-preview WebGLPreview in a Web Worker with OffscreenCanvas.
 *
 * Why the regex parser and not the `gcode-preview` library for v1:
 * The `gcode-preview` package (v2.18+) exports only `WebGLPreview` and `init`.
 * Its internal `Parser` class is not part of the public API surface.
 * `WebGLPreview` requires a DOM canvas + WebGL context, making it unsuitable
 * for headless parsing in service code and unit tests.
 *
 * This regex parser is the intentional v1 design choice. It matches
 * gcode-preview's layer-detection semantics: a new layer is confirmed only
 * when the first extrusion command appears at the new Z height (a bare Z move
 * without extrusion is treated as a Z-hop candidate until extrusion confirms it).
 *
 * The v2 swap will replace this body with Web Worker + OffscreenCanvas —
 * zero component changes needed at that point.
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
  let currentLayerZ = -Infinity;
  // Tracks a Z increase that hasn't been confirmed as a real layer yet.
  // A bare "G1 Z<h>" without extrusion could be a Z-hop travel move; we only
  // promote it to a new layer when the first extrusion at that height appears.
  let pendingZ: number | null = null;

  for (let i = 0; i < lines.length; i++) {
    const line = lines[i].trim();
    if (!line || line.startsWith(';')) continue;

    const isMove = line.startsWith('G0 ') || line.startsWith('G1 ');
    if (!isMove) continue;

    const zMatch = line.match(/Z([\d.]+)/);
    const hasExtrusion = /E[\d.]+/.test(line);

    if (zMatch) {
      const z = parseFloat(zMatch[1]);
      if (z > currentLayerZ) {
        // Z went up — candidate for a new layer, but could be a Z-hop.
        pendingZ = z;
      } else {
        // Z dropped back (or stayed the same) — cancel any pending Z-hop candidate.
        pendingZ = null;
      }
    }

    if (hasExtrusion) {
      if (pendingZ !== null) {
        // First extrusion after a Z increase confirms a real layer transition.
        currentLayerZ = pendingZ;
        pendingZ = null;
        layerAccumulators.push({ z: currentLayerZ, commandCount: 1, lineNumber: i + 1 });
      } else if (layerAccumulators.length > 0) {
        layerAccumulators[layerAccumulators.length - 1].commandCount++;
      }
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

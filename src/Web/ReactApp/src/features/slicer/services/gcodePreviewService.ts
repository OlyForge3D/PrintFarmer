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

/** A single parsed move/extrusion point with tool info. */
export interface GCodePoint {
  x: number;
  y: number;
  z: number;
  e: number;
  feedRate: number;
  type: 'move' | 'extrude';
  tool: number;
}

/** A layer with full point data for Three.js rendering. */
export interface DetailedLayer {
  index: number;
  z: number;
  points: GCodePoint[];
}

/** Full parse result including rendering data and tool info. */
export interface DetailedParsedGCode {
  layers: DetailedLayer[];
  layerCount: number;
  tools: number[];
}

export interface IGcodePreviewService {
  parseGCode(gcodeText: string): Promise<ParsedGCode>;
  parseGCodeDetailed(gcodeText: string): Promise<DetailedParsedGCode>;
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

function parseDetailedLayers(gcodeText: string): { layers: DetailedLayer[]; tools: number[] } {
  const lines = gcodeText.split('\n');
  const layerMap = new Map<number, GCodePoint[]>();
  const toolsFound = new Set<number>();
  let currentTool = 0;
  let pos = { x: 0, y: 0, z: 0, e: 0, f: 0 };

  for (const rawLine of lines) {
    const line = rawLine.split(';')[0].trim();
    if (!line) continue;

    // Tool change
    const toolMatch = line.match(/^T(\d+)/);
    if (toolMatch) {
      currentTool = parseInt(toolMatch[1], 10);
      toolsFound.add(currentTool);
      continue;
    }

    if (!line.startsWith('G0') && !line.startsWith('G1')) continue;

    const x = line.match(/X([-\d.]+)/)?.[1];
    const y = line.match(/Y([-\d.]+)/)?.[1];
    const z = line.match(/Z([-\d.]+)/)?.[1];
    const e = line.match(/E([-\d.]+)/)?.[1];
    const f = line.match(/F([\d.]+)/)?.[1];

    const newPos = {
      x: x ? parseFloat(x) : pos.x,
      y: y ? parseFloat(y) : pos.y,
      z: z ? parseFloat(z) : pos.z,
      e: e ? parseFloat(e) : pos.e,
      f: f ? parseFloat(f) : pos.f,
    };

    const isExtrude = e !== undefined && parseFloat(e) > pos.e;
    const layerZ = Math.round(newPos.z * 100) / 100;

    if (!layerMap.has(layerZ)) {
      layerMap.set(layerZ, []);
    }

    layerMap.get(layerZ)!.push({
      x: newPos.x,
      y: newPos.y,
      z: newPos.z,
      e: newPos.e,
      feedRate: newPos.f,
      type: isExtrude ? 'extrude' : 'move',
      tool: currentTool,
    });

    pos = newPos;
  }

  // Ensure tool 0 is always present
  if (toolsFound.size === 0) toolsFound.add(0);

  const layers = Array.from(layerMap.entries())
    .sort(([a], [b]) => a - b)
    .map(([z, points], idx) => ({ index: idx, z, points }));

  return { layers, tools: Array.from(toolsFound).sort((a, b) => a - b) };
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

    async parseGCodeDetailed(gcodeText: string): Promise<DetailedParsedGCode> {
      const { layers, tools } = parseDetailedLayers(gcodeText);
      return { layers, layerCount: layers.length, tools };
    },

    dispose(): void {
      // v1: no resources to release
      // v2: will terminate Web Worker here
    },
  };
}

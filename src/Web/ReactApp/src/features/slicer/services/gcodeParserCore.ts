/**
 * Pure, environment-agnostic G-code parsing core.
 *
 * Extracted so the exact same parsing logic can run either inside a Web
 * Worker (see `gcodeParser.worker.ts`) or, as a fallback, on the main thread
 * when `Worker` is unavailable (SSR, older browsers, or the jsdom test
 * environment).
 *
 * `parseDetailedLayersCore` intentionally builds parallel typed arrays
 * (Structure-of-Arrays) instead of an array of per-point objects. That
 * representation is what makes the worker boundary cheap: typed arrays are
 * *transferable* via `postMessage`'s transfer list (zero-copy, ownership
 * moves to the receiver) whereas an array of ~1M plain point objects would
 * be structured-cloned — duplicating the retained heap and adding real
 * serialization CPU. See #1788 / #1766 for the measurements that motivated
 * this.
 */

import type { ParsedLayer } from './gcodePreviewService';

/** Structure-of-Arrays representation of every parsed point, grouped by layer. */
export interface DetailedParseBuffers {
  x: Float32Array;
  y: Float32Array;
  z: Float32Array;
  e: Float32Array;
  feedRate: Float32Array;
  /** Tool index per point. */
  tool: Int32Array;
  /** 0 = move, 1 = extrude, per point. */
  type: Uint8Array;
  /** Index into the point arrays where each layer begins (length = layerCount). */
  layerStart: Int32Array;
  /** Z height per layer (length = layerCount). */
  layerZ: Float32Array;
  /** Sorted distinct tool ids found in the file. */
  tools: Int32Array;
  pointCount: number;
  layerCount: number;
}

interface LayerAccumulator {
  z: number;
  commandCount: number;
  lineNumber: number;
}

/** Lightweight layer-index-only parse (used by `parseGCode`). Cheap — not worker-routed. */
export function parseLayersCore(gcodeText: string): ParsedLayer[] {
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
 * Full detailed parse producing typed-array buffers grouped by layer
 * (ascending Z), matching the grouping/ordering the previous
 * object-per-point implementation used.
 */
export function parseDetailedLayersCore(gcodeText: string): DetailedParseBuffers {
  const lines = gcodeText.split('\n');

  const xs: number[] = [];
  const ys: number[] = [];
  const zs: number[] = [];
  const es: number[] = [];
  const fs: number[] = [];
  const types: number[] = [];
  const toolsPerPoint: number[] = [];
  const layerKeys: number[] = [];

  const toolsFound = new Set<number>();
  let currentTool = 0;
  let pos = { x: 0, y: 0, z: 0, e: 0, f: 0 };

  for (const rawLine of lines) {
    const line = rawLine.split(';')[0].trim();
    if (!line) continue;

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

    xs.push(newPos.x);
    ys.push(newPos.y);
    zs.push(newPos.z);
    es.push(newPos.e);
    fs.push(newPos.f);
    types.push(isExtrude ? 1 : 0);
    toolsPerPoint.push(currentTool);
    layerKeys.push(layerZ);

    pos = newPos;
  }

  if (toolsFound.size === 0) toolsFound.add(0);

  // Group point indices by layer key, preserving encounter order within a
  // layer (matches the previous Map<z, GCodePoint[]>.push behaviour).
  const layerIndexMap = new Map<number, number[]>();
  for (let i = 0; i < layerKeys.length; i++) {
    const key = layerKeys[i];
    let indices = layerIndexMap.get(key);
    if (!indices) {
      indices = [];
      layerIndexMap.set(key, indices);
    }
    indices.push(i);
  }

  const sortedKeys = Array.from(layerIndexMap.keys()).sort((a, b) => a - b);
  const pointCount = xs.length;

  const outX = new Float32Array(pointCount);
  const outY = new Float32Array(pointCount);
  const outZ = new Float32Array(pointCount);
  const outE = new Float32Array(pointCount);
  const outF = new Float32Array(pointCount);
  const outType = new Uint8Array(pointCount);
  const outTool = new Int32Array(pointCount);
  const layerStart = new Int32Array(sortedKeys.length);
  const layerZArr = new Float32Array(sortedKeys.length);

  let cursor = 0;
  for (let li = 0; li < sortedKeys.length; li++) {
    const key = sortedKeys[li];
    layerStart[li] = cursor;
    layerZArr[li] = key;

    const indices = layerIndexMap.get(key)!;
    for (const idx of indices) {
      outX[cursor] = xs[idx];
      outY[cursor] = ys[idx];
      outZ[cursor] = zs[idx];
      outE[cursor] = es[idx];
      outF[cursor] = fs[idx];
      outType[cursor] = types[idx];
      outTool[cursor] = toolsPerPoint[idx];
      cursor++;
    }
  }

  return {
    x: outX,
    y: outY,
    z: outZ,
    e: outE,
    feedRate: outF,
    tool: outTool,
    type: outType,
    layerStart,
    layerZ: layerZArr,
    tools: Int32Array.from(Array.from(toolsFound).sort((a, b) => a - b)),
    pointCount,
    layerCount: sortedKeys.length,
  };
}

/** Transfer list (the underlying ArrayBuffers) for a `DetailedParseBuffers` postMessage. */
export function detailedParseBuffersTransferList(buffers: DetailedParseBuffers): ArrayBuffer[] {
  // Typed arrays created via `.from`/`new TypedArray(length)` always back onto a plain
  // ArrayBuffer, never a SharedArrayBuffer, but TS types `.buffer` as `ArrayBufferLike`.
  return [
    buffers.x.buffer,
    buffers.y.buffer,
    buffers.z.buffer,
    buffers.e.buffer,
    buffers.feedRate.buffer,
    buffers.tool.buffer,
    buffers.type.buffer,
    buffers.layerStart.buffer,
    buffers.layerZ.buffer,
    buffers.tools.buffer,
  ] as ArrayBuffer[];
}

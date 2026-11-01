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

const LAYER_CHANGE_MARKER = ';LAYER_CHANGE';
const Z_MARKER = ';Z:';

/** Rounds a Z height to the 0.01 mm grid used for layer identity. */
function layerKey(z: number): number {
  return Math.round(z * 100) / 100;
}

/**
 * Calls `visit` for every line in `text` without materializing a
 * `string[]` of all lines (a 45 MB file has ~1.7M lines).
 */
function forEachLine(text: string, visit: (line: string) => void): void {
  let start = 0;
  const length = text.length;
  while (start < length) {
    let end = text.indexOf('\n', start);
    if (end === -1) end = length;
    visit(text.slice(start, end));
    start = end + 1;
  }
}

/**
 * Tracks layer boundaries for a stream of G0/G1 points.
 *
 * When the file contains slicer `;LAYER_CHANGE` markers (OrcaSlicer, Prusa,
 * Bambu), each marker starts a layer and the following `;Z:` comment gives
 * its height — this is the slicer's own layer count. Without markers, a layer
 * starts at the first point reached at a new Z, but only once an extrusion
 * happens at that Z, so Z-hop travel heights never become layers.
 */
class LayerTracker {
  readonly starts: number[] = [];
  readonly zs: number[] = [];
  private markerZ: number | null = null;
  private markerPending = false;
  private candidateStart = -1;
  private candidateZ = Number.NaN;
  private currentZ = Number.NaN;

  constructor(private readonly useMarkers: boolean) {}

  /** Handles a comment line, recording `;LAYER_CHANGE` / `;Z:` markers. */
  comment(line: string, nextPointIndex: number): void {
    if (!this.useMarkers) return;
    if (line.startsWith(LAYER_CHANGE_MARKER)) {
      this.markerPending = true;
      this.markerZ = null;
      this.starts.push(nextPointIndex);
      this.zs.push(Number.NaN);
    } else if (this.markerPending && line.startsWith(Z_MARKER)) {
      const z = parseFloat(line.slice(Z_MARKER.length));
      if (!Number.isNaN(z)) {
        this.markerZ = layerKey(z);
        this.zs[this.zs.length - 1] = this.markerZ;
      }
    }
  }

  point(index: number, z: number, isExtrude: boolean): void {
    if (this.useMarkers) {
      if (this.markerPending) {
        if (this.markerZ === null) this.zs[this.zs.length - 1] = layerKey(z);
        this.markerPending = false;
      }
      return;
    }

    const key = layerKey(z);
    if (key !== this.candidateZ) {
      this.candidateZ = key;
      this.candidateStart = index;
    }
    if (isExtrude && key !== this.currentZ) {
      this.currentZ = key;
      this.starts.push(this.candidateStart);
      this.zs.push(key);
    }
  }

  /** Finalizes boundaries: drops empty layers and folds leading points into layer 0. */
  finish(pointCount: number): { starts: number[]; zs: number[] } {
    const starts: number[] = [];
    const zs: number[] = [];
    for (let i = 0; i < this.starts.length; i++) {
      const start = this.starts[i];
      const end = i + 1 < this.starts.length ? this.starts[i + 1] : pointCount;
      if (end <= start || Number.isNaN(this.zs[i])) continue;
      starts.push(start);
      zs.push(this.zs[i]);
    }
    if (starts.length > 0) starts[0] = 0;
    return { starts, zs };
  }
}

/** Lightweight layer-index-only parse (used by `parseGCode`). Cheap — not worker-routed. */
export function parseLayersCore(gcodeText: string): ParsedLayer[] {
  const tracker = new LayerTracker(gcodeText.includes(LAYER_CHANGE_MARKER));
  const pointLines: number[] = [];
  const pointExtrudes: number[] = [];
  const state = new MotionState();
  let lineNumber = 0;

  forEachLine(gcodeText, rawLine => {
    lineNumber++;
    const trimmed = rawLine.trim();
    if (trimmed.startsWith(';')) {
      tracker.comment(trimmed, pointLines.length);
      return;
    }
    const move = state.apply(trimmed);
    if (!move) return;
    tracker.point(pointLines.length, move.z, move.isExtrude);
    pointLines.push(lineNumber);
    pointExtrudes.push(move.isExtrude ? 1 : 0);
  });

  const { starts, zs } = tracker.finish(pointLines.length);
  return starts.map((start, idx) => {
    const end = idx + 1 < starts.length ? starts[idx + 1] : pointLines.length;
    let commandCount = 0;
    for (let i = start; i < end; i++) commandCount += pointExtrudes[i];
    return { index: idx, commandCount, lineNumber: pointLines[start], z: zs[idx] };
  });
}

interface Move {
  x: number;
  y: number;
  z: number;
  e: number;
  f: number;
  isExtrude: boolean;
}

/**
 * Tracks printer motion state across lines, including absolute (M82) vs
 * relative (M83) extrusion and `G92 E` resets. OrcaSlicer emits relative E,
 * so "E increased" is not a valid extrusion test there.
 */
class MotionState {
  x = 0;
  y = 0;
  z = 0;
  e = 0;
  f = 0;
  tool = 0;
  relativeE = false;
  readonly tools = new Set<number>();

  /** Applies one comment-stripped line; returns the resulting point for G0/G1 moves. */
  apply(rawLine: string): Move | null {
    const semicolon = rawLine.indexOf(';');
    const line = (semicolon === -1 ? rawLine : rawLine.slice(0, semicolon)).trim();
    if (!line) return null;

    const first = line.charCodeAt(0);
    if (first === 84 /* T */) {
      const toolMatch = line.match(/^T(\d+)/);
      if (toolMatch) {
        this.tool = parseInt(toolMatch[1], 10);
        this.tools.add(this.tool);
      }
      return null;
    }
    if (first === 77 /* M */) {
      if (line.startsWith('M83')) this.relativeE = true;
      else if (line.startsWith('M82')) this.relativeE = false;
      return null;
    }
    if (line.startsWith('G92')) {
      const e = line.match(/E([-\d.]+)/)?.[1];
      if (e !== undefined) this.e = parseFloat(e);
      return null;
    }
    if (!/^G[01](?!\d)/.test(line)) return null;

    const x = line.match(/X([-\d.]+)/)?.[1];
    const y = line.match(/Y([-\d.]+)/)?.[1];
    const z = line.match(/Z([-\d.]+)/)?.[1];
    const e = line.match(/E([-\d.]+)/)?.[1];
    const f = line.match(/F([\d.]+)/)?.[1];

    let isExtrude = false;
    if (e !== undefined) {
      const eValue = parseFloat(e);
      if (this.relativeE) {
        isExtrude = eValue > 0;
        this.e += eValue;
      } else {
        isExtrude = eValue > this.e;
        this.e = eValue;
      }
    }
    // A pure E move (retract/unretract) has no travel.
    if (isExtrude && x === undefined && y === undefined) isExtrude = false;

    if (x !== undefined) this.x = parseFloat(x);
    if (y !== undefined) this.y = parseFloat(y);
    if (z !== undefined) this.z = parseFloat(z);
    if (f !== undefined) this.f = parseFloat(f);

    return { x: this.x, y: this.y, z: this.z, e: this.e, f: this.f, isExtrude };
  }
}

class GrowableFloat32 {
  data = new Float32Array(1 << 16);
  length = 0;
  push(value: number): void {
    if (this.length === this.data.length) {
      const next = new Float32Array(this.data.length * 2);
      next.set(this.data);
      this.data = next;
    }
    this.data[this.length++] = value;
  }
  toArray(): Float32Array {
    return this.data.slice(0, this.length);
  }
}

/**
 * Full detailed parse producing typed-array buffers grouped by layer.
 * Points stay in file order; layer boundaries come from `LayerTracker`.
 */
export function parseDetailedLayersCore(gcodeText: string): DetailedParseBuffers {
  const tracker = new LayerTracker(gcodeText.includes(LAYER_CHANGE_MARKER));
  const state = new MotionState();

  const xs = new GrowableFloat32();
  const ys = new GrowableFloat32();
  const zs = new GrowableFloat32();
  const es = new GrowableFloat32();
  const fs = new GrowableFloat32();
  const types = new GrowableFloat32();
  const toolsPerPoint = new GrowableFloat32();

  forEachLine(gcodeText, rawLine => {
    const trimmed = rawLine.trim();
    if (trimmed.startsWith(';')) {
      tracker.comment(trimmed, xs.length);
      return;
    }
    const move = state.apply(trimmed);
    if (!move) return;

    tracker.point(xs.length, move.z, move.isExtrude);
    xs.push(move.x);
    ys.push(move.y);
    zs.push(move.z);
    es.push(move.e);
    fs.push(move.f);
    types.push(move.isExtrude ? 1 : 0);
    toolsPerPoint.push(state.tool);
  });

  if (state.tools.size === 0) state.tools.add(0);

  const pointCount = xs.length;
  const { starts, zs: layerZs } = tracker.finish(pointCount);

  return {
    x: xs.toArray(),
    y: ys.toArray(),
    z: zs.toArray(),
    e: es.toArray(),
    feedRate: fs.toArray(),
    tool: Int32Array.from(toolsPerPoint.data.subarray(0, pointCount)),
    type: Uint8Array.from(types.data.subarray(0, pointCount)),
    layerStart: Int32Array.from(starts),
    layerZ: Float32Array.from(layerZs),
    tools: Int32Array.from(Array.from(state.tools).sort((a, b) => a - b)),
    pointCount,
    layerCount: starts.length,
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

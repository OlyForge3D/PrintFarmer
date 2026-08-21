/**
 * GcodePreviewService — abstraction over G-code parsing.
 *
 * v1: lightweight synchronous layer parser (no WebGL dependency).
 * v2 (#1788): `parseGCodeDetailed` now runs in a dedicated Web Worker when
 * one is available, using transferable typed arrays (Structure-of-Arrays)
 * to move the parse cost off the main thread without the structured-clone
 * copy overhead a plain array-of-objects payload would incur. See #1766 for
 * the measurements that motivated this (single long tasks of 51.6ms–~1s).
 *
 * The worker fetches the G-code text itself from the URL it is given —
 * `parseGCodeDetailed` now takes a `gcodeUrl`, not the raw text, so the
 * (potentially tens-of-MB) text is never posted into the worker as a
 * structured-clone payload.
 *
 * Environments without `Worker` (SSR, very old browsers, the jsdom test
 * environment) fall back to running the exact same parsing core
 * synchronously on the main thread, fetching the URL directly.
 *
 * The gcode-preview package (v2.18+) is installed as the intended rendering
 * engine for a future v3. Its WebGLPreview class requires a real WebGL
 * context, so this service uses a standalone parser to avoid DOM/GPU
 * coupling in service code and tests.
 */

import {
  parseLayersCore,
  parseDetailedLayersCore,
  detailedParseBuffersTransferList,
  type DetailedParseBuffers,
} from './gcodeParserCore';

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

/**
 * A layer's point data as typed-array views (Structure-of-Arrays), for
 * Three.js rendering.
 *
 * These are `Float32Array`/`Uint8Array`/`Int32Array` *views* (`.subarray`)
 * over the buffers the worker transferred back — building this layer index
 * costs O(layerCount), not O(pointCount): no per-point `GCodePoint` object is
 * materialized here. Per #1788, materializing every point into a discrete JS
 * object at this boundary would reintroduce the ~130MB retained-heap problem
 * the worker + typed-array parse was built to avoid. Consumers (GCodePath)
 * read individual points via `pointAt(layer, i)` only for the points they
 * actually render.
 */
export interface DetailedLayer {
  index: number;
  z: number;
  count: number;
  x: Float32Array;
  y: Float32Array;
  /** Per-point Z (usually == the layer's `z`, but Z-hop moves can differ). */
  pz: Float32Array;
  e: Float32Array;
  feedRate: Float32Array;
  /** 0 = move, 1 = extrude. */
  type: Uint8Array;
  tool: Int32Array;
}

/** Reads a single point out of a `DetailedLayer`'s typed-array views on demand. */
export function pointAt(layer: DetailedLayer, i: number): GCodePoint {
  return {
    x: layer.x[i],
    y: layer.y[i],
    z: layer.pz[i],
    e: layer.e[i],
    feedRate: layer.feedRate[i],
    type: layer.type[i] === 1 ? 'extrude' : 'move',
    tool: layer.tool[i],
  };
}

/** Full parse result including rendering data and tool info. */
export interface DetailedParsedGCode {
  layers: DetailedLayer[];
  layerCount: number;
  tools: number[];
}

export interface IGcodePreviewService {
  parseGCode(gcodeText: string): Promise<ParsedGCode>;
  /**
   * Parses the detailed (per-point) G-code data for 3D rendering.
   *
   * Takes the G-code file's URL, not its text: the parse runs in a Web
   * Worker that fetches the file itself, so the raw text never round-trips
   * across the worker boundary as a structured-clone payload (#1788).
   */
  parseGCodeDetailed(gcodeUrl: string): Promise<DetailedParsedGCode>;
  dispose(): void;
}

/** Builds the (mostly zero-copy) layer index from typed-array buffers consumers (GCodeViewer3D) read. */
function buffersToDetailedParsedGCode(buffers: DetailedParseBuffers): DetailedParsedGCode {
  const layers: DetailedLayer[] = new Array(buffers.layerCount);

  for (let li = 0; li < buffers.layerCount; li++) {
    const start = buffers.layerStart[li];
    const end = li + 1 < buffers.layerCount ? buffers.layerStart[li + 1] : buffers.pointCount;

    layers[li] = {
      index: li,
      z: buffers.layerZ[li],
      count: end - start,
      // `.subarray` is a zero-copy view over the same underlying ArrayBuffer
      // — no per-point object is allocated building this layer index.
      x: buffers.x.subarray(start, end),
      y: buffers.y.subarray(start, end),
      pz: buffers.z.subarray(start, end),
      e: buffers.e.subarray(start, end),
      feedRate: buffers.feedRate.subarray(start, end),
      type: buffers.type.subarray(start, end),
      tool: buffers.tool.subarray(start, end),
    };
  }

  return { layers, layerCount: buffers.layerCount, tools: Array.from(buffers.tools) };
}

async function fetchGCodeText(gcodeUrl: string, signal: AbortSignal): Promise<string> {
  const res = await fetch(gcodeUrl, { signal });
  if (!res.ok) throw new Error(`Failed to load G-code: ${res.status}`);
  return res.text();
}

function isWorkerSupported(): boolean {
  return typeof Worker !== 'undefined';
}

interface PendingRequest {
  resolve: (buffers: DetailedParseBuffers) => void;
  reject: (error: Error) => void;
}

/**
 * v1/v2 implementation.
 * - `parseGCode` (layer index only, cheap) stays synchronous on the main
 *   thread — the #1766 spike measured this at 9-12ms with zero long tasks
 *   even at 1.2M-point scale, so it's out of scope for #1788.
 * - `parseGCodeDetailed` (the flagged 51.6ms-~1s long task) is routed to a
 *   dedicated Web Worker when available, falling back to the same parsing
 *   core on the main thread otherwise.
 */
export function createGcodePreviewService(): IGcodePreviewService {
  let worker: Worker | null = null;
  let requestSeq = 0;
  const pending = new Map<number, PendingRequest>();
  // Tracks in-flight fallback (no-Worker) fetches so dispose() can abort them
  // the same way it rejects pending Worker requests.
  const fallbackControllers = new Set<AbortController>();

  function rejectAllPending(error: Error): void {
    for (const [id, request] of pending) {
      request.reject(error);
      pending.delete(id);
    }
  }

  function ensureWorker(): Worker {
    if (worker) return worker;

    worker = new Worker(new URL('./gcodeParser.worker.ts', import.meta.url), { type: 'module' });

    worker.onmessage = (event: MessageEvent) => {
      const data = event.data as { requestId: number; ok: boolean; buffers?: DetailedParseBuffers; error?: string };
      const request = pending.get(data.requestId);
      if (!request) return;
      pending.delete(data.requestId);

      if (data.ok && data.buffers) {
        request.resolve(data.buffers);
      } else {
        request.reject(new Error(data.error ?? 'Unknown G-code worker error'));
      }
    };

    worker.onerror = (event: ErrorEvent) => {
      rejectAllPending(new Error(event.message || 'G-code worker error'));
    };

    return worker;
  }

  return {
    async parseGCode(gcodeText: string): Promise<ParsedGCode> {
      const layers = parseLayersCore(gcodeText);
      return { layers, layerCount: layers.length };
    },

    async parseGCodeDetailed(gcodeUrl: string): Promise<DetailedParsedGCode> {
      if (isWorkerSupported()) {
        const w = ensureWorker();
        const requestId = ++requestSeq;

        const buffers = await new Promise<DetailedParseBuffers>((resolve, reject) => {
          pending.set(requestId, { resolve, reject });
          w.postMessage({ requestId, gcodeUrl });
        });

        return buffersToDetailedParsedGCode(buffers);
      }

      // Fallback for environments without Worker support (SSR, legacy
      // browsers, jsdom test environment): run the same parsing core
      // synchronously on the main thread. Tracked via AbortController so
      // dispose() can cancel it, mirroring the Worker path's pending-request
      // rejection.
      const controller = new AbortController();
      fallbackControllers.add(controller);
      try {
        const gcodeText = await fetchGCodeText(gcodeUrl, controller.signal);
        const buffers = parseDetailedLayersCore(gcodeText);
        return buffersToDetailedParsedGCode(buffers);
      } finally {
        fallbackControllers.delete(controller);
      }
    },

    dispose(): void {
      if (worker) {
        worker.terminate();
        worker = null;
      }
      rejectAllPending(new Error('G-code preview service disposed'));
      for (const controller of fallbackControllers) {
        controller.abort();
      }
      fallbackControllers.clear();
    },
  };
}

// Exported for tests exercising the typed-array transfer contract directly.
export { detailedParseBuffersTransferList };

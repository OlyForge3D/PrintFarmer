import type { DetailedLayer } from '@/features/slicer/services';

export const COLOR_MODE_LAYER = 0;
export const COLOR_MODE_SPEED = 1;
export const COLOR_MODE_EXTRUSION = 2;

const MIN_FEED_MM_PER_S = 20;
const MAX_FEED_MM_PER_S = 150;
const EXTRUSION_COLOR: Rgb = [0xff, 0x6b, 0x35];

type Rgb = [number, number, number];

/**
 * One merged `LineSegments` vertex stream. Each segment contributes two
 * vertices, so `positions.length === 6 * segments` and
 * `colors.length === 6 * segments`. `layerEnds[i]` is the vertex count that
 * covers layers `0..i`, so the layer slider maps to `setDrawRange(0, layerEnds[i])`
 * without rebuilding geometry.
 */
export interface GcodeLineStream {
  positions: Float32Array;
  /** Normalized RGB (0-255) per vertex. */
  colors: Uint8Array;
  layerEnds: Uint32Array;
}

export interface GcodeLineBuffers {
  extrude: GcodeLineStream;
  travel: GcodeLineStream;
}

export interface BuildGcodeLineBuffersOptions {
  colorMode: number;
  minFeedColor: string;
  maxFeedColor: string;
  /** When provided, only segments whose end point uses an enabled tool are emitted. */
  enabledTools?: ReadonlySet<number>;
}

/**
 * Converts the parsed per-layer typed arrays into exactly two vertex streams
 * (extrusion + travel). The number of render objects and draw calls therefore
 * stays constant regardless of file size, unlike the previous one-`<Line>`-per-
 * run approach that allocated a `Vector3` per point and a mesh per run and
 * exhausted memory on large (~45 MB) G-code files.
 *
 * A segment runs from point `i-1` to point `i` and takes point `i`'s type,
 * because the parser records each move's end point and the move's type.
 */
export function buildGcodeLineBuffers(
  layers: readonly DetailedLayer[],
  options: BuildGcodeLineBuffersOptions,
): GcodeLineBuffers {
  const { colorMode, enabledTools } = options;
  const minColor = parseHexColor(options.minFeedColor);
  const maxColor = parseHexColor(options.maxFeedColor);

  let extrudeSegments = 0;
  let travelSegments = 0;
  let hasPrev = false;
  for (const layer of layers) {
    for (let i = 0; i < layer.count; i++) {
      if (hasPrev && (!enabledTools || enabledTools.has(layer.tool[i]))) {
        if (layer.type[i] === 1) extrudeSegments++;
        else travelSegments++;
      }
      hasPrev = true;
    }
  }

  const extrude = allocateStream(extrudeSegments, layers.length);
  const travel = allocateStream(travelSegments, layers.length);

  let ev = 0;
  let tv = 0;
  hasPrev = false;
  let px = 0;
  let py = 0;
  let pz = 0;
  const segmentColor: Rgb = [0, 0, 0];

  for (let li = 0; li < layers.length; li++) {
    const layer = layers[li];
    const layerColor = colorMode === COLOR_MODE_LAYER ? layerHueColor(li) : null;

    for (let i = 0; i < layer.count; i++) {
      const x = layer.x[i];
      const y = layer.y[i];
      const z = layer.pz[i];

      if (hasPrev && (!enabledTools || enabledTools.has(layer.tool[i]))) {
        if (layer.type[i] === 1) {
          if (layerColor) {
            segmentColor[0] = layerColor[0];
            segmentColor[1] = layerColor[1];
            segmentColor[2] = layerColor[2];
          } else if (colorMode === COLOR_MODE_SPEED) {
            lerpRgb(minColor, maxColor, normalizeFeed(layer.feedRate[i]), segmentColor);
          } else {
            segmentColor[0] = EXTRUSION_COLOR[0];
            segmentColor[1] = EXTRUSION_COLOR[1];
            segmentColor[2] = EXTRUSION_COLOR[2];
          }
          writeSegment(extrude, ev, px, py, pz, x, y, z, segmentColor);
          ev += 2;
        } else {
          writeSegment(travel, tv, px, py, pz, x, y, z, TRAVEL_COLOR);
          tv += 2;
        }
      }

      px = x;
      py = y;
      pz = z;
      hasPrev = true;
    }

    extrude.layerEnds[li] = ev;
    travel.layerEnds[li] = tv;
  }

  return { extrude, travel };
}

const TRAVEL_COLOR: Rgb = [0x66, 0x66, 0x66];

function allocateStream(segments: number, layerCount: number): GcodeLineStream {
  return {
    positions: new Float32Array(segments * 6),
    colors: new Uint8Array(segments * 6),
    layerEnds: new Uint32Array(layerCount),
  };
}

function writeSegment(
  stream: GcodeLineStream,
  vertex: number,
  ax: number, ay: number, az: number,
  bx: number, by: number, bz: number,
  color: Rgb,
): void {
  const o = vertex * 3;
  const p = stream.positions;
  p[o] = ax; p[o + 1] = ay; p[o + 2] = az;
  p[o + 3] = bx; p[o + 4] = by; p[o + 5] = bz;
  const c = stream.colors;
  c[o] = color[0]; c[o + 1] = color[1]; c[o + 2] = color[2];
  c[o + 3] = color[0]; c[o + 4] = color[1]; c[o + 5] = color[2];
}

// Feed rates are stored in mm/min; the color ramp is defined in mm/s.
function normalizeFeed(feedMmPerMin: number): number {
  const feed = (feedMmPerMin || 0) / 60;
  return Math.max(0, Math.min(1, (feed - MIN_FEED_MM_PER_S) / (MAX_FEED_MM_PER_S - MIN_FEED_MM_PER_S)));
}

function lerpRgb(a: Rgb, b: Rgb, t: number, out: Rgb): void {
  out[0] = Math.round(a[0] + (b[0] - a[0]) * t);
  out[1] = Math.round(a[1] + (b[1] - a[1]) * t);
  out[2] = Math.round(a[2] + (b[2] - a[2]) * t);
}

/** Deterministic, well-spread hue per layer (golden-ratio stepping). */
export function layerHueColor(layerIndex: number): Rgb {
  const hue = (layerIndex * 0.618033988749895) % 1;
  return hslToRgb(hue, 0.8, 0.6);
}

function hslToRgb(h: number, s: number, l: number): Rgb {
  const q = l < 0.5 ? l * (1 + s) : l + s - l * s;
  const p = 2 * l - q;
  const channel = (t: number): number => {
    let tt = t;
    if (tt < 0) tt += 1;
    if (tt > 1) tt -= 1;
    if (tt < 1 / 6) return p + (q - p) * 6 * tt;
    if (tt < 1 / 2) return q;
    if (tt < 2 / 3) return p + (q - p) * (2 / 3 - tt) * 6;
    return p;
  };
  return [
    Math.round(channel(h + 1 / 3) * 255),
    Math.round(channel(h) * 255),
    Math.round(channel(h - 1 / 3) * 255),
  ];
}

function parseHexColor(hex: string): Rgb {
  const value = hex.replace('#', '');
  const full = value.length === 3 ? value.split('').map(c => c + c).join('') : value;
  const n = Number.parseInt(full.slice(0, 6), 16);
  if (Number.isNaN(n)) return [0, 0, 0];
  return [(n >> 16) & 0xff, (n >> 8) & 0xff, n & 0xff];
}

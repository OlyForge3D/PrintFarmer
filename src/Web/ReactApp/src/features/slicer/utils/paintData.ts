/**
 * Paint data serialization, deserialization, and manipulation utilities.
 * Handles per-model face paint data for support, seam, fuzzy skin (binary)
 * and color painting (multi-value per face).
 */

/** Serialized binary paint data — model ID → array of painted face indices */
export interface SerializedBinaryPaintData {
  [modelId: string]: number[];
}

/** Serialized color paint data — model ID → array of [faceIndex, colorIndex] tuples */
export interface SerializedColorPaintData {
  [modelId: string]: [number, number][];
}

/** Full serialized paint payload for API submission */
export interface SerializedPaintPayload {
  supportPaint: SerializedBinaryPaintData;
  seamPaint: SerializedBinaryPaintData;
  fuzzySkinPaint: SerializedBinaryPaintData;
  colorPaint: SerializedColorPaintData;
}

// --- Binary paint (support, seam, fuzzy skin) ---

export function serializeBinaryPaintData(
  data: Map<string, Set<number>>,
): SerializedBinaryPaintData {
  const result: SerializedBinaryPaintData = {};
  for (const [modelId, faces] of data) {
    if (faces.size > 0) {
      result[modelId] = Array.from(faces).sort((a, b) => a - b);
    }
  }
  return result;
}

export function deserializeBinaryPaintData(
  data: SerializedBinaryPaintData,
): Map<string, Set<number>> {
  const result = new Map<string, Set<number>>();
  for (const [modelId, faces] of Object.entries(data)) {
    result.set(modelId, new Set(faces));
  }
  return result;
}

// --- Color paint (multi-value per face) ---

export function serializeColorPaintData(
  data: Map<string, Map<number, number>>,
): SerializedColorPaintData {
  const result: SerializedColorPaintData = {};
  for (const [modelId, faceMap] of data) {
    if (faceMap.size > 0) {
      const entries: [number, number][] = [];
      for (const [faceIndex, colorIndex] of faceMap) {
        entries.push([faceIndex, colorIndex]);
      }
      entries.sort((a, b) => a[0] - b[0]);
      result[modelId] = entries;
    }
  }
  return result;
}

export function deserializeColorPaintData(
  data: SerializedColorPaintData,
): Map<string, Map<number, number>> {
  const result = new Map<string, Map<number, number>>();
  for (const [modelId, entries] of Object.entries(data)) {
    const faceMap = new Map<number, number>();
    for (const [faceIndex, colorIndex] of entries) {
      faceMap.set(faceIndex, colorIndex);
    }
    result.set(modelId, faceMap);
  }
  return result;
}

// --- Full payload ---

export function serializePaintData(
  supportPaintData: Map<string, Set<number>>,
  seamPaintData: Map<string, Set<number>>,
  fuzzySkinPaintData: Map<string, Set<number>>,
  colorPaintData: Map<string, Map<number, number>>,
): SerializedPaintPayload {
  return {
    supportPaint: serializeBinaryPaintData(supportPaintData),
    seamPaint: serializeBinaryPaintData(seamPaintData),
    fuzzySkinPaint: serializeBinaryPaintData(fuzzySkinPaintData),
    colorPaint: serializeColorPaintData(colorPaintData),
  };
}

export function deserializePaintData(
  payload: SerializedPaintPayload,
): {
  supportPaintData: Map<string, Set<number>>;
  seamPaintData: Map<string, Set<number>>;
  fuzzySkinPaintData: Map<string, Set<number>>;
  colorPaintData: Map<string, Map<number, number>>;
} {
  return {
    supportPaintData: deserializeBinaryPaintData(payload.supportPaint),
    seamPaintData: deserializeBinaryPaintData(payload.seamPaint),
    fuzzySkinPaintData: deserializeBinaryPaintData(payload.fuzzySkinPaint),
    colorPaintData: deserializeColorPaintData(payload.colorPaint),
  };
}

// --- Per-model operations ---

/** Merge paint data from a source model into a target (for model duplication) */
export function mergeBinaryPaintData(
  data: Map<string, Set<number>>,
  sourceModelId: string,
  targetModelId: string,
): Map<string, Set<number>> {
  const sourceFaces = data.get(sourceModelId);
  if (!sourceFaces || sourceFaces.size === 0) return data;
  const next = new Map(data);
  next.set(targetModelId, new Set(sourceFaces));
  return next;
}

export function mergeColorPaintData(
  data: Map<string, Map<number, number>>,
  sourceModelId: string,
  targetModelId: string,
): Map<string, Map<number, number>> {
  const sourceFaces = data.get(sourceModelId);
  if (!sourceFaces || sourceFaces.size === 0) return data;
  const next = new Map(data);
  next.set(targetModelId, new Map(sourceFaces));
  return next;
}

/** Remove all paint data for a model (cleanup on model removal) */
export function clearBinaryPaintDataForModel(
  data: Map<string, Set<number>>,
  modelId: string,
): Map<string, Set<number>> {
  if (!data.has(modelId)) return data;
  const next = new Map(data);
  next.delete(modelId);
  return next;
}

export function clearColorPaintDataForModel(
  data: Map<string, Map<number, number>>,
  modelId: string,
): Map<string, Map<number, number>> {
  if (!data.has(modelId)) return data;
  const next = new Map(data);
  next.delete(modelId);
  return next;
}

/**
 * Slice payload helpers — pure functions for turning the active plate's models
 * into the URL/transform fields of a slice job request. Kept framework-free so
 * the plate-aware payload logic can be unit tested independently of the page.
 *
 * Only the ACTIVE plate is sliced. Blob-url (failed upload) models are not
 * sliceable and are filtered out.
 */
import type { LoadedModel } from '@/features/slicer/components/viewer/SlicerBedVisualization';

export interface SlicePayloadModels {
  /** First sliceable model on the active plate, or null if none. */
  primary: LoadedModel | null;
  /** Number of sliceable (server-hosted) models on the active plate. */
  sliceableCount: number;
  /** Server-hosted model URLs when more than one sliceable model exists. */
  modelFileUrls?: string[];
  /** Per-model transform JSON aligned with modelFileUrls. */
  modelFileTransforms?: string[];
}

/** A model is sliceable when it has a server-hosted (non-blob) URL. */
export function isSliceableModel(model: LoadedModel): boolean {
  return !!model.url && !model.url.startsWith('blob:');
}

/** Serialize a model's transform the way the slice worker expects. */
export function modelTransformJson(model: LoadedModel): string {
  return JSON.stringify({
    rotation: model.rotation,
    scale: model.scale,
    position: model.position,
  });
}

/**
 * Build the URL/transform portion of a slice request from the active plate's
 * models only. The caller is responsible for blocking submission when
 * `sliceableCount === 0`.
 */
export function buildSlicePayloadModels(activePlateModels: LoadedModel[]): SlicePayloadModels {
  const sliceable = activePlateModels.filter(isSliceableModel);

  if (sliceable.length === 0) {
    return { primary: null, sliceableCount: 0 };
  }

  const primary = sliceable[0];

  if (sliceable.length === 1) {
    return { primary, sliceableCount: 1 };
  }

  return {
    primary,
    sliceableCount: sliceable.length,
    modelFileUrls: sliceable.map(m => m.url),
    modelFileTransforms: sliceable.map(modelTransformJson),
  };
}

/**
 * Compute the process-setting overrides to send with a slice job: ONLY the keys
 * whose current editor value differs from the original baseline.
 *
 * The slicer editor seeds a complete ~300-key baseline (so every field gets a
 * reset/modified affordance), but the named process profile and its `inherits:`
 * chain are resolved worker-side. Sending the whole baseline would overwrite the
 * profile's tuned/inherited values with generic frontend defaults, so we must
 * send only the user's actual edits. `current` and `original` are seeded from
 * the same coerced object, so this diff is exactly the set of user changes.
 */
export function diffProcessOverrides(
  current: Record<string, unknown>,
  original: Record<string, unknown>,
): Record<string, unknown> {
  const overrides: Record<string, unknown> = {};
  for (const [key, value] of Object.entries(current)) {
    if (JSON.stringify(value) !== JSON.stringify(original[key])) {
      overrides[key] = value;
    }
  }
  return overrides;
}

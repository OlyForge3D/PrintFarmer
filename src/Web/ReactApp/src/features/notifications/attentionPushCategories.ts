import {
  ATTENTION_CATEGORY_IDS,
  AttentionKind,
  type AttentionCategoriesResponse,
  type AttentionCategoryDto,
  type AttentionCategoryId,
  type AttentionPushPreferencesDto,
  type UpdateAttentionPushPreferencesRequest,
} from '@/types/api';

/**
 * Attention push categories exposed by #708 backend.
 *
 * The server publishes the authoritative list at GET /api/notifications/attention-categories,
 * and per-user opt-in state at GET/PUT /api/notifications/attention-push-preferences with the
 * shape `{ enabled, categories: { PRINTER_FAILURE: bool, ... } }`.
 *
 * Older servers do not expose either endpoint (both return 404) and the newer server also
 * returns 404 ProblemDetails with `code=featureDisabled` when the NativePush flag is off.
 * All hooks/adapters here treat any of those failures as "feature unavailable" and never
 * corrupt saved preferences.
 */

export interface CategoryUiMeta {
  id: AttentionCategoryId;
  label: string;
  description: string;
  kind: AttentionKind;
}

/**
 * Fallback UI labels used when the server metadata is unavailable. Kept in the canonical
 * server order so the UI stays deterministic across renders.
 */
export const FALLBACK_CATEGORY_META: Readonly<Record<AttentionCategoryId, CategoryUiMeta>> = {
  PRINTER_FAILURE: {
    id: 'PRINTER_FAILURE',
    kind: AttentionKind.Failure,
    label: 'Printer Failure',
    description: 'When a printer reports a hard failure that needs operator intervention',
  },
  PRINTER_OFFLINE: {
    id: 'PRINTER_OFFLINE',
    kind: AttentionKind.Offline,
    label: 'Printer Offline',
    description: 'When a printer stops responding or drops its connection',
  },
  MAINTENANCE_DUE: {
    id: 'MAINTENANCE_DUE',
    kind: AttentionKind.Maintenance,
    label: 'Maintenance Due',
    description: 'When a printer has scheduled maintenance coming due',
  },
  HARVEST_READY: {
    id: 'HARVEST_READY',
    kind: AttentionKind.Harvest,
    label: 'Harvest Ready',
    description: 'When a completed print is ready to be removed from the printer',
  },
  FILAMENT_RUNOUT: {
    id: 'FILAMENT_RUNOUT',
    kind: AttentionKind.Runout,
    label: 'Filament Runout Risk',
    description: 'When a print is at risk of running out of filament before it finishes',
  },
};

const KNOWN_ID_SET: ReadonlySet<AttentionCategoryId> = new Set(ATTENTION_CATEGORY_IDS);

export function isKnownAttentionCategoryId(id: string): id is AttentionCategoryId {
  return KNOWN_ID_SET.has(id as AttentionCategoryId);
}

/**
 * Merge server-published metadata with the fallback table so the UI can render even when
 * `/attention-categories` is unavailable. Ordering follows the server for known IDs, then
 * falls back to the canonical order; unknown IDs from the server are preserved verbatim at
 * the end so a forward-compatible client cannot silently drop new categories.
 */
export function buildCategoryUiMeta(
  serverResponse: AttentionCategoriesResponse | null | undefined,
): CategoryUiMeta[] {
  const seen = new Set<string>();
  const meta: CategoryUiMeta[] = [];

  if (serverResponse?.categories?.length) {
    for (const cat of serverResponse.categories) {
      if (isKnownAttentionCategoryId(cat.id)) {
        seen.add(cat.id);
        const fallback = FALLBACK_CATEGORY_META[cat.id];
        meta.push({ ...fallback, kind: cat.kind ?? fallback.kind });
      }
    }
  }

  for (const id of ATTENTION_CATEGORY_IDS) {
    if (!seen.has(id)) {
      seen.add(id);
      meta.push(FALLBACK_CATEGORY_META[id]);
    }
  }

  if (serverResponse?.categories?.length) {
    for (const cat of serverResponse.categories) {
      if (!seen.has(cat.id)) {
        seen.add(cat.id);
        meta.push({
          id: cat.id as AttentionCategoryId,
          kind: cat.kind ?? AttentionKind.Failure,
          label: humanizeIdentifier(cat.id),
          description: 'Category exposed by a newer server',
        });
      }
    }
  }

  return meta;
}

function humanizeIdentifier(raw: string): string {
  return raw
    .split(/[_\s]+/)
    .filter(Boolean)
    .map(part => part.charAt(0) + part.slice(1).toLowerCase())
    .join(' ');
}

/**
 * Normalize the raw preferences response so every known category has an explicit boolean.
 * Unknown category keys are preserved verbatim so the client never drops server state.
 */
export function hydrateAttentionPushPreferences(
  raw: AttentionPushPreferencesDto | null | undefined,
): AttentionPushPreferencesDto {
  const categories: Record<string, boolean> = {};
  const rawCats = raw?.categories ?? {};

  for (const id of ATTENTION_CATEGORY_IDS) {
    categories[id] = Boolean(rawCats[id]);
  }
  for (const [key, value] of Object.entries(rawCats)) {
    if (!isKnownAttentionCategoryId(key)) {
      categories[key] = Boolean(value);
    }
  }

  return {
    enabled: Boolean(raw?.enabled),
    categories,
  };
}

/**
 * Prepare the PUT payload. Guarantees each known category boolean is present and preserves
 * unknown keys so a UI that has not yet been updated cannot drop server-side state.
 */
export function buildAttentionPushSavePayload(
  current: AttentionPushPreferencesDto,
): UpdateAttentionPushPreferencesRequest {
  return hydrateAttentionPushPreferences(current);
}

/**
 * Return true when an error response should be treated as "feature unavailable" (older
 * server or NativePush feature disabled). Any 404 — including `code=featureDisabled`
 * ProblemDetails from a newer server — falls into this bucket.
 */
export function isAttentionFeatureUnavailableError(err: unknown): boolean {
  if (!err || typeof err !== 'object') return false;
  const anyErr = err as { response?: { status?: number } };
  return anyErr.response?.status === 404;
}

export type { AttentionCategoryDto };

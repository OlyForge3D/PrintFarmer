import { describe, expect, it } from 'vitest';
import {
  ATTENTION_CATEGORY_IDS,
  AttentionKind,
  type AttentionCategoriesResponse,
  type AttentionPushPreferencesDto,
} from '@/types/api';
import {
  FALLBACK_CATEGORY_META,
  buildAttentionPushSavePayload,
  buildCategoryUiMeta,
  hydrateAttentionPushPreferences,
  isAttentionFeatureUnavailableError,
  isKnownAttentionCategoryId,
} from '../attentionPushCategories';

describe('attentionPushCategories.isKnownAttentionCategoryId', () => {
  it('recognizes the canonical APNs category identifiers', () => {
    for (const id of ATTENTION_CATEGORY_IDS) {
      expect(isKnownAttentionCategoryId(id)).toBe(true);
    }
  });

  it('rejects unknown identifiers', () => {
    expect(isKnownAttentionCategoryId('SOMETHING_ELSE')).toBe(false);
    expect(isKnownAttentionCategoryId('')).toBe(false);
  });
});

describe('attentionPushCategories.buildCategoryUiMeta', () => {
  it('returns the canonical fallback list in order when the server response is null', () => {
    const meta = buildCategoryUiMeta(null);
    expect(meta.map(m => m.id)).toEqual([...ATTENTION_CATEGORY_IDS]);
    for (const m of meta) {
      expect(m.label).toBe(FALLBACK_CATEGORY_META[m.id].label);
    }
  });

  it('honors server ordering for known ids and appends any missing known ids', () => {
    const response: AttentionCategoriesResponse = {
      categories: [
        { id: 'HARVEST_READY', kind: AttentionKind.Harvest, actions: [], threadIdTemplate: '' },
        { id: 'PRINTER_FAILURE', kind: AttentionKind.Failure, actions: [], threadIdTemplate: '' },
      ],
    };
    const meta = buildCategoryUiMeta(response);
    expect(meta[0].id).toBe('HARVEST_READY');
    expect(meta[1].id).toBe('PRINTER_FAILURE');
    // remaining known ids appended in canonical order
    expect(meta.slice(2).map(m => m.id)).toEqual([
      'PRINTER_OFFLINE',
      'MAINTENANCE_DUE',
      'FILAMENT_RUNOUT',
    ]);
  });

  it('preserves unknown server-provided category ids at the end', () => {
    const response: AttentionCategoriesResponse = {
      categories: [
        { id: 'SOME_FUTURE_CATEGORY' as string, kind: AttentionKind.Failure, actions: [], threadIdTemplate: '' },
      ],
    };
    const meta = buildCategoryUiMeta(response);
    expect(meta[meta.length - 1].id).toBe('SOME_FUTURE_CATEGORY');
    expect(meta[meta.length - 1].label.length).toBeGreaterThan(0);
  });
});

describe('attentionPushCategories.hydrateAttentionPushPreferences', () => {
  it('returns a fully populated categories map when the response is null (feature disabled)', () => {
    const hydrated = hydrateAttentionPushPreferences(null);
    expect(hydrated.enabled).toBe(false);
    for (const id of ATTENTION_CATEGORY_IDS) {
      expect(hydrated.categories[id]).toBe(false);
    }
  });

  it('preserves server-provided booleans for known ids and coerces missing ones to false', () => {
    const raw: AttentionPushPreferencesDto = {
      enabled: true,
      categories: { HARVEST_READY: true, PRINTER_FAILURE: true },
    };
    const hydrated = hydrateAttentionPushPreferences(raw);
    expect(hydrated.enabled).toBe(true);
    expect(hydrated.categories.HARVEST_READY).toBe(true);
    expect(hydrated.categories.PRINTER_FAILURE).toBe(true);
    expect(hydrated.categories.PRINTER_OFFLINE).toBe(false);
    expect(hydrated.categories.MAINTENANCE_DUE).toBe(false);
    expect(hydrated.categories.FILAMENT_RUNOUT).toBe(false);
  });

  it('preserves unknown category keys returned by newer servers', () => {
    const raw: AttentionPushPreferencesDto = {
      enabled: false,
      categories: { NEW_THING: true },
    };
    const hydrated = hydrateAttentionPushPreferences(raw);
    expect(hydrated.categories.NEW_THING).toBe(true);
  });
});

describe('attentionPushCategories.buildAttentionPushSavePayload', () => {
  it('emits an exhaustive payload for every known category plus any unknown keys', () => {
    const current: AttentionPushPreferencesDto = {
      enabled: true,
      categories: { PRINTER_FAILURE: true, WEIRD: true },
    };
    const payload = buildAttentionPushSavePayload(current);
    expect(payload.enabled).toBe(true);
    for (const id of ATTENTION_CATEGORY_IDS) {
      expect(payload.categories).toHaveProperty(id);
    }
    expect(payload.categories.WEIRD).toBe(true);
    expect(payload.categories.PRINTER_FAILURE).toBe(true);
    expect(payload.categories.HARVEST_READY).toBe(false);
  });
});

describe('attentionPushCategories.isAttentionFeatureUnavailableError', () => {
  it('treats 404 responses as feature-unavailable', () => {
    expect(isAttentionFeatureUnavailableError({ response: { status: 404 } })).toBe(true);
  });

  it('does not treat other status codes or non-http errors as feature-unavailable', () => {
    expect(isAttentionFeatureUnavailableError({ response: { status: 500 } })).toBe(false);
    expect(isAttentionFeatureUnavailableError(new Error('network'))).toBe(false);
    expect(isAttentionFeatureUnavailableError(null)).toBe(false);
    expect(isAttentionFeatureUnavailableError(undefined)).toBe(false);
    expect(isAttentionFeatureUnavailableError('boom')).toBe(false);
  });
});

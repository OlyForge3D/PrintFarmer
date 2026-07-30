/**
 * Version-scoped schema hook tests (issue #578).
 *
 * These tests verify that switching the pinned OrcaSlicer engine version:
 *   1. Threads the `engineVersion` query parameter through to the API.
 *   2. Uses a distinct queryKey per version so cache entries do not
 *      cross-contaminate between engines.
 *   3. Correctly returns the version-scoped process schema (added fields
 *      appear for 2.4.1, retired fields absent, renamed key resolves).
 */

import { describe, it, expect, vi, beforeEach } from 'vitest';
import { renderHook, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import { useProfileSchema } from '../useProfileSchema';
import type { ProfileSchemasResponse } from '@/types/api';

vi.mock('@/services/api', () => ({
  apiClient: {
    getProfileSchemas: vi.fn(),
  },
}));

const { apiClient } = await import('@/services/api');
const getProfileSchemasMock = apiClient.getProfileSchemas as unknown as ReturnType<typeof vi.fn>;

const legacySchema: ProfileSchemasResponse = {
  process: {
    profileType: 'process',
    categories: ['quality', 'adhesion'],
    fields: [
      { key: 'layerHeight', label: 'Layer Height', fieldType: 'number', category: 'quality', isAdvanced: false },
      { key: 'firstLayerAdhesion', label: 'Bed Adhesion Override', fieldType: 'enum', category: 'adhesion', isAdvanced: true },
      { key: 'legacyPreviewSetting', label: 'Legacy Preview Setting', fieldType: 'boolean', category: 'quality', isAdvanced: true },
    ],
  },
  machine: { profileType: 'machine', categories: [], fields: [] },
  filament: { profileType: 'filament', categories: [], fields: [] },
};

const currentSchema: ProfileSchemasResponse = {
  process: {
    profileType: 'process',
    categories: ['quality', 'adhesion'],
    fields: [
      { key: 'layerHeight', label: 'Layer Height', fieldType: 'number', category: 'quality', isAdvanced: false },
      { key: 'wallGenerator', label: 'Wall Generator', fieldType: 'enum', category: 'quality', isAdvanced: true, minEngineVersion: '2.4.0' },
      { key: 'enableArcFitting', label: 'Enable Arc Fitting', fieldType: 'boolean', category: 'quality', isAdvanced: true, minEngineVersion: '2.4.0' },
      { key: 'bedAdhesionOverride', label: 'Bed Adhesion Override', fieldType: 'enum', category: 'adhesion', isAdvanced: true, renamedFromKey: 'firstLayerAdhesion', renamedInVersion: '2.4.0' },
    ],
  },
  machine: { profileType: 'machine', categories: [], fields: [] },
  filament: { profileType: 'filament', categories: [], fields: [] },
};

function makeWrapper() {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: 0 } },
  });
  return function Wrapper({ children }: { children: ReactNode }) {
    return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
  };
}

describe('useProfileSchema (version-scoped)', () => {
  beforeEach(() => {
    getProfileSchemasMock.mockReset();
    getProfileSchemasMock.mockImplementation((v?: string) => {
      if (v === '2.3.1') return Promise.resolve(legacySchema);
      if (v === '2.4.1') return Promise.resolve(currentSchema);
      return Promise.resolve(currentSchema);
    });
  });

  it('threads engineVersion into the API call', async () => {
    const { result } = renderHook(() => useProfileSchema('process', '2.3.1'), {
      wrapper: makeWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(getProfileSchemasMock).toHaveBeenCalledWith('2.3.1');
  });

  it('returns 2.3.1 process schema with legacy fields for engineVersion=2.3.1', async () => {
    const { result } = renderHook(() => useProfileSchema('process', '2.3.1'), {
      wrapper: makeWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    const keys = result.current.data?.fields.map((f) => f.key) ?? [];
    expect(keys).toContain('firstLayerAdhesion');
    expect(keys).toContain('legacyPreviewSetting');
    expect(keys).not.toContain('wallGenerator');
    expect(keys).not.toContain('enableArcFitting');
    expect(keys).not.toContain('bedAdhesionOverride');
  });

  it('returns 2.4.1 process schema with added/renamed fields for engineVersion=2.4.1', async () => {
    const { result } = renderHook(() => useProfileSchema('process', '2.4.1'), {
      wrapper: makeWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    const keys = result.current.data?.fields.map((f) => f.key) ?? [];
    expect(keys).toContain('wallGenerator');
    expect(keys).toContain('enableArcFitting');
    expect(keys).toContain('bedAdhesionOverride');
    expect(keys).not.toContain('firstLayerAdhesion');
    expect(keys).not.toContain('legacyPreviewSetting');
  });

  it('uses distinct queryKey per engineVersion so caches do not cross-contaminate', async () => {
    const client = new QueryClient({
      defaultOptions: { queries: { retry: false, gcTime: 0 } },
    });
    const Wrapper = ({ children }: { children: ReactNode }) => (
      <QueryClientProvider client={client}>{children}</QueryClientProvider>
    );

    const legacy = renderHook(() => useProfileSchema('process', '2.3.1'), { wrapper: Wrapper });
    const current = renderHook(() => useProfileSchema('process', '2.4.1'), { wrapper: Wrapper });

    await waitFor(() => {
      expect(legacy.result.current.isSuccess).toBe(true);
      expect(current.result.current.isSuccess).toBe(true);
    });

    const legacyKeys = legacy.result.current.data?.fields.map((f) => f.key) ?? [];
    const currentKeys = current.result.current.data?.fields.map((f) => f.key) ?? [];

    expect(legacyKeys).toContain('firstLayerAdhesion');
    expect(currentKeys).toContain('bedAdhesionOverride');
    expect(legacyKeys).not.toEqual(currentKeys);

    expect(getProfileSchemasMock).toHaveBeenCalledTimes(2);
    expect(getProfileSchemasMock).toHaveBeenCalledWith('2.3.1');
    expect(getProfileSchemasMock).toHaveBeenCalledWith('2.4.1');
  });

  it('omits the engineVersion argument when undefined', async () => {
    const { result } = renderHook(() => useProfileSchema('process'), {
      wrapper: makeWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(getProfileSchemasMock).toHaveBeenCalledWith(undefined);
  });
});

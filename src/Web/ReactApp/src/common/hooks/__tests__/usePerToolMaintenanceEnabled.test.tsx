import { describe, it, expect, vi } from 'vitest';
import { renderHook, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import React from 'react';

vi.mock('@/services/api', () => ({
  apiClient: {
    getSystemCapabilities: vi.fn(),
  },
}));

import { apiClient } from '@/services/api';
import { usePerToolMaintenanceEnabled } from '../usePerToolMaintenanceEnabled';

const baseCaps = {
  architecture: 'x64',
  slicingEnabled: true,
  modelFilesEnabled: true,
  thumbnailGenerationEnabled: true,
  gcodeUploadEnabled: true,
};

function wrapper() {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false, staleTime: 0 } },
  });
  return function Wrapper({ children }: { children: React.ReactNode }) {
    return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
  };
}

describe('usePerToolMaintenanceEnabled', () => {
  it('returns enabled=true while capabilities are loading (avoids flash-of-disabled)', () => {
    (apiClient.getSystemCapabilities as unknown as ReturnType<typeof vi.fn>).mockImplementation(
      () => new Promise(() => {})
    );
    const { result } = renderHook(() => usePerToolMaintenanceEnabled(), { wrapper: wrapper() });
    expect(result.current.enabled).toBe(true);
    expect(result.current.loading).toBe(true);
  });

  it('returns enabled=true when operatorFeatures block is missing (older API)', async () => {
    (apiClient.getSystemCapabilities as unknown as ReturnType<typeof vi.fn>).mockResolvedValue(baseCaps);
    const { result } = renderHook(() => usePerToolMaintenanceEnabled(), { wrapper: wrapper() });
    await waitFor(() => expect(result.current.loading).toBe(false));
    expect(result.current.enabled).toBe(true);
  });

  it('returns enabled=true when the flag is omitted from operatorFeatures', async () => {
    (apiClient.getSystemCapabilities as unknown as ReturnType<typeof vi.fn>).mockResolvedValue({
      ...baseCaps,
      operatorFeatures: {},
    });
    const { result } = renderHook(() => usePerToolMaintenanceEnabled(), { wrapper: wrapper() });
    await waitFor(() => expect(result.current.loading).toBe(false));
    expect(result.current.enabled).toBe(true);
  });

  it('returns enabled=false only when the flag is explicitly false', async () => {
    (apiClient.getSystemCapabilities as unknown as ReturnType<typeof vi.fn>).mockResolvedValue({
      ...baseCaps,
      operatorFeatures: { multiSlotFallbackEnabled: false },
    });
    const { result } = renderHook(() => usePerToolMaintenanceEnabled(), { wrapper: wrapper() });
    await waitFor(() => expect(result.current.enabled).toBe(false));
  });

  it('returns enabled=true when the flag is explicitly true', async () => {
    (apiClient.getSystemCapabilities as unknown as ReturnType<typeof vi.fn>).mockResolvedValue({
      ...baseCaps,
      operatorFeatures: { multiSlotFallbackEnabled: true },
    });
    const { result } = renderHook(() => usePerToolMaintenanceEnabled(), { wrapper: wrapper() });
    await waitFor(() => expect(result.current.loading).toBe(false));
    expect(result.current.enabled).toBe(true);
  });
});

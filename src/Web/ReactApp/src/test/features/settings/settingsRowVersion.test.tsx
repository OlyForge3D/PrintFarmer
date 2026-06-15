import { describe, it, expect, vi, beforeEach } from 'vitest';
import { renderHook, waitFor, act } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import { toast } from 'sonner';

// Mock apiClient
const mockGet = vi.fn();
const mockPut = vi.fn();
vi.mock('@/services/api', () => ({
  apiClient: {
    get: (...args: unknown[]) => mockGet(...args),
    put: (...args: unknown[]) => mockPut(...args),
  },
}));

// Mock sonner toast
vi.mock('sonner', () => ({
  toast: {
    error: vi.fn(),
    success: vi.fn(),
  },
}));

import { useFarmSettings, useUpdateFarmSettings } from '@/features/settings/hooks/useFarmSettings';
import { useUserSettings, useUpdateUserSettings } from '@/features/settings/hooks/useUserSettings';

function createWrapper() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
  );
}

describe('Settings rowVersion concurrency', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  describe('useFarmSettings', () => {
    it('includes rowVersion from GET response', async () => {
      mockGet.mockResolvedValueOnce({
        data: {
          electricityRatePerKwh: 0.12,
          defaultMachineHourlyRate: 2.5,
          averagePrinterWattage: 200,
          canWrite: true,
          rowVersion: 'AAAAABCD',
        },
      });

      const { result } = renderHook(() => useFarmSettings(), { wrapper: createWrapper() });

      await waitFor(() => expect(result.current.isSuccess).toBe(true));
      expect(result.current.data?.rowVersion).toBe('AAAAABCD');
    });
  });

  describe('useUpdateFarmSettings', () => {
    it('sends rowVersion in mutation payload', async () => {
      mockPut.mockResolvedValueOnce({
        data: {
          electricityRatePerKwh: 0.15,
          defaultMachineHourlyRate: 2.5,
          averagePrinterWattage: 200,
          canWrite: true,
          rowVersion: 'AAAAABCE',
        },
      });

      const { result } = renderHook(() => useUpdateFarmSettings(), { wrapper: createWrapper() });

      act(() => {
        result.current.mutate({
          electricityRatePerKwh: 0.15,
          rowVersion: 'AAAAABCD',
        });
      });

      await waitFor(() => expect(result.current.isSuccess).toBe(true));
      expect(mockPut).toHaveBeenCalledWith('/settings/farm', {
        electricityRatePerKwh: 0.15,
        rowVersion: 'AAAAABCD',
      });
    });

    it('shows toast and invalidates query on 409 Conflict', async () => {
      const conflictError = { message: 'Conflict', statusCode: 409 };
      mockPut.mockRejectedValueOnce(conflictError);

      const { result } = renderHook(() => useUpdateFarmSettings(), { wrapper: createWrapper() });

      act(() => {
        result.current.mutate({
          electricityRatePerKwh: 0.15,
          rowVersion: 'AAAAABCD',
        });
      });

      await waitFor(() => expect(result.current.isError).toBe(true));
      expect(toast.error).toHaveBeenCalledWith('Settings were updated elsewhere — please refresh');
    });
  });

  describe('useUserSettings', () => {
    it('includes rowVersion from GET response', async () => {
      mockGet.mockResolvedValueOnce({
        data: {
          userId: 'user-1',
          theme: 'dark',
          locale: 'en',
          itemsPerPage: 25,
          defaultSlicerPreset: null,
          printablesUsername: 'alice',
          rowVersion: 'BBBBBBBB',
        },
      });

      const { result } = renderHook(() => useUserSettings(), { wrapper: createWrapper() });

      await waitFor(() => expect(result.current.isSuccess).toBe(true));
      expect(result.current.data?.rowVersion).toBe('BBBBBBBB');
    });
  });

  describe('useUpdateUserSettings', () => {
    it('sends rowVersion in mutation payload', async () => {
      mockPut.mockResolvedValueOnce({
        data: {
          userId: 'user-1',
          theme: 'light',
          locale: 'en',
          itemsPerPage: 25,
          defaultSlicerPreset: null,
          printablesUsername: 'alice',
          rowVersion: 'BBBBBBBC',
        },
      });

      const { result } = renderHook(() => useUpdateUserSettings(), { wrapper: createWrapper() });

      act(() => {
        result.current.mutate({
          theme: 'light',
          printablesUsername: 'alice',
          rowVersion: 'BBBBBBBB',
        });
      });

      await waitFor(() => expect(result.current.isSuccess).toBe(true));
      expect(mockPut).toHaveBeenCalledWith('/settings/user', {
        theme: 'light',
        printablesUsername: 'alice',
        rowVersion: 'BBBBBBBB',
      });
    });

    it('shows toast and invalidates query on 409 Conflict', async () => {
      const conflictError = { message: 'Conflict', statusCode: 409 };
      mockPut.mockRejectedValueOnce(conflictError);

      const { result } = renderHook(() => useUpdateUserSettings(), { wrapper: createWrapper() });

      act(() => {
        result.current.mutate({
          theme: 'light',
          rowVersion: 'BBBBBBBB',
        });
      });

      await waitFor(() => expect(result.current.isError).toBe(true));
      expect(toast.error).toHaveBeenCalledWith('Settings were updated elsewhere — please refresh');
    });
  });
});

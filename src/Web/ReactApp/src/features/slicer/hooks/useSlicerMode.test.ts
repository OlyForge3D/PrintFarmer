import { describe, it, expect, vi } from 'vitest';
import { renderHook } from '@testing-library/react';
import { useSlicerMode } from './useSlicerMode';

vi.mock('@/features/settings/hooks/useFarmSettings', () => ({
  useFarmSettings: vi.fn(),
}));

import { useFarmSettings } from '@/features/settings/hooks/useFarmSettings';

const mockUseFarmSettings = vi.mocked(useFarmSettings);

describe('useSlicerMode', () => {
  it('returns Advanced when slicerMode is Advanced', () => {
    mockUseFarmSettings.mockReturnValue({ data: { slicerMode: 'Advanced' } } as ReturnType<typeof useFarmSettings>);

    const { result } = renderHook(() => useSlicerMode());

    expect(result.current).toBe('Advanced');
  });

  it('returns null while settings data is loading', () => {
    mockUseFarmSettings.mockReturnValue({ data: undefined, isLoading: true } as ReturnType<typeof useFarmSettings>);

    const { result } = renderHook(() => useSlicerMode());

    expect(result.current).toBeNull();
  });

  it('returns null when settings data is undefined and not loading', () => {
    mockUseFarmSettings.mockReturnValue({ data: undefined, isLoading: false } as ReturnType<typeof useFarmSettings>);

    const { result } = renderHook(() => useSlicerMode());

    expect(result.current).toBeNull();
  });

  it('returns Simple when slicerMode is undefined', () => {
    mockUseFarmSettings.mockReturnValue({ data: { slicerMode: undefined } } as ReturnType<typeof useFarmSettings>);

    const { result } = renderHook(() => useSlicerMode());

    expect(result.current).toBe('Simple');
  });
});

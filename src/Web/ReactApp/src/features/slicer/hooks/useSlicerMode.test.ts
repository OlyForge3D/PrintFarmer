import { describe, it, expect, vi, beforeEach } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import { useSlicerMode, SLICER_MODE_STORAGE_KEY } from './useSlicerMode';

vi.mock('@/features/settings/hooks/useFarmSettings', () => ({
  useFarmSettings: vi.fn(),
}));

import { useFarmSettings } from '@/features/settings/hooks/useFarmSettings';

const mockUseFarmSettings = vi.mocked(useFarmSettings);

function mockSettings(data: unknown, isLoading = false) {
  mockUseFarmSettings.mockReturnValue({ data, isLoading } as ReturnType<typeof useFarmSettings>);
}

describe('useSlicerMode', () => {
  beforeEach(() => {
    localStorage.clear();
    vi.clearAllMocks();
  });

  it('returns null mode while settings are loading', () => {
    mockSettings(undefined, true);
    const { result } = renderHook(() => useSlicerMode());
    expect(result.current.mode).toBeNull();
    expect(result.current.canToggle).toBe(false);
    expect(result.current.enabledModes).toEqual([]);
  });

  it('returns null mode when data is undefined and not loading', () => {
    mockSettings(undefined, false);
    const { result } = renderHook(() => useSlicerMode());
    expect(result.current.mode).toBeNull();
  });

  it('falls back to default mode and single enabled mode for legacy settings (no enabledModes)', () => {
    mockSettings({ slicerMode: 'Advanced' });
    const { result } = renderHook(() => useSlicerMode());
    expect(result.current.mode).toBe('Advanced');
    expect(result.current.enabledModes).toEqual(['Advanced']);
    expect(result.current.canToggle).toBe(false);
  });

  it('defaults slicerMode to Simple when undefined', () => {
    mockSettings({ slicerMode: undefined });
    const { result } = renderHook(() => useSlicerMode());
    expect(result.current.mode).toBe('Simple');
  });

  it('forces the only enabled mode and disallows toggling', () => {
    mockSettings({ slicerMode: 'Simple', enabledModes: ['Simple'] });
    const { result } = renderHook(() => useSlicerMode());
    expect(result.current.mode).toBe('Simple');
    expect(result.current.canToggle).toBe(false);
  });

  it('allows toggling when both modes are enabled and uses the default initially', () => {
    mockSettings({ slicerMode: 'Simple', enabledModes: ['Simple', 'Advanced'] });
    const { result } = renderHook(() => useSlicerMode());
    expect(result.current.canToggle).toBe(true);
    expect(result.current.mode).toBe('Simple');
  });

  it('honors a stored per-user override when toggling is allowed', () => {
    localStorage.setItem(SLICER_MODE_STORAGE_KEY, 'Advanced');
    mockSettings({ slicerMode: 'Simple', enabledModes: ['Simple', 'Advanced'] });
    const { result } = renderHook(() => useSlicerMode());
    expect(result.current.mode).toBe('Advanced');
  });

  it('ignores a stored override that is not in the enabled set', () => {
    localStorage.setItem(SLICER_MODE_STORAGE_KEY, 'Advanced');
    mockSettings({ slicerMode: 'Simple', enabledModes: ['Simple'] });
    const { result } = renderHook(() => useSlicerMode());
    expect(result.current.mode).toBe('Simple');
  });

  it('setMode persists and updates the effective mode', () => {
    mockSettings({ slicerMode: 'Simple', enabledModes: ['Simple', 'Advanced'] });
    const { result } = renderHook(() => useSlicerMode());

    act(() => result.current.setMode('Advanced'));

    expect(localStorage.getItem(SLICER_MODE_STORAGE_KEY)).toBe('Advanced');
    expect(result.current.mode).toBe('Advanced');
  });

  it('setMode is a no-op for a mode that is not enabled', () => {
    mockSettings({ slicerMode: 'Simple', enabledModes: ['Simple'] });
    const { result } = renderHook(() => useSlicerMode());

    act(() => result.current.setMode('Advanced'));

    expect(localStorage.getItem(SLICER_MODE_STORAGE_KEY)).toBeNull();
    expect(result.current.mode).toBe('Simple');
  });
});

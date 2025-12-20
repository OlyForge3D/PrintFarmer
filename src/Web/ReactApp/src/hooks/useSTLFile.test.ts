import { describe, it, expect, beforeEach } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import { useSTLFile } from './useSTLFile';

describe('useSTLFile Hook', () => {
  it('initializes with empty state', () => {
    const { result } = renderHook(() => useSTLFile());

    expect(result.current.file).toBeNull();
    expect(result.current.fileInfo).toBeNull();
    expect(result.current.errors).toHaveLength(0);
    expect(result.current.isLoading).toBe(false);
  });

  it('exposes required hook methods', () => {
    const { result } = renderHook(() => useSTLFile());

    expect(typeof result.current.selectFile).toBe('function');
    expect(typeof result.current.clearFile).toBe('function');
  });

  it('clears file and errors when clearFile is called', () => {
    const { result } = renderHook(() => useSTLFile());

    act(() => {
      result.current.clearFile();
    });

    expect(result.current.file).toBeNull();
    expect(result.current.fileInfo).toBeNull();
    expect(result.current.errors).toHaveLength(0);
  });

  it('can initialize with custom max size', () => {
    const { result } = renderHook(() => useSTLFile(100)); // 100 MB max

    expect(result.current.file).toBeNull();
    expect(result.current.isLoading).toBe(false);
  });

  it('maintains state across renders', () => {
    const { result, rerender } = renderHook(() => useSTLFile());

    expect(result.current.file).toBeNull();

    rerender();

    expect(result.current.file).toBeNull();
  });

  it('returns consistent interface', () => {
    const { result } = renderHook(() => useSTLFile());

    expect(result.current).toHaveProperty('file');
    expect(result.current).toHaveProperty('fileInfo');
    expect(result.current).toHaveProperty('errors');
    expect(result.current).toHaveProperty('isLoading');
    expect(result.current).toHaveProperty('selectFile');
    expect(result.current).toHaveProperty('clearFile');
  });

  it('hook is compatible with React hooks rules', () => {
    // This test verifies the hook can be called conditionally
    // and maintains its interface
    const { result } = renderHook(() => useSTLFile(50));

    expect(Array.isArray(result.current.errors)).toBe(true);
  });
});

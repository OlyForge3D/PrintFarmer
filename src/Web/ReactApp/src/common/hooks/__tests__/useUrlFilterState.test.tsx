import { renderHook, act } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { MemoryRouter, useSearchParams } from 'react-router';
import type { ReactNode } from 'react';
import { useUrlFilterState } from '../useUrlFilterState';

function createWrapper(initialEntries: string[] = ['/']) {
  return function Wrapper({ children }: { children: ReactNode }) {
    return <MemoryRouter initialEntries={initialEntries}>{children}</MemoryRouter>;
  };
}

/** Helper to read search params alongside the hook under test. */
function useTestHarness() {
  const [searchParams] = useSearchParams();
  const hook = useUrlFilterState({
    search: { key: 'q', type: 'string' as const, defaultValue: '' },
    material: { key: 'material', type: 'string' as const, defaultValue: '' },
    page: { key: 'page', type: 'number' as const, defaultValue: 1 },
    showEmpty: { key: 'showEmpty', type: 'boolean' as const, defaultValue: false },
  });
  return { ...hook, searchParams };
}

describe('useUrlFilterState', () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });

  it('should return default values when URL has no params', () => {
    const { result } = renderHook(() => useTestHarness(), {
      wrapper: createWrapper(['/spools']),
    });

    expect(result.current.search).toBe('');
    expect(result.current.material).toBe('');
    expect(result.current.page).toBe(1);
    expect(result.current.showEmpty).toBe(false);
    expect(result.current.hasActiveFilters).toBe(false);
  });

  it('should initialize from URL params', () => {
    const { result } = renderHook(() => useTestHarness(), {
      wrapper: createWrapper(['/spools?q=PLA&material=PLA&page=3&showEmpty=true']),
    });

    expect(result.current.search).toBe('PLA');
    expect(result.current.material).toBe('PLA');
    expect(result.current.page).toBe(3);
    expect(result.current.showEmpty).toBe(true);
    expect(result.current.hasActiveFilters).toBe(true);
  });

  it('should update URL when setter is called', () => {
    const { result } = renderHook(() => useTestHarness(), {
      wrapper: createWrapper(['/spools']),
    });

    act(() => {
      result.current.setMaterial('PETG');
    });

    expect(result.current.material).toBe('PETG');
    expect(result.current.searchParams.get('material')).toBe('PETG');
    expect(result.current.hasActiveFilters).toBe(true);
  });

  it('should omit default values from URL', () => {
    const { result } = renderHook(() => useTestHarness(), {
      wrapper: createWrapper(['/spools?material=PETG']),
    });

    act(() => {
      result.current.setMaterial('');
    });

    expect(result.current.material).toBe('');
    expect(result.current.searchParams.has('material')).toBe(false);
  });

  it('should handle number params', () => {
    const { result } = renderHook(() => useTestHarness(), {
      wrapper: createWrapper(['/spools']),
    });

    act(() => {
      result.current.setPage(5);
    });

    expect(result.current.page).toBe(5);
    expect(result.current.searchParams.get('page')).toBe('5');
  });

  it('should handle boolean params', () => {
    const { result } = renderHook(() => useTestHarness(), {
      wrapper: createWrapper(['/spools']),
    });

    act(() => {
      result.current.setShowEmpty(true);
    });

    expect(result.current.showEmpty).toBe(true);
    expect(result.current.searchParams.get('showEmpty')).toBe('true');
  });

  it('should remove boolean param when set to false (default)', () => {
    const { result } = renderHook(() => useTestHarness(), {
      wrapper: createWrapper(['/spools?showEmpty=true']),
    });

    act(() => {
      result.current.setShowEmpty(false);
    });

    expect(result.current.showEmpty).toBe(false);
    expect(result.current.searchParams.has('showEmpty')).toBe(false);
  });

  it('should reset all params', () => {
    const { result } = renderHook(() => useTestHarness(), {
      wrapper: createWrapper(['/spools?q=PLA&material=PETG&page=3&showEmpty=true']),
    });

    expect(result.current.hasActiveFilters).toBe(true);

    act(() => {
      result.current.resetAll();
    });

    expect(result.current.search).toBe('');
    expect(result.current.material).toBe('');
    expect(result.current.page).toBe(1);
    expect(result.current.showEmpty).toBe(false);
    expect(result.current.hasActiveFilters).toBe(false);
  });

  it('should return default for malformed number params', () => {
    const { result } = renderHook(() => useTestHarness(), {
      wrapper: createWrapper(['/spools?page=abc']),
    });

    expect(result.current.page).toBe(1);
  });

  it('should debounce URL updates for search field', () => {
    const { result } = renderHook(
      () => {
        const [searchParams] = useSearchParams();
        const hook = useUrlFilterState({
          search: { key: 'q', type: 'string' as const, defaultValue: '', debounce: 300 },
        });
        return { ...hook, searchParams };
      },
      { wrapper: createWrapper(['/spools']) },
    );

    act(() => {
      result.current.setSearch('P');
    });

    // URL should NOT be updated yet (debounced)
    expect(result.current.searchParams.has('q')).toBe(false);

    act(() => {
      vi.advanceTimersByTime(300);
    });

    // Now the URL should be updated
    expect(result.current.searchParams.get('q')).toBe('P');

    vi.useRealTimers();
  });
});

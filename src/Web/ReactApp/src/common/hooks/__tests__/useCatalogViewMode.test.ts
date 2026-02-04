import { renderHook, act } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { useCatalogViewMode, CatalogTab } from '../useCatalogViewMode';

describe('useCatalogViewMode', () => {
  beforeEach(() => {
    localStorage.clear();
    vi.clearAllMocks();
  });

  it('should default to grid view', () => {
    const { result } = renderHook(() => useCatalogViewMode('filaments'));

    expect(result.current[0]).toBe('grid');
  });

  it('should persist view mode to localStorage', () => {
    const { result } = renderHook(() => useCatalogViewMode('filaments'));

    act(() => {
      result.current[1]('table');
    });

    expect(result.current[0]).toBe('table');
    expect(localStorage.getItem('catalog-view-filaments')).toBe('table');
  });

  it('should restore view mode from localStorage', () => {
    localStorage.setItem('catalog-view-hotends', 'table');

    const { result } = renderHook(() => useCatalogViewMode('hotends'));

    expect(result.current[0]).toBe('table');
  });

  it('should handle different tabs separately', () => {
    const { result: filaments } = renderHook(() => useCatalogViewMode('filaments'));
    const { result: hotends } = renderHook(() => useCatalogViewMode('hotends'));

    act(() => {
      filaments.current[1]('table');
    });

    act(() => {
      hotends.current[1]('grid');
    });

    expect(filaments.current[0]).toBe('table');
    expect(hotends.current[0]).toBe('grid');
    expect(localStorage.getItem('catalog-view-filaments')).toBe('table');
    expect(localStorage.getItem('catalog-view-hotends')).toBe('grid');
  });

  it('should update when tab prop changes', () => {
    localStorage.setItem('catalog-view-filaments', 'table');
    localStorage.setItem('catalog-view-nozzles', 'grid');

    const { result, rerender } = renderHook(
      ({ tab }) => useCatalogViewMode(tab),
      { initialProps: { tab: 'filaments' as CatalogTab } }
    );

    expect(result.current[0]).toBe('table');

    rerender({ tab: 'nozzles' as CatalogTab });

    expect(result.current[0]).toBe('grid');
  });

  it('should handle localStorage errors gracefully', () => {
    const setItemSpy = vi.spyOn(Storage.prototype, 'setItem');
    setItemSpy.mockImplementation(() => {
      throw new Error('localStorage unavailable');
    });

    const { result } = renderHook(() => useCatalogViewMode('filaments'));

    act(() => {
      result.current[1]('table');
    });

    expect(result.current[0]).toBe('table');
    setItemSpy.mockRestore();
  });

  it('should handle all catalog tabs', () => {
    const tabs: CatalogTab[] = ['filaments', 'hotends', 'extruders', 'toolheads', 'nozzles', 'printer-models'];

    tabs.forEach((tab) => {
      const { result } = renderHook(() => useCatalogViewMode(tab));
      expect(result.current[0]).toBe('grid');
    });
  });
});

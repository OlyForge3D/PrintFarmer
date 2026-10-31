import { renderHook, act } from '@testing-library/react';
import { describe, it, expect } from 'vitest';
import { compareTableValues, useTableSort } from '../useTableSort';

interface TestData {
  name: string;
  value: number;
  date: Date;
}

describe('useTableSort', () => {
  it('compares raw numeric, date, boolean and text values without bringing missing values first descending', () => {
    expect(compareTableValues(2, 10, 'asc')).toBeLessThan(0);
    expect(compareTableValues(Date.parse('2026-01-01'), Date.parse('2026-10-01'), 'desc')).toBeGreaterThan(0);
    expect(compareTableValues(false, true, 'asc')).toBeLessThan(0);
    expect(compareTableValues('Beta', 'Alpha', 'asc')).toBeGreaterThan(0);
    for (const direction of ['asc', 'desc'] as const) {
      expect(compareTableValues(null, 10, direction)).toBeGreaterThan(0);
      expect(compareTableValues(undefined, 'Alpha', direction)).toBeGreaterThan(0);
      expect(compareTableValues(Number.NaN, 10, direction)).toBeGreaterThan(0);
      expect(compareTableValues(0, null, direction)).toBeLessThan(0);
      expect(compareTableValues(null, undefined, direction)).toBe(0);
    }
  });

  const mockData: TestData[] = [
    { name: 'Beta', value: 20, date: new Date('2024-01-02') },
    { name: 'Alpha', value: 10, date: new Date('2024-01-01') },
    { name: 'Gamma', value: 30, date: new Date('2024-01-03') },
  ];

  it('should initialize with no sorting', () => {
    const { result } = renderHook(() => useTableSort<'name' | 'value'>());

    expect(result.current.sortState.column).toBe(null);
    expect(result.current.sortState.direction).toBe(null);
  });

  it('should initialize with default column and direction', () => {
    const { result } = renderHook(() => useTableSort('name', 'asc'));

    expect(result.current.sortState.column).toBe('name');
    expect(result.current.sortState.direction).toBe('asc');
  });

  it('should sort ascending on first click', () => {
    const { result } = renderHook(() => useTableSort<'name' | 'value'>());

    act(() => {
      result.current.handleSort('name');
    });

    expect(result.current.sortState.column).toBe('name');
    expect(result.current.sortState.direction).toBe('asc');
  });

  it('should cycle through asc -> desc -> null', () => {
    const { result } = renderHook(() => useTableSort<'name' | 'value'>());

    // First click: asc
    act(() => {
      result.current.handleSort('name');
    });
    expect(result.current.sortState.direction).toBe('asc');

    // Second click: desc
    act(() => {
      result.current.handleSort('name');
    });
    expect(result.current.sortState.direction).toBe('desc');

    // Third click: null (unsorted)
    act(() => {
      result.current.handleSort('name');
    });
    expect(result.current.sortState.direction).toBe(null);
  });

  it('applies batched clicks atomically and preserves descending ties', () => {
    const { result } = renderHook(() => useTableSort<'name' | 'value'>());
    act(() => {
      result.current.handleSort('value');
      result.current.handleSort('value');
    });
    expect(result.current.sortState).toEqual({ column: 'value', direction: 'desc' });
    const data = [{ ...mockData[0], value: 10 }, mockData[1], mockData[2]];
    expect(result.current.sortData(data, { value: (a, b) => a.value - b.value }).map(item => item.name))
      .toEqual(['Gamma', 'Beta', 'Alpha']);
    act(() => result.current.handleSort('value'));
    expect(result.current.sortState).toEqual({ column: null, direction: null });
    expect(result.current.sortData(data, { value: (a, b) => a.value - b.value })).toBe(data);
  });

  it('should sort data ascending', () => {
    const { result } = renderHook(() => useTableSort<'name' | 'value'>('name', 'asc'));

    const sorted = result.current.sortData(mockData, {
      name: (a, b) => a.name.localeCompare(b.name),
    });

    expect(sorted[0].name).toBe('Alpha');
    expect(sorted[1].name).toBe('Beta');
    expect(sorted[2].name).toBe('Gamma');
  });

  it('should sort data descending', () => {
    const { result } = renderHook(() => useTableSort<'name' | 'value'>('name', 'desc'));

    const sorted = result.current.sortData(mockData, {
      name: (a, b) => a.name.localeCompare(b.name),
    });

    expect(sorted[0].name).toBe('Gamma');
    expect(sorted[1].name).toBe('Beta');
    expect(sorted[2].name).toBe('Alpha');
  });

  it('should return unsorted data when direction is null', () => {
    const { result } = renderHook(() => useTableSort<'name' | 'value'>());

    const sorted = result.current.sortData(mockData, {
      name: (a, b) => a.name.localeCompare(b.name),
    });

    expect(sorted).toEqual(mockData);
  });

  it('should sort by numeric values', () => {
    const { result } = renderHook(() => useTableSort<'name' | 'value'>('value', 'asc'));

    const sorted = result.current.sortData(mockData, {
      value: (a, b) => a.value - b.value,
    });

    expect(sorted[0].value).toBe(10);
    expect(sorted[1].value).toBe(20);
    expect(sorted[2].value).toBe(30);
  });

  it('should return unsorted when comparator is missing', () => {
    const { result } = renderHook(() => useTableSort<'name' | 'value'>('name', 'asc'));

    const sorted = result.current.sortData(mockData, {});

    expect(sorted).toEqual(mockData);
  });

  it('should change sort column', () => {
    const { result } = renderHook(() => useTableSort<'name' | 'value'>('name', 'asc'));

    expect(result.current.sortState.column).toBe('name');

    act(() => {
      result.current.handleSort('value');
    });

    expect(result.current.sortState.column).toBe('value');
    expect(result.current.sortState.direction).toBe('asc');
  });

  it('should allow manual sort state update', () => {
    const { result } = renderHook(() => useTableSort<'name' | 'value'>());

    act(() => {
      result.current.setSortState({ column: 'value', direction: 'desc' });
    });

    expect(result.current.sortState.column).toBe('value');
    expect(result.current.sortState.direction).toBe('desc');
  });
});

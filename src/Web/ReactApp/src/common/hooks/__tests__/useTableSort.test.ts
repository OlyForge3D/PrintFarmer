import { renderHook, act } from '@testing-library/react';
import { describe, it, expect } from 'vitest';
import { useTableSort } from '../useTableSort';

interface TestData {
  name: string;
  value: number;
  date: Date;
}

describe('useTableSort', () => {
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

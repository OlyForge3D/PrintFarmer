import { StrictMode, createElement, type PropsWithChildren } from 'react';
import { act, renderHook } from '@testing-library/react';
import { beforeEach, describe, expect, it } from 'vitest';
import { useSlicerViewMode } from '@/features/slicer/hooks/useSlicerViewMode';

const storageKey = 'printfarmer-slicer-viewmode';
const wrapper = ({ children }: PropsWithChildren) => createElement(StrictMode, null, children);

describe('useSlicerViewMode', () => {
  beforeEach(() => localStorage.clear());

  it('switches both directions exactly once in StrictMode and synchronizes mounted editors', () => {
    const { result } = renderHook(() => ({
      first: useSlicerViewMode(),
      second: useSlicerViewMode(),
    }), { wrapper });

    act(() => result.current.first[1]());
    expect(result.current.first[0]).toBe('advanced');
    expect(result.current.second[0]).toBe('advanced');
    expect(localStorage.getItem(storageKey)).toBe('advanced');

    act(() => result.current.second[1]());
    expect(result.current.first[0]).toBe('simple');
    expect(result.current.second[0]).toBe('simple');
    expect(localStorage.getItem(storageKey)).toBe('simple');
  });

  it('restores persisted mode without toggling during StrictMode mount', () => {
    localStorage.setItem(storageKey, 'advanced');
    const { result } = renderHook(useSlicerViewMode, { wrapper });
    expect(result.current[0]).toBe('advanced');
    expect(localStorage.getItem(storageKey)).toBe('advanced');
  });
});

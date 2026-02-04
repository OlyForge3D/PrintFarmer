import { renderHook } from '@testing-library/react';
import { describe, it, expect, vi } from 'vitest';
import { useSlicer } from '../useSlicer';
import { SlicerContext, SlicerContextValue } from '@/contexts/SlicerTypes';

describe('useSlicer', () => {
  it('should return slicer context value', () => {
    const mockContextValue: SlicerContextValue = {
      isSlicerAvailable: true,
      workerCount: 2,
      activeJobs: 1,
      queuedJobs: 0
    };

    const wrapper = ({ children }: { children: React.ReactNode }) => (
      <SlicerContext.Provider value={mockContextValue}>
        {children}
      </SlicerContext.Provider>
    );

    const { result } = renderHook(() => useSlicer(), { wrapper });

    expect(result.current).toEqual(mockContextValue);
  });

  it('should throw error when used outside SlicerProvider', () => {
    // Suppress console.error for this test
    const consoleSpy = vi.spyOn(console, 'error').mockImplementation(() => {});

    expect(() => {
      renderHook(() => useSlicer());
    }).toThrow('useSlicer must be used within a SlicerProvider');

    consoleSpy.mockRestore();
  });

  it('should return isSlicerAvailable as false when slicer is unavailable', () => {
    const mockContextValue: SlicerContextValue = {
      isSlicerAvailable: false,
      workerCount: 0,
      activeJobs: 0,
      queuedJobs: 5
    };

    const wrapper = ({ children }: { children: React.ReactNode }) => (
      <SlicerContext.Provider value={mockContextValue}>
        {children}
      </SlicerContext.Provider>
    );

    const { result } = renderHook(() => useSlicer(), { wrapper });

    expect(result.current.isSlicerAvailable).toBe(false);
    expect(result.current.workerCount).toBe(0);
    expect(result.current.queuedJobs).toBe(5);
  });

  it('should return correct worker count', () => {
    const mockContextValue: SlicerContextValue = {
      isSlicerAvailable: true,
      workerCount: 4,
      activeJobs: 2,
      queuedJobs: 1
    };

    const wrapper = ({ children }: { children: React.ReactNode }) => (
      <SlicerContext.Provider value={mockContextValue}>
        {children}
      </SlicerContext.Provider>
    );

    const { result } = renderHook(() => useSlicer(), { wrapper });

    expect(result.current.workerCount).toBe(4);
  });

  it('should handle zero active and queued jobs', () => {
    const mockContextValue: SlicerContextValue = {
      isSlicerAvailable: true,
      workerCount: 2,
      activeJobs: 0,
      queuedJobs: 0
    };

    const wrapper = ({ children }: { children: React.ReactNode }) => (
      <SlicerContext.Provider value={mockContextValue}>
        {children}
      </SlicerContext.Provider>
    );

    const { result } = renderHook(() => useSlicer(), { wrapper });

    expect(result.current.activeJobs).toBe(0);
    expect(result.current.queuedJobs).toBe(0);
  });
});

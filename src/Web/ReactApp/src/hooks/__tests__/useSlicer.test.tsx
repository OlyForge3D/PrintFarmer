import { renderHook } from "@testing-library/react";
import { describe, it, expect, vi } from "vitest";
import { useSlicer } from "../useSlicer";
import { SlicerContext, SlicerContextValue } from "@/contexts/SlicerTypes";

describe("useSlicer", () => {
  it("should return slicer context value", () => {
    const mockContextValue: SlicerContextValue = {
      isSlicerAvailable: true,
      settingEnabled: true,
      hasWorkers: true,
      isLoading: false,
      workerCount: 2,
      refreshWorkers: vi.fn().mockResolvedValue(undefined),
    };

    const wrapper = ({ children }: { children: React.ReactNode }) => (
      <SlicerContext.Provider value={mockContextValue}>
        {children}
      </SlicerContext.Provider>
    );

    const { result } = renderHook(() => useSlicer(), { wrapper });

    expect(result.current).toEqual(mockContextValue);
  });

  it("should throw error when used outside SlicerProvider", () => {
    // Suppress console.error for this test
    const consoleSpy = vi.spyOn(console, "error").mockImplementation(() => {});

    expect(() => {
      renderHook(() => useSlicer());
    }).toThrow("useSlicer must be used within a SlicerProvider");

    consoleSpy.mockRestore();
  });

  it("should return isSlicerAvailable as false when slicer is unavailable", () => {
    const mockContextValue: SlicerContextValue = {
      isSlicerAvailable: false,
      settingEnabled: true,
      hasWorkers: true,
      isLoading: false,
      workerCount: 0,
      refreshWorkers: vi.fn().mockResolvedValue(undefined),
    };

    const wrapper = ({ children }: { children: React.ReactNode }) => (
      <SlicerContext.Provider value={mockContextValue}>
        {children}
      </SlicerContext.Provider>
    );

    const { result } = renderHook(() => useSlicer(), { wrapper });

    expect(result.current.isSlicerAvailable).toBe(false);
    expect(result.current.workerCount).toBe(0);
    expect(result.current.settingEnabled).toBe(true);
    expect(result.current.hasWorkers).toBe(true);
  });

  it("should return correct worker count", () => {
    const mockContextValue: SlicerContextValue = {
      isSlicerAvailable: true,
      settingEnabled: true,
      hasWorkers: true,
      isLoading: false,
      workerCount: 4,
      refreshWorkers: vi.fn().mockResolvedValue(undefined),
    };

    const wrapper = ({ children }: { children: React.ReactNode }) => (
      <SlicerContext.Provider value={mockContextValue}>
        {children}
      </SlicerContext.Provider>
    );

    const { result } = renderHook(() => useSlicer(), { wrapper });

    expect(result.current.workerCount).toBe(4);
  });

  it("should return the worker state controls", () => {
    const mockContextValue: SlicerContextValue = {
      isSlicerAvailable: true,
      settingEnabled: true,
      hasWorkers: true,
      isLoading: false,
      workerCount: 2,
      refreshWorkers: vi.fn().mockResolvedValue(undefined),
    };

    const wrapper = ({ children }: { children: React.ReactNode }) => (
      <SlicerContext.Provider value={mockContextValue}>
        {children}
      </SlicerContext.Provider>
    );

    const { result } = renderHook(() => useSlicer(), { wrapper });

    expect(result.current.isLoading).toBe(false);
    expect(result.current.refreshWorkers).toBe(mockContextValue.refreshWorkers);
  });
});

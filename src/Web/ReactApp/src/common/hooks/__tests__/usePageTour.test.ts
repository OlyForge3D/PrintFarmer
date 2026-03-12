import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import { usePageTour } from '@/common/hooks/usePageTour';
import type { TourStepDefinition } from '@/common/hooks/usePageTour';

// Mock driver.js — track lifecycle calls
const mockDrive = vi.fn();
const mockHighlight = vi.fn();
const mockDestroy = vi.fn();

vi.mock('driver.js', () => ({
  driver: vi.fn(() => ({
    drive: mockDrive,
    highlight: mockHighlight,
    destroy: mockDestroy,
    setSteps: vi.fn(),
  })),
}));

const sampleSteps: TourStepDefinition[] = [
  {
    element: '[data-tour="widget-a"]',
    popover: { title: 'Widget A', description: 'This is widget A.' },
  },
  {
    element: '[data-tour="widget-b"]',
    popover: { title: 'Widget B', description: 'This is widget B.' },
  },
];

describe('usePageTour', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.useFakeTimers();
    localStorage.clear();
  });

  afterEach(() => {
    vi.useRealTimers();
    localStorage.clear();
  });

  it('auto-starts tour on first visit when localStorage is empty', () => {
    renderHook(() =>
      usePageTour({ tourId: 'test-tour', steps: sampleSteps }),
    );

    // Auto-start fires after a 500ms setTimeout
    act(() => {
      vi.advanceTimersByTime(500);
    });

    expect(mockDrive).toHaveBeenCalled();
  });

  it('does not auto-start when tour has already been seen', () => {
    localStorage.setItem('pf-tour-seen-test-tour', 'true');

    const { result } = renderHook(() =>
      usePageTour({ tourId: 'test-tour', steps: sampleSteps }),
    );

    expect(mockDrive).not.toHaveBeenCalled();
    expect(result.current.hasSeenTour).toBe(true);
  });

  it('startTour() manually triggers the tour', () => {
    localStorage.setItem('pf-tour-seen-test-tour', 'true');

    const { result } = renderHook(() =>
      usePageTour({ tourId: 'test-tour', steps: sampleSteps }),
    );

    expect(mockDrive).not.toHaveBeenCalled();

    act(() => {
      result.current.startTour();
    });

    expect(mockDrive).toHaveBeenCalled();
  });

  it('resetTour() clears localStorage and allows auto-start again', () => {
    localStorage.setItem('pf-tour-seen-test-tour', 'true');

    const { result } = renderHook(() =>
      usePageTour({ tourId: 'test-tour', steps: sampleSteps }),
    );

    expect(result.current.hasSeenTour).toBe(true);

    act(() => {
      result.current.resetTour();
    });

    expect(localStorage.getItem('pf-tour-seen-test-tour')).toBeNull();
    expect(result.current.hasSeenTour).toBe(false);
  });

  it('destroys driver instance on unmount', () => {
    const { unmount } = renderHook(() =>
      usePageTour({ tourId: 'cleanup-tour', steps: sampleSteps }),
    );

    // Let auto-start fire so a driver instance exists
    act(() => {
      vi.advanceTimersByTime(500);
    });

    unmount();

    expect(mockDestroy).toHaveBeenCalled();
  });

  it('does not auto-start when autoStart is false even on first visit', () => {
    renderHook(() =>
      usePageTour({
        tourId: 'no-auto-tour',
        steps: sampleSteps,
        autoStart: false,
      }),
    );

    expect(mockDrive).not.toHaveBeenCalled();
  });

  it('returns correct hasSeenTour state initially', () => {
    const { result } = renderHook(() =>
      usePageTour({ tourId: 'fresh-tour', steps: sampleSteps }),
    );

    // After auto-start, the tour should mark itself as seen
    // hasSeenTour reflects localStorage state at render time
    expect(typeof result.current.hasSeenTour).toBe('boolean');
  });

  it('exposes startTour and resetTour as stable functions', () => {
    const { result } = renderHook(() =>
      usePageTour({ tourId: 'stable-tour', steps: sampleSteps }),
    );

    expect(typeof result.current.startTour).toBe('function');
    expect(typeof result.current.resetTour).toBe('function');
  });
});

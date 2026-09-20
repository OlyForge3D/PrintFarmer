import '@testing-library/jest-dom';
import { act, renderHook } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { usePageTour } from '@/common/hooks/usePageTour';

const destroyMock = vi.fn();
const driveMock = vi.fn();
const driverMock = vi.fn(() => ({
  drive: driveMock,
  destroy: destroyMock,
}));

vi.mock('driver.js', () => ({
  driver: (...args: unknown[]) => driverMock(...args),
}));

describe('usePageTour', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.clearAllMocks();
    window.localStorage.clear();
    Object.defineProperty(window, 'matchMedia', {
      writable: true,
      value: vi.fn().mockImplementation(() => ({
        matches: false,
        addEventListener: vi.fn(),
        removeEventListener: vi.fn(),
      })),
    });
  });

  it('auto-starts after the first-visit delay when enabled', () => {
    renderHook(() => usePageTour({
      tourId: 'settings-tour',
      steps: [{ element: 'body', popover: { title: 'Tour', description: 'Desc' } }],
      autoStart: true,
    }));

    act(() => {
      vi.advanceTimersByTime(499);
    });
    expect(driveMock).not.toHaveBeenCalled();

    act(() => {
      vi.advanceTimersByTime(1);
    });
    expect(driverMock).toHaveBeenCalledTimes(1);
    expect(driveMock).toHaveBeenCalledTimes(1);
  });

  it('cancels a pending auto-start when autoStart flips false before the timer fires', () => {
    const { rerender } = renderHook(
      ({ autoStart }) => usePageTour({
        tourId: 'settings-tour',
        steps: [{ element: 'body', popover: { title: 'Tour', description: 'Desc' } }],
        autoStart,
      }),
      { initialProps: { autoStart: true } },
    );

    act(() => {
      vi.advanceTimersByTime(300);
    });

    rerender({ autoStart: false });

    act(() => {
      vi.advanceTimersByTime(300);
    });

    expect(driverMock).not.toHaveBeenCalled();
    expect(driveMock).not.toHaveBeenCalled();
  });
});

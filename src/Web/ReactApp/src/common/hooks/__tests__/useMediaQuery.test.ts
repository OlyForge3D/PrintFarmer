import { describe, it, expect, vi, afterEach } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import { useMediaQuery, useIsLgBreakpoint, LG_BREAKPOINT_QUERY } from '../useMediaQuery';

type Listener = (event: MediaQueryListEvent) => void;

function mockMatchMedia(initialMatches: boolean) {
  let matches = initialMatches;
  const listeners = new Set<Listener>();

  const mql = {
    get matches() {
      return matches;
    },
    media: '',
    onchange: null,
    addEventListener: vi.fn((_event: 'change', listener: Listener) => {
      listeners.add(listener);
    }),
    removeEventListener: vi.fn((_event: 'change', listener: Listener) => {
      listeners.delete(listener);
    }),
    addListener: vi.fn(),
    removeListener: vi.fn(),
    dispatchEvent: vi.fn(),
  };

  const matchMediaMock = vi.fn().mockReturnValue(mql);
  window.matchMedia = matchMediaMock as unknown as typeof window.matchMedia;

  const fireChange = (nextMatches: boolean) => {
    matches = nextMatches;
    listeners.forEach((listener) => listener({ matches: nextMatches } as MediaQueryListEvent));
  };

  return { matchMediaMock, fireChange };
}

describe('useMediaQuery', () => {
  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('returns the initial match state from matchMedia', () => {
    mockMatchMedia(true);
    const { result } = renderHook(() => useMediaQuery('(min-width: 1024px)'));
    expect(result.current).toBe(true);
  });

  it('returns false when matchMedia initially does not match', () => {
    mockMatchMedia(false);
    const { result } = renderHook(() => useMediaQuery('(min-width: 1024px)'));
    expect(result.current).toBe(false);
  });

  it('updates when the media query change event fires', () => {
    const { fireChange } = mockMatchMedia(false);
    const { result } = renderHook(() => useMediaQuery('(min-width: 1024px)'));

    expect(result.current).toBe(false);

    act(() => {
      fireChange(true);
    });

    expect(result.current).toBe(true);
  });

  it('unsubscribes the previous listener on unmount', () => {
    const { matchMediaMock } = mockMatchMedia(false);
    const { unmount } = renderHook(() => useMediaQuery('(min-width: 1024px)'));

    const mql = matchMediaMock.mock.results[0].value;
    expect(mql.removeEventListener).not.toHaveBeenCalled();

    unmount();

    expect(mql.removeEventListener).toHaveBeenCalledWith('change', expect.any(Function));
  });
});

describe('useIsLgBreakpoint', () => {
  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('queries Tailwind\'s lg breakpoint (1024px)', () => {
    const { matchMediaMock } = mockMatchMedia(true);
    const { result } = renderHook(() => useIsLgBreakpoint());

    expect(matchMediaMock).toHaveBeenCalledWith(LG_BREAKPOINT_QUERY);
    expect(result.current).toBe(true);
  });
});

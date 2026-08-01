import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  clearFloatingBarInset,
  observeFloatingBarWidth,
} from '@/common/components/floatingBarInset';

/**
 * #1010 — page headers used to reserve a hardcoded `lg:mr-72` (288px) to keep
 * their action buttons out from under the fixed FloatingControlBar. The
 * reservation is real and load-bearing; the magic number was the problem.
 */

function insetValue(): string {
  return document.documentElement.style.getPropertyValue('--pf-floating-bar-inset');
}

function barOfWidth(width: number): HTMLElement {
  const element = document.createElement('div');
  vi.spyOn(element, 'getBoundingClientRect').mockReturnValue({
    width,
    height: 44,
    top: 0,
    left: 0,
    right: width,
    bottom: 44,
    x: 0,
    y: 0,
    toJSON: () => ({}),
  } as DOMRect);
  return element;
}

describe('floating bar inset (#1010)', () => {
  afterEach(() => {
    clearFloatingBarInset();
    vi.restoreAllMocks();
  });

  it('reserves the bar width plus its offset and a gutter', () => {
    const cleanup = observeFloatingBarWidth(barOfWidth(200));

    // 200 measured + 16 (the bar's own right-4) + 16 gutter.
    expect(insetValue()).toBe('232px');

    cleanup();
  });

  it('reserves nothing while the bar is hidden, since a hidden element measures zero', () => {
    const cleanup = observeFloatingBarWidth(barOfWidth(0));

    expect(insetValue()).toBe('32px');

    cleanup();
  });

  it('stops reserving space once the bar unmounts', () => {
    const cleanup = observeFloatingBarWidth(barOfWidth(200));
    expect(insetValue()).toBe('232px');

    cleanup();

    expect(insetValue()).toBe('');
  });

  it('republishes when the bar resizes, which is the whole point of measuring', () => {
    const observed: (() => void)[] = [];
    class FakeResizeObserver {
      constructor(private readonly callback: () => void) {
        observed.push(callback);
      }
      observe() {}
      disconnect() {}
    }
    vi.stubGlobal('ResizeObserver', FakeResizeObserver);

    const element = document.createElement('div');
    let width = 200;
    vi.spyOn(element, 'getBoundingClientRect').mockImplementation(
      () => ({ width, height: 44, top: 0, left: 0, right: width, bottom: 44, x: 0, y: 0, toJSON: () => ({}) }) as DOMRect,
    );

    const cleanup = observeFloatingBarWidth(element);
    expect(insetValue()).toBe('232px');

    width = 320;
    observed.forEach((callback) => callback());

    expect(insetValue()).toBe('352px');

    cleanup();
    vi.unstubAllGlobals();
  });
});

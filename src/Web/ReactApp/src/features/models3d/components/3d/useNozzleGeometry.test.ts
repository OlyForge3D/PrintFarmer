/**
 * Tests for useNozzleGeometry (PrinterBedVisualization.tsx)
 *
 * Verifies that nozzle geometry is memoized by diameter so that live
 * position/state updates reuse the existing Three.js geometry instance,
 * while diameter changes allocate exactly one replacement and dispose the
 * previous geometry (issue #1747).
 */

import { describe, it, expect, vi, beforeEach } from 'vitest';
import { renderHook } from '@testing-library/react';
import { useNozzleGeometry } from './useNozzleGeometry';

const { generateNozzleGeometryMock } = vi.hoisted(() => ({
  generateNozzleGeometryMock: vi.fn(),
}));

vi.mock('@/common/utils/bedGeometryGenerator', () => ({
  generateNozzleGeometry: generateNozzleGeometryMock,
}));

function createFakeGeometry() {
  return { dispose: vi.fn() };
}

describe('useNozzleGeometry', () => {
  beforeEach(() => {
    generateNozzleGeometryMock.mockReset();
    generateNozzleGeometryMock.mockImplementation(() => createFakeGeometry());
  });

  it('reuses the existing geometry instance across re-renders with the same diameter', () => {
    const { result, rerender } = renderHook(
      ({ diameter }) => useNozzleGeometry(diameter),
      { initialProps: { diameter: 0.4 } }
    );

    const firstGeometry = result.current;
    expect(generateNozzleGeometryMock).toHaveBeenCalledTimes(1);

    // Simulate a live nozzle position update: diameter is unchanged.
    rerender({ diameter: 0.4 });
    rerender({ diameter: 0.4 });

    expect(result.current).toBe(firstGeometry);
    expect(generateNozzleGeometryMock).toHaveBeenCalledTimes(1);
    expect(firstGeometry.dispose).not.toHaveBeenCalled();
  });

  it('creates exactly one replacement geometry and disposes the previous one when diameter changes', () => {
    const { result, rerender } = renderHook(
      ({ diameter }) => useNozzleGeometry(diameter),
      { initialProps: { diameter: 0.4 } }
    );

    const firstGeometry = result.current;
    expect(generateNozzleGeometryMock).toHaveBeenCalledTimes(1);

    rerender({ diameter: 0.6 });

    const secondGeometry = result.current;
    expect(secondGeometry).not.toBe(firstGeometry);
    expect(generateNozzleGeometryMock).toHaveBeenCalledTimes(2);
    expect(firstGeometry.dispose).toHaveBeenCalledTimes(1);
    expect(secondGeometry.dispose).not.toHaveBeenCalled();
  });

  it('disposes the current geometry on unmount', () => {
    const { result, unmount } = renderHook(
      ({ diameter }) => useNozzleGeometry(diameter),
      { initialProps: { diameter: 0.4 } }
    );

    const geometry = result.current;
    unmount();

    expect(geometry.dispose).toHaveBeenCalledTimes(1);
  });
});

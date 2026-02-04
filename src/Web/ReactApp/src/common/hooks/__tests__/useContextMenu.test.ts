import { renderHook, act } from '@testing-library/react';
import { describe, it, expect } from 'vitest';
import { useContextMenu } from '../useContextMenu';

describe('useContextMenu', () => {
  it('should initialize with menu closed', () => {
    const { result } = renderHook(() => useContextMenu());

    expect(result.current.isOpen).toBe(false);
    expect(result.current.position).toBe(null);
  });

  it('should open menu with correct position on context menu event', () => {
    const { result } = renderHook(() => useContextMenu());

    const mockEvent = {
      preventDefault: vi.fn(),
      clientX: 100,
      clientY: 200,
    } as unknown as React.MouseEvent;

    act(() => {
      result.current.handleContextMenu(mockEvent);
    });

    expect(mockEvent.preventDefault).toHaveBeenCalled();
    expect(result.current.isOpen).toBe(true);
    expect(result.current.position).toEqual({ x: 100, y: 200 });
  });

  it('should close menu', () => {
    const { result } = renderHook(() => useContextMenu());

    // Open menu first
    act(() => {
      result.current.handleContextMenu({
        preventDefault: vi.fn(),
        clientX: 100,
        clientY: 200,
      } as unknown as React.MouseEvent);
    });

    expect(result.current.isOpen).toBe(true);

    // Close menu
    act(() => {
      result.current.closeMenu();
    });

    expect(result.current.isOpen).toBe(false);
    expect(result.current.position).toBe(null);
  });

  it('should handle multiple context menu events', () => {
    const { result } = renderHook(() => useContextMenu());

    // First event
    act(() => {
      result.current.handleContextMenu({
        preventDefault: vi.fn(),
        clientX: 100,
        clientY: 200,
      } as unknown as React.MouseEvent);
    });

    expect(result.current.position).toEqual({ x: 100, y: 200 });

    // Second event at different position
    act(() => {
      result.current.handleContextMenu({
        preventDefault: vi.fn(),
        clientX: 300,
        clientY: 400,
      } as unknown as React.MouseEvent);
    });

    expect(result.current.position).toEqual({ x: 300, y: 400 });
  });

  it('should prevent default browser context menu', () => {
    const { result } = renderHook(() => useContextMenu());

    const mockEvent = {
      preventDefault: vi.fn(),
      clientX: 100,
      clientY: 200,
    } as unknown as React.MouseEvent;

    act(() => {
      result.current.handleContextMenu(mockEvent);
    });

    expect(mockEvent.preventDefault).toHaveBeenCalled();
  });
});

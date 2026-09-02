import { describe, it, expect, vi, afterEach } from 'vitest';
import { renderHook } from '@testing-library/react';
import { fireEvent } from '@testing-library/react';
import { useKeyboardNavigation } from '../useKeyboardNavigation';

describe('useKeyboardNavigation', () => {
  afterEach(() => {
    document.body.innerHTML = '';
  });

  it('selects the first item on Enter when nothing else has focus', () => {
    const items = ['a', 'b', 'c'];
    const onSelect = vi.fn();
    renderHook(() => useKeyboardNavigation(items, onSelect));

    fireEvent.keyDown(document.body, { key: 'Enter' });

    expect(onSelect).toHaveBeenCalledWith('a', 0);
  });

  // Regression test for #2373: a page-wide keyboard-navigation hook with no
  // container/focus scoping was hijacking every Enter keypress, including on
  // the "Skip to main content" link. Activating the skip link opened the
  // first job's details modal instead of moving focus into <main>.
  it('does not hijack Enter pressed on a focused link (e.g. the skip link)', () => {
    const items = ['a', 'b', 'c'];
    const onSelect = vi.fn();
    renderHook(() => useKeyboardNavigation(items, onSelect));

    const link = document.createElement('a');
    link.href = '#main-content';
    link.textContent = 'Skip to main content';
    document.body.appendChild(link);
    link.focus();

    const event = new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true });
    link.dispatchEvent(event);

    expect(onSelect).not.toHaveBeenCalled();
    // The hook must not preventDefault() the link's own activation.
    expect(event.defaultPrevented).toBe(false);
  });

  it('does not hijack Enter pressed inside a focused input or button', () => {
    const items = ['a', 'b', 'c'];
    const onSelect = vi.fn();
    renderHook(() => useKeyboardNavigation(items, onSelect));

    const input = document.createElement('input');
    document.body.appendChild(input);
    input.focus();
    fireEvent.keyDown(input, { key: 'Enter' });

    const button = document.createElement('button');
    document.body.appendChild(button);
    button.focus();
    fireEvent.keyDown(button, { key: 'Enter' });

    expect(onSelect).not.toHaveBeenCalled();
  });

  it('still selects items via arrow keys plus Enter when focus is not on an interactive element', () => {
    const items = ['a', 'b', 'c'];
    const onSelect = vi.fn();
    renderHook(() => useKeyboardNavigation(items, onSelect));

    fireEvent.keyDown(document.body, { key: 'ArrowDown' });
    fireEvent.keyDown(document.body, { key: 'Enter' });

    expect(onSelect).toHaveBeenCalledWith('b', 1);
  });
});

import { describe, it, expect, vi, afterEach } from 'vitest';
import { renderHook, fireEvent } from '@testing-library/react';
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
    const inputEvent = new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true });
    input.dispatchEvent(inputEvent);

    const button = document.createElement('button');
    document.body.appendChild(button);
    button.focus();
    fireEvent.keyDown(button, { key: 'Enter' });

    expect(onSelect).not.toHaveBeenCalled();
    // The hook must not preventDefault() Enter-to-submit inside a form field.
    expect(inputEvent.defaultPrevented).toBe(false);
  });

  it('does not hijack Enter when the target is nested inside a focused button (e.g. an icon)', () => {
    const items = ['a', 'b', 'c'];
    const onSelect = vi.fn();
    renderHook(() => useKeyboardNavigation(items, onSelect));

    const button = document.createElement('button');
    const icon = document.createElement('span');
    button.appendChild(icon);
    document.body.appendChild(button);
    button.focus();

    fireEvent.keyDown(icon, { key: 'Enter' });

    expect(onSelect).not.toHaveBeenCalled();
  });

  // Regression coverage for a sibling manifestation of #2373 raised in review:
  // custom focusable widgets (e.g. a queue job row/card with tabIndex={0})
  // that already handle their own Enter key must not also be double-handled
  // by this page-wide listener with a stale selectedIndex.
  it('does not hijack Enter pressed on a focused custom widget with an explicit tabindex', () => {
    const items = ['a', 'b', 'c'];
    const onSelect = vi.fn();
    renderHook(() => useKeyboardNavigation(items, onSelect));

    const jobRow = document.createElement('article');
    jobRow.tabIndex = 0;
    document.body.appendChild(jobRow);
    jobRow.focus();

    fireEvent.keyDown(jobRow, { key: 'Enter' });

    expect(onSelect).not.toHaveBeenCalled();
  });

  it('does not hijack Escape pressed on a focused interactive element', () => {
    const items = ['a', 'b', 'c'];
    const onSelect = vi.fn();
    const onEscapeKey = vi.fn();
    renderHook(() => useKeyboardNavigation(items, onSelect, { onEscapeKey }));

    const button = document.createElement('button');
    document.body.appendChild(button);
    button.focus();
    fireEvent.keyDown(button, { key: 'Escape' });

    expect(onEscapeKey).not.toHaveBeenCalled();
  });

  it('still handles Escape and arrow keys when focus is not on an interactive element', () => {
    const items = ['a', 'b', 'c'];
    const onSelect = vi.fn();
    const onEscapeKey = vi.fn();
    renderHook(() => useKeyboardNavigation(items, onSelect, { onEscapeKey }));

    fireEvent.keyDown(document.body, { key: 'ArrowDown' });
    fireEvent.keyDown(document.body, { key: 'Escape' });

    expect(onEscapeKey).toHaveBeenCalledTimes(1);
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

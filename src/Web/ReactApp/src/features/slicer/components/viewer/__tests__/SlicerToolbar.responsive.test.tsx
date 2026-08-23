import { render, screen } from '@testing-library/react';
import { describe, it, expect, vi } from 'vitest';
import { SlicerToolbar } from '../SlicerToolbar';

// Regression coverage for issue #1902: the slicer toolbar's pinned left
// (hamburger/add-model/add-plate) and right (undo/redo/settings/keyboard)
// groups, plus the middle tool-button region, overlapped and clipped at
// narrow widths — 375x667, and even 1024x768 when the settings drawer ate
// into the available width. jsdom does not compute real flex layout, so
// these assertions pin the Tailwind classes that make the toolbar wrap whole
// groups onto additional lines (flex-wrap) instead of squeezing them into a
// single non-wrapping row or relying on an invisible horizontal scrollbar.
describe('SlicerToolbar narrow-width layout (issue #1902)', () => {
  const noop = () => vi.fn();

  it('wraps the toolbar row instead of forcing every group onto a single non-wrapping line', () => {
    const { container } = render(<SlicerToolbar onAddModel={noop()} />);

    const row = container.firstElementChild;
    expect(row).toHaveClass('flex-wrap');
  });

  it('keeps the pinned left group (hamburger/add model/add plate) from shrinking below its content', () => {
    render(<SlicerToolbar onAddModel={noop()} onToggleSidebar={noop()} />);

    const addModelButton = screen.getByTitle('Add Model (Ctrl+O)');
    const leftGroup = addModelButton.closest('div');
    expect(leftGroup).toHaveClass('shrink-0');
  });

  it('lets the tool-button region wrap its own buttons instead of clipping or scrolling invisibly', () => {
    render(<SlicerToolbar onAddModel={noop()} onArrange={noop()} />);

    const arrangeButton = screen.getByTitle('Auto Arrange (A)');
    const toolRegion = arrangeButton.closest('div');
    expect(toolRegion).toHaveClass('flex-wrap');
    expect(toolRegion).toHaveClass('min-w-0');
    expect(toolRegion).toHaveClass('flex-1');
    // No longer relies on an invisible/hidden horizontal scrollbar for reachability.
    expect(toolRegion?.className).not.toMatch(/overflow-x-auto/);
  });

  it('keeps the pinned right-side actions (undo/redo/settings/keyboard) reachable, not clipped, on narrow rows', () => {
    render(<SlicerToolbar onAddModel={noop()} onUndo={noop()} canUndo />);

    const undoButton = screen.getByTitle('Undo (Ctrl+Z)');
    expect(undoButton).toBeVisible();

    const rightGroup = undoButton.closest('div');
    expect(rightGroup).toHaveClass('shrink-0');
  });
});

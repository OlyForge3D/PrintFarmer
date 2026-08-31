import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { describe, it, expect, vi } from 'vitest';
import { SlicerToolbar } from '../SlicerToolbar';

// Regression coverage for issue #1902: the slicer toolbar's pinned left
// (hamburger/add-model/add-plate) and right (undo/redo/keyboard)
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

  it('keeps the pinned right-side actions (undo/redo/keyboard) reachable, not clipped, on narrow rows', () => {
    render(<SlicerToolbar onAddModel={noop()} onUndo={noop()} canUndo />);

    const undoButton = screen.getByTitle('Undo (Ctrl+Z)');
    expect(undoButton).toBeVisible();

    const rightGroup = undoButton.closest('div');
    expect(rightGroup).toHaveClass('shrink-0');
  });

  it('removes the settings and beta controls and opens the supported shortcuts flyout', async () => {
    render(<SlicerToolbar />);

    expect(screen.queryByText(/settings & profiles/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/^beta$/i)).not.toBeInTheDocument();

    const trigger = screen.getByRole('button', { name: 'Show keyboard shortcuts' });
    expect(trigger).toHaveAttribute('aria-expanded', 'false');

    fireEvent.click(trigger);

    const dialog = screen.getByRole('dialog', { name: 'Keyboard shortcuts' });
    expect(trigger).toHaveAttribute('aria-expanded', 'true');
    expect(trigger).toHaveAttribute('aria-controls', dialog.id);
    expect(within(dialog).getByText('Auto arrange models')).toBeInTheDocument();
    expect(within(dialog).getByText('Move selected model')).toBeInTheDocument();
    expect(within(dialog).getByText('Rotate selected model')).toBeInTheDocument();
    expect(within(dialog).getByText('Scale selected model')).toBeInTheDocument();
    expect(within(dialog).getByText('Cycle paint tools')).toBeInTheDocument();
    expect(within(dialog).getByText('Decrease or increase brush size')).toBeInTheDocument();
    expect(within(dialog).getByText('Toggle paint and erase while painting')).toBeInTheDocument();
    expect(within(dialog).getByText('Exit an active tool or clear the selection')).toBeInTheDocument();
    await waitFor(() => {
      expect(within(dialog).getByRole('button', { name: 'Close keyboard shortcuts' })).toHaveFocus();
    });
  });

  it('dismisses the shortcuts flyout with Escape and restores focus to its trigger', async () => {
    render(<SlicerToolbar />);

    const trigger = screen.getByRole('button', { name: 'Show keyboard shortcuts' });
    fireEvent.click(trigger);
    fireEvent.keyDown(document, { key: 'Escape' });

    await waitFor(() => {
      expect(screen.queryByRole('dialog', { name: 'Keyboard shortcuts' })).not.toBeInTheDocument();
      expect(trigger).toHaveFocus();
    });
  });

  it('dismisses the shortcuts flyout on an outside pointer press without stealing focus', async () => {
    render(
      <>
        <SlicerToolbar />
        <button type="button">Outside control</button>
      </>,
    );

    fireEvent.click(screen.getByRole('button', { name: 'Show keyboard shortcuts' }));
    const outsideControl = screen.getByRole('button', { name: 'Outside control' });

    fireEvent.mouseDown(outsideControl);
    outsideControl.focus();
    fireEvent.click(outsideControl);

    await waitFor(() => {
      expect(screen.queryByRole('dialog', { name: 'Keyboard shortcuts' })).not.toBeInTheDocument();
      expect(outsideControl).toHaveFocus();
    });
  });
});

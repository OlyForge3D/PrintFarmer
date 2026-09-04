import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { describe, it, expect, vi, afterEach } from 'vitest';
import { SlicerToolbar } from '../SlicerToolbar';
import { MOBILE_BREAKPOINT_QUERY } from '@/common/hooks/useMediaQuery';

// Mocks `window.matchMedia` for the mobile breakpoint query used by
// `useIsMobileBreakpoint()`, mirroring `PrintersPage.test.tsx`'s
// `mockLgBreakpoint` helper. The global jsdom polyfill in `src/test/setup.ts`
// always reports `matches: false`, so tests only need this helper when they
// want to exercise the compact/narrow-viewport branch.
function mockCompactViewport(matches: boolean) {
  const mql = {
    matches,
    media: MOBILE_BREAKPOINT_QUERY,
    onchange: null,
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
    addListener: vi.fn(),
    removeListener: vi.fn(),
    dispatchEvent: vi.fn(),
  };
  window.matchMedia = vi.fn().mockReturnValue(mql) as unknown as typeof window.matchMedia;
}

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
    const dialog = screen.getByRole('dialog', { name: 'Keyboard shortcuts' });
    const closeButton = within(dialog).getByRole('button', { name: 'Close keyboard shortcuts' });
    const workspaceKeyDown = vi.fn();
    window.addEventListener('keydown', workspaceKeyDown);

    fireEvent.keyDown(closeButton, { key: 'A' });
    fireEvent.keyDown(closeButton, { key: 't' });
    window.removeEventListener('keydown', workspaceKeyDown);

    expect(workspaceKeyDown).not.toHaveBeenCalled();

    fireEvent.keyDown(closeButton, { key: 'Escape' });

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

// Coverage for issue #2406: below Tailwind's `sm` breakpoint (e.g. 375px),
// the tool-button region collapses into a single "More tools" trigger
// instead of wrapping into a multi-row strip that pushes the canvas and
// slice controls below the fold.
describe('SlicerToolbar compact mobile layout (issue #2406)', () => {
  const noop = () => vi.fn();

  afterEach(() => {
    // Restore the default (non-compact) breakpoint so later tests/files
    // aren't affected by this override.
    mockCompactViewport(false);
  });

  it('keeps rendering individual tool buttons at the default (non-mobile) viewport', () => {
    render(<SlicerToolbar onAddModel={noop()} onArrange={noop()} />);

    expect(screen.getByTitle('Auto Arrange (A)')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'More tools' })).not.toBeInTheDocument();
  });

  it('collapses the tool region into a single "More tools" trigger at the mobile breakpoint', () => {
    mockCompactViewport(true);
    render(<SlicerToolbar onAddModel={noop()} onArrange={noop()} onUndo={noop()} canUndo />);

    expect(screen.queryByTitle('Auto Arrange (A)')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'More tools' })).toBeInTheDocument();

    // Pinned left/right groups stay visible and reachable.
    expect(screen.getByTitle('Add Model (Ctrl+O)')).toBeInTheDocument();
    expect(screen.getByTitle('Undo (Ctrl+Z)')).toBeInTheDocument();
  });

  it('opens the "More tools" menu showing grouped tools and invokes the handler on click', () => {
    mockCompactViewport(true);
    const onArrange = vi.fn();
    render(<SlicerToolbar onAddModel={noop()} onArrange={onArrange} hasModels />);

    const trigger = screen.getByRole('button', { name: 'More tools' });
    expect(trigger).toHaveAttribute('aria-expanded', 'false');

    fireEvent.click(trigger);

    const menu = screen.getByRole('menu', { name: 'More tools' });
    expect(trigger).toHaveAttribute('aria-expanded', 'true');
    expect(trigger).toHaveAttribute('aria-controls', menu.id);

    const arrangeItem = within(menu).getByRole('menuitem', { name: /Auto Arrange/ });
    fireEvent.click(arrangeItem);

    expect(onArrange).toHaveBeenCalledTimes(1);
    expect(screen.queryByRole('menu', { name: 'More tools' })).not.toBeInTheDocument();
  });

  it('dismisses the "More tools" menu with Escape and restores focus to its trigger', async () => {
    mockCompactViewport(true);
    render(<SlicerToolbar onAddModel={noop()} onArrange={noop()} />);

    const trigger = screen.getByRole('button', { name: 'More tools' });
    fireEvent.click(trigger);
    const menu = screen.getByRole('menu', { name: 'More tools' });

    fireEvent.keyDown(menu, { key: 'Escape' });

    await waitFor(() => {
      expect(screen.queryByRole('menu', { name: 'More tools' })).not.toBeInTheDocument();
      expect(trigger).toHaveFocus();
    });
  });

  it('dismisses the "More tools" menu on an outside pointer press', async () => {
    mockCompactViewport(true);
    render(
      <>
        <SlicerToolbar onAddModel={noop()} onArrange={noop()} />
        <button type="button">Outside control</button>
      </>,
    );

    fireEvent.click(screen.getByRole('button', { name: 'More tools' }));
    const outsideControl = screen.getByRole('button', { name: 'Outside control' });

    fireEvent.mouseDown(outsideControl);

    await waitFor(() => {
      expect(screen.queryByRole('menu', { name: 'More tools' })).not.toBeInTheDocument();
    });
  });

  it('does not invoke the handler or close the menu when clicking a disabled item (defense in depth)', () => {
    mockCompactViewport(true);
    const onArrange = vi.fn();
    // hasModels defaults to false, so Auto Arrange stays disabled.
    render(<SlicerToolbar onAddModel={noop()} onArrange={onArrange} />);

    fireEvent.click(screen.getByRole('button', { name: 'More tools' }));
    const menu = screen.getByRole('menu', { name: 'More tools' });
    const arrangeItem = within(menu).getByRole('menuitem', { name: /Auto Arrange/ });

    expect(arrangeItem).toBeDisabled();
    fireEvent.click(arrangeItem);

    expect(onArrange).not.toHaveBeenCalled();
    expect(screen.getByRole('menu', { name: 'More tools' })).toBeInTheDocument();
  });

  it('marks an active tool with aria-checked (menuitemcheckbox) inside the menu', () => {
    mockCompactViewport(true);
    render(<SlicerToolbar onAddModel={noop()} onMove={noop()} moveActive />);

    fireEvent.click(screen.getByRole('button', { name: 'More tools' }));
    const menu = screen.getByRole('menu', { name: 'More tools' });

    expect(within(menu).getByRole('menuitemcheckbox', { name: 'Move' })).toHaveAttribute('aria-checked', 'true');
    expect(within(menu).getByRole('menuitemcheckbox', { name: 'Rotate' })).toHaveAttribute('aria-checked', 'false');
  });

  it('hides Advanced-only tools from the menu in Simple mode but keeps them when not simple', () => {
    mockCompactViewport(true);
    const { rerender } = render(
      <SlicerToolbar onAddModel={noop()} onSplit={noop()} hasSelection simpleMode />,
    );

    fireEvent.click(screen.getByRole('button', { name: 'More tools' }));
    let menu = screen.getByRole('menu', { name: 'More tools' });
    expect(within(menu).queryByRole('menuitem', { name: 'Split Model' })).not.toBeInTheDocument();
    // Simple-mode-visible tool stays present (a toggle tool, so menuitemcheckbox).
    expect(within(menu).getByRole('menuitemcheckbox', { name: /Color Painting/ })).toBeInTheDocument();
    // Close before switching props so the next click re-opens rather than closes.
    fireEvent.click(screen.getByRole('button', { name: 'More tools' }));

    rerender(<SlicerToolbar onAddModel={noop()} onSplit={noop()} hasSelection simpleMode={false} />);
    fireEvent.click(screen.getByRole('button', { name: 'More tools' }));
    menu = screen.getByRole('menu', { name: 'More tools' });
    expect(within(menu).getByRole('menuitem', { name: 'Split Model' })).toBeInTheDocument();
  });

  it('moves focus between enabled menu items with ArrowDown/ArrowUp, skipping disabled ones', () => {
    mockCompactViewport(true);
    // Auto Arrange (disabled, hasModels=false) should be skipped by roving focus.
    render(<SlicerToolbar onAddModel={noop()} onOrient={noop()} onLayFlat={noop()} hasSelection />);

    fireEvent.click(screen.getByRole('button', { name: 'More tools' }));
    const menu = screen.getByRole('menu', { name: 'More tools' });
    const orientItem = within(menu).getByRole('menuitem', { name: 'Auto-Orient' });
    const layFlatItem = within(menu).getByRole('menuitem', { name: /Lay Flat/ });

    orientItem.focus();
    fireEvent.keyDown(menu, { key: 'ArrowDown' });
    expect(layFlatItem).toHaveFocus();

    fireEvent.keyDown(menu, { key: 'ArrowUp' });
    expect(orientItem).toHaveFocus();
  });

  it('focuses the first enabled menu item automatically when the menu opens', async () => {
    mockCompactViewport(true);
    // With no hasModels/hasSelection, only the always-enabled transform tools
    // (Move/Rotate/Scale) are enabled; simpleMode trims the rest of the list
    // so "Move" is unambiguously the first enabled item.
    render(<SlicerToolbar onAddModel={noop()} onMove={noop()} simpleMode />);

    fireEvent.click(screen.getByRole('button', { name: 'More tools' }));
    const menu = screen.getByRole('menu', { name: 'More tools' });
    const moveItem = within(menu).getByRole('menuitemcheckbox', { name: 'Move' });

    await waitFor(() => {
      expect(moveItem).toHaveFocus();
    });
  });

  it('jumps to the first/last enabled item with Home/End', () => {
    mockCompactViewport(true);
    // simpleMode plus no hasModels/hasSelection leaves exactly Move, Rotate,
    // and Scale enabled (the only tools with no disabled condition), so
    // "Move" and "Scale" are the deterministic first/last enabled items.
    render(
      <SlicerToolbar
        onAddModel={noop()}
        onMove={noop()}
        onRotate={noop()}
        onScale={noop()}
        simpleMode
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: 'More tools' }));
    const menu = screen.getByRole('menu', { name: 'More tools' });
    const moveItem = within(menu).getByRole('menuitemcheckbox', { name: 'Move' });
    const scaleItem = within(menu).getByRole('menuitemcheckbox', { name: 'Scale' });

    scaleItem.focus();
    fireEvent.keyDown(menu, { key: 'Home' });
    expect(moveItem).toHaveFocus();

    fireEvent.keyDown(menu, { key: 'End' });
    expect(scaleItem).toHaveFocus();
  });

  it('stops the slicer\'s single-letter global hotkeys from firing while the menu has keyboard focus', () => {
    mockCompactViewport(true);
    const onRotate = vi.fn();
    render(<SlicerToolbar onAddModel={noop()} onOrient={noop()} onRotate={onRotate} hasSelection />);

    fireEvent.click(screen.getByRole('button', { name: 'More tools' }));
    const menu = screen.getByRole('menu', { name: 'More tools' });

    // "R" is the global hotkey for Rotate elsewhere in the workspace; while
    // the menu panel has focus, keydowns must not bubble to a document/window
    // listener that could otherwise trigger it a second time.
    const event = new KeyboardEvent('keydown', { key: 'r', bubbles: true, cancelable: true });
    const stopPropagationSpy = vi.spyOn(event, 'stopPropagation');
    menu.dispatchEvent(event);

    expect(stopPropagationSpy).toHaveBeenCalled();
    expect(onRotate).not.toHaveBeenCalled();
  });

  it('lists every desktop tool from all three groups in the compact menu (tool parity)', () => {
    mockCompactViewport(true);
    render(
      <SlicerToolbar
        onAddModel={noop()}
        onArrange={noop()}
        onOrient={noop()}
        onLayFlat={noop()}
        onMove={noop()}
        onRotate={noop()}
        onScale={noop()}
        onSplit={noop()}
        onCut={noop()}
        onMeshBoolean={noop()}
        onVariableLayerHeight={noop()}
        onColorPaint={noop()}
        onSupportPaint={noop()}
        onSeamPaint={noop()}
        onFuzzySkinPaint={noop()}
        onTextTool={noop()}
        onMeasure={noop()}
        onAssemblyView={noop()}
        onSequentialToggle={noop()}
        hasModels
        hasSelection
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: 'More tools' }));
    const menu = screen.getByRole('menu', { name: 'More tools' });

    // Object Operations group.
    expect(within(menu).getByRole('menuitem', { name: /Auto Arrange/ })).toBeInTheDocument();
    expect(within(menu).getByRole('menuitem', { name: 'Auto-Orient' })).toBeInTheDocument();
    expect(within(menu).getByRole('menuitem', { name: /Lay Flat/ })).toBeInTheDocument();
    // Transform & Tools group.
    expect(within(menu).getByRole('menuitemcheckbox', { name: 'Move' })).toBeInTheDocument();
    expect(within(menu).getByRole('menuitemcheckbox', { name: 'Rotate' })).toBeInTheDocument();
    expect(within(menu).getByRole('menuitemcheckbox', { name: 'Scale' })).toBeInTheDocument();
    expect(within(menu).getByRole('menuitem', { name: 'Split Model' })).toBeInTheDocument();
    expect(within(menu).getByRole('menuitemcheckbox', { name: /Cut Model/ })).toBeInTheDocument();
    expect(within(menu).getByRole('menuitem', { name: /Mesh Boolean/ })).toBeInTheDocument();
    expect(within(menu).getByRole('menuitem', { name: /Variable Layer Height/ })).toBeInTheDocument();
    // Paint & Inspection group.
    expect(within(menu).getByRole('menuitemcheckbox', { name: /Color Painting/ })).toBeInTheDocument();
    expect(within(menu).getByRole('menuitemcheckbox', { name: 'Support Painting' })).toBeInTheDocument();
    expect(within(menu).getByRole('menuitemcheckbox', { name: 'Seam Painting' })).toBeInTheDocument();
    expect(within(menu).getByRole('menuitemcheckbox', { name: 'Fuzzy Skin Painting' })).toBeInTheDocument();
    expect(within(menu).getByRole('menuitemcheckbox', { name: 'Text Tool' })).toBeInTheDocument();
    expect(within(menu).getByRole('menuitemcheckbox', { name: /Measure/ })).toBeInTheDocument();
    expect(within(menu).getByRole('menuitemcheckbox', { name: 'Assembly View' })).toBeInTheDocument();
    expect(within(menu).getByRole('menuitemcheckbox', { name: /Sequential Printing/ })).toBeInTheDocument();
  });

  it('reflects an active tool on the "More tools" trigger itself', () => {
    mockCompactViewport(true);
    const { rerender } = render(<SlicerToolbar onAddModel={noop()} onMove={noop()} />);

    const trigger = screen.getByRole('button', { name: 'More tools' });
    expect(trigger.className).not.toMatch(/bg-pf-accent\/20/);

    rerender(<SlicerToolbar onAddModel={noop()} onMove={noop()} moveActive />);
    expect(trigger.className).toMatch(/bg-pf-accent\/20/);
  });
});

import { render, screen } from '@testing-library/react';
import { describe, it, expect, vi } from 'vitest';
import { SlicerStatusBar } from '../SlicerStatusBar';

// Regression coverage for issue #1974: at a 390px mobile viewport, the status
// bar's left (object count/bed size) group and right (failed-plate note/slice
// button) group didn't fit on one line. Without `flex-wrap`, flexbox squeezed
// both groups instead of wrapping, and the unprotected `sliceNote` span
// collapsed into a narrow, barely-readable vertical column right next to the
// disabled Slice button instead of wrapping normally across the row.
//
// jsdom does not compute real flex/box layout, so these assertions pin the
// Tailwind classes that make the row wrap onto additional lines and keep the
// slice button from being squeezed, matching the pattern already used for the
// toolbar's own narrow-width regression test (SlicerToolbar.responsive.test.tsx,
// issue #1902).
describe('SlicerStatusBar narrow-width layout (issue #1974)', () => {
  it('wraps the status bar row instead of squeezing both groups onto one line', () => {
    const { container } = render(<SlicerStatusBar objectCount={1} bedWidth={256} bedDepth={256} bedHeight={256} />);

    const row = container.firstElementChild;
    expect(row).toHaveClass('flex-wrap');
  });

  it('keeps the object-count/bed-size group from word-wrapping unreadably', () => {
    render(<SlicerStatusBar objectCount={3} bedWidth={256} bedDepth={256} bedHeight={256} />);

    const objectCount = screen.getByText('3 objects');
    const leftGroup = objectCount.closest('div');
    expect(leftGroup).toHaveClass('whitespace-nowrap');
  });

  it('lets the failed-plate slice note wrap onto its own full-width row instead of collapsing into a narrow column next to the button', () => {
    render(
      <SlicerStatusBar
        objectCount={2}
        bedWidth={256}
        bedDepth={256}
        bedHeight={256}
        canSlice={false}
        sliceNote="A model on this plate failed to load. Retry or remove them before slicing."
      />,
    );

    const note = screen.getByTestId('slice-note');
    // `w-full` (with a `sm:w-auto` override for larger viewports) forces the
    // note onto its own row on narrow screens, giving it the full row width to
    // wrap across instead of being squeezed into a sliver next to the button.
    expect(note).toHaveClass('w-full');

    const rightGroup = note.closest('div');
    expect(rightGroup).toHaveClass('flex-wrap');
    expect(rightGroup).toHaveClass('min-w-0');
  });

  it('keeps the disabled slice button from shrinking/overlapping when the failed-plate note is shown', () => {
    render(
      <SlicerStatusBar
        objectCount={2}
        bedWidth={256}
        bedDepth={256}
        bedHeight={256}
        canSlice={false}
        sliceNote="A model on this plate failed to load. Retry or remove them before slicing."
      />,
    );

    const sliceButton = screen.getByRole('button', { name: 'Slice' });
    expect(sliceButton).toBeDisabled();
    expect(sliceButton).toHaveClass('shrink-0');
  });

  it('renders the failed-plate note and disabled slice button together without overlap (desktop-unaffected content contract)', () => {
    const onSlice = vi.fn();
    render(
      <SlicerStatusBar
        objectCount={1}
        bedWidth={256}
        bedDepth={256}
        bedHeight={256}
        canSlice={false}
        onSlice={onSlice}
        sliceNote="A model on this plate failed to load. Retry or remove them before slicing."
      />,
    );

    expect(screen.getByTestId('slice-note')).toHaveTextContent(
      'A model on this plate failed to load. Retry or remove them before slicing.',
    );
    expect(screen.getByRole('button', { name: 'Slice' })).toBeDisabled();
  });
});

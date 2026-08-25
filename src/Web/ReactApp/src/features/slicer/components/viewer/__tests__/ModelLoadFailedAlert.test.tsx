import { render, screen } from '@testing-library/react';
import { describe, it, expect } from 'vitest';
import { ModelLoadFailedAlert } from '../ModelViewerErrorBoundary';

/**
 * Regression coverage for issue #1974: at a 390px mobile viewport, the
 * per-model "Failed to load this 3D model..." alert — rendered by
 * `SlicerBedVisualization` inside drei's `<Html center>` overlay — collapsed
 * into a narrow, few-characters-per-line column over the model view.
 *
 * Root cause: drei's `<Html>` wraps its content in nested
 * `position: absolute` elements with no explicit `left`/`right`/`width`. Those
 * elements fall back to CSS shrink-to-fit sizing for their `auto` width,
 * which for a transformed/centered absolute box can resolve to a near-zero
 * available width — a `max-w-*` class alone only caps the width, it doesn't
 * establish one, so the box still shrank to the sliver and wrapped text one
 * word (or character) per line. Confirmed with a real Chromium render at
 * 390x844 against the exact nested-absolute markup drei produces (see the
 * PR description for the before/after screenshots); jsdom does not compute
 * real box layout, so this test pins the alert's classes instead of pixel
 * measurements — an explicit `w-64` (paired with `max-w-[80vw]` so it never
 * exceeds an even narrower viewport) is what actually fixes the collapse.
 */
describe('ModelLoadFailedAlert (issue #1974)', () => {
  it('renders the expected failure copy as a readable alert', () => {
    render(<ModelLoadFailedAlert />);

    const alert = screen.getByRole('alert');
    expect(alert).toHaveTextContent(
      'Failed to load this 3D model. Select another model or retry with a refreshed source.',
    );
  });

  it('gives the alert an explicit width so it cannot shrink-to-fit toward zero inside nested absolute-positioned wrappers', () => {
    render(<ModelLoadFailedAlert />);

    const alert = screen.getByRole('alert');
    // An explicit `w-*` fixes a real pixel width regardless of the
    // surrounding wrappers' shrink-to-fit result. `max-w-*` alone (the
    // pre-fix markup used `max-w-xs` with no `w-*`) only caps an
    // otherwise-auto width and does not prevent the collapse.
    expect(alert).toHaveClass('w-64');
    expect(alert).toHaveClass('max-w-[80vw]');
  });
});

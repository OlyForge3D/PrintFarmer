/**
 * Publishes how much horizontal room the desktop floating control bar occupies
 * in the top-right corner, as a CSS variable page chrome can reserve against.
 *
 * ## Why this exists
 *
 * `FloatingControlBar` is `position: fixed` in the top-right on `lg` and up. It
 * overlaps whatever the page renders at the top of its content column — in
 * practice, the page header's action buttons. The previous fix was a hardcoded
 * `lg:mr-72` on every page header: 288px reserved unconditionally, chosen to
 * clear the widest the bar could ever get.
 *
 * A magic number is wrong in both directions. It over-reserves for the common
 * case (truncating page titles that had room to spare) and silently breaks the
 * moment the bar grows — a longer status pill, another icon button, a
 * translated label. Measuring costs one `ResizeObserver` and can never drift.
 */

const CSS_VARIABLE = '--pf-floating-bar-inset';

/** Matches the bar's own `right-4`, so the reservation starts at the viewport edge. */
const RIGHT_OFFSET_PX = 16;

/** Breathing room between the bar and whatever sits to its left. */
const GUTTER_PX = 16;

function setInset(pixels: number): void {
  if (typeof document === 'undefined') {
    return;
  }
  document.documentElement.style.setProperty(CSS_VARIABLE, `${Math.round(pixels)}px`);
}

export function clearFloatingBarInset(): void {
  if (typeof document === 'undefined') {
    return;
  }
  document.documentElement.style.removeProperty(CSS_VARIABLE);
}

/**
 * Observes `element` and keeps the CSS variable in sync with its width.
 * Returns a cleanup function that disconnects the observer and clears the
 * variable, so pages stop reserving space the instant the bar unmounts.
 */
export function observeFloatingBarWidth(element: HTMLElement): () => void {
  const publish = () => {
    setInset(element.getBoundingClientRect().width + RIGHT_OFFSET_PX + GUTTER_PX);
  };

  publish();

  if (typeof ResizeObserver === 'undefined') {
    // jsdom and older browsers: the initial measurement still applies, it just
    // will not track later changes. Better than reserving nothing.
    return clearFloatingBarInset;
  }

  const observer = new ResizeObserver(publish);
  observer.observe(element);

  return () => {
    observer.disconnect();
    clearFloatingBarInset();
  };
}

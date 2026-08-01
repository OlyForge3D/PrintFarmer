import { describe, expect, it } from 'vitest';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';

/**
 * `controls.css` styles bare `<button>` elements so that a button written
 * without any styling still looks like a button. That is reasonable. What is
 * not reasonable is applying it to a button that already carries classes.
 *
 * `background` and `box-shadow` there are shorthand declarations on an element
 * selector. A Tailwind `bg-*` utility only sets `background-color`, so it can
 * never displace the gradient, and no utility clears the glow at all. The
 * result was that every author-styled raw button in the app was silently
 * repainted as a primary action: 35 of them across 9 routes, none of them an
 * action. The settings sidebar (`rounded-md border`), the settings sub-tabs
 * (`rounded-none`) and the mobile category selector (`bg-pf-bg-1`) each
 * declared their own surface and were overridden into glowing 8px pills.
 *
 * The cascade for a plain element selector in a stylesheet is not observable
 * from jsdom — it never loads the CSS — so this asserts the guard at the
 * source. It is narrow on purpose: it only checks that the decorative
 * declarations stay behind `:not([class])`.
 */
describe('controls.css — the bare-button skin stays off styled buttons', () => {
  const css = readFileSync(resolve(__dirname, '../../styles/controls.css'), 'utf8');

  const skinSelectors = [
    'button:not([data-pf-button]):not([class])',
    'button:not([data-pf-button]):not([class]):hover:not(:disabled)',
    'button:not([data-pf-button]):not([class]):active:not(:disabled)',
  ];

  it.each(skinSelectors)('%s is guarded by :not([class])', (selector) => {
    expect(css).toContain(selector);
  });

  it('never paints a surface onto a raw button without the guard', () => {
    const rules = [
      ...css.matchAll(/button:not\(\[data-pf-button\]\)(?!:not\(\[class\]\))([^{]*)\{([^}]*)\}/g),
    ];

    const offenders = rules
      .filter(([, , body]) =>
        /(^|\s|;)(background|box-shadow|border-radius|border|padding|font-size|transform)\s*:/.test(
          body,
        ),
      )
      .map((match) => `${'button:not([data-pf-button])'}${match[1].trim()}`);

    // `:focus-visible` is allowed to set box-shadow — that is the focus ring,
    // and it must reach every raw button, styled or not.
    const decorative = offenders.filter((selector) => !selector.includes(':focus-visible'));

    expect(decorative).toEqual([]);
  });

  it('keeps ergonomics and focus affordances on every raw button', () => {
    // Not decoration. Removing these would cost a11y and pointer feedback on
    // exactly the buttons the guard just un-skinned.
    expect(css).toMatch(/button:not\(\[data-pf-button\]\)\s*\{\s*cursor:\s*pointer;\s*\}/);
    expect(css).toContain('button:not([data-pf-button]):focus-visible');
    expect(css).toContain('button:not([data-pf-button]):disabled');
  });
});

import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { AdminSection } from '../AdminSection';

describe('AdminSection', () => {
  it('labels the section with its caption', () => {
    render(
      <AdminSection caption="System health" captionId="health">
        <p>body</p>
      </AdminSection>,
    );

    const section = screen.getByRole('region', { name: 'System health' });
    expect(section).toBeInTheDocument();
    expect(section.tagName).toBe('SECTION');
  });

  it('renders an h2 by default and an h3 on request', () => {
    const { rerender } = render(
      <AdminSection caption="Band" captionId="b">
        <p>body</p>
      </AdminSection>,
    );
    expect(screen.getByRole('heading', { level: 2, name: 'Band' })).toBeInTheDocument();

    rerender(
      <AdminSection caption="Band" captionId="b" headingLevel={3}>
        <p>body</p>
      </AdminSection>,
    );
    expect(screen.getByRole('heading', { level: 3, name: 'Band' })).toBeInTheDocument();
  });

  it('omits the count badge when the count is absent or zero', () => {
    const { rerender } = render(
      <AdminSection caption="Needs attention" captionId="a">
        <p>body</p>
      </AdminSection>,
    );
    expect(screen.queryByText('0')).not.toBeInTheDocument();

    rerender(
      <AdminSection caption="Needs attention" captionId="a" count={0}>
        <p>body</p>
      </AdminSection>,
    );
    expect(screen.queryByText('0')).not.toBeInTheDocument();

    rerender(
      <AdminSection caption="Needs attention" captionId="a" count={3}>
        <p>body</p>
      </AdminSection>,
    );
    expect(screen.getByText('3')).toBeInTheDocument();
  });

  it('renders the header aside beside the caption', () => {
    render(
      <AdminSection caption="System health" captionId="h" headerAside={<span>Checked at 10:04</span>}>
        <p>body</p>
      </AdminSection>,
    );
    expect(screen.getByText('Checked at 10:04')).toBeInTheDocument();
  });

  it('sets the caption as a quiet eyebrow, not a heading that competes with the page title', () => {
    // A band caption classifies a region; it is not the loudest thing on the
    // page. An earlier revision set it at `text-lg` display-bold on the theory
    // that a parent must out-shout its children — a rule the design never
    // stated and which made a six-band settings page read as six page titles.
    // Asserting classes rather than computed sizes because jsdom does not
    // apply Tailwind.
    render(
      <AdminSection caption="Everything you can manage" captionId="d">
        <h3 className="text-[15px]">Printers &amp; Hardware</h3>
      </AdminSection>,
    );
    const caption = screen.getByRole('heading', { level: 2, name: 'Everything you can manage' });
    expect(caption.className).toContain('text-xs');
    expect(caption.className).toContain('uppercase');
    expect(caption.className).toContain('tracking-[0.06em]');
    expect(caption.className).toContain('text-pf-text-secondary');
    expect(caption.className).not.toContain('text-lg');
  });

  it('renders identically at either heading level', () => {
    // index.css force-uppercases h1/h2 in the display face but leaves h3 alone.
    // A band that inherits that rule changes character between pages, so the
    // component must state its own face and case at both levels. `font-pf-sans`
    // is load-bearing, and must be the *theme* sans token rather than Tailwind's
    // built-in `font-sans`: the latter resolves to the generic ui-sans-serif
    // stack, so an `<h2>` band would escape Bebas only to land on the system UI
    // font instead of the app's body face. Caught by measuring the live page.
    const captionClass = (level: 2 | 3) => {
      const { unmount } = render(
        <AdminSection caption="Band" captionId="x" headingLevel={level}>
          <p>body</p>
        </AdminSection>,
      );
      const cls = screen.getByRole('heading', { level, name: 'Band' }).className;
      unmount();
      return cls;
    };

    const asH2 = captionClass(2);
    const asH3 = captionClass(3);
    expect(asH3).toBe(asH2);
    for (const token of ['font-pf-sans', 'uppercase', 'font-semibold']) {
      expect(asH2).toContain(token);
    }
  });
});

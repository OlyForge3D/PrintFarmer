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

  it('keeps the caption louder than a nested group label', () => {
    // The inversion this component exists to fix: a band caption must not be
    // set smaller than the headings nested inside it. Asserting the class
    // rather than a computed size because jsdom does not apply Tailwind.
    render(
      <AdminSection caption="Everything you can manage" captionId="d">
        <h3 className="text-sm">Printers &amp; Hardware</h3>
      </AdminSection>,
    );
    const caption = screen.getByRole('heading', { level: 2, name: 'Everything you can manage' });
    expect(caption.className).toContain('text-lg');
    expect(caption.className).not.toContain('text-sm');
  });

  it('renders identically at either heading level', () => {
    // index.css force-uppercases h1/h2 in the display face but leaves h3 alone.
    // A band that inherits that rule changes character between pages, so the
    // component must state its own face and case at both levels.
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
    for (const token of ['font-pf-display', 'uppercase', 'font-bold']) {
      expect(asH2).toContain(token);
    }
  });
});

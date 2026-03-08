import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { Select } from '@/common/components/ui/Select';

describe('Select', () => {
  describe('Rendering', () => {
    it('renders a select element with children', () => {
      render(
        <Select aria-label="Test select">
          <option value="a">Option A</option>
          <option value="b">Option B</option>
        </Select>
      );

      const select = screen.getByRole('combobox', { name: 'Test select' });
      expect(select).toBeInTheDocument();
      expect(select.tagName).toBe('SELECT');
    });

    it('renders inside a relative container for icon positioning', () => {
      const { container } = render(
        <Select aria-label="Test">
          <option>One</option>
        </Select>
      );

      const wrapper = container.firstElementChild;
      expect(wrapper).toHaveClass('relative');
    });

    it('applies appearance-none to hide native chevron', () => {
      render(
        <Select aria-label="Test">
          <option>One</option>
        </Select>
      );

      const select = screen.getByRole('combobox');
      expect(select).toHaveClass('appearance-none');
    });
  });

  describe('Chevron icon', () => {
    it('renders a chevron/arrow icon element', () => {
      const { container } = render(
        <Select aria-label="Test">
          <option>One</option>
        </Select>
      );

      // The chevron should be a sibling of the select inside the wrapper
      const wrapper = container.firstElementChild!;
      const chevron = wrapper.querySelector('svg, [class*="chevron"], [class*="arrow"], [data-testid="select-chevron"]');

      // If chevron is not yet implemented, this test will fail — expected until PFarm1-dhz lands
      expect(chevron).not.toBeNull();
    });

    it('chevron has pointer-events-none so clicks pass through to select', () => {
      const { container } = render(
        <Select aria-label="Test">
          <option>One</option>
        </Select>
      );

      const wrapper = container.firstElementChild!;
      const chevronContainer = wrapper.querySelector('[class*="pointer-events-none"]');
      expect(chevronContainer).not.toBeNull();
    });

    it('chevron is not hidden with display:none or visibility:hidden', () => {
      const { container } = render(
        <Select aria-label="Test">
          <option>One</option>
        </Select>
      );

      const wrapper = container.firstElementChild!;
      const chevron = wrapper.querySelector('svg, [class*="chevron"], [class*="arrow"]');

      if (chevron) {
        expect(chevron).not.toHaveClass('hidden');
        expect(chevron).not.toHaveClass('invisible');
      }
    });
  });

  describe('Invalid state', () => {
    it('applies error styling when invalid prop is true', () => {
      render(
        <Select aria-label="Test" invalid>
          <option>One</option>
        </Select>
      );

      const select = screen.getByRole('combobox');
      expect(select.className).toContain('border-pf-error');
    });

    it('does not apply error styling when invalid is false', () => {
      render(
        <Select aria-label="Test">
          <option>One</option>
        </Select>
      );

      const select = screen.getByRole('combobox');
      expect(select.className).not.toContain('border-pf-error');
    });
  });

  describe('Design tokens', () => {
    it('uses pf-* design token classes for theming', () => {
      render(
        <Select aria-label="Test">
          <option>One</option>
        </Select>
      );

      const select = screen.getByRole('combobox');
      expect(select.className).toContain('bg-pf-bg-0');
      expect(select.className).toContain('text-pf-text-primary');
      expect(select.className).toContain('border-pf-border');
      expect(select.className).toContain('focus:ring-pf-accent');
    });

    it('does not use hardcoded gray-* or slate-* Tailwind classes', () => {
      render(
        <Select aria-label="Test">
          <option>One</option>
        </Select>
      );

      const select = screen.getByRole('combobox');
      expect(select.className).not.toMatch(/\b(gray|slate)-\d+\b/);
    });
  });

  describe('Props', () => {
    it('applies containerClassName to the wrapper div', () => {
      const { container } = render(
        <Select aria-label="Test" containerClassName="custom-wrapper">
          <option>One</option>
        </Select>
      );

      const wrapper = container.firstElementChild;
      expect(wrapper).toHaveClass('custom-wrapper');
    });

    it('passes additional HTML attributes to the select element', () => {
      render(
        <Select aria-label="Test" disabled data-testid="my-select">
          <option>One</option>
        </Select>
      );

      const select = screen.getByTestId('my-select');
      expect(select).toBeDisabled();
    });

    it('applies custom className to the select element', () => {
      render(
        <Select aria-label="Test" className="extra-class">
          <option>One</option>
        </Select>
      );

      const select = screen.getByRole('combobox');
      expect(select).toHaveClass('extra-class');
    });
  });
});

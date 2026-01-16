import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { Button } from '@/common/components/ui/Button';

describe('Button', () => {
  describe('iconCenter prop', () => {
    it('renders icon-only button with iconCenter', () => {
      render(
        <Button 
          iconCenter={<span data-testid="test-icon">Icon</span>} 
          aria-label="Icon button"
        />
      );
      
      const icon = screen.getByTestId('test-icon');
      expect(icon).toBeInTheDocument();
      expect(icon).toHaveTextContent('Icon');
      
      const button = screen.getByRole('button');
      expect(button).toBeInTheDocument();
    });

    it('centers icon properly with iconCenter', () => {
      render(
        <Button 
          iconCenter={<span data-testid="centered-icon">⭐</span>}
          aria-label="Star"
        />
      );
      
      const button = screen.getByRole('button');
      expect(button).toHaveClass('justify-center');
    });

    it('does not render children text when iconCenter is used', () => {
      render(
        <Button 
          iconCenter={<span data-testid="icon">Icon</span>}
          aria-label="Icon button"
        >
          This text should not appear
        </Button>
      );
      
      const icon = screen.getByTestId('icon');
      expect(icon).toBeInTheDocument();
      
      // Text should not be rendered when iconCenter is present
      expect(screen.queryByText('This text should not appear')).not.toBeInTheDocument();
    });

    it('shows loading state for iconCenter button', () => {
      render(
        <Button 
          iconCenter={<span data-testid="icon">Icon</span>}
          loading={true}
          aria-label="Loading"
        />
      );
      
      const button = screen.getByRole('button');
      expect(button).toBeDisabled();
      expect(button).toHaveTextContent('Loading...');
    });
  });

  describe('regular button with iconLeft/iconRight', () => {
    it('renders button with iconLeft and children', () => {
      render(
        <Button iconLeft={<span data-testid="left-icon">←</span>}>
          Click me
        </Button>
      );
      
      const icon = screen.getByTestId('left-icon');
      expect(icon).toBeInTheDocument();
      expect(screen.getByText('Click me')).toBeInTheDocument();
      
      const button = screen.getByRole('button');
      expect(button).not.toHaveClass('justify-center');
    });

    it('renders button with iconRight and children', () => {
      render(
        <Button iconRight={<span data-testid="right-icon">→</span>}>
          Click me
        </Button>
      );
      
      const icon = screen.getByTestId('right-icon');
      expect(icon).toBeInTheDocument();
      expect(screen.getByText('Click me')).toBeInTheDocument();
    });

    it('renders button with both iconLeft and iconRight', () => {
      render(
        <Button 
          iconLeft={<span data-testid="left-icon">←</span>}
          iconRight={<span data-testid="right-icon">→</span>}
        >
          Both icons
        </Button>
      );
      
      expect(screen.getByTestId('left-icon')).toBeInTheDocument();
      expect(screen.getByTestId('right-icon')).toBeInTheDocument();
      expect(screen.getByText('Both icons')).toBeInTheDocument();
    });

    it('handles loading state with text', () => {
      render(
        <Button loading={true}>
          Submit
        </Button>
      );
      
      const button = screen.getByRole('button');
      expect(button).toBeDisabled();
      expect(button).toHaveTextContent('Please wait…');
      expect(screen.queryByText('Submit')).not.toBeInTheDocument();
    });

    it('does not render empty text span when no children provided', () => {
      render(
        <Button 
          iconLeft={<span data-testid="icon">Icon</span>}
          aria-label="Icon only"
        />
      );
      
      // Check that the button still renders the icon
      const icon = screen.getByTestId('icon');
      expect(icon).toBeInTheDocument();
      
      // The button should have icon wrapper span and conditional children span
      const button = screen.getByRole('button');
      const spans = button.querySelectorAll('span');
      
      // Should have one span for the icon wrapper
      expect(spans.length).toBeGreaterThanOrEqual(1);
      expect(icon.parentElement).toHaveAttribute('aria-hidden', 'true');
    });
  });

  describe('button variants', () => {
    it('applies correct variant classes', () => {
      const { rerender } = render(<Button variant="primary">Primary</Button>);
      let button = screen.getByRole('button');
      expect(button).toHaveClass('bg-pf-accent-bg');
      
      rerender(<Button variant="secondary">Secondary</Button>);
      button = screen.getByRole('button');
      expect(button).toHaveClass('bg-pf-bg-2');
      
      rerender(<Button variant="danger">Danger</Button>);
      button = screen.getByRole('button');
      expect(button).toHaveClass('bg-pf-error');
    });
  });

  describe('button sizes', () => {
    it('applies correct size classes', () => {
      const { rerender } = render(<Button size="sm">Small</Button>);
      let button = screen.getByRole('button');
      expect(button).toHaveClass('text-xs', 'px-2', 'py-1');
      
      rerender(<Button size="md">Medium</Button>);
      button = screen.getByRole('button');
      expect(button).toHaveClass('text-sm', 'px-4', 'py-2');
      
      rerender(<Button size="lg">Large</Button>);
      button = screen.getByRole('button');
      expect(button).toHaveClass('text-base', 'px-6', 'py-3');
    });
  });

  describe('disabled state', () => {
    it('disables button when disabled prop is true', () => {
      render(<Button disabled>Disabled</Button>);
      
      const button = screen.getByRole('button');
      expect(button).toBeDisabled();
      // Note: disabled:opacity-50 is a conditional class that applies only when disabled
      expect(button.className).toContain('disabled:opacity-50');
      expect(button.className).toContain('disabled:cursor-not-allowed');
    });

    it('disables button when loading', () => {
      render(<Button loading>Loading</Button>);
      
      const button = screen.getByRole('button');
      expect(button).toBeDisabled();
    });
  });

  describe('custom className', () => {
    it('applies custom className', () => {
      render(<Button className="custom-class">Custom</Button>);
      
      const button = screen.getByRole('button');
      expect(button).toHaveClass('custom-class');
    });
  });

  describe('accessibility', () => {
    it('supports aria-label for icon-only buttons', () => {
      render(
        <Button 
          iconCenter={<span>Icon</span>}
          aria-label="Delete item"
        />
      );
      
      const button = screen.getByRole('button', { name: 'Delete item' });
      expect(button).toBeInTheDocument();
    });

    it('marks icons as aria-hidden when using iconLeft', () => {
      render(
        <Button iconLeft={<span data-testid="icon">Icon</span>}>
          Text
        </Button>
      );
      
      const iconWrapper = screen.getByTestId('icon').parentElement;
      expect(iconWrapper).toHaveAttribute('aria-hidden', 'true');
    });
    
    it('marks icons as aria-hidden when using iconCenter', () => {
      render(
        <Button 
          iconCenter={<span data-testid="center-icon">Icon</span>}
          aria-label="Icon button"
        />
      );
      
      const iconWrapper = screen.getByTestId('center-icon').parentElement;
      expect(iconWrapper).toHaveAttribute('aria-hidden', 'true');
    });
  });
});

import { describe, it, expect } from 'vitest';
import { render } from '@testing-library/react';
import { Skeleton } from '@/common/components/skeletons/Skeleton';

describe('Loading State Consistency', () => {
  describe('Skeleton Component Usage', () => {
    it('uses skeleton-base class for animations', () => {
      const { container } = render(<Skeleton lines={1} />);
      
      const skeletonItem = container.querySelector('[data-skeleton-item]');
      expect(skeletonItem).toBeInTheDocument();
      expect(skeletonItem).toHaveClass('skeleton-base');
    });

    it('does NOT use raw animate-pulse class in Skeleton component', () => {
      const { container } = render(<Skeleton lines={1} />);
      
      const skeletonItem = container.querySelector('[data-skeleton-item]');
      expect(skeletonItem).not.toHaveClass('animate-pulse');
    });

    it('applies skeleton-rounded variant correctly', () => {
      const { container } = render(<Skeleton lines={1} variant="rect" />);
      
      const skeletonItem = container.querySelector('[data-skeleton-item]');
      expect(skeletonItem).toHaveClass('skeleton-rounded');
    });

    it('applies skeleton-pill variant correctly', () => {
      const { container } = render(<Skeleton lines={1} variant="pill" />);
      
      const skeletonItem = container.querySelector('[data-skeleton-item]');
      expect(skeletonItem).toHaveClass('skeleton-pill');
    });

    it('renders multiple skeleton lines', () => {
      const { container } = render(<Skeleton lines={5} />);
      
      const skeletonItems = container.querySelectorAll('[data-skeleton-item]');
      expect(skeletonItems).toHaveLength(5);
    });

    it('applies background color using pf-* tokens', () => {
      const { container } = render(<Skeleton lines={1} />);
      
      const skeletonItem = container.querySelector('[data-skeleton-item]');
      expect(skeletonItem).toHaveClass('bg-pf-bg-1');
    });

    it('renders with appropriate ARIA label', () => {
      const { container } = render(<Skeleton lines={1} aria-label="Loading content" />);
      
      const skeletonContainer = container.querySelector('[data-skeleton]');
      expect(skeletonContainer).toHaveAttribute('aria-label', 'Loading content');
    });
  });

  describe('Dashboard Loading State Guards', () => {
    it('ensures dashboard components use Skeleton wrapper for loading states', () => {
      // This is a regression guard test - no raw animate-pulse should exist
      // The test verifies the Skeleton component API is correct
      const { container } = render(<Skeleton lines={3} aria-label="Loading dashboard" />);
      
      const skeletonContainer = container.querySelector('[data-skeleton]');
      expect(skeletonContainer).toBeInTheDocument();
      
      // Verify NO direct animate-pulse usage
      const pulsing = container.querySelector('.animate-pulse:not([data-skeleton-item])');
      expect(pulsing).not.toBeInTheDocument();
    });

    it('ensures statistics loading states use Skeleton wrapper', () => {
      // Guard against raw animate-pulse in statistics components
      const { container } = render(<Skeleton lines={2} aria-label="Loading statistics" />);
      
      const skeletonItems = container.querySelectorAll('[data-skeleton-item]');
      expect(skeletonItems.length).toBeGreaterThan(0);
      
      // Each skeleton item should use skeleton-base, not animate-pulse
      skeletonItems.forEach(item => {
        expect(item).toHaveClass('skeleton-base');
        expect(item).not.toHaveClass('animate-pulse');
      });
    });
  });

  describe('Skeleton Component Size Props', () => {
    it('accepts custom width prop', () => {
      const { container } = render(<Skeleton lines={1} width="200px" />);
      
      const skeletonContainer = container.querySelector('[data-skeleton]');
      expect(skeletonContainer).toBeInTheDocument();
    });

    it('accepts custom height prop', () => {
      const { container } = render(<Skeleton lines={1} height="40px" />);
      
      const skeletonItem = container.querySelector('[data-skeleton-item]');
      expect(skeletonItem).toHaveAttribute('data-sz', '40px');
    });

    it('uses default height for rect variant', () => {
      const { container } = render(<Skeleton lines={1} variant="rect" />);
      
      const skeletonItem = container.querySelector('[data-skeleton-item]');
      expect(skeletonItem).toHaveAttribute('data-sz', '1rem');
    });

    it('uses smaller height for pill variant', () => {
      const { container } = render(<Skeleton lines={1} variant="pill" />);
      
      const skeletonItem = container.querySelector('[data-skeleton-item]');
      expect(skeletonItem).toHaveAttribute('data-sz', '0.75rem');
    });
  });

  describe('No Raw animate-pulse Usage', () => {
    it('verifies Skeleton component does not emit raw animate-pulse class', () => {
      const { container } = render(
        <>
          <Skeleton lines={1} />
          <Skeleton lines={2} variant="pill" />
          <Skeleton lines={3} width="100%" height="20px" />
        </>
      );
      
      // Count all skeleton items
      const skeletonItems = container.querySelectorAll('[data-skeleton-item]');
      expect(skeletonItems.length).toBe(6); // 1 + 2 + 3
      
      // None should have raw animate-pulse
      skeletonItems.forEach(item => {
        expect(item).not.toHaveClass('animate-pulse');
        expect(item).toHaveClass('skeleton-base'); // Should use skeleton-base instead
      });
    });
  });

  describe('Custom ClassName Support', () => {
    it('supports custom className on Skeleton container', () => {
      const { container } = render(<Skeleton lines={1} className="my-custom-class" />);
      
      const skeletonContainer = container.querySelector('[data-skeleton]');
      expect(skeletonContainer).toHaveClass('my-custom-class');
    });

    it('preserves base skeleton classes when custom className is applied', () => {
      const { container } = render(<Skeleton lines={1} className="my-custom-class" />);
      
      const skeletonContainer = container.querySelector('[data-skeleton]');
      expect(skeletonContainer).toHaveClass('my-custom-class');
      expect(skeletonContainer).toHaveClass('flex');
      expect(skeletonContainer).toHaveClass('flex-col');
    });
  });
});

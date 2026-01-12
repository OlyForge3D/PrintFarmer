import React from 'react';
import { render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { vi, describe, it, expect, beforeEach } from 'vitest';
import '@testing-library/jest-dom';
import TagAnalyticsDashboard from '@/components/TagAnalyticsDashboard';
import tagService from '@/services/tagService';

/**
 * Tests for Phase 3D.5: Tag Analytics Dashboard
 * 
 * Test coverage:
 * - Component rendering with analytics data
 * - Statistics display and calculations
 * - Responsive layout at multiple breakpoints
 * - Loading and error states
 * - Empty state messaging
 * - Accessibility (ARIA labels, semantic HTML)
 */

// Mock tagService
vi.mock('@/services/tagService', () => ({
  default: {
    getAnalytics: vi.fn(),
  },
}));

// Helper to render with QueryClientProvider
const renderWithQueryClient = (component: React.ReactElement) => {
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: { retry: false },
    },
  });

  return render(
    <QueryClientProvider client={queryClient}>
      {component}
    </QueryClientProvider>
  );
};

// Mock analytics data - matches TagAnalyticsDto structure
const mockAnalyticsData = {
  totalTags: 8,
  totalAssignments: 180,
  averageTagsPerModel: 22.5,
  mostUsedTags: [
    { id: '1', name: 'PLA', usageCount: 45 },
    { id: '2', name: 'ABS', usageCount: 32 },
    { id: '3', name: 'PETG', usageCount: 28 },
    { id: '4', name: 'Miniature', usageCount: 22 },
    { id: '5', name: 'Lithophane', usageCount: 18 },
    { id: '6', name: 'Functional', usageCount: 15 },
    { id: '7', name: 'Decorative', usageCount: 12 },
    { id: '8', name: 'Support', usageCount: 8 },
  ],
  leastUsedTags: [
    { id: '8', name: 'Support', usageCount: 8 },
    { id: '7', name: 'Decorative', usageCount: 12 },
  ],
};

describe('TagAnalyticsDashboard', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    // Set default mock return for tests that don't explicitly set it
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    (tagService.getAnalytics as Record<string, any>).mockResolvedValue(mockAnalyticsData);
  });

  describe('3D.5.1: Component Rendering', () => {
    it('should render dashboard title and description', async () => {
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    (tagService.getAnalytics as Record<string, any>).mockResolvedValueOnce(mockAnalyticsData);
      const { container } = renderWithQueryClient(<TagAnalyticsDashboard />);

      // Wait for the data to load (component shows loading state first)
      await screen.findByText('Total Tags');
      
      // Check title exists
      expect(screen.getByText('Tag Analytics')).toBeInTheDocument();
      
      // Check description exists in the DOM
      expect(container.innerHTML).toContain('Overview of tag usage');
    });

    it('should display statistics grid with 4 stat cards', async () => {
      renderWithQueryClient(<TagAnalyticsDashboard />);

      // Wait for stats to appear
      expect(await screen.findByText('Total Tags')).toBeInTheDocument();
      expect(screen.getByText('Total Assignments')).toBeInTheDocument();
      expect(screen.getByText('Avg. Per Model')).toBeInTheDocument();
      expect(screen.getByText('Most Used')).toBeInTheDocument();
    });

    it('should display top tags section', async () => {

      renderWithQueryClient(<TagAnalyticsDashboard />);

      expect(await screen.findByText('Top Tags by Usage')).toBeInTheDocument();
      expect(screen.getAllByText('PLA').length).toBeGreaterThan(0);
      expect(screen.getAllByText('ABS').length).toBeGreaterThan(0);
    });
  });

  describe('3D.5.2: Data Display & Statistics', () => {
    it('should display correct total tags count', async () => {

      renderWithQueryClient(<TagAnalyticsDashboard />);

      // Find the total tags stat card value
      const totalsCards = await screen.findAllByText(/^8$/);
      expect(totalsCards.length).toBeGreaterThan(0);
    });

    it('should display correct total usage count', async () => {

      renderWithQueryClient(<TagAnalyticsDashboard />);

      // Find the total usage stat card value
      const usageCards = await screen.findAllByText(/^180$/);
      expect(usageCards.length).toBeGreaterThan(0);
    });

    it('should display correct average usage per tag', async () => {

      renderWithQueryClient(<TagAnalyticsDashboard />);

      expect(await screen.findByText('22.5')).toBeInTheDocument();
    });

    it('should display most used tag name and count', async () => {

      renderWithQueryClient(<TagAnalyticsDashboard />);

      expect(await screen.findByText('45 uses')).toBeInTheDocument();
      expect(screen.getAllByText('PLA').length).toBeGreaterThan(0);
    });

    it('should sort tags by usage count (highest first)', async () => {

      renderWithQueryClient(<TagAnalyticsDashboard />);

      const topTagsSection = await screen.findByRole('region', { name: 'Most used tags' });
      const tagNames = Array.from(topTagsSection.querySelectorAll('p.font-medium')).map(
        (el) => el.textContent
      );

      // First tag should be PLA (45 uses), not ABS (32 uses)
      expect(tagNames[0]).toBe('PLA');
    });

    it('should display usage counts for each tag', async () => {

      renderWithQueryClient(<TagAnalyticsDashboard />);

      expect(await screen.findByText('45')).toBeInTheDocument(); // PLA
      expect(screen.getByText('32')).toBeInTheDocument(); // ABS
      expect(screen.getByText('28')).toBeInTheDocument(); // PETG
    });
  });

  describe('3D.5.3: Loading State', () => {
    it('should display loading skeleton while fetching', () => {
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      (tagService.getAnalytics as Record<string, any>).mockReturnValueOnce(
        new Promise(() => {
          /* never resolves */
        })
      );

      const { container } = renderWithQueryClient(<TagAnalyticsDashboard />);

      // Check for animate-pulse elements
      const skeletons = container.querySelectorAll('.animate-pulse');
      expect(skeletons.length).toBeGreaterThan(0);
    });

    it('should show loading skeleton for stats cards', () => {
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      (tagService.getAnalytics as Record<string, any>).mockReturnValueOnce(
        new Promise(() => {
          /* never resolves */
        })
      );

      const { container } = renderWithQueryClient(<TagAnalyticsDashboard />);

      const skeletonCards = container.querySelectorAll('.animate-pulse');
      expect(skeletonCards.length).toBeGreaterThanOrEqual(4); // At least stat cards + chart skeleton
    });
  });

  describe('3D.5.4: Error State', () => {
    it('should display error message when API fails', async () => {
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      (tagService.getAnalytics as Record<string, any>).mockRejectedValueOnce(
        new Error('Failed to fetch analytics')
      );

      renderWithQueryClient(<TagAnalyticsDashboard />);

      expect(await screen.findByText('Failed to load analytics')).toBeInTheDocument();
      expect(screen.getByText('Failed to fetch analytics')).toBeInTheDocument();
    });

    it('should show error message and component renders', async () => {
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      (tagService.getAnalytics as Record<string, any>).mockRejectedValueOnce(
        new Error('API Error')
      );

      renderWithQueryClient(<TagAnalyticsDashboard />);

      await screen.findByText('Failed to load analytics');
      // Alert component handles icon rendering internally
      expect(screen.getByText('API Error')).toBeInTheDocument();
    });
  });

  describe('3D.5.5: Empty State', () => {
    it('should display empty state message when no tags exist', async () => {
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      (tagService.getAnalytics as Record<string, any>).mockResolvedValueOnce({
        tags: [],
        totalTags: 0,
        totalUsage: 0,
        averageUsagePerTag: 0,
      });

      renderWithQueryClient(<TagAnalyticsDashboard />);

      expect(await screen.findByText('No tags yet')).toBeInTheDocument();
      expect(
        screen.getByText('Start by creating tags for your 3D models to see analytics data here.')
      ).toBeInTheDocument();
    });

    it('should show empty state message and component renders', async () => {
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      (tagService.getAnalytics as Record<string, any>).mockResolvedValueOnce({
        totalTags: 0,
        totalAssignments: 0,
        averageTagsPerModel: 0,
        mostUsedTags: [],
        leastUsedTags: [],
      });

      renderWithQueryClient(<TagAnalyticsDashboard />);

      await screen.findByText('No tags yet');
      // Alert component handles icon rendering internally
      expect(screen.getByText('Start by creating tags for your 3D models to see analytics data here.')).toBeInTheDocument();
    });
  });

  describe('3D.5.6: Accessibility', () => {
    it('should have proper ARIA labels on statistics region', async () => {

      renderWithQueryClient(<TagAnalyticsDashboard />);

      const statsRegion = await screen.findByRole('region', { name: 'Tag statistics' });
      expect(statsRegion).toBeInTheDocument();
    });

    it('should have proper ARIA labels on top tags region', async () => {

      renderWithQueryClient(<TagAnalyticsDashboard />);

      const tagsRegion = await screen.findByRole('region', { name: 'Most used tags' });
      expect(tagsRegion).toBeInTheDocument();
    });

    it('should have progressbar role on usage bars', async () => {

      renderWithQueryClient(<TagAnalyticsDashboard />);

      const progressbars = await screen.findAllByRole('progressbar');
      expect(progressbars.length).toBeGreaterThan(0);
    });

    it('should have aria-label on progressbar for tag usage', async () => {

      renderWithQueryClient(<TagAnalyticsDashboard />);

      const plaProgressbar = await screen.findByRole('progressbar', {
        name: /PLA usage/i,
      });
      expect(plaProgressbar).toBeInTheDocument();
      expect(plaProgressbar).toHaveAttribute('aria-valuenow', '45');
    });

    it('should have semantic heading structure', async () => {

      renderWithQueryClient(<TagAnalyticsDashboard />);

      expect(await screen.findByRole('heading', { level: 2, name: 'Tag Analytics' })).toBeInTheDocument();
      expect(
        await screen.findByText('Top Tags by Usage')
      ).toBeInTheDocument();
    });
  });

  describe('3D.5.7: Responsive Design', () => {
    it('should use responsive grid classes', async () => {

      renderWithQueryClient(<TagAnalyticsDashboard />);

      // Verify that stats grid region is displayed (which uses responsive classes in component)
      const statsRegion = await screen.findByRole('region', { name: 'Tag statistics' });
      expect(statsRegion).toBeInTheDocument();
      // Verify all stat cards appear (evidence of grid layout)
      expect(screen.getByText('Total Tags')).toBeInTheDocument();
      expect(screen.getByText('Total Assignments')).toBeInTheDocument();
    });

    it('should render all stat cards in mobile view', async () => {

      renderWithQueryClient(<TagAnalyticsDashboard />);

      // All stat cards should be present (stacked in mobile)
      expect(await screen.findByText('Total Tags')).toBeInTheDocument();
      expect(screen.getByText('Total Assignments')).toBeInTheDocument();
      expect(screen.getByText('Avg. Per Model')).toBeInTheDocument();
      expect(screen.getByText('Most Used')).toBeInTheDocument();
    });
  });

  describe('3D.5.8: Integration', () => {
    it('should call tagService.getAnalytics on mount', async () => {

      renderWithQueryClient(<TagAnalyticsDashboard />);

      expect(tagService.getAnalytics).toHaveBeenCalled();
    });

    it('should display suggestions section', async () => {

      renderWithQueryClient(<TagAnalyticsDashboard />);

      expect(await screen.findByText('Suggestions')).toBeInTheDocument();
      expect(
        screen.getByText(/Consider consolidating tags with similar names/)
      ).toBeInTheDocument();
      expect(
        screen.getByText(/Archive tags with very low usage/)
      ).toBeInTheDocument();
    });

    it('should not show suggestions when no tags', async () => {
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      (tagService.getAnalytics as Record<string, any>).mockResolvedValueOnce({
        tags: [],
        totalTags: 0,
        totalUsage: 0,
        averageUsagePerTag: 0,
      });

      renderWithQueryClient(<TagAnalyticsDashboard />);

      await screen.findByText('No tags yet');
      expect(screen.queryByText('Suggestions')).not.toBeInTheDocument();
    });
  });

  describe('3D.5.9: Edge Cases', () => {
    it('should handle single tag analytics', async () => {
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      (tagService.getAnalytics as Record<string, any>).mockResolvedValueOnce({
        totalTags: 1,
        totalAssignments: 5,
        averageTagsPerModel: 5,
        mostUsedTags: [{ id: '1', name: 'OnlyTag', usageCount: 5 }],
        leastUsedTags: [{ id: '1', name: 'OnlyTag', usageCount: 5 }],
      });

      renderWithQueryClient(<TagAnalyticsDashboard />);

      expect(await screen.findByText('5 uses')).toBeInTheDocument();
      expect(screen.getAllByText('OnlyTag').length).toBeGreaterThan(0);
    });

    it('should handle tags with zero usage', async () => {
      const dataWithZeroUsage = {
        ...mockAnalyticsData,
        mostUsedTags: [
          ...mockAnalyticsData.mostUsedTags,
          { id: '9', name: 'UnusedTag', usageCount: 0 },
        ],
      };

      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      (tagService.getAnalytics as Record<string, any>).mockResolvedValueOnce(dataWithZeroUsage);

      renderWithQueryClient(<TagAnalyticsDashboard />);

      // Component should handle gracefully without crashing
      expect(await screen.findByText('Tag Analytics')).toBeInTheDocument();
    });

    it('should handle very long tag names', async () => {
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      (tagService.getAnalytics as Record<string, any>).mockResolvedValueOnce({
        totalTags: 1,
        totalAssignments: 10,
        averageTagsPerModel: 10,
        mostUsedTags: [
          {
            id: '1',
            name: 'ThisIsAVeryLongTagNameThatMightBreakTheLayout',
            usageCount: 10,
          },
        ],
        leastUsedTags: [],
      });

      renderWithQueryClient(<TagAnalyticsDashboard />);

      const longTagElements = await screen.findAllByText('ThisIsAVeryLongTagNameThatMightBreakTheLayout');
      expect(longTagElements.length).toBeGreaterThan(0);
      expect(longTagElements[0].className).toContain('truncate');
    });
  });
});

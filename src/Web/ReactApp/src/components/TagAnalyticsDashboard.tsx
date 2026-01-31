import React, { useMemo } from 'react';
import { useQuery } from '@tanstack/react-query';
import { tagService } from '@/services/tagService';
import { Card } from '@/common/components/ui/Card';
import { Alert } from '@/common/components/ui/Alert';
import { Badge } from '@/common/components/ui/Badge';

/**
 * TagAnalyticsDashboard Component
 * 
 * Displays comprehensive tag usage statistics and analytics.
 * Shows most used tags, usage counts, and cleanup recommendations.
 * Built with PrintFarmer design system for consistent styling.
 * 
 * Features:
 * - Tag usage statistics
 * - Most used tags visualization
 * - Tag cleanup recommendations
 * - Responsive grid layout
 * - Loading and error states
 * - Empty state messaging
 * - WCAG 2.2 AA accessibility
 */
const TagAnalyticsDashboard: React.FC = () => {
  // Fetch analytics data
  const { data, isLoading, isError, error } = useQuery({
    queryKey: ['tagAnalytics'],
    queryFn: async () => {
      return await tagService.getAnalytics();
    },
    staleTime: 5 * 60 * 1000, // 5 minutes
    gcTime: 10 * 60 * 1000, // 10 minutes (formerly cacheTime)
  });

  // Calculate statistics
  const stats = useMemo(() => {
    if (!data?.topTags) {
      return {
        topTags: [],
        totalTags: 0,
        tagsInUse: 0,
        totalModelTagAssociations: 0,
        averageTagsPerModel: 0,
        maxUsage: 0,
      };
    }

    const topTags = data.topTags || [];
    const maxUsage = topTags[0]?.modelCount || 0;

    return {
      topTags,
      totalTags: data.totalTags || 0,
      tagsInUse: data.tagsInUse || 0,
      totalModelTagAssociations: data.totalModelTagAssociations || 0,
      averageTagsPerModel: data.averageTagsPerModel || 0,
      maxUsage,
    };
  }, [data]);

  // Calculate percentage for bar width
  const getBarPercentage = (count: number): number => {
    return stats.maxUsage > 0 ? (count / stats.maxUsage) * 100 : 0;
  };

  if (isLoading) {
    return (
      <div className="space-y-6">
        <h2 className="text-2xl font-bold text-pf-text-primary">Tag Analytics</h2>
        
        {/* Loading skeleton */}
        <div className="grid grid-cols-1 gap-6 md:grid-cols-4">
          {[1, 2, 3, 4].map((i) => (
            <div key={i} className="h-24 animate-pulse bg-pf-bg-1 rounded-lg" />
          ))}
        </div>

        <div className="h-96 animate-pulse bg-pf-bg-1 rounded-lg" />
      </div>
    );
  }

  if (isError) {
    return (
      <div className="space-y-6">
        <h2 className="text-2xl font-bold text-pf-text-primary">Tag Analytics</h2>
        
        <Alert type="error" title="Failed to load analytics">
          {error instanceof Error ? error.message : 'An error occurred while fetching analytics data.'}
        </Alert>
      </div>
    );
  }

  if (!data?.topTags || stats.totalTags === 0) {
    return (
      <div className="space-y-6">
        <h2 className="text-2xl font-bold text-pf-text-primary">Tag Analytics</h2>
        
        <Alert type="info" title="No tags yet">
          Start by creating tags to see analytics data here.
        </Alert>
      </div>
    );
  }

  return (
    <div className="space-y-8">
      <div>
        <h2 className="text-2xl font-bold text-pf-text-primary">Tag Analytics</h2>
        <p className="mt-1 text-sm text-pf-text-secondary">
          Overview of tag usage and statistics across your 3D model library
        </p>
      </div>

      {/* Statistics Grid */}
      <div
        className="grid grid-cols-1 gap-6 md:grid-cols-2 lg:grid-cols-4"
        role="region"
        aria-label="Tag statistics"
      >
        {/* Total Tags */}
        <Card className="flex items-center justify-between p-6">
          <div>
            <p className="text-sm text-pf-text-secondary">Total Tags</p>
            <p className="mt-2 text-3xl font-bold text-pf-text-primary">
              {stats.totalTags}
            </p>
          </div>
          <Badge>#</Badge>
        </Card>

        {/* Total Assignments */}
        <Card className="flex items-center justify-between p-6">
          <div>
            <p className="text-sm text-pf-text-secondary">Total Assignments</p>
            <p className="mt-2 text-3xl font-bold text-pf-text-primary">
              {stats.totalModelTagAssociations}
            </p>
          </div>
          <Badge variant="success">✓</Badge>
        </Card>

        {/* Average Tags Per Model */}
        <Card className="flex items-center justify-between p-6">
          <div>
            <p className="text-sm text-pf-text-secondary">Avg. Per Model</p>
            <p className="mt-2 text-3xl font-bold text-pf-text-primary">
              {stats.averageTagsPerModel.toFixed(1)}
            </p>
          </div>
          <Badge variant="info">∿</Badge>
        </Card>

        {/* Most Used Tag */}
        <Card className="flex items-center justify-between p-6">
          <div>
            <p className="text-sm text-pf-text-secondary">Most Used</p>
            <p className="mt-2 text-lg font-bold text-pf-text-primary truncate">
              {stats.topTags[0]?.name || 'N/A'}
            </p>
            <p className="text-sm text-pf-text-secondary">
              {stats.topTags[0]?.modelCount || 0} uses
            </p>
          </div>
          <Badge variant="warning">★</Badge>
        </Card>
      </div>

      {/* Most Used Tags Chart */}
      <Card>
        <Card.Body>
          <h3 className="text-lg font-semibold text-pf-text-primary mb-6">
            Top Tags by Usage
          </h3>
          
          <div
            className="space-y-4"
            role="region"
            aria-label="Most used tags"
          >
            {stats.topTags.map((tag, index) => (
              <div key={tag.id} className="flex items-center gap-4">
                {/* Rank Badge */}
                <div className="shrink-0 w-8">
                  <Badge variant="default" className="inline-flex items-center justify-center h-8 w-8 rounded-full p-0 text-xs">
                    {index + 1}
                  </Badge>
                </div>

                {/* Tag Name */}
                <div className="shrink-0 w-32">
                  <p className="text-sm font-medium text-pf-text-primary truncate">
                    {tag.name}
                  </p>
                </div>

                {/* Bar */}
                <div className="grow">
                  <div className="h-8 bg-pf-bg-1 rounded-md overflow-hidden">
                    <div
                      className="h-full bg-linear-to-r from-pf-accent to-pf-accent/80 transition-all duration-300"
                      style={{ width: `${getBarPercentage(tag.modelCount)}%` }}
                      role="progressbar"
                      aria-valuenow={tag.modelCount}
                      aria-valuemin={0}
                      aria-valuemax={stats.maxUsage}
                      aria-label={`${tag.name} usage: ${tag.modelCount}`}
                    />
                  </div>
                </div>

                {/* Count */}
                <div className="shrink-0 w-12 text-right">
                  <p className="text-sm font-semibold text-pf-text-primary">
                    {tag.modelCount}
                  </p>
                </div>
              </div>
            ))}
          </div>

          {stats.topTags.length === 0 && (
            <p className="text-sm text-pf-text-secondary text-center py-8">
              No tags to display
            </p>
          )}
        </Card.Body>
      </Card>

      {/* Cleanup Recommendations */}
      {stats.topTags.length > 0 && (
        <Alert type="warning" title="Suggestions">
          <ul className="space-y-1 text-sm">
            <li>• Consider consolidating tags with similar names</li>
            <li>• Archive tags with very low usage (&lt; 2 uses)</li>
            <li>• Use the most popular tags ({stats.topTags[0]?.name}) as templates</li>
          </ul>
        </Alert>
      )}
    </div>
  );
};

export default TagAnalyticsDashboard;

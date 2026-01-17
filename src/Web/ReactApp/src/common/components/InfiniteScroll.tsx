/**
 * Infinite scroll wrapper component
 * Handles scroll detection and loading more items
 */
import React, { useEffect, useRef } from 'react';

interface InfiniteScrollProps {
  children: React.ReactNode;
  onLoadMore: () => void;
  hasMore: boolean;
  isLoading?: boolean;
  loader?: React.ReactNode;
  threshold?: number;
  className?: string;
}

export const InfiniteScroll: React.FC<InfiniteScrollProps> = ({
  children,
  onLoadMore,
  hasMore,
  isLoading = false,
  loader,
  threshold = 200,
  className = '',
}) => {
  const observerTarget = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!observerTarget.current) return;
    if (!hasMore || isLoading) return;

    const observer = new IntersectionObserver(
      (entries) => {
        if (entries[0].isIntersecting && hasMore && !isLoading) {
          onLoadMore();
        }
      },
      {
        rootMargin: `${threshold}px`,
      }
    );

    observer.observe(observerTarget.current);

    return () => observer.disconnect();
  }, [hasMore, isLoading, onLoadMore, threshold]);

  return (
    <div className={`flex flex-col ${className}`}>
      {children}
      
      {/* Loading indicator */}
      {isLoading && (
        <div className="flex justify-center py-8">
          {loader || (
            <div className="flex items-center gap-2">
              <div className="pf-animate-spin rounded-full h-6 w-6 border-b-2 border-pf-accent" />
              <span className="text-pf-text-secondary text-sm">Loading more...</span>
            </div>
          )}
        </div>
      )}
      
      {/* Intersection observer target */}
      {hasMore && <div ref={observerTarget} className="h-4" />}
      
      {/* End of list indicator */}
      {!hasMore && (
        <div className="text-center py-8 text-pf-text-secondary text-sm">
          No more items
        </div>
      )}
    </div>
  );
};

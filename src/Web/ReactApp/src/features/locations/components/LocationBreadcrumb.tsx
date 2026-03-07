import React, { useState, useEffect } from 'react';
import clsx from 'clsx';
import { Button } from '@/common/components/ui';
import type { LocationBreadcrumbItem } from '@/types/api';
import { locationService } from '@/services/locationService';

export interface LocationBreadcrumbProps {
  locationId: string;
  onNavigate?: (locationId: string) => void;
  className?: string;
}

export const LocationBreadcrumb: React.FC<LocationBreadcrumbProps> = ({
  locationId,
  onNavigate,
  className,
}) => {
  const [ancestors, setAncestors] = useState<LocationBreadcrumbItem[]>([]);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    let cancelled = false;
    const load = async () => {
      try {
        setLoading(true);
        const data = await locationService.getLocationAncestors(locationId);
        if (!cancelled) setAncestors(data);
      } catch {
        if (!cancelled) setAncestors([]);
      } finally {
        if (!cancelled) setLoading(false);
      }
    };
    load();
    return () => {
      cancelled = true;
    };
  }, [locationId]);

  if (loading) {
    return <span className={clsx('text-sm text-pf-text-tertiary', className)}>…</span>;
  }

  if (ancestors.length === 0) {
    return null;
  }

  return (
    <nav className={clsx('flex items-center text-sm', className)} aria-label="Location breadcrumb">
      {ancestors.map((item, index) => {
        const isLast = index === ancestors.length - 1;
        return (
          <React.Fragment key={item.id}>
            {onNavigate && !isLast ? (
              <Button
                variant="unstyled"
                className="text-pf-text-secondary hover:text-pf-accent transition-colors"
                onClick={() => onNavigate(item.id)}
              >
                {item.name}
              </Button>
            ) : (
              <span className={clsx(isLast ? 'text-pf-text-primary font-medium' : 'text-pf-text-secondary')}>
                {item.name}
              </span>
            )}
            {!isLast && <span className="mx-1.5 text-pf-text-tertiary">/</span>}
          </React.Fragment>
        );
      })}
    </nav>
  );
};

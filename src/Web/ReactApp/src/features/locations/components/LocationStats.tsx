import React from 'react';
import { Card } from '@/common/components/ui';
import clsx from 'clsx';

export interface LocationStatsData {
  totalPrinters: number;
  online: number;
  offline: number;
  attention?: number;
  printing: number;
  idle: number;
  activeJobs: number;
}

interface LocationStatsProps {
  stats: LocationStatsData;
  locationName: string;
  isLoading?: boolean;
}

interface StatCardProps {
  label: string;
  value: number;
  variant: 'default' | 'success' | 'warning' | 'error' | 'info';
}

const VARIANT_STYLES: Record<StatCardProps['variant'], string> = {
  default: 'text-pf-text-primary',
  success: 'text-pf-success',
  warning: 'text-pf-warning',
  error: 'text-pf-error',
  info: 'text-pf-accent',
};

function StatCard({ label, value, variant }: StatCardProps) {
  return (
    <Card>
      <Card.Body className="flex flex-col items-center justify-center p-4">
        <span className={clsx('text-3xl font-bold', VARIANT_STYLES[variant])}>
          {value}
        </span>
        <span className="text-sm text-pf-text-secondary mt-1">{label}</span>
      </Card.Body>
    </Card>
  );
}

export const LocationStats: React.FC<LocationStatsProps> = ({
  stats,
  locationName,
  isLoading,
}) => {
  if (isLoading) {
    return (
      <div className="grid grid-cols-2 md:grid-cols-3 lg:grid-cols-6 gap-4">
        {Array.from({ length: 6 }).map((_, i) => (
          <Card key={i}>
            <Card.Body className="flex flex-col items-center justify-center p-4">
              <div className="h-9 w-12 pf-skeleton pf-animate-skeleton rounded" />
              <div className="h-4 w-16 pf-skeleton pf-animate-skeleton rounded mt-2" />
            </Card.Body>
          </Card>
        ))}
      </div>
    );
  }

  return (
    <div>
      <h3 className="text-lg font-semibold text-pf-text-primary mb-3">
        {locationName} — Overview
      </h3>
      <div className="grid grid-cols-2 md:grid-cols-3 lg:grid-cols-6 gap-4">
        <StatCard label="Total Printers" value={stats.totalPrinters} variant="default" />
        <StatCard label="Online" value={stats.online} variant="success" />
        <StatCard label="Offline" value={stats.offline} variant="error" />
        <StatCard label="Printing" value={stats.printing} variant="info" />
        <StatCard label="Idle" value={stats.idle} variant="warning" />
        <StatCard label="Active Jobs" value={stats.activeJobs} variant="info" />
      </div>
    </div>
  );
};

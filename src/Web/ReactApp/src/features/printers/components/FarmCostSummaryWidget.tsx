/**
 * FarmCostSummaryWidget Component
 *
 * Dashboard widget showing farm-level cost summary with 7-day / 30-day toggle.
 * Uses the existing getCostSummary API endpoint.
 */

import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { DashboardWidget } from '@/common/components/DashboardWidget';
import { Button } from '@/common/components/ui';
import { ChartIcon } from '@/common/components/icons/MdiIcons';
import { apiClient } from '@/services/api';

const PERIOD_OPTIONS = [7, 30] as const;
type Period = (typeof PERIOD_OPTIONS)[number];

function formatCurrency(value: number | null | undefined): string {
  if (value == null) return '—';
  return `$${value.toFixed(2)}`;
}

export function FarmCostSummaryWidget({ className = '' }: { className?: string }) {
  const [period, setPeriod] = useState<Period>(7);

  const { data: summary, isLoading, error } = useQuery({
    queryKey: ['cost-summary', period],
    queryFn: () => apiClient.getCostSummary(period),
    staleTime: 60_000,
  });

  const periodToggle = (
    <div className="flex gap-1">
      {PERIOD_OPTIONS.map((p) => (
        <Button
          key={p}
          variant={period === p ? 'primary' : 'ghost'}
          size="sm"
          onClick={() => setPeriod(p)}
          aria-pressed={period === p}
        >
          {p}d
        </Button>
      ))}
    </div>
  );

  return (
    <DashboardWidget
      title="Farm Cost Summary"
      icon={ChartIcon}
      iconColorClass="text-pf-accent"
      iconBgClass="bg-pf-accent/10"
      subtitle={`Last ${period} days`}
      headerAction={periodToggle}
      moreInfoLink="/statistics/costs"
      moreInfoText="Details"
      className={className}
      isLoading={isLoading}
      error={error ? String(error) : undefined}
      hasContent={!!summary}
    >
      {summary && (
        <div className="space-y-3">
          <div className="text-center p-3 bg-pf-bg-0 rounded-lg">
            <p className="text-xs text-pf-text-muted mb-1">Total Cost</p>
            <p className="text-2xl font-bold text-pf-text-primary">
              {formatCurrency(summary.totalCostUsd)}
            </p>
            <p className="text-xs text-pf-text-muted mt-1">
              {summary.jobsWithCostData} job{summary.jobsWithCostData !== 1 ? 's' : ''} tracked
            </p>
          </div>

          <div className="grid grid-cols-3 gap-2">
            <div className="text-center p-2 bg-pf-bg-0 rounded-md">
              <p className="text-xs text-pf-text-muted mb-1">Energy</p>
              <p className="text-sm font-semibold text-pf-text-primary">
                {formatCurrency(summary.totalEnergyCostUsd)}
              </p>
            </div>
            <div className="text-center p-2 bg-pf-bg-0 rounded-md">
              <p className="text-xs text-pf-text-muted mb-1">Material</p>
              <p className="text-sm font-semibold text-pf-text-primary">
                {formatCurrency(summary.totalMaterialCostUsd)}
              </p>
            </div>
            <div className="text-center p-2 bg-pf-bg-0 rounded-md">
              <p className="text-xs text-pf-text-muted mb-1">Machine</p>
              <p className="text-sm font-semibold text-pf-text-primary">
                {formatCurrency(summary.totalMachineTimeCostUsd)}
              </p>
            </div>
          </div>

          {summary.averageCostPerJobUsd > 0 && (
            <p className="text-xs text-pf-text-muted text-center">
              Avg. {formatCurrency(summary.averageCostPerJobUsd)} per job
            </p>
          )}
        </div>
      )}
    </DashboardWidget>
  );
}

export default FarmCostSummaryWidget;

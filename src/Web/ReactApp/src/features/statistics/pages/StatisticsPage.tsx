import React, { useState } from 'react';
import { Card } from '@/common/components/ui/Card';
import { PageTemplate } from '@/common/components/PageTemplate';
import { TimePeriodFilter } from '@/common/components/ui/TimePeriodFilter';
import { ChartIcon } from '@/common/components/icons/MdiIcons';
import {
  useStatisticsSummary,
  useJobsOverTime,
  useCostOverTime,
  useFilamentByMaterial,
  usePrinterUtilization,
} from '../hooks/useStatistics';
import { JobsOverTimeChart } from '../components/JobsOverTimeChart';
import { CostOverTimeChart } from '../components/CostOverTimeChart';
import { FilamentByMaterialChart } from '../components/FilamentByMaterialChart';
import { PrinterUtilizationChart } from '../components/PrinterUtilizationChart';
import type { TimePeriodFilterValue } from '@/common/components/ui/timePeriodOptions';

export interface StatisticsDashboardContentProps {
  days?: number;
  startDate?: string;
  endDate?: string;
  showSummaryCards?: boolean;
}

export function StatisticsDashboardContent({
  days,
  startDate,
  endDate,
  showSummaryCards = true,
}: StatisticsDashboardContentProps) {
  const { data: summary, isLoading: summaryLoading } = useStatisticsSummary(days, startDate, endDate);
  const { data: jobsData, isLoading: jobsLoading, error: jobsError } = useJobsOverTime(days, startDate, endDate);
  const { data: costData, isLoading: costLoading, error: costError } = useCostOverTime(days, startDate, endDate);
  const { data: filamentData, isLoading: filamentLoading, error: filamentError } = useFilamentByMaterial(days, startDate, endDate);
  const { data: utilizationData, isLoading: utilizationLoading, error: utilizationError } = usePrinterUtilization(days, startDate, endDate);

  return (
    <div className="space-y-6">
      {showSummaryCards && (
        <div className="grid grid-cols-2 gap-4 md:grid-cols-4">
          <KpiCard label="Total Jobs" value={summary?.totalJobs ?? 0} loading={summaryLoading} />
          <KpiCard
            label="Success Rate"
            value={`${summary?.successRate ?? 0}%`}
            loading={summaryLoading}
            color={
              (summary?.successRate ?? 0) >= 90
                ? 'text-pf-success'
                : (summary?.successRate ?? 0) >= 70
                  ? 'text-pf-warning'
                  : 'text-pf-error'
            }
          />
          <KpiCard
            label="Total Cost"
            value={`$${(summary?.totalCost ?? 0).toFixed(2)}`}
            loading={summaryLoading}
          />
          <KpiCard
            label="Print Hours"
            value={(summary?.totalPrintHours ?? 0).toFixed(1)}
            loading={summaryLoading}
          />
          <KpiCard
            label="Completed"
            value={summary?.completedJobs ?? 0}
            loading={summaryLoading}
            color="text-pf-success"
          />
          <KpiCard
            label="Failed"
            value={summary?.failedJobs ?? 0}
            loading={summaryLoading}
            color="text-pf-error"
          />
          <KpiCard
            label="Cancelled"
            value={summary?.cancelledJobs ?? 0}
            loading={summaryLoading}
            color="text-pf-warning"
          />
          <KpiCard
            label="Filament Used"
            value={`${((summary?.totalFilamentGrams ?? 0) / 1000).toFixed(2)} kg`}
            loading={summaryLoading}
          />
        </div>
      )}

      <div className="grid grid-cols-1 gap-6 lg:grid-cols-2">
        <JobsOverTimeChart data={jobsData ?? []} isLoading={jobsLoading} error={jobsError} />
        <CostOverTimeChart data={costData ?? []} isLoading={costLoading} error={costError} />
        <FilamentByMaterialChart data={filamentData ?? []} isLoading={filamentLoading} error={filamentError} />
        <PrinterUtilizationChart data={utilizationData ?? []} isLoading={utilizationLoading} error={utilizationError} />
      </div>
    </div>
  );
}

export const StatisticsPage: React.FC = () => {
  const [period, setPeriod] = useState<TimePeriodFilterValue>({ type: 'preset', days: 30 });
  const days = period.type === 'preset' ? period.days : undefined;
  const startDate = period.type === 'custom' ? period.startDate : undefined;
  const endDate = period.type === 'custom' ? period.endDate : undefined;

  return (
    <PageTemplate
      title="Print Statistics"
      subtitle="Track print jobs, costs, and printer utilization"
      icon={ChartIcon}
      actions={<TimePeriodFilter value={period} onChange={setPeriod} />}
    >
      <StatisticsDashboardContent days={days} startDate={startDate} endDate={endDate} />
    </PageTemplate>
  );
};

interface KpiCardProps {
  label: string;
  value: string | number;
  loading?: boolean;
  color?: string;
}

const KpiCard: React.FC<KpiCardProps> = ({ label, value, loading, color }) => (
  <Card className="p-4">
    <p className="text-sm text-pf-text-secondary">{label}</p>
    {loading ? (
      <div className="mt-1" aria-busy="true">
        <div className="pf-skeleton pf-animate-skeleton h-8 w-20 rounded" />
      </div>
    ) : (
      <p className={`mt-1 text-2xl font-bold ${color ?? 'text-pf-text'}`}>{value}</p>
    )}
  </Card>
);

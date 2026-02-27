import React, { useState } from 'react';
import { Card } from '@/common/components/ui/Card';
import { Button } from '@/common/components/ui';
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

const PERIOD_OPTIONS = [
  { label: '7 days', value: 7 },
  { label: '30 days', value: 30 },
  { label: '90 days', value: 90 },
  { label: 'All time', value: undefined },
] as const;

export const StatisticsPage: React.FC = () => {
  const [days, setDays] = useState<number | undefined>(30);
  const { data: summary, isLoading: summaryLoading } = useStatisticsSummary(days);
  const { data: jobsData, isLoading: jobsLoading, error: jobsError } = useJobsOverTime(days ?? 365);
  const { data: costData, isLoading: costLoading, error: costError } = useCostOverTime(days ?? 365);
  const { data: filamentData, isLoading: filamentLoading, error: filamentError } = useFilamentByMaterial(days);
  const { data: utilizationData, isLoading: utilizationLoading, error: utilizationError } = usePrinterUtilization(days);

  return (
    <div className="space-y-6 p-4">
      <div className="flex items-center justify-between">
        <h1 className="text-2xl font-bold text-pf-text">Print Statistics</h1>
        <div className="flex gap-2" role="group" aria-label="Time period filter">
          {PERIOD_OPTIONS.map((opt) => (
            <Button
              variant="unstyled"
              key={opt.label}
              onClick={() => setDays(opt.value)}
              className={`rounded-md px-3 py-1.5 text-sm font-medium transition-colors ${
                days === opt.value
                  ? 'bg-pf-primary text-white'
                  : 'bg-pf-surface text-pf-text-secondary hover:bg-pf-hover'
              }`}
              aria-pressed={days === opt.value}
            >
              {opt.label}
            </Button>
          ))}
        </div>
      </div>

      {/* KPI Cards */}
      <div className="grid grid-cols-2 gap-4 md:grid-cols-4">
        <KpiCard
          label="Total Jobs"
          value={summary?.totalJobs ?? 0}
          loading={summaryLoading}
        />
        <KpiCard
          label="Success Rate"
          value={`${summary?.successRate ?? 0}%`}
          loading={summaryLoading}
          color={
            (summary?.successRate ?? 0) >= 90 ? 'text-green-500' :
            (summary?.successRate ?? 0) >= 70 ? 'text-yellow-500' : 'text-red-500'
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
          color="text-green-500"
        />
        <KpiCard
          label="Failed"
          value={summary?.failedJobs ?? 0}
          loading={summaryLoading}
          color="text-red-500"
        />
        <KpiCard
          label="Cancelled"
          value={summary?.cancelledJobs ?? 0}
          loading={summaryLoading}
          color="text-yellow-500"
        />
        <KpiCard
          label="Filament Used"
          value={`${((summary?.totalFilamentGrams ?? 0) / 1000).toFixed(2)} kg`}
          loading={summaryLoading}
        />
      </div>

      {/* Charts Grid */}
      <div className="grid grid-cols-1 gap-6 lg:grid-cols-2">
        <JobsOverTimeChart data={jobsData ?? []} isLoading={jobsLoading} error={jobsError} />
        <CostOverTimeChart data={costData ?? []} isLoading={costLoading} error={costError} />
        <FilamentByMaterialChart data={filamentData ?? []} isLoading={filamentLoading} error={filamentError} />
        <PrinterUtilizationChart data={utilizationData ?? []} isLoading={utilizationLoading} error={utilizationError} />
      </div>
    </div>
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
      <div className="mt-1 h-8 w-20 animate-pulse rounded bg-pf-hover" />
    ) : (
      <p className={`mt-1 text-2xl font-bold ${color ?? 'text-pf-text'}`}>{value}</p>
    )}
  </Card>
);

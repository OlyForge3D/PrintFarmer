import React, { useState } from 'react';
import { Card } from '@/common/components/ui/Card';
import { Button } from '@/common/components/ui';
import { Tabs } from '@/common/components/ui/Tabs';
import { PageTemplate } from '@/common/components/PageTemplate';
import { TrendingUpIcon } from '@/common/components/icons/MdiIcons';
import {
  useStatisticsSummary,
  useJobsOverTime,
  useCostOverTime,
  useFilamentByMaterial,
  usePrinterUtilization,
} from '@/features/statistics/hooks/useStatistics';
import { JobsOverTimeChart } from '@/features/statistics/components/JobsOverTimeChart';
import { CostOverTimeChart } from '@/features/statistics/components/CostOverTimeChart';
import { FilamentByMaterialChart } from '@/features/statistics/components/FilamentByMaterialChart';
import { PrinterUtilizationChart } from '@/features/statistics/components/PrinterUtilizationChart';
import { ExportMenu } from '../components/ExportMenu';
import { PredictiveAlertsPanel } from '../components/PredictiveAlertsPanel';
import { CorrelationChartsSection } from '../components/CorrelationChartsSection';
import { useMaintenanceForecast } from '../hooks/usePredictiveAnalytics';

const PERIOD_OPTIONS = [
  { label: '7 days', value: 7 },
  { label: '30 days', value: 30 },
  { label: '90 days', value: 90 },
  { label: '1 year', value: 365 },
  { label: 'All time', value: undefined },
] as const;

export const AnalyticsDashboardPage: React.FC = () => {
  const [days, setDays] = useState<number | undefined>(30);

  const { data: summary, isLoading: summaryLoading } = useStatisticsSummary(days);
  const { data: jobsData, isLoading: jobsLoading, error: jobsError } = useJobsOverTime(days ?? 365);
  const { data: costData, isLoading: costLoading, error: costError } = useCostOverTime(days ?? 365);
  const { data: filamentData, isLoading: filamentLoading, error: filamentError } = useFilamentByMaterial(days);
  const { data: utilizationData, isLoading: utilizationLoading, error: utilizationError } = usePrinterUtilization(days);
  const { data: forecasts = [] } = useMaintenanceForecast(days);

  return (
    <PageTemplate
      title="Business Analytics"
      subtitle="Comprehensive print farm performance insights"
      icon={TrendingUpIcon}
      actions={
        <div className="flex items-center gap-3">
          <div className="flex gap-1" role="group" aria-label="Time period filter">
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
          <ExportMenu days={days} />
        </div>
      }
    >
      <div className="space-y-6">
        {/* Predictive Alerts */}
        <PredictiveAlertsPanel />

        {/* KPI Cards */}
        <div className="grid grid-cols-2 gap-4 md:grid-cols-4">
          <KpiCard label="Total Jobs" value={summary?.totalJobs ?? 0} loading={summaryLoading} />
          <KpiCard
            label="Success Rate"
            value={`${summary?.successRate ?? 0}%`}
            loading={summaryLoading}
            color={
              (summary?.successRate ?? 0) >= 90 ? 'text-pf-success' :
              (summary?.successRate ?? 0) >= 70 ? 'text-pf-warning' : 'text-pf-error'
            }
          />
          <KpiCard label="Total Cost" value={`$${(summary?.totalCost ?? 0).toFixed(2)}`} loading={summaryLoading} />
          <KpiCard label="Print Hours" value={(summary?.totalPrintHours ?? 0).toFixed(1)} loading={summaryLoading} />
          <KpiCard label="Completed" value={summary?.completedJobs ?? 0} loading={summaryLoading} color="text-pf-success" />
          <KpiCard label="Failed" value={summary?.failedJobs ?? 0} loading={summaryLoading} color="text-pf-error" />
          <KpiCard label="Cancelled" value={summary?.cancelledJobs ?? 0} loading={summaryLoading} color="text-pf-warning" />
          <KpiCard
            label="Filament Used"
            value={`${((summary?.totalFilamentGrams ?? 0) / 1000).toFixed(2)} kg`}
            loading={summaryLoading}
          />
        </div>

        {/* Tab Navigation */}
        <Tabs defaultTab="overview">
          <Tabs.List>
            <Tabs.Tab id="overview">Overview</Tabs.Tab>
            <Tabs.Tab id="correlations">Performance Correlations</Tabs.Tab>
            <Tabs.Tab id="maintenance">Maintenance Forecast</Tabs.Tab>
          </Tabs.List>
          <Tabs.Panels>
            <Tabs.Panel id="overview">
              <div className="grid grid-cols-1 gap-6 pt-4 lg:grid-cols-2">
                <JobsOverTimeChart data={jobsData ?? []} isLoading={jobsLoading} error={jobsError} />
                <CostOverTimeChart data={costData ?? []} isLoading={costLoading} error={costError} />
                <FilamentByMaterialChart data={filamentData ?? []} isLoading={filamentLoading} error={filamentError} />
                <PrinterUtilizationChart data={utilizationData ?? []} isLoading={utilizationLoading} error={utilizationError} />
              </div>
            </Tabs.Panel>
            <Tabs.Panel id="correlations">
              <div className="pt-4">
                <CorrelationChartsSection days={days} />
              </div>
            </Tabs.Panel>
            <Tabs.Panel id="maintenance">
              <div className="pt-4">
                <MaintenanceForecastSection forecasts={forecasts} />
              </div>
            </Tabs.Panel>
          </Tabs.Panels>
        </Tabs>
      </div>
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

interface MaintenanceForecastSectionProps {
  forecasts: import('../hooks/usePredictiveAnalytics').MaintenanceForecast[];
}

const MaintenanceForecastSection: React.FC<MaintenanceForecastSectionProps> = ({ forecasts }) => {
  if (forecasts.length === 0) {
    return (
      <Card className="p-6">
        <p className="text-center text-pf-text-secondary">No upcoming maintenance tasks predicted</p>
      </Card>
    );
  }

  return (
    <div className="space-y-4">
      {forecasts.map((forecast) => (
        <Card key={forecast.printerId} className="p-4">
          <h4 className="mb-2 font-semibold text-pf-text-primary">{forecast.printerName}</h4>
          <div className="space-y-2">
            {forecast.upcomingTasks.map((task, idx) => (
              <div
                key={`${task.taskName}-${idx}`}
                className="flex items-center justify-between rounded-md border border-pf-border bg-pf-bg-1 px-3 py-2"
              >
                <span className="text-sm text-pf-text-primary">{task.taskName}</span>
                <div className="flex items-center gap-3">
                  <span className="text-xs text-pf-text-secondary">
                    ~{task.estimatedDaysUntilDue} days
                  </span>
                  <span
                    className={`rounded-full px-2 py-0.5 text-xs font-medium ${
                      task.priority === 'High'
                        ? 'bg-pf-error/10 text-pf-error'
                        : 'bg-pf-warning/10 text-pf-warning'
                    }`}
                  >
                    {task.priority}
                  </span>
                </div>
              </div>
            ))}
          </div>
        </Card>
      ))}
    </div>
  );
};

import { useEffect, useMemo, useState } from 'react';
import { useSearchParams } from 'react-router';
import { PageTemplate } from '@/common/components/PageTemplate';
import { ChartIcon, PrinterIcon, TrendingUpIcon } from '@/common/components/icons/MdiIcons';
import { Badge, Card, Tabs, TimePeriodFilter } from '@/common/components/ui';
import type { TimePeriodFilterValue } from '@/common/components/ui';
import { useCostSummary, usePrinters } from '@/common/hooks/useApi';
import { usePrinterUtilization, useStatisticsSummary, type PrinterUtilization } from '@/features/statistics/hooks/useStatistics';
import { StatisticsDashboardContent } from '@/features/statistics/pages/StatisticsPage';
import { CostDashboardContent } from '@/features/statistics/pages/CostDashboardPage';
import { AnalyticsDashboardContent } from '@/features/analytics/pages/AnalyticsDashboardPage';
import { ExportMenu } from '@/features/analytics/components/ExportMenu';

const DEFAULT_PERIOD: TimePeriodFilterValue = { type: 'preset', days: 30 };
const DAY_IN_MS = 24 * 60 * 60 * 1000;
const currencyFormatter = new Intl.NumberFormat('en-US', {
  style: 'currency',
  currency: 'USD',
  maximumFractionDigits: 2,
});

type AnalyticsLens = 'production' | 'cost' | 'fleet';

interface SummaryMetric {
  label: string;
  value: string;
  source: string;
  loading: boolean;
  hasError?: boolean;
}

const lensTabs: Array<{ id: AnalyticsLens; label: string; icon: JSX.Element }> = [
  { id: 'production', label: 'Production', icon: <ChartIcon className="h-4 w-4" aria-hidden="true" /> },
  { id: 'cost', label: 'Cost', icon: <TrendingUpIcon className="h-4 w-4" aria-hidden="true" /> },
  { id: 'fleet', label: 'Fleet', icon: <PrinterIcon className="h-4 w-4" aria-hidden="true" /> },
];

function isAnalyticsLens(value: string | null): value is AnalyticsLens {
  return value === 'production' || value === 'cost' || value === 'fleet';
}

function formatCurrency(value: number | undefined): string {
  return currencyFormatter.format(value ?? 0);
}

function resolvePeriodRange(period: TimePeriodFilterValue) {
  if (period.type === 'custom') {
    const startDate = period.startDate;
    const endDate = period.endDate;
    const start = new Date(startDate);
    const end = new Date(endDate);
    const dayCount = Number.isNaN(start.getTime()) || Number.isNaN(end.getTime())
      ? 1
      : Math.max(1, Math.ceil((end.getTime() - start.getTime()) / DAY_IN_MS) + 1);

    return {
      days: undefined,
      startDate,
      endDate,
      dayCount,
      isAllTime: false,
    };
  }

  return {
    days: period.days,
    startDate: undefined,
    endDate: undefined,
    dayCount: period.days,
    isAllTime: period.days === undefined,
  };
}

function calculateFleetUtilization(
  utilizationData: PrinterUtilization[] | undefined,
  printerCount: number | undefined,
  dayCount?: number,
): number | null {
  if (!dayCount || dayCount <= 0 || printerCount === undefined || printerCount < 0) {
    return null;
  }

  if (printerCount === 0) {
    return 0;
  }

  const totalPrintHours = (utilizationData ?? []).reduce((sum, printer) => sum + (printer.totalPrintHours ?? 0), 0);
  const availableHours = printerCount * dayCount * 24;

  if (availableHours <= 0) {
    return null;
  }

  return Math.min(100, (totalPrintHours / availableHours) * 100);
}

function AnalyticsMetricCard({ label, value, source, loading, hasError = false }: SummaryMetric) {
  return (
    <Card className="border-pf-border bg-pf-bg-0 p-4 shadow-sm">
      <div className="flex items-start justify-between gap-3">
        <div className="min-w-0">
          <p className="text-sm font-medium text-pf-text-secondary">{label}</p>
          {loading ? (
            <div className="mt-2" aria-busy="true">
              <div className="pf-skeleton pf-animate-skeleton h-8 w-24 rounded" />
            </div>
          ) : (
            <p className={`mt-2 text-2xl font-semibold ${hasError ? 'text-pf-warning' : 'text-pf-text-primary'}`}>
              {value}
            </p>
          )}
        </div>
        <Badge variant={hasError ? 'warning' : 'info'} size="sm" className="shrink-0 uppercase tracking-wide">
          {source}
        </Badge>
      </div>
    </Card>
  );
}

export function AnalyticsHubPage() {
  const [period, setPeriod] = useState<TimePeriodFilterValue>(DEFAULT_PERIOD);
  const [searchParams, setSearchParams] = useSearchParams();
  const requestedLens = searchParams.get('lens');
  const activeLens: AnalyticsLens = isAnalyticsLens(requestedLens) ? requestedLens : 'production';
  const { days, startDate, endDate, dayCount, isAllTime } = resolvePeriodRange(period);

  const {
    data: statisticsSummary,
    isLoading: statisticsLoading,
    error: statisticsError,
  } = useStatisticsSummary(days, startDate, endDate);
  const {
    data: costSummary,
    isLoading: costLoading,
    error: costError,
  } = useCostSummary(days, startDate, endDate);
  const {
    data: utilizationData,
    isLoading: utilizationLoading,
    error: utilizationError,
  } = usePrinterUtilization(days, startDate, endDate);
  const {
    data: printers = [],
    isLoading: printersLoading,
    error: printersError,
  } = usePrinters();

  useEffect(() => {
    if (requestedLens === null || isAnalyticsLens(requestedLens)) {
      return;
    }

    const nextParams = new URLSearchParams(searchParams);
    nextParams.set('lens', 'production');
    setSearchParams(nextParams, { replace: true });
  }, [requestedLens, searchParams, setSearchParams]);

  const fleetUtilization = useMemo(
    () => calculateFleetUtilization(utilizationData, printers.length, dayCount),
    [utilizationData, printers.length, dayCount],
  );

  const summaryMetrics: SummaryMetric[] = [
    {
      label: 'Jobs Completed',
      value: statisticsError ? 'Unavailable' : String(statisticsSummary?.completedJobs ?? 0),
      source: statisticsError ? 'Check source' : 'Production',
      loading: statisticsLoading,
      hasError: !!statisticsError,
    },
    {
      label: 'Success Rate',
      value: statisticsError ? 'Unavailable' : `${statisticsSummary?.successRate ?? 0}%`,
      source: statisticsError ? 'Check source' : 'Production',
      loading: statisticsLoading,
      hasError: !!statisticsError,
    },
    {
      label: 'Cost/Print',
      value: costError ? 'Unavailable' : formatCurrency(costSummary?.averageCostPerJobUsd),
      source: costError ? 'Check source' : 'Cost',
      loading: costLoading,
      hasError: !!costError,
    },
    {
      label: 'Filament Spend',
      value: costError ? 'Unavailable' : formatCurrency(costSummary?.totalMaterialCostUsd),
      source: costError ? 'Check source' : 'Cost',
      loading: costLoading,
      hasError: !!costError,
    },
    {
      label: 'Fleet Utilization',
      value: utilizationError || printersError || fleetUtilization === null ? 'Unavailable' : `${fleetUtilization.toFixed(1)}%`,
      source: utilizationError || printersError ? 'Check source' : isAllTime ? 'Bounded only' : 'Fleet',
      loading: utilizationLoading || printersLoading,
      hasError: !!utilizationError || !!printersError || fleetUtilization === null,
    },
  ];

  const handleLensChange = (lens: string) => {
    if (!isAnalyticsLens(lens)) {
      return;
    }

    const nextParams = new URLSearchParams(searchParams);
    nextParams.set('lens', lens);
    setSearchParams(nextParams);
  };

  return (
    <PageTemplate
      title="Analytics"
      subtitle="One place for production health, cost visibility, and fleet insight."
      icon={TrendingUpIcon}
      actions={
        <div className="flex items-center gap-3">
          <TimePeriodFilter value={period} onChange={setPeriod} />
          <ExportMenu days={days} />
        </div>
      }
    >
      <div className="space-y-6">
        <section aria-label="Analytics key performance indicators" className="space-y-3">
          <div className="grid grid-cols-1 gap-4 md:grid-cols-2 xl:grid-cols-5">
            {summaryMetrics.map((metric) => (
              <AnalyticsMetricCard
                key={metric.label}
                label={metric.label}
                value={metric.value}
                source={metric.source}
                loading={metric.loading}
                hasError={metric.hasError}
              />
            ))}
          </div>
        </section>

        <Tabs activeTab={activeLens} onTabChange={handleLensChange} className="overflow-hidden rounded-xl border border-pf-border bg-pf-bg-1">
          <Tabs.List className="border-b border-pf-border bg-pf-bg-1 px-3 pt-3">
            {lensTabs.map((lensTab) => (
              <Tabs.Tab key={lensTab.id} id={lensTab.id} icon={lensTab.icon}>
                {lensTab.label}
              </Tabs.Tab>
            ))}
          </Tabs.List>
          <Tabs.Panels className="border-0 bg-pf-bg-2 p-4 md:p-6">
            <Tabs.Panel id="production">
              <StatisticsDashboardContent days={days} startDate={startDate} endDate={endDate} showSummaryCards={false} />
            </Tabs.Panel>
            <Tabs.Panel id="cost">
              <CostDashboardContent days={days} startDate={startDate} endDate={endDate} showSummaryCards={false} />
            </Tabs.Panel>
            <Tabs.Panel id="fleet">
              <AnalyticsDashboardContent days={days} startDate={startDate} endDate={endDate} showSummaryCards={false} />
            </Tabs.Panel>
          </Tabs.Panels>
        </Tabs>
      </div>
    </PageTemplate>
  );
}

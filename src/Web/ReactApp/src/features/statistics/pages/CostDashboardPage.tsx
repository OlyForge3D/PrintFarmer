import React, { useMemo, useState } from 'react';
import { Card } from '@/common/components/ui/Card';
import { DataTable } from '@/common/components/ui/DataTable';
import { Spinner } from '@/common/components/ui/Spinner';
import { Tabs } from '@/common/components/ui/Tabs';
import { TimePeriodFilter } from '@/common/components/ui/TimePeriodFilter';
import { PageTemplate } from '@/common/components/PageTemplate';
import { TrendingUpIcon } from '@/common/components/icons/MdiIcons';
import {
  useCostSummary,
  useCostsByPrinter,
  useCostsByMaterial,
  useCostsByJob,
} from '@/common/hooks/useApi';
import type { CostByPrinter, CostByJob, CostByMaterial } from '@/types/api';
import type { TimePeriodFilterValue } from '@/common/components/ui/timePeriodOptions';

const formatCurrency = (value: number) =>
  new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' }).format(value);

const formatDuration = (seconds?: number) => {
  if (!seconds) return '—';
  const h = Math.floor(seconds / 3600);
  const m = Math.floor((seconds % 3600) / 60);
  return h > 0 ? `${h}h ${m}m` : `${m}m`;
};

export interface CostDashboardContentProps {
  days?: number;
  startDate?: string;
  endDate?: string;
  showSummaryCards?: boolean;
}

export function CostDashboardContent({
  days,
  startDate,
  endDate,
  showSummaryCards = true,
}: CostDashboardContentProps) {
  const { data: summary, isLoading: summaryLoading, error: summaryError } = useCostSummary(days, startDate, endDate);
  const { data: printerCosts, isLoading: printerLoading, error: printerError } = useCostsByPrinter(days, startDate, endDate);
  const { data: materialCosts, isLoading: materialLoading, error: materialError } = useCostsByMaterial(days, startDate, endDate);
  const { data: jobCosts, isLoading: jobLoading, error: jobError } = useCostsByJob(days, startDate, endDate);

  const materialCostPercentage = useMemo(() => {
    if (!summary || !summary.totalCostUsd) return '0';
    return (((summary.totalMaterialCostUsd ?? 0) / summary.totalCostUsd) * 100).toFixed(1);
  }, [summary]);

  const energyCostPercentage = useMemo(() => {
    if (!summary || !summary.totalCostUsd) return '0';
    return (((summary.totalEnergyCostUsd ?? 0) / summary.totalCostUsd) * 100).toFixed(1);
  }, [summary]);

  if (summaryError || printerError || materialError || jobError) {
    return (
      <Card>
        <Card.Body>
          <div className="p-4 text-pf-error">
            Failed to load cost data: {String(summaryError || printerError || materialError || jobError)}
          </div>
        </Card.Body>
      </Card>
    );
  }

  return (
    <div className="space-y-6">
      {showSummaryCards && (
        <div className="grid grid-cols-2 gap-4 md:grid-cols-4">
          <KpiCard
            label="Total Cost"
            value={formatCurrency(summary?.totalCostUsd ?? 0)}
            loading={summaryLoading}
          />
          <KpiCard
            label="Avg Cost/Job"
            value={formatCurrency(summary?.averageCostPerJobUsd ?? 0)}
            loading={summaryLoading}
          />
          <KpiCard
            label="Material Cost %"
            value={`${materialCostPercentage}%`}
            loading={summaryLoading}
            color="text-pf-primary"
          />
          <KpiCard
            label="Energy Cost %"
            value={`${energyCostPercentage}%`}
            loading={summaryLoading}
            color="text-pf-warning"
          />
        </div>
      )}

      <Tabs defaultTab="by-printer">
        <Tabs.List>
          <Tabs.Tab id="by-printer">Cost by Printer</Tabs.Tab>
          <Tabs.Tab id="by-job">Costs by Job</Tabs.Tab>
          <Tabs.Tab id="by-material">Costs by Material</Tabs.Tab>
        </Tabs.List>
        <Tabs.Panels>
          <Tabs.Panel id="by-printer">
            <CostByPrinterTab data={printerCosts} loading={printerLoading} />
          </Tabs.Panel>
          <Tabs.Panel id="by-job">
            <CostByJobTab data={jobCosts} loading={jobLoading} />
          </Tabs.Panel>
          <Tabs.Panel id="by-material">
            <CostByMaterialTab data={materialCosts} loading={materialLoading} />
          </Tabs.Panel>
        </Tabs.Panels>
      </Tabs>
    </div>
  );
}

export const CostDashboardPage: React.FC = () => {
  const [period, setPeriod] = useState<TimePeriodFilterValue>({ type: 'preset', days: 30 });
  const days = period.type === 'preset' ? period.days : undefined;
  const startDate = period.type === 'custom' ? period.startDate : undefined;
  const endDate = period.type === 'custom' ? period.endDate : undefined;

  return (
    <PageTemplate
      title="Cost Analytics"
      subtitle="Track print job costs and analyze spending patterns"
      icon={TrendingUpIcon}
      actions={<TimePeriodFilter value={period} onChange={setPeriod} />}
    >
      <CostDashboardContent days={days} startDate={startDate} endDate={endDate} />
    </PageTemplate>
  );
};

function CostByPrinterTab({ data, loading }: { data?: CostByPrinter[]; loading: boolean }) {
  const columns = [
    { key: 'printerName', header: 'Printer', sortable: true, render: (r: CostByPrinter) => r.printerName },
    { key: 'jobCount', header: 'Jobs', sortable: true, render: (r: CostByPrinter) => String(r.jobCount) },
    { key: 'materialCostUsd', header: 'Material', sortable: true, render: (r: CostByPrinter) => formatCurrency(r.materialCostUsd) },
    { key: 'energyCostUsd', header: 'Energy', sortable: true, render: (r: CostByPrinter) => formatCurrency(r.energyCostUsd) },
    { key: 'machineTimeCostUsd', header: 'Machine Time', sortable: true, render: (r: CostByPrinter) => formatCurrency(r.machineTimeCostUsd) },
    { key: 'laborCostUsd', header: 'Labor', sortable: true, render: (r: CostByPrinter) => formatCurrency(r.laborCostUsd) },
    { key: 'totalCostUsd', header: 'Total', sortable: true, render: (r: CostByPrinter) => <span className="font-semibold">{formatCurrency(r.totalCostUsd)}</span> },
    { key: 'avgCost', header: 'Avg/Job', sortable: true, render: (r: CostByPrinter) => formatCurrency(r.jobCount > 0 ? r.totalCostUsd / r.jobCount : 0) },
  ];

  return (
    <Card className="mt-4">
      <Card.Body>
        {loading ? (
          <LoadingState />
        ) : data && data.length > 0 ? (
          <DataTable columns={columns} data={data} getRowKey={(r: CostByPrinter) => r.printerId} sortable />
        ) : (
          <EmptyState message="No printer cost data available" />
        )}
      </Card.Body>
    </Card>
  );
}

function CostByJobTab({ data, loading }: { data?: CostByJob[]; loading: boolean }) {
  const columns = [
    { key: 'jobName', header: 'Job', sortable: true, render: (r: CostByJob) => <span className="max-w-48 truncate block" title={r.jobName}>{r.jobName}</span> },
    { key: 'printerName', header: 'Printer', sortable: true, render: (r: CostByJob) => r.printerName ?? '—' },
    { key: 'filamentName', header: 'Filament', sortable: true, render: (r: CostByJob) => r.filamentName ?? r.materialType ?? '—' },
    { key: 'filamentUsedGrams', header: 'Weight (g)', sortable: true, render: (r: CostByJob) => r.filamentUsedGrams ? `${r.filamentUsedGrams.toFixed(1)}g` : '—' },
    { key: 'printTimeSeconds', header: 'Print Time', sortable: true, render: (r: CostByJob) => formatDuration(r.printTimeSeconds) },
    { key: 'materialCostUsd', header: 'Material', sortable: true, render: (r: CostByJob) => formatCurrency(r.materialCostUsd) },
    { key: 'energyCostUsd', header: 'Energy', sortable: true, render: (r: CostByJob) => formatCurrency(r.energyCostUsd) },
    { key: 'machineTimeCostUsd', header: 'Machine', sortable: true, render: (r: CostByJob) => formatCurrency(r.machineTimeCostUsd) },
    { key: 'laborCostUsd', header: 'Labor', sortable: true, render: (r: CostByJob) => formatCurrency(r.laborCostUsd) },
    { key: 'totalCostUsd', header: 'Total', sortable: true, render: (r: CostByJob) => <span className="font-semibold">{formatCurrency(r.totalCostUsd)}</span> },
    {
      key: 'completedAt', header: 'Completed', sortable: true,
      render: (r: CostByJob) => r.completedAt ? new Date(r.completedAt).toLocaleDateString() : '—',
    },
  ];

  return (
    <Card className="mt-4">
      <Card.Body>
        {loading ? (
          <LoadingState />
        ) : data && data.length > 0 ? (
          <DataTable columns={columns} data={data} getRowKey={(r: CostByJob) => r.jobId} sortable />
        ) : (
          <EmptyState message="No job cost data available" />
        )}
      </Card.Body>
    </Card>
  );
}

function CostByMaterialTab({ data, loading }: { data?: CostByMaterial[]; loading: boolean }) {
  const columns = [
    { key: 'materialType', header: 'Material', sortable: true, render: (r: CostByMaterial) => r.materialType },
    { key: 'jobCount', header: 'Jobs', sortable: true, render: (r: CostByMaterial) => String(r.jobCount) },
    { key: 'totalFilamentUsageGrams', header: 'Weight (kg)', sortable: true, render: (r: CostByMaterial) => (r.totalFilamentUsageGrams / 1000).toFixed(2) },
    { key: 'averageCostPerJobUsd', header: 'Avg/Job', sortable: true, render: (r: CostByMaterial) => formatCurrency(r.averageCostPerJobUsd) },
    { key: 'totalCostUsd', header: 'Total Cost', sortable: true, render: (r: CostByMaterial) => <span className="font-semibold">{formatCurrency(r.totalCostUsd)}</span> },
  ];

  return (
    <Card className="mt-4">
      <Card.Body>
        {loading ? (
          <LoadingState />
        ) : data && data.length > 0 ? (
          <DataTable columns={columns} data={data} getRowKey={(r: CostByMaterial) => r.materialType} sortable />
        ) : (
          <EmptyState message="No material cost data available" />
        )}
      </Card.Body>
    </Card>
  );
}

function LoadingState() {
  return <div className="flex justify-center py-8"><Spinner size="lg" /></div>;
}

function EmptyState({ message }: { message: string }) {
  return <div className="py-8 text-center text-pf-text-secondary">{message}</div>;
}

interface KpiCardProps {
  label: string;
  value: string;
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

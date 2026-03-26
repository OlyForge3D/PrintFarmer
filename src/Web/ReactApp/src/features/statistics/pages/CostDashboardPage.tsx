import React, { useState, useMemo } from 'react';
import { Card } from '@/common/components/ui/Card';
import { DataTable } from '@/common/components/ui/DataTable';
import { Spinner } from '@/common/components/ui/Spinner';
import { TimePeriodFilter } from '@/common/components/ui/TimePeriodFilter';
import { PageTemplate } from '@/common/components/PageTemplate';
import { TrendingUpIcon } from '@/common/components/icons/MdiIcons';
import {
  useCostSummary,
  useCostsByPrinter,
  useCostsByMaterial,
} from '@/common/hooks/useApi';

export const CostDashboardPage: React.FC = () => {
  const [days, setDays] = useState<number | undefined>(30);
  const { data: summary, isLoading: summaryLoading, error: summaryError } = useCostSummary(days);
  const { data: printerCosts, isLoading: printerLoading, error: printerError } = useCostsByPrinter(days);
  const { data: materialCosts, isLoading: materialLoading, error: materialError } = useCostsByMaterial(days);

  const formatCurrency = (value: number) => {
    return new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' }).format(value);
  };

  const materialCostPercentage = useMemo(() => {
    if (!summary || !summary.totalCostUsd) return '0';
    return (((summary.totalMaterialCostUsd ?? 0) / summary.totalCostUsd) * 100).toFixed(1);
  }, [summary]);

  const energyCostPercentage = useMemo(() => {
    if (!summary || !summary.totalCostUsd) return '0';
    return (((summary.totalEnergyCostUsd ?? 0) / summary.totalCostUsd) * 100).toFixed(1);
  }, [summary]);

  const printerTableColumns = [
    {
      key: 'printerName',
      header: 'Printer',
      sortable: true,
      render: (row: { printerName: string }) => row.printerName,
    },
    {
      key: 'jobCount',
      header: 'Jobs',
      sortable: true,
      render: (row: { jobCount: number }) => String(row.jobCount),
    },
    {
      key: 'totalCostUsd',
      header: 'Total Cost',
      sortable: true,
      render: (row: { totalCostUsd: number }) => formatCurrency(row.totalCostUsd),
    },
    {
      key: 'avgCost',
      header: 'Avg Cost/Job',
      sortable: true,
      render: (row: { totalCostUsd: number; jobCount: number }) => {
        const avg = row.jobCount > 0 ? row.totalCostUsd / row.jobCount : 0;
        return formatCurrency(avg);
      },
    },
  ];

  const materialTableColumns = [
    {
      key: 'materialType',
      header: 'Material',
      sortable: true,
      render: (row: { materialType: string }) => row.materialType,
    },
    {
      key: 'jobCount',
      header: 'Jobs',
      sortable: true,
      render: (row: { jobCount: number }) => String(row.jobCount),
    },
    {
      key: 'totalFilamentUsageGrams',
      header: 'Weight (kg)',
      sortable: true,
      render: (row: { totalFilamentUsageGrams: number }) => (row.totalFilamentUsageGrams / 1000).toFixed(2),
    },
    {
      key: 'totalCostUsd',
      header: 'Total Cost',
      sortable: true,
      render: (row: { totalCostUsd: number }) => formatCurrency(row.totalCostUsd),
    },
  ];

  if (summaryError || printerError || materialError) {
    return (
      <PageTemplate title="Cost Analytics" icon={TrendingUpIcon}>
        <div className="p-4 text-pf-error">
          Failed to load cost data: {String(summaryError || printerError || materialError)}
        </div>
      </PageTemplate>
    );
  }

  return (
    <PageTemplate
      title="Cost Analytics"
      subtitle="Track print job costs and analyze spending patterns"
      icon={TrendingUpIcon}
      actions={<TimePeriodFilter value={days} onChange={setDays} />}
    >
      <div className="space-y-6">
        {/* Summary Cards */}
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

        {/* Cost Breakdown by Printer */}
        <Card>
          <Card.Header>
            <h2 className="text-lg font-semibold text-pf-text-primary">Cost by Printer</h2>
          </Card.Header>
          <Card.Body>
            {printerLoading ? (
              <div className="flex justify-center py-8">
                <Spinner size="lg" />
              </div>
            ) : printerCosts && printerCosts.length > 0 ? (
              <DataTable
                columns={printerTableColumns}
                data={printerCosts}
                getRowKey={(row: { printerName: string }) => row.printerName}
                sortable
              />
            ) : (
              <div className="py-8 text-center text-pf-text-secondary">
                No printer cost data available
              </div>
            )}
          </Card.Body>
        </Card>

        {/* Cost Breakdown by Material */}
        <Card>
          <Card.Header>
            <h2 className="text-lg font-semibold text-pf-text-primary">Cost by Material</h2>
          </Card.Header>
          <Card.Body>
            {materialLoading ? (
              <div className="flex justify-center py-8">
                <Spinner size="lg" />
              </div>
            ) : materialCosts && materialCosts.length > 0 ? (
              <DataTable
                columns={materialTableColumns}
                data={materialCosts}
                getRowKey={(row: { materialType: string }) => row.materialType}
                sortable
              />
            ) : (
              <div className="py-8 text-center text-pf-text-secondary">
                No material cost data available
              </div>
            )}
          </Card.Body>
        </Card>
      </div>
    </PageTemplate>
  );
};

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

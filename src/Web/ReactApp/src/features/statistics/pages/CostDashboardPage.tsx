import React, { useMemo } from 'react';
import { Card } from '@/common/components/ui/Card';
import { DataTable } from '@/common/components/ui/DataTable';
import { Spinner } from '@/common/components/ui/Spinner';
import { PageTemplate } from '@/common/components/PageTemplate';
import { TrendingUpIcon } from '@/common/components/icons/MdiIcons';
import {
  useCostSummary,
  useCostsByPrinter,
  useCostsByMaterial,
} from '@/common/hooks/useApi';

export const CostDashboardPage: React.FC = () => {
  const { data: summary, isLoading: summaryLoading, error: summaryError } = useCostSummary();
  const { data: printerCosts, isLoading: printerLoading, error: printerError } = useCostsByPrinter();
  const { data: materialCosts, isLoading: materialLoading, error: materialError } = useCostsByMaterial();

  const formatCurrency = (value: number) => {
    return new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' }).format(value);
  };

  const materialCostPercentage = useMemo(() => {
    if (!summary || !summary.totalCost) return '0';
    return (((summary.totalMaterialCost ?? 0) / summary.totalCost) * 100).toFixed(1);
  }, [summary]);

  const energyCostPercentage = useMemo(() => {
    if (!summary || !summary.totalCost) return '0';
    return (((summary.totalEnergyCost ?? 0) / summary.totalCost) * 100).toFixed(1);
  }, [summary]);

  const printerTableColumns = [
    {
      key: 'printerName',
      label: 'Printer',
      sortable: true,
    },
    {
      key: 'jobCount',
      label: 'Jobs',
      sortable: true,
    },
    {
      key: 'totalCost',
      label: 'Total Cost',
      sortable: true,
      render: (row: { totalCost: number }) => formatCurrency(row.totalCost),
    },
    {
      key: 'avgCost',
      label: 'Avg Cost/Job',
      sortable: true,
      render: (row: { totalCost: number; jobCount: number }) => {
        const avg = row.jobCount > 0 ? row.totalCost / row.jobCount : 0;
        return formatCurrency(avg);
      },
    },
  ];

  const materialTableColumns = [
    {
      key: 'materialType',
      label: 'Material',
      sortable: true,
    },
    {
      key: 'jobCount',
      label: 'Jobs',
      sortable: true,
    },
    {
      key: 'totalWeight',
      label: 'Weight (kg)',
      sortable: true,
      render: (row: { totalWeight: number }) => (row.totalWeight / 1000).toFixed(2),
    },
    {
      key: 'totalCost',
      label: 'Total Cost',
      sortable: true,
      render: (row: { totalCost: number }) => formatCurrency(row.totalCost),
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
    >
      <div className="space-y-6">
        {/* Summary Cards */}
        <div className="grid grid-cols-2 gap-4 md:grid-cols-4">
          <KpiCard
            label="Total Cost"
            value={formatCurrency(summary?.totalCost ?? 0)}
            loading={summaryLoading}
          />
          <KpiCard
            label="Avg Cost/Job"
            value={formatCurrency(summary?.averageCostPerJob ?? 0)}
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

import React from 'react';
import { Card } from '@/common/components/ui/Card';
import {
  ResponsiveContainer,
  BarChart,
  Bar,
  XAxis,
  YAxis,
  Tooltip,
  CartesianGrid,
  Legend,
} from 'recharts';
import { ChartSkeleton } from '@/common/components/skeletons/ChartSkeleton';
import type { PrinterMaterialPerformance } from '../hooks/useCorrelationAnalytics';

interface Props {
  data: PrinterMaterialPerformance[];
  isLoading: boolean;
  error: Error | null;
}

interface GroupedData {
  printerName: string;
  [material: string]: string | number;
}

function groupByPrinter(data: PrinterMaterialPerformance[]): { grouped: GroupedData[]; materials: string[] } {
  const materialsSet = new Set<string>();
  const printerMap = new Map<string, GroupedData>();

  for (const item of data) {
    materialsSet.add(item.material);
    if (!printerMap.has(item.printerName)) {
      printerMap.set(item.printerName, { printerName: item.printerName });
    }
    const entry = printerMap.get(item.printerName)!;
    entry[`${item.material}_rate`] = item.successRate;
    entry[`${item.material}_jobs`] = item.totalJobs;
  }

  return { grouped: Array.from(printerMap.values()), materials: Array.from(materialsSet) };
}

const COLORS = ['#4F8AFA', '#34D399', '#F87171', '#FBBF24', '#A78BFA', '#F472B6', '#60A5FA', '#FB923C'];

export const PrinterMaterialHeatmap: React.FC<Props> = ({ data, isLoading, error }) => {
  const { grouped, materials } = groupByPrinter(data);

  return (
    <Card title="Printer × Material Performance" className="h-96">
      {isLoading ? (
        <ChartSkeleton />
      ) : error ? (
        <div className="text-pf-error-text">Error loading data</div>
      ) : data.length === 0 ? (
        <div className="flex h-full items-center justify-center text-pf-text-secondary">No data available</div>
      ) : (
        <ResponsiveContainer width="100%" height="90%">
          <BarChart data={grouped} margin={{ top: 16, right: 24, left: 0, bottom: 0 }}>
            <CartesianGrid strokeDasharray="3 3" />
            <XAxis dataKey="printerName" tick={{ fontSize: 11 }} />
            <YAxis domain={[0, 100]} tickFormatter={(v: number) => `${v}%`} />
            <Tooltip formatter={(value: number) => [`${value}%`, '']} />
            <Legend />
            {materials.map((mat, idx) => (
              <Bar
                key={mat}
                dataKey={`${mat}_rate`}
                fill={COLORS[idx % COLORS.length]}
                name={mat}
              />
            ))}
          </BarChart>
        </ResponsiveContainer>
      )}
    </Card>
  );
};

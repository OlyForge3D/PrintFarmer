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
import type { PrinterUtilization } from '../hooks/useStatistics';

interface Props {
  data: PrinterUtilization[];
  isLoading: boolean;
  error: Error | null;
}

export const PrinterUtilizationChart: React.FC<Props> = ({ data, isLoading, error }) => (
  <Card title="Printer Utilization" className="h-96">
    {isLoading ? (
      <ChartSkeleton />
    ) : error ? (
      <div className="text-pf-error-text">Error loading utilization data</div>
    ) : data.length === 0 ? (
      <div className="flex h-full items-center justify-center text-pf-text-secondary">No printer data available</div>
    ) : (
      <ResponsiveContainer width="100%" height="90%">
        <BarChart data={data} margin={{ top: 16, right: 24, left: 0, bottom: 0 }}>
          <CartesianGrid strokeDasharray="3 3" />
          <XAxis dataKey="printerName" tick={{ fontSize: 12 }} />
          <YAxis yAxisId="left" allowDecimals={false} />
          <YAxis yAxisId="right" orientation="right" domain={[0, 100]} tickFormatter={(v: number) => `${v}%`} />
          <Tooltip />
          <Legend />
          <Bar yAxisId="left" dataKey="completedJobs" fill="#34D399" name="Completed" />
          <Bar yAxisId="left" dataKey="failedJobs" fill="#F87171" name="Failed" />
          <Bar yAxisId="right" dataKey="successRate" fill="#4F8AFA" name="Success %" />
        </BarChart>
      </ResponsiveContainer>
    )}
  </Card>
);

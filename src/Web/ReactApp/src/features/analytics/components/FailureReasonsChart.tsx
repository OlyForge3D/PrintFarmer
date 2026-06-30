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
} from 'recharts';
import { ChartSkeleton } from '@/common/components/skeletons/ChartSkeleton';
import type { FailureReason } from '../hooks/useCorrelationAnalytics';

interface Props {
  data: FailureReason[];
  isLoading: boolean;
  error: Error | null;
}

export const FailureReasonsChart = React.memo(function FailureReasonsChart({ data, isLoading, error }: Props) {
  return (
    <Card title="Failure Reasons" className="h-96">
    {isLoading ? (
      <ChartSkeleton />
    ) : error ? (
      <div className="text-pf-error-text">Error loading data</div>
    ) : data.length === 0 ? (
      <div className="flex h-full items-center justify-center text-pf-text-secondary">No failure data available</div>
    ) : (
      <ResponsiveContainer width="100%" height="90%">
        <BarChart
          data={data}
          layout="vertical"
          margin={{ top: 16, right: 24, left: 100, bottom: 0 }}
        >
          <CartesianGrid strokeDasharray="3 3" />
          <XAxis type="number" allowDecimals={false} />
          <YAxis type="category" dataKey="reason" tick={{ fontSize: 11 }} width={90} />
          <Tooltip />
          <Bar dataKey="count" fill="#F87171" name="Failures" />
        </BarChart>
      </ResponsiveContainer>
    )}
    </Card>
  );
});

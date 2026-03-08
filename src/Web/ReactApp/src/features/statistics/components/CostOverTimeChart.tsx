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
import type { DailyCost } from '../hooks/useStatistics';

interface Props {
  data: DailyCost[];
  isLoading: boolean;
  error: Error | null;
}

export const CostOverTimeChart: React.FC<Props> = ({ data, isLoading, error }) => (
  <Card title="Cost Over Time" className="h-96">
    {isLoading ? (
      <ChartSkeleton />
    ) : error ? (
      <div className="text-pf-error-text">Error loading cost data</div>
    ) : data.length === 0 ? (
      <div className="flex h-full items-center justify-center text-pf-text-secondary">No cost data available</div>
    ) : (
      <ResponsiveContainer width="100%" height="90%">
        <BarChart data={data} margin={{ top: 16, right: 24, left: 0, bottom: 0 }}>
          <CartesianGrid strokeDasharray="3 3" />
          <XAxis dataKey="date" tick={{ fontSize: 12 }} />
          <YAxis tickFormatter={(v: number) => `$${v}`} />
          <Tooltip formatter={(value: number) => [`$${value.toFixed(2)}`, 'Cost']} />
          <Bar dataKey="cost" fill="#4F8AFA" name="Cost" />
        </BarChart>
      </ResponsiveContainer>
    )}
  </Card>
);

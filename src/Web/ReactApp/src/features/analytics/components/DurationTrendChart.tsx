import React from 'react';
import { Card } from '@/common/components/ui/Card';
import {
  ResponsiveContainer,
  LineChart,
  Line,
  XAxis,
  YAxis,
  Tooltip,
  CartesianGrid,
  Legend,
} from 'recharts';
import { ChartSkeleton } from '@/common/components/skeletons/ChartSkeleton';
import type { DurationTrend } from '../hooks/useCorrelationAnalytics';

interface Props {
  data: DurationTrend[];
  isLoading: boolean;
  error: Error | null;
}

export const DurationTrendChart: React.FC<Props> = ({ data, isLoading, error }) => (
  <Card title="Print Duration Trends" className="h-96">
    {isLoading ? (
      <ChartSkeleton />
    ) : error ? (
      <div className="text-pf-error-text">Error loading data</div>
    ) : data.length === 0 ? (
      <div className="flex h-full items-center justify-center text-pf-text-secondary">No data available</div>
    ) : (
      <ResponsiveContainer width="100%" height="90%">
        <LineChart data={data} margin={{ top: 16, right: 24, left: 0, bottom: 0 }}>
          <CartesianGrid strokeDasharray="3 3" />
          <XAxis dataKey="date" tick={{ fontSize: 12 }} />
          <YAxis tickFormatter={(v: number) => `${Math.round(v)}m`} />
          <Tooltip
            formatter={(value: number, name: string) => [
              `${value.toFixed(1)} min`,
              name,
            ]}
          />
          <Legend />
          <Line type="monotone" dataKey="averageDurationMinutes" stroke="#4F8AFA" name="Average" strokeWidth={2} dot={false} />
          <Line type="monotone" dataKey="maxDurationMinutes" stroke="#F87171" name="Max" strokeDasharray="5 5" dot={false} />
          <Line type="monotone" dataKey="minDurationMinutes" stroke="#34D399" name="Min" strokeDasharray="5 5" dot={false} />
        </LineChart>
      </ResponsiveContainer>
    )}
  </Card>
);

import React from 'react';
import { Card } from '@/common/components/ui/Card';
import {
  ResponsiveContainer,
  AreaChart,
  Area,
  XAxis,
  YAxis,
  Tooltip,
  CartesianGrid,
  Legend,
} from 'recharts';
import type { DailyJobCount } from '../hooks/useStatistics';

interface Props {
  data: DailyJobCount[];
  isLoading: boolean;
  error: Error | null;
}

export const JobsOverTimeChart: React.FC<Props> = ({ data, isLoading, error }) => (
  <Card title="Jobs Over Time" className="h-96">
    {isLoading ? (
      <div className="flex h-full items-center justify-center">Loading...</div>
    ) : error ? (
      <div className="text-pf-error-text">Error loading job data</div>
    ) : data.length === 0 ? (
      <div className="flex h-full items-center justify-center text-pf-text-secondary">No job data available</div>
    ) : (
      <ResponsiveContainer width="100%" height="90%">
        <AreaChart data={data} margin={{ top: 16, right: 24, left: 0, bottom: 0 }}>
          <CartesianGrid strokeDasharray="3 3" />
          <XAxis dataKey="date" tick={{ fontSize: 12 }} />
          <YAxis allowDecimals={false} />
          <Tooltip />
          <Legend />
          <Area type="monotone" dataKey="completed" stackId="1" stroke="#34D399" fill="#34D399" fillOpacity={0.6} name="Completed" />
          <Area type="monotone" dataKey="failed" stackId="1" stroke="#F87171" fill="#F87171" fillOpacity={0.6} name="Failed" />
          <Area type="monotone" dataKey="cancelled" stackId="1" stroke="#FBBF24" fill="#FBBF24" fillOpacity={0.6} name="Cancelled" />
        </AreaChart>
      </ResponsiveContainer>
    )}
  </Card>
);

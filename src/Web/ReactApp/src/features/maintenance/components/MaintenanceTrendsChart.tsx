import React from 'react';
import { useMaintenanceTrends } from '../hooks/useMaintenanceTrends';
import { Card } from '@/common/components/ui/Card';
import { ResponsiveContainer, LineChart, Line, XAxis, YAxis, Tooltip, CartesianGrid, Legend } from 'recharts';

export const MaintenanceTrendsChart: React.FC = () => {
  const { data, isLoading, error } = useMaintenanceTrends();

  return (
    <Card title="Maintenance Trends" className="h-96">
      {isLoading ? (
        <div>Loading...</div>
      ) : error ? (
        <div className="text-pf-error-text">Error loading trends</div>
      ) : (
        <ResponsiveContainer width="100%" height="90%">
          <LineChart data={data} margin={{ top: 16, right: 24, left: 0, bottom: 0 }}>
            <CartesianGrid strokeDasharray="3 3" />
            <XAxis dataKey="date" />
            <YAxis allowDecimals={false} />
            <Tooltip />
            <Legend />
            <Line type="monotone" dataKey="completed" stroke="#4F8AFA" name="Completed" />
            <Line type="monotone" dataKey="overdue" stroke="#F87171" name="Overdue" />
          </LineChart>
        </ResponsiveContainer>
      )}
    </Card>
  );
};

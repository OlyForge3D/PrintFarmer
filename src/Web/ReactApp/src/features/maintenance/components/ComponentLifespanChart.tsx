import React from 'react';
import { useComponentLifespan } from '../hooks/useComponentLifespan';
import { Card } from '@/common/components/ui/Card';
import { ResponsiveContainer, BarChart, Bar, XAxis, YAxis, Tooltip, CartesianGrid, Legend } from 'recharts';

export const ComponentLifespanChart: React.FC = () => {
  const { data, isLoading, error } = useComponentLifespan();

  return (
    <Card title="Component Lifespan" className="h-96">
      {isLoading ? (
        <div>Loading...</div>
      ) : error ? (
        <div className="text-pf-error-text">Error loading lifespan data</div>
      ) : (
        <ResponsiveContainer width="100%" height="90%">
          <BarChart data={data} margin={{ top: 16, right: 24, left: 0, bottom: 0 }}>
            <CartesianGrid strokeDasharray="3 3" />
            <XAxis dataKey="component" />
            <YAxis allowDecimals={false} />
            <Tooltip />
            <Legend />
            <Bar dataKey="averageInterval" fill="#4F8AFA" name="Avg. Interval (days)" />
          </BarChart>
        </ResponsiveContainer>
      )}
    </Card>
  );
};

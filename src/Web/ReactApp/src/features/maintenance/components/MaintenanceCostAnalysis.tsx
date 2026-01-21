import React from 'react';
import { useMaintenanceCostAnalysis } from '../hooks/useMaintenanceCostAnalysis';
import { Card } from '@/common/components/ui/Card';
import { ResponsiveContainer, PieChart, Pie, Cell, Tooltip, Legend } from 'recharts';

const COLORS = ['#4F8AFA', '#F87171', '#34D399', '#FBBF24', '#A78BFA', '#F472B6', '#60A5FA'];

export const MaintenanceCostAnalysis: React.FC = () => {
  const { data, isLoading, error } = useMaintenanceCostAnalysis();

  return (
    <Card title="Maintenance Cost Analysis" className="h-96">
      {isLoading ? (
        <div>Loading...</div>
      ) : error ? (
        <div className="text-pf-error-text">Error loading cost data</div>
      ) : (
        <ResponsiveContainer width="100%" height="90%">
          <PieChart>
            <Pie data={data} dataKey="cost" nameKey="component" cx="50%" cy="50%" outerRadius={100} label>
              {data.map((entry: any, idx: number) => (
                <Cell key={`cell-${idx}`} fill={COLORS[idx % COLORS.length]} />
              ))}
            </Pie>
            <Tooltip />
            <Legend />
          </PieChart>
        </ResponsiveContainer>
      )}
    </Card>
  );
};

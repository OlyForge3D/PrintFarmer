import React from 'react';
import { Card } from '@/common/components/ui/Card';
import {
  ResponsiveContainer,
  PieChart,
  Pie,
  Cell,
  Tooltip,
  Legend,
} from 'recharts';
import { ChartSkeleton } from '@/common/components/skeletons/ChartSkeleton';
import type { FilamentByMaterial } from '../hooks/useStatistics';

const COLORS = ['#4F8AFA', '#34D399', '#F87171', '#FBBF24', '#A78BFA', '#F472B6', '#60A5FA', '#FB923C'];

interface Props {
  data: FilamentByMaterial[];
  isLoading: boolean;
  error: Error | null;
}

export const FilamentByMaterialChart: React.FC<Props> = ({ data, isLoading, error }) => (
  <Card title="Filament Usage by Material" className="h-96">
    {isLoading ? (
      <ChartSkeleton />
    ) : error ? (
      <div className="text-pf-error-text">Error loading filament data</div>
    ) : data.length === 0 ? (
      <div className="flex h-full items-center justify-center text-pf-text-secondary">No filament data available</div>
    ) : (
      <ResponsiveContainer width="100%" height="90%">
        <PieChart>
          <Pie
            data={data}
            dataKey="grams"
            nameKey="material"
            cx="50%"
            cy="50%"
            outerRadius={100}
            label={({ material, grams }: { material: string; grams: number }) =>
              `${material}: ${(grams / 1000).toFixed(2)}kg`
            }
          >
            {data.map((entry, idx) => (
              <Cell key={`cell-${entry.material}`} fill={COLORS[idx % COLORS.length]} />
            ))}
          </Pie>
          <Tooltip formatter={(value: number) => [`${(value / 1000).toFixed(2)} kg`, 'Usage']} />
          <Legend />
        </PieChart>
      </ResponsiveContainer>
    )}
  </Card>
);

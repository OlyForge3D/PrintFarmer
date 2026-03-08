import React from 'react';
import { Card } from '@/common/components/ui/Card';
import {
  ResponsiveContainer,
  ScatterChart,
  Scatter,
  XAxis,
  YAxis,
  Tooltip,
  CartesianGrid,
  Legend,
  ZAxis,
} from 'recharts';
import { ChartSkeleton } from '@/common/components/skeletons/ChartSkeleton';
import type { TemperatureQualityCorrelation } from '../hooks/useCorrelationAnalytics';

interface Props {
  data: TemperatureQualityCorrelation[];
  isLoading: boolean;
  error: Error | null;
}

export const TemperatureScatterPlot: React.FC<Props> = ({ data, isLoading, error }) => {
  const successful = data.filter((d) => d.success);
  const failed = data.filter((d) => !d.success);

  return (
    <Card title="Temperature vs Quality" className="h-96">
      {isLoading ? (
        <ChartSkeleton />
      ) : error ? (
        <div className="text-pf-error-text">Error loading data</div>
      ) : data.length === 0 ? (
        <div className="flex h-full items-center justify-center text-pf-text-secondary">No data available</div>
      ) : (
        <ResponsiveContainer width="100%" height="90%">
          <ScatterChart margin={{ top: 16, right: 24, left: 0, bottom: 0 }}>
            <CartesianGrid strokeDasharray="3 3" />
            <XAxis type="number" dataKey="nozzleTemp" name="Nozzle °C" unit="°C" tick={{ fontSize: 12 }} />
            <YAxis type="number" dataKey="bedTemp" name="Bed °C" unit="°C" />
            <ZAxis type="number" dataKey="durationMinutes" range={[20, 200]} name="Duration" />
            <Tooltip cursor={{ strokeDasharray: '3 3' }} />
            <Legend />
            <Scatter name="Successful" data={successful} fill="#34D399" />
            <Scatter name="Failed" data={failed} fill="#F87171" />
          </ScatterChart>
        </ResponsiveContainer>
      )}
    </Card>
  );
};

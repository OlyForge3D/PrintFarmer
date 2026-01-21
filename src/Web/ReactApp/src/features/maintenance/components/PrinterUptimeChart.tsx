import React from 'react';
import { usePrinterUptime } from '../hooks/usePrinterUptime';
import { Card } from '@/common/components/ui/Card';
import { ResponsiveContainer, AreaChart, Area, XAxis, YAxis, Tooltip, CartesianGrid, Legend } from 'recharts';

export const PrinterUptimeChart: React.FC = () => {
  const { data, isLoading, error } = usePrinterUptime();

  return (
    <Card title="Printer Uptime" className="h-96">
      {isLoading ? (
        <div>Loading...</div>
      ) : error ? (
        <div className="text-pf-error-text">Error loading uptime data</div>
      ) : (
        <ResponsiveContainer width="100%" height="90%">
          <AreaChart data={data} margin={{ top: 16, right: 24, left: 0, bottom: 0 }}>
            <CartesianGrid strokeDasharray="3 3" />
            <XAxis dataKey="date" />
            <YAxis allowDecimals={false} />
            <Tooltip />
            <Legend />
            <Area type="monotone" dataKey="uptime" stroke="#34D399" fill="#A7F3D0" name="Uptime (%)" />
            <Area type="monotone" dataKey="downtime" stroke="#F87171" fill="#FECACA" name="Downtime (%)" />
          </AreaChart>
        </ResponsiveContainer>
      )}
    </Card>
  );
};

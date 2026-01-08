import React from 'react';
import { Card } from '@/common/components/ui/Card';

interface HealthStatisticsProps {
  totalModel3DFiles: number;
  model3DHealthy: number;
  model3DMissing: number;
  model3DCorrupted: number;
  totalGcodeFiles: number;
  gcodeHealthy: number;
  gcodeMissing: number;
  gcodeCorrupted: number;
}

export function HealthStatistics({
  totalModel3DFiles,
  model3DHealthy,
  model3DMissing,
  model3DCorrupted,
  totalGcodeFiles,
  gcodeHealthy,
  gcodeMissing,
  gcodeCorrupted,
}: HealthStatisticsProps) {
  const totalFiles = totalModel3DFiles + totalGcodeFiles;
  const totalHealthy = model3DHealthy + gcodeHealthy;
  const totalMissing = model3DMissing + gcodeMissing;
  const totalCorrupted = model3DCorrupted + gcodeCorrupted;

  const stats = [
    {
      label: 'Total Files',
      value: totalFiles,
      bgColor: 'bg-pf-bg-2',
      textColor: 'text-pf-text-primary',
      borderColor: 'border-pf-border',
    },
    {
      label: 'Healthy',
      value: totalHealthy,
      bgColor: 'bg-pf-bg-2',
      textColor: 'text-pf-success',
      borderColor: 'border-pf-border',
    },
    {
      label: 'Missing',
      value: totalMissing,
      bgColor: 'bg-pf-bg-2',
      textColor: 'text-pf-error-text',
      borderColor: 'border-pf-border',
    },
    {
      label: 'Corrupted',
      value: totalCorrupted,
      bgColor: 'bg-pf-bg-2',
      textColor: 'text-pf-warning-text',
      borderColor: 'border-pf-border',
    },
  ];

  return (
    <Card>
      <Card.Header>
        <h3 className="text-lg font-semibold">File Statistics</h3>
      </Card.Header>
      <Card.Body>
        <div className="space-y-3">
          {stats.map((stat) => (
            <div
              key={stat.label}
              className="bg-pf-bg-2 border border-pf-border rounded-lg p-3 flex justify-between items-center"
            >
              <span className={`${stat.textColor} font-medium`}>{stat.label}</span>
              <span className={`${stat.textColor} text-lg font-bold`}>{stat.value}</span>
            </div>
          ))}
        </div>
        <div className="mt-6 pt-6 border-t border-pf-border space-y-2 text-xs text-pf-text-secondary">
          <div className="flex justify-between">
            <span>Model3D Files:</span>
            <span>{model3DHealthy}/{totalModel3DFiles} healthy</span>
          </div>
          <div className="flex justify-between">
            <span>G-code Files:</span>
            <span>{gcodeHealthy}/{totalGcodeFiles} healthy</span>
          </div>
        </div>
      </Card.Body>
    </Card>
  );
}

import React from 'react';

interface HealthStatisticsProps {
  totalFiles: number;
  healthyFiles: number;
  missingFiles: number;
  corruptedFiles: number;
  inaccessibleFiles: number;
}

export function HealthStatistics({
  totalFiles,
  healthyFiles,
  missingFiles,
  corruptedFiles,
  inaccessibleFiles,
}: HealthStatisticsProps) {
  const stats = [
    {
      label: 'Total Files',
      value: totalFiles,
      bgColor: 'bg-blue-50 dark:bg-blue-900/20',
      textColor: 'text-blue-700 dark:text-blue-200',
      borderColor: 'border-blue-200 dark:border-blue-800',
    },
    {
      label: 'Healthy',
      value: healthyFiles,
      bgColor: 'bg-green-50 dark:bg-green-900/20',
      textColor: 'text-green-700 dark:text-green-200',
      borderColor: 'border-green-200 dark:border-green-800',
    },
    {
      label: 'Missing',
      value: missingFiles,
      bgColor: 'bg-red-50 dark:bg-red-900/20',
      textColor: 'text-red-700 dark:text-red-200',
      borderColor: 'border-red-200 dark:border-red-800',
    },
    {
      label: 'Corrupted',
      value: corruptedFiles,
      bgColor: 'bg-orange-50 dark:bg-orange-900/20',
      textColor: 'text-orange-700 dark:text-orange-200',
      borderColor: 'border-orange-200 dark:border-orange-800',
    },
    {
      label: 'Inaccessible',
      value: inaccessibleFiles,
      bgColor: 'bg-purple-50 dark:bg-purple-900/20',
      textColor: 'text-purple-700 dark:text-purple-200',
      borderColor: 'border-purple-200 dark:border-purple-800',
    },
  ];

  return (
    <div className="bg-pf-surface rounded-lg border border-pf-border p-6">
      <h3 className="text-lg font-semibold text-pf-text mb-4">File Statistics</h3>
      <div className="space-y-3">
        {stats.map((stat) => (
          <div
            key={stat.label}
            className={`${stat.bgColor} border ${stat.borderColor} rounded-lg p-3 flex justify-between items-center`}
          >
            <span className={`${stat.textColor} font-medium`}>{stat.label}</span>
            <span className={`${stat.textColor} text-lg font-bold`}>{stat.value}</span>
          </div>
        ))}
      </div>
    </div>
  );
}

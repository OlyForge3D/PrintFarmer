import React from 'react';

interface HealthGaugeProps {
  percentage: number;
}

export function HealthGauge({ percentage }: HealthGaugeProps) {
  // Clamp percentage between 0 and 100
  const safePercentage = Math.max(0, Math.min(100, percentage));

  // Determine color based on percentage
  const getColor = (value: number): string => {
    if (value >= 95) return '#10b981'; // green
    if (value >= 75) return '#f59e0b'; // amber
    if (value >= 50) return '#ef4444'; // red
    return '#dc2626'; // dark red
  };

  const circumference = 2 * Math.PI * 45; // radius = 45
  const offset = circumference - (safePercentage / 100) * circumference;
  const color = getColor(safePercentage);

  return (
    <div className="flex items-center justify-center">
      <svg width="120" height="120" viewBox="0 0 120 120" className="transform -rotate-90">
        {/* Background circle */}
        <circle
          cx="60"
          cy="60"
          r="45"
          fill="none"
          stroke="currentColor"
          strokeWidth="8"
          className="text-pf-border"
        />
        {/* Progress circle */}
        <circle
          cx="60"
          cy="60"
          r="45"
          fill="none"
          stroke={color}
          strokeWidth="8"
          strokeDasharray={circumference}
          strokeDashoffset={offset}
          strokeLinecap="round"
          className="transition-all duration-500"
        />
      </svg>
    </div>
  );
}

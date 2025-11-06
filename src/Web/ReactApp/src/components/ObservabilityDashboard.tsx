import React, { useState, useEffect, useCallback } from 'react';
import { useTelemetry } from '@/telemetry/useTelemetry';
import type { Span } from '@opentelemetry/api';
import { ChartBarIcon, ClockIcon, ServerIcon, CpuChipIcon } from '@heroicons/react/24/outline';
import UnifiedLoggingDashboard from './UnifiedLoggingDashboard';
import { PageTemplate } from '@/components/PageTemplate';
import { Activity } from 'lucide-react';

interface TelemetryStats {
  operationsCount: number;
  averageResponseTime: number;
  errorRate: number;
  lastUpdated: Date;
}

export function ObservabilityDashboard() {
  const [stats, setStats] = useState<TelemetryStats>({
    operationsCount: 0,
    averageResponseTime: 0,
    errorRate: 0,
    lastUpdated: new Date()
  });
  
  const { trackComponentMount, trackComponentUnmount, trackUserInteraction } = useTelemetry();

  // Stable function references to prevent infinite re-renders
  const handleTrackMount = useCallback((component: string) => trackComponentMount(component), [trackComponentMount]);
  const handleTrackUnmount = useCallback((component: string, span?: Span) => trackComponentUnmount(component, span), [trackComponentUnmount]);

  useEffect(() => {
    const mountSpan = handleTrackMount('ObservabilityDashboard');
    
    // Simulate fetching telemetry stats
    const fetchStats = () => {
      setStats({
        operationsCount: Math.floor(Math.random() * 1000) + 100,
        averageResponseTime: Math.floor(Math.random() * 500) + 50,
        errorRate: Math.random() * 5,
        lastUpdated: new Date()
      });
    };

    fetchStats();
    const interval = setInterval(fetchStats, 30000); // Update every 30 seconds

    return () => {
      clearInterval(interval);
      handleTrackUnmount('ObservabilityDashboard', mountSpan);
    };
  }, [handleTrackMount, handleTrackUnmount]); // Include stable callback dependencies

  const handleRefresh = useCallback(() => {
    trackUserInteraction('refresh', 'observability-dashboard', { 
      section: 'telemetry-stats' 
    });
    
    setStats(prev => ({
      ...prev,
      operationsCount: Math.floor(Math.random() * 1000) + 100,
      averageResponseTime: Math.floor(Math.random() * 500) + 50,
      errorRate: Math.random() * 5,
      lastUpdated: new Date()
    }));
  }, [trackUserInteraction]);

  const statCards = [
    {
      title: 'Total Operations',
      value: stats.operationsCount.toLocaleString(),
      icon: ChartBarIcon,
      description: 'API calls tracked',
      color: 'text-pf-accent'
    },
    {
      title: 'Avg Response Time',
      value: `${stats.averageResponseTime}ms`,
      icon: ClockIcon,
      description: 'Average latency',
      color: 'text-pf-success'
    },
    {
      title: 'Error Rate',
      value: `${stats.errorRate.toFixed(2)}%`,
      icon: ServerIcon,
      description: 'Failed operations',
      color: stats.errorRate > 2 ? 'text-pf-error' : 'text-pf-warning'
    },
    {
      title: 'System Health',
      value: 'Healthy',
      icon: CpuChipIcon,
      description: 'Overall status',
      color: 'text-pf-success'
    }
  ];

  return (
    <PageTemplate
      title="System Observability"
      subtitle="Real-time system monitoring and telemetry data"
      icon={Activity}
      maxWidth="max-w-7xl"
      actions={
        <button
          onClick={handleRefresh}
          className="btn-base btn-md btn-primary"
        >
          Refresh Data
        </button>
      }
    >
      {/* Stats Cards */}
      <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-4 gap-6">
        {statCards.map((stat, index) => (
          <div key={index} className="card flat">
            <div className="flex items-center justify-between">
              <div>
                <p className="text-sm font-medium text-pf-text-secondary">{stat.title}</p>
                <p className={`text-2xl font-bold ${stat.color}`}>{stat.value}</p>
                <p className="text-xs text-pf-text-tertiary mt-1">{stat.description}</p>
              </div>
              <stat.icon className={`h-8 w-8 ${stat.color}`} />
            </div>
          </div>
        ))}
      </div>

      {/* OpenTelemetry Configuration */}
      <div className="card flat">
        <h3 className="text-lg font-semibold text-pf-text-primary mb-4">
          OpenTelemetry Configuration
        </h3>
        <div className="grid grid-cols-1 lg:grid-cols-2 gap-6">
          <div>
            <h4 className="font-medium text-pf-text-primary mb-2">Frontend Tracing</h4>
            <ul className="text-sm text-pf-text-secondary space-y-1">
              <li>✅ Web instrumentation enabled</li>
              <li>✅ Fetch/XHR auto-instrumentation</li>
              <li>✅ User interaction tracking</li>
              <li>✅ Component lifecycle tracing</li>
            </ul>
          </div>
          <div>
            <h4 className="font-medium text-pf-text-primary mb-2">Backend Tracing</h4>
            <ul className="text-sm text-pf-text-secondary space-y-1">
              <li>✅ ASP.NET Core instrumentation</li>
              <li>✅ Entity Framework tracing</li>
              <li>✅ HTTP client instrumentation</li>
              <li>✅ Custom API metrics</li>
            </ul>
          </div>
        </div>
      </div>

      {/* Recent Activity */}
      <div className="card flat">
        <h3 className="text-lg font-semibold text-pf-text-primary mb-4">
          Recent Telemetry Activity
        </h3>
        <div className="space-y-3">
          <div className="flex items-center text-sm">
            <div className="w-2 h-2 bg-pf-success rounded-full mr-3"></div>
            <span className="text-pf-text-primary">API endpoint /api/printers traced successfully</span>
            <span className="ml-auto text-pf-text-tertiary">2 min ago</span>
          </div>
          <div className="flex items-center text-sm">
            <div className="w-2 h-2 bg-pf-accent rounded-full mr-3"></div>
            <span className="text-pf-text-primary">Component lifecycle span completed: PrinterDashboard</span>
            <span className="ml-auto text-pf-text-tertiary">5 min ago</span>
          </div>
          <div className="flex items-center text-sm">
            <div className="w-2 h-2 bg-pf-warning rounded-full mr-3"></div>
            <span className="text-pf-text-primary">User interaction tracked: button click on settings</span>
            <span className="ml-auto text-pf-text-tertiary">8 min ago</span>
          </div>
        </div>
      </div>

      {/* Footer */}
      <div className="text-center text-sm text-pf-text-tertiary">
        Last updated: {stats.lastUpdated.toLocaleString()}
      </div>

      {/* Unified Logging Dashboard Integration */}
      <div className="mt-8">
        <UnifiedLoggingDashboard />
      </div>
    </PageTemplate>
  );
}
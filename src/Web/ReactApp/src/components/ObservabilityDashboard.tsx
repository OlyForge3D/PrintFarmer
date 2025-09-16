import { useState, useEffect } from 'react';
import { useTelemetry } from '../telemetry/useTelemetry';
import { ChartBarIcon, ClockIcon, ServerIcon, CpuChipIcon } from '@heroicons/react/24/outline';

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

  useEffect(() => {
    const mountSpan = trackComponentMount('ObservabilityDashboard');
    
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
      trackComponentUnmount('ObservabilityDashboard', mountSpan);
    };
  }, [trackComponentMount, trackComponentUnmount]);

  const handleRefresh = () => {
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
  };

  const statCards = [
    {
      title: 'Total Operations',
      value: stats.operationsCount.toLocaleString(),
      icon: ChartBarIcon,
      description: 'API calls tracked',
      color: 'text-blue-600'
    },
    {
      title: 'Avg Response Time',
      value: `${stats.averageResponseTime}ms`,
      icon: ClockIcon,
      description: 'Average latency',
      color: 'text-green-600'
    },
    {
      title: 'Error Rate',
      value: `${stats.errorRate.toFixed(2)}%`,
      icon: ServerIcon,
      description: 'Failed operations',
      color: stats.errorRate > 2 ? 'text-red-600' : 'text-yellow-600'
    },
    {
      title: 'System Health',
      value: 'Healthy',
      icon: CpuChipIcon,
      description: 'Overall status',
      color: 'text-green-600'
    }
  ];

  return (
    <div className="space-y-6">
      <div className="flex justify-between items-center">
        <div>
          <h2 className="text-2xl font-bold text-gray-900">System Observability</h2>
          <p className="text-gray-600 mt-1">
            Real-time system monitoring and telemetry data
          </p>
        </div>
        <button
          onClick={handleRefresh}
          className="px-4 py-2 bg-blue-600 text-white rounded-md hover:bg-blue-700 transition-colors"
        >
          Refresh Data
        </button>
      </div>

      {/* Stats Cards */}
      <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-4 gap-6">
        {statCards.map((stat, index) => (
          <div key={index} className="bg-white rounded-lg shadow-sm border p-6">
            <div className="flex items-center justify-between">
              <div>
                <p className="text-sm font-medium text-gray-600">{stat.title}</p>
                <p className={`text-2xl font-bold ${stat.color}`}>{stat.value}</p>
                <p className="text-xs text-gray-500 mt-1">{stat.description}</p>
              </div>
              <stat.icon className={`h-8 w-8 ${stat.color}`} />
            </div>
          </div>
        ))}
      </div>

      {/* OpenTelemetry Configuration */}
      <div className="bg-white rounded-lg shadow-sm border p-6">
        <h3 className="text-lg font-semibold text-gray-900 mb-4">
          OpenTelemetry Configuration
        </h3>
        <div className="grid grid-cols-1 lg:grid-cols-2 gap-6">
          <div>
            <h4 className="font-medium text-gray-700 mb-2">Frontend Tracing</h4>
            <ul className="text-sm text-gray-600 space-y-1">
              <li>✅ Web instrumentation enabled</li>
              <li>✅ Fetch/XHR auto-instrumentation</li>
              <li>✅ User interaction tracking</li>
              <li>✅ Component lifecycle tracing</li>
            </ul>
          </div>
          <div>
            <h4 className="font-medium text-gray-700 mb-2">Backend Tracing</h4>
            <ul className="text-sm text-gray-600 space-y-1">
              <li>✅ ASP.NET Core instrumentation</li>
              <li>✅ Entity Framework tracing</li>
              <li>✅ HTTP client instrumentation</li>
              <li>✅ Custom API metrics</li>
            </ul>
          </div>
        </div>
      </div>

      {/* Recent Activity */}
      <div className="bg-white rounded-lg shadow-sm border p-6">
        <h3 className="text-lg font-semibold text-gray-900 mb-4">
          Recent Telemetry Activity
        </h3>
        <div className="space-y-3">
          <div className="flex items-center text-sm">
            <div className="w-2 h-2 bg-green-500 rounded-full mr-3"></div>
            <span className="text-gray-700">API endpoint /api/printers traced successfully</span>
            <span className="ml-auto text-gray-500">2 min ago</span>
          </div>
          <div className="flex items-center text-sm">
            <div className="w-2 h-2 bg-blue-500 rounded-full mr-3"></div>
            <span className="text-gray-700">Component lifecycle span completed: PrinterDashboard</span>
            <span className="ml-auto text-gray-500">5 min ago</span>
          </div>
          <div className="flex items-center text-sm">
            <div className="w-2 h-2 bg-yellow-500 rounded-full mr-3"></div>
            <span className="text-gray-700">User interaction tracked: button click on settings</span>
            <span className="ml-auto text-gray-500">8 min ago</span>
          </div>
        </div>
      </div>

      {/* Footer */}
      <div className="text-center text-sm text-gray-500">
        Last updated: {stats.lastUpdated.toLocaleString()}
      </div>
    </div>
  );
}
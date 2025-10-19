import { useEffect, useState } from 'react';
import { HubConnection, HubConnectionBuilder } from '@microsoft/signalr';

interface SlicerService {
  id: string;
  name: string;
  slicerType: number;
  version: string;
  host: string;
  capabilitiesJson: string;
  maxConcurrentJobs: number;
  status: string;
  lastSeen: string;
  createdAt: string;
}

const slicerTypeNames: Record<number, string> = {
  0: 'PrusaSlicer',
  1: 'OrcaSlicer',
  2: 'Cura',
  3: 'SuperSlicer'
};

export default function SlicerRegistryPage() {
  const [services, setServices] = useState<SlicerService[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [connection, setConnection] = useState<HubConnection | null>(null);

  useEffect(() => {
    loadServices();
    setupSignalR();
    
    // Cleanup on unmount
    return () => {
      if (connection) {
        connection.stop();
      }
    };
  }, []);

  const setupSignalR = async () => {
    const hubConnection = new HubConnectionBuilder()
      .withUrl('/hubs/slicer-registry')
      .withAutomaticReconnect()
      .build();

    hubConnection.on('SlicerRegistered', (data: any) => {
      console.log('SlicerRegistered:', data);
      setServices(prev => [...prev, {
        id: data.id,
        name: data.name,
        slicerType: data.slicerType,
        version: data.version || 'unknown',
        host: data.host || '',
        capabilitiesJson: data.capabilitiesJson || '[]',
        maxConcurrentJobs: data.maxConcurrentJobs || 1,
        status: data.status || 'Online',
        lastSeen: data.lastSeen || new Date().toISOString(),
        createdAt: new Date().toISOString()
      }]);
    });

    hubConnection.on('SlicerHeartbeat', (data: any) => {
      console.log('SlicerHeartbeat:', data);
      setServices(prev => prev.map(s => 
        s.id === data.id 
          ? { ...s, status: data.status, lastSeen: data.lastSeen }
          : s
      ));
    });

    hubConnection.on('SlicerDeregistered', (data: any) => {
      console.log('SlicerDeregistered:', data);
      setServices(prev => prev.filter(s => s.id !== data.id));
    });

    try {
      await hubConnection.start();
      console.log('Connected to SlicerHub');
      setConnection(hubConnection);
    } catch (err) {
      console.error('SignalR connection error:', err);
    }
  };

  const loadServices = async () => {
    try {
      setError(null);
      const response = await fetch('/api/slicers');
      if (!response.ok) {
        throw new Error(`Failed to load slicer services: ${response.statusText}`);
      }
      const data = await response.json();
      setServices(data);
      setLoading(false);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to load slicer services');
      setLoading(false);
    }
  };

  const getStatusColor = (status: string): string => {
    switch (status?.toLowerCase()) {
      case 'online': return 'bg-green-500';
      case 'offline': return 'bg-gray-500';
      case 'busy': return 'bg-yellow-500';
      case 'error': return 'bg-red-500';
      case 'draining': return 'bg-blue-500';
      default: return 'bg-gray-400';
    }
  };

  const formatLastSeen = (lastSeen: string): string => {
    const date = new Date(lastSeen);
    const now = new Date();
    const diffMs = now.getTime() - date.getTime();
    const diffSecs = Math.floor(diffMs / 1000);
    
    if (diffSecs < 60) return `${diffSecs}s ago`;
    if (diffSecs < 3600) return `${Math.floor(diffSecs / 60)}m ago`;
    if (diffSecs < 86400) return `${Math.floor(diffSecs / 3600)}h ago`;
    return `${Math.floor(diffSecs / 86400)}d ago`;
  };

  const parseCapabilities = (json: string): string[] => {
    try {
      const parsed = JSON.parse(json);
      if (parsed.capabilities && Array.isArray(parsed.capabilities)) {
        return parsed.capabilities;
      }
      return [];
    } catch {
      return [];
    }
  };

  if (loading) {
    return (
      <div className="flex items-center justify-center min-h-screen">
        <div className="text-center">
          <div className="animate-spin rounded-full h-12 w-12 border-b-2 border-blue-600 mx-auto mb-4"></div>
          <p className="text-gray-600">Loading slicer services...</p>
        </div>
      </div>
    );
  }

  if (error) {
    return (
      <div className="p-6">
        <div className="bg-red-50 border border-red-200 rounded-lg p-4">
          <h3 className="text-red-800 font-semibold mb-2">Error Loading Services</h3>
          <p className="text-red-600">{error}</p>
          <button
            onClick={loadServices}
            className="mt-4 px-4 py-2 bg-red-600 text-white rounded hover:bg-red-700"
          >
            Retry
          </button>
        </div>
      </div>
    );
  }

  return (
    <div className="p-6">
      <div className="mb-6">
        <h1 className="text-3xl font-bold text-gray-900 mb-2">Slicer Worker Registry</h1>
        <p className="text-gray-600">
          Manage and monitor registered slicer workers. Workers auto-register on startup and send periodic heartbeats.
        </p>
        {connection?.state === 'Connected' && (
          <div className="mt-2 flex items-center text-sm text-green-600">
            <span className="inline-block w-2 h-2 bg-green-500 rounded-full mr-2 animate-pulse"></span>
            Real-time updates active
          </div>
        )}
      </div>

      {services.length === 0 ? (
        <div className="bg-gray-50 border border-gray-200 rounded-lg p-8 text-center">
          <svg className="mx-auto h-12 w-12 text-gray-400 mb-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 12h6m-6 4h6m2 5H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z" />
          </svg>
          <h3 className="text-lg font-medium text-gray-900 mb-2">No Registered Workers</h3>
          <p className="text-gray-600">
            Workers will appear here when they register with the API. Check worker configuration and network connectivity.
          </p>
        </div>
      ) : (
        <div className="grid gap-6 md:grid-cols-2 lg:grid-cols-3">
          {services.map((service) => (
            <div key={service.id} className="bg-white rounded-lg shadow-md border border-gray-200 p-6 hover:shadow-lg transition-shadow">
              {/* Header */}
              <div className="flex items-start justify-between mb-4">
                <div className="flex-1">
                  <h3 className="text-lg font-semibold text-gray-900 mb-1">{service.name}</h3>
                  <p className="text-sm text-gray-600">{slicerTypeNames[service.slicerType] || 'Unknown'}</p>
                </div>
                <div className="flex items-center gap-2">
                  <span className={`inline-block w-3 h-3 rounded-full ${getStatusColor(service.status)}`}></span>
                  <span className="text-sm font-medium text-gray-700">{service.status}</span>
                </div>
              </div>

              {/* Details */}
              <div className="space-y-2 mb-4">
                <div className="flex justify-between text-sm">
                  <span className="text-gray-600">Version:</span>
                  <span className="font-medium text-gray-900">{service.version}</span>
                </div>
                <div className="flex justify-between text-sm">
                  <span className="text-gray-600">Max Jobs:</span>
                  <span className="font-medium text-gray-900">{service.maxConcurrentJobs}</span>
                </div>
                <div className="flex justify-between text-sm">
                  <span className="text-gray-600">Last Seen:</span>
                  <span className="font-medium text-gray-900">{formatLastSeen(service.lastSeen)}</span>
                </div>
              </div>

              {/* Capabilities */}
              {parseCapabilities(service.capabilitiesJson).length > 0 && (
                <div className="mb-4">
                  <h4 className="text-xs font-semibold text-gray-700 uppercase mb-2">Capabilities</h4>
                  <div className="flex flex-wrap gap-2">
                    {parseCapabilities(service.capabilitiesJson).map((cap, idx) => (
                      <span
                        key={idx}
                        className="inline-block px-2 py-1 text-xs font-medium bg-blue-100 text-blue-800 rounded"
                      >
                        {cap}
                      </span>
                    ))}
                  </div>
                </div>
              )}

              {/* Host */}
              <div className="text-xs text-gray-500 truncate" title={service.host}>
                {service.host}
              </div>
            </div>
          ))}
        </div>
      )}

      {/* Stats Footer */}
      <div className="mt-8 bg-gray-50 rounded-lg p-4 border border-gray-200">
        <div className="grid grid-cols-2 md:grid-cols-4 gap-4 text-center">
          <div>
            <div className="text-2xl font-bold text-gray-900">{services.length}</div>
            <div className="text-sm text-gray-600">Total Services</div>
          </div>
          <div>
            <div className="text-2xl font-bold text-green-600">{services.filter(s => s.status === 'Online').length}</div>
            <div className="text-sm text-gray-600">Online</div>
          </div>
          <div>
            <div className="text-2xl font-bold text-gray-600">{services.filter(s => s.status === 'Offline').length}</div>
            <div className="text-sm text-gray-600">Offline</div>
          </div>
          <div>
            <div className="text-2xl font-bold text-blue-600">
              {services.reduce((sum, s) => sum + s.maxConcurrentJobs, 0)}
            </div>
            <div className="text-sm text-gray-600">Total Capacity</div>
          </div>
        </div>
      </div>
    </div>
  );
}

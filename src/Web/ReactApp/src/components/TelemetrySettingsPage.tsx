if (!window.PrintFarmerDebug) {
  window.PrintFarmerDebug = {};
}
import { useState, useEffect } from 'react';
import { useTelemetry } from '../telemetry/useTelemetry';
import { isTelemetryInitialized } from '../telemetry/config';
import UnifiedLoggingDashboard from './UnifiedLoggingDashboard';
import { 
  CogIcon, 
  EyeIcon, 
  EyeSlashIcon,
  CheckCircleIcon,
  ExclamationTriangleIcon 
} from '@heroicons/react/24/outline';

interface TelemetrySettings {
  enabled: boolean;
  consoleLogging: boolean;
  otlpEndpoint: string;
  samplingRate: number;
  trackUserInteractions: boolean;
  trackApiCalls: boolean;
  trackComponentLifecycle: boolean;
}

export function TelemetrySettingsPage() {
  const [settings, setSettings] = useState<TelemetrySettings>({
    enabled: true,
    consoleLogging: import.meta.env.DEV === true,
    otlpEndpoint: import.meta.env.VITE_OTEL_EXPORTER_OTLP_ENDPOINT || '',
    samplingRate: 1.0,
    trackUserInteractions: true,
    trackApiCalls: true,
    trackComponentLifecycle: true
  });

  const [showEndpoint, setShowEndpoint] = useState(false);
  const { trackComponentMount, trackComponentUnmount, trackUserInteraction } = useTelemetry();

  useEffect(() => {
    const mountSpan = trackComponentMount('TelemetrySettingsPage');
    
    return () => {
      trackComponentUnmount('TelemetrySettingsPage', mountSpan);
    };
  }, [trackComponentMount, trackComponentUnmount]);

  const handleSettingChange = (key: keyof TelemetrySettings, value: boolean | string | number) => {
    setSettings(prev => ({
      ...prev,
      [key]: value
    }));

    trackUserInteraction('setting_change', `telemetry-${key}`, { 
      newValue: value,
      settingType: typeof value
    });
  };

  const handleSave = () => {
    trackUserInteraction('save', 'telemetry-settings', { 
      settingsCount: Object.keys(settings).length 
    });
    
    // In a real application, these settings would be persisted
    if (typeof window !== 'undefined' && (window as unknown as { PrintFarmerDebug?: Record<string, unknown> }).PrintFarmerDebug?.telemetrySettingsPage) {
      console.log('[PrintFarmer] TelemetrySettingsPage: Telemetry settings saved:', settings);
    }
  };

  const telemetryStatus = isTelemetryInitialized();

  return (
    <div className="max-w-4xl mx-auto space-y-6">
      <div className="flex items-center space-x-3">
        <CogIcon className="h-8 w-8 text-gray-700" />
        <div>
          <h1 className="text-2xl font-bold text-gray-900">OpenTelemetry Settings</h1>
          <p className="text-gray-600">Configure system observability and tracing options</p>
        </div>
      </div>

      {/* Status Card */}
      <div className="bg-white rounded-lg shadow-sm border p-6">
        <div className="flex items-center justify-between mb-4">
          <h2 className="text-lg font-semibold text-gray-900">Telemetry Status</h2>
          <div className={`flex items-center space-x-2 ${telemetryStatus ? 'text-green-600' : 'text-red-600'}`}>
            {telemetryStatus ? (
              <CheckCircleIcon className="h-5 w-5" />
            ) : (
              <ExclamationTriangleIcon className="h-5 w-5" />
            )}
            <span className="text-sm font-medium">
              {telemetryStatus ? 'Active' : 'Inactive'}
            </span>
          </div>
        </div>
        
        <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
          <div>
            <p className="text-sm text-gray-600">Service Name</p>
            <p className="font-mono text-sm">PrintFarmer.Frontend</p>
          </div>
          <div>
            <p className="text-sm text-gray-600">Environment</p>
            <p className="font-mono text-sm">{import.meta.env.MODE}</p>
          </div>
        </div>
      </div>

      {/* Configuration */}
      <div className="bg-white rounded-lg shadow-sm border p-6">
        <h2 className="text-lg font-semibold text-gray-900 mb-6">Configuration</h2>
        
        <div className="space-y-6">
          {/* Enable Telemetry */}
          <div className="flex items-center justify-between">
            <div>
              <label htmlFor="enable-telemetry" className="text-sm font-medium text-gray-700">Enable Telemetry</label>
              <p className="text-sm text-gray-500">Master switch for all telemetry collection</p>
            </div>
            <input
              id="enable-telemetry"
              type="checkbox"
              checked={settings.enabled}
              onChange={(e) => handleSettingChange('enabled', e.target.checked)}
              className="h-4 w-4 text-blue-600 focus:ring-blue-500 border-gray-300 rounded"
            />
          </div>

          {/* Console Logging */}
          <div className="flex items-center justify-between">
            <div>
              <label htmlFor="console-logging" className="text-sm font-medium text-gray-700">Console Logging</label>
              <p className="text-sm text-gray-500">Output traces to browser console</p>
            </div>
            <input
              id="console-logging"
              type="checkbox"
              checked={settings.consoleLogging}
              onChange={(e) => handleSettingChange('consoleLogging', e.target.checked)}
              className="h-4 w-4 text-blue-600 focus:ring-blue-500 border-gray-300 rounded"
            />
          </div>

          {/* OTLP Endpoint */}
          <div>
            <label htmlFor="otlp-endpoint" className="block text-sm font-medium text-gray-700 mb-2">
              OTLP Endpoint
            </label>
            <div className="flex space-x-2">
              <input
                id="otlp-endpoint"
                type={showEndpoint ? 'text' : 'password'}
                value={settings.otlpEndpoint}
                onChange={(e) => handleSettingChange('otlpEndpoint', e.target.value)}
                placeholder="https://otel-collector.example.com/v1/traces"
                className="flex-1 px-3 py-2 border border-gray-300 rounded-md focus:outline-none focus:ring-blue-500 focus:border-blue-500"
              />
              <button
                type="button"
                onClick={() => setShowEndpoint(!showEndpoint)}
                className="px-3 py-2 border border-gray-300 rounded-md hover:bg-gray-50"
                aria-label={showEndpoint ? 'Hide endpoint' : 'Show endpoint'}
              >
                {showEndpoint ? (
                  <EyeSlashIcon className="h-4 w-4" />
                ) : (
                  <EyeIcon className="h-4 w-4" />
                )}
              </button>
            </div>
            <p className="text-sm text-gray-500 mt-1">
              External collector endpoint for trace export
            </p>
          </div>

          {/* Sampling Rate */}
          <div>
            <label htmlFor="sampling-rate" className="block text-sm font-medium text-gray-700 mb-2">
              Sampling Rate
            </label>
            <input
              id="sampling-rate"
              type="range"
              min="0"
              max="1"
              step="0.1"
              value={settings.samplingRate}
              onChange={(e) => handleSettingChange('samplingRate', parseFloat(e.target.value))}
              className="w-full h-2 bg-gray-200 rounded-lg appearance-none cursor-pointer"
            />
            <div className="flex justify-between text-xs text-gray-500 mt-1">
              <span>0% (Disabled)</span>
              <span className="font-medium">{(settings.samplingRate * 100).toFixed(0)}%</span>
              <span>100% (All traces)</span>
            </div>
          </div>
        </div>
      </div>

      {/* Instrumentation Options */}
      <div className="bg-white rounded-lg shadow-sm border p-6">
        <h2 className="text-lg font-semibold text-gray-900 mb-6">Instrumentation</h2>
        
        <div className="space-y-4">
          <div className="flex items-center justify-between">
            <div>
              <label htmlFor="track-interactions" className="text-sm font-medium text-gray-700">Track User Interactions</label>
              <p className="text-sm text-gray-500">Monitor clicks, form submissions, etc.</p>
            </div>
            <input
              id="track-interactions"
              type="checkbox"
              checked={settings.trackUserInteractions}
              onChange={(e) => handleSettingChange('trackUserInteractions', e.target.checked)}
              className="h-4 w-4 text-blue-600 focus:ring-blue-500 border-gray-300 rounded"
            />
          </div>

          <div className="flex items-center justify-between">
            <div>
              <label htmlFor="track-api-calls" className="text-sm font-medium text-gray-700">Track API Calls</label>
              <p className="text-sm text-gray-500">Monitor HTTP requests and responses</p>
            </div>
            <input
              id="track-api-calls"
              type="checkbox"
              checked={settings.trackApiCalls}
              onChange={(e) => handleSettingChange('trackApiCalls', e.target.checked)}
              className="h-4 w-4 text-blue-600 focus:ring-blue-500 border-gray-300 rounded"
            />
          </div>

          <div className="flex items-center justify-between">
            <div>
              <label htmlFor="track-components" className="text-sm font-medium text-gray-700">Track Component Lifecycle</label>
              <p className="text-sm text-gray-500">Monitor React component mount/unmount</p>
            </div>
            <input
              id="track-components"
              type="checkbox"
              checked={settings.trackComponentLifecycle}
              onChange={(e) => handleSettingChange('trackComponentLifecycle', e.target.checked)}
              className="h-4 w-4 text-blue-600 focus:ring-blue-500 border-gray-300 rounded"
            />
          </div>
        </div>
      </div>

      {/* Save Button */}
      <div className="flex justify-end">
        <button
          onClick={handleSave}
          className="px-6 py-2 bg-blue-600 text-white rounded-md hover:bg-blue-700 transition-colors"
        >
          Save Settings
        </button>
      </div>

      {/* Unified Logging Dashboard */}
      <div className="mt-8">
        <UnifiedLoggingDashboard />
      </div>
    </div>
  );
}
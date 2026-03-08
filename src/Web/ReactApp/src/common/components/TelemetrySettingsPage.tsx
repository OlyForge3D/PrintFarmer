if (!window.PrintFarmerDebug) {
  window.PrintFarmerDebug = {};
}
import { useState, useEffect } from 'react';
import { useTelemetry } from '../telemetry/useTelemetry';
import { isTelemetryInitialized } from '../telemetry/config';
import UnifiedLoggingDashboard from './UnifiedLoggingDashboard';
import { Button, Checkbox, Input } from '@/common/components/ui';
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
        <CogIcon className="h-8 w-8 text-pf-text-primary" />
        <div>
          <h1 className="text-2xl font-bold text-pf-text-primary">OpenTelemetry Settings</h1>
          <p className="text-pf-text-secondary">Configure system observability and tracing options</p>
        </div>
      </div>

      {/* Status Card */}
      <div className="bg-pf-bg-0 rounded-lg shadow-xs border p-6">
        <div className="flex items-center justify-between mb-4">
          <h2 className="text-lg font-semibold text-pf-text-primary">Telemetry Status</h2>
          <div className={`flex items-center space-x-2 ${telemetryStatus ? 'text-pf-success' : 'text-pf-error'}`}>
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
            <p className="text-sm text-pf-text-secondary">Service Name</p>
            <p className="font-mono text-sm">PrintFarmer.Frontend</p>
          </div>
          <div>
            <p className="text-sm text-pf-text-secondary">Environment</p>
            <p className="font-mono text-sm">{import.meta.env.MODE}</p>
          </div>
        </div>
      </div>

      {/* Configuration */}
      <div className="bg-pf-bg-0 rounded-lg shadow-xs border p-6">
        <h2 className="text-lg font-semibold text-pf-text-primary mb-6">Configuration</h2>
        
        <div className="space-y-6">
          {/* Enable Telemetry */}
          <div className="flex items-center justify-between">
            <div>
              <label htmlFor="enable-telemetry" className="text-sm font-medium text-pf-text-primary">Enable Telemetry</label>
              <p className="text-sm text-pf-text-secondary">Master switch for all telemetry collection</p>
            </div>
            <Checkbox
              id="enable-telemetry"
              checked={settings.enabled}
              onChange={(e) => handleSettingChange('enabled', e.target.checked)}
            />
          </div>

          {/* Console Logging */}
          <div className="flex items-center justify-between">
            <div>
              <label htmlFor="console-logging" className="text-sm font-medium text-pf-text-primary">Console Logging</label>
              <p className="text-sm text-pf-text-secondary">Output traces to browser console</p>
            </div>
            <Checkbox
              id="console-logging"
              checked={settings.consoleLogging}
              onChange={(e) => handleSettingChange('consoleLogging', e.target.checked)}
            />
          </div>

          {/* OTLP Endpoint */}
          <div>
            <label htmlFor="otlp-endpoint" className="block text-sm font-medium text-pf-text-primary mb-2">
              OTLP Endpoint
            </label>
            <div className="flex space-x-2">
              <Input
                id="otlp-endpoint"
                type={showEndpoint ? 'text' : 'password'}
                value={settings.otlpEndpoint}
                onChange={(e) => handleSettingChange('otlpEndpoint', e.target.value)}
                placeholder="https://otel-collector.example.com/v1/traces"
              />
              <Button
                type="button"
                onClick={() => setShowEndpoint(!showEndpoint)}
                variant="secondary"
                size="sm"
                aria-label={showEndpoint ? 'Hide endpoint' : 'Show endpoint'}
              >
                {showEndpoint ? (
                  <EyeSlashIcon className="h-4 w-4" />
                ) : (
                  <EyeIcon className="h-4 w-4" />
                )}
              </Button>
            </div>
            <p className="text-sm text-pf-text-secondary mt-1">
              External collector endpoint for trace export
            </p>
          </div>

          {/* Sampling Rate */}
          <div>
            <label htmlFor="sampling-rate" className="block text-sm font-medium text-pf-text-primary mb-2">
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
              className="w-full h-2 bg-pf-bg-2 rounded-lg appearance-none cursor-pointer"
            />
            <div className="flex justify-between text-xs text-pf-text-secondary mt-1">
              <span>0% (Disabled)</span>
              <span className="font-medium">{(settings.samplingRate * 100).toFixed(0)}%</span>
              <span>100% (All traces)</span>
            </div>
          </div>
        </div>
      </div>

      {/* Instrumentation Options */}
      <div className="bg-pf-bg-0 rounded-lg shadow-xs border p-6">
        <h2 className="text-lg font-semibold text-pf-text-primary mb-6">Instrumentation</h2>
        
        <div className="space-y-4">
          <div className="flex items-center justify-between">
            <div>
              <label htmlFor="track-interactions" className="text-sm font-medium text-pf-text-primary">Track User Interactions</label>
              <p className="text-sm text-pf-text-secondary">Monitor clicks, form submissions, etc.</p>
            </div>
            <Checkbox
              id="track-interactions"
              checked={settings.trackUserInteractions}
              onChange={(e) => handleSettingChange('trackUserInteractions', e.target.checked)}
            />
          </div>

          <div className="flex items-center justify-between">
            <div>
              <label htmlFor="track-api-calls" className="text-sm font-medium text-pf-text-primary">Track API Calls</label>
              <p className="text-sm text-pf-text-secondary">Monitor HTTP requests and responses</p>
            </div>
            <Checkbox
              id="track-api-calls"
              checked={settings.trackApiCalls}
              onChange={(e) => handleSettingChange('trackApiCalls', e.target.checked)}
            />
          </div>

          <div className="flex items-center justify-between">
            <div>
              <label htmlFor="track-components" className="text-sm font-medium text-pf-text-primary">Track Component Lifecycle</label>
              <p className="text-sm text-pf-text-secondary">Monitor React component mount/unmount</p>
            </div>
            <Checkbox
              id="track-components"
              checked={settings.trackComponentLifecycle}
              onChange={(e) => handleSettingChange('trackComponentLifecycle', e.target.checked)}
            />
          </div>
        </div>
      </div>

      {/* Save Button */}
      <div className="flex justify-end">
        <Button
          type="button"
          onClick={handleSave}
          variant="primary"
          className="flex items-center justify-center"
        >
          Save Settings
        </Button>
      </div>

      {/* Unified Logging Dashboard */}
      <div className="mt-8">
        <UnifiedLoggingDashboard />
      </div>
    </div>
  );
}
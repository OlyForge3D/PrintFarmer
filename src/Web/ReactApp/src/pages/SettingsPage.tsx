import { useState, useEffect } from 'react';
import { apiClient } from '@/services/api';
import { Save, TestTube, Plus, X, ExternalLink, RefreshCw } from 'lucide-react';
import type { FilamentPresets, TempTargets } from '@/types/api';

interface NetworkRange {
  cidr: string;
}

interface SettingsData {
  spoolmanBaseUrl?: string;
  networkRanges?: string[];
  discoveryTimeout?: number;
  maxConcurrentScans?: number;
  scanPorts?: number[];
  filamentPresets?: FilamentPresets;
}

export function SettingsPage() {
  const [spoolmanBase, setSpoolmanBase] = useState('');
  const [networkRanges, setNetworkRanges] = useState<NetworkRange[]>([]);
  const [discoveryTimeout, setDiscoveryTimeout] = useState(5000);
  const [maxConcurrentScans, setMaxConcurrentScans] = useState(20);
  const [scanPorts, setScanPorts] = useState<number[]>([80, 7125]);
  const [filamentPresets, setFilamentPresets] = useState<FilamentPresets>({
    abs: { hotend: 250, bed: 100 },
    asa: { hotend: 250, bed: 100 },
    pla: { hotend: 210, bed: 60 },
    pc: { hotend: 280, bed: 110 },
    pctg: { hotend: 270, bed: 80 },
    petg: { hotend: 240, bed: 80 }
  });
  
  const [testing, setTesting] = useState(false);
  const [testOk, setTestOk] = useState<boolean | null>(null);
  const [testMessage, setTestMessage] = useState('');
  const [loading, setLoading] = useState(true);
  const [loadingDynamic, setLoadingDynamic] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    loadSettings();
  }, []);

  const loadSettings = async () => {
    try {
      setLoading(true);
      // Load filament presets (this endpoint exists)
      const presets = await apiClient.getFilamentPresets();
      setFilamentPresets(presets);
      
      // For other settings, we might need to implement API endpoints
      // For now, use default values or localStorage
      const savedSpoolman = localStorage.getItem('spoolman-base-url') || '';
      setSpoolmanBase(savedSpoolman);
      
      const savedRanges = localStorage.getItem('network-ranges');
      if (savedRanges) {
        setNetworkRanges(JSON.parse(savedRanges));
      } else {
        setNetworkRanges([]);
      }
      
      setError(null);
    } catch (err) {
      setError('Failed to load settings');
      console.error('Error loading settings:', err);
    } finally {
      setLoading(false);
    }
  };

  const normalizedUrl = spoolmanBase.trim().replace(/\/$/, '');

  const testSpoolman = async () => {
    if (!normalizedUrl) return;
    
    setTesting(true);
    setTestOk(null);
    setTestMessage('');
    
    try {
      // Test Spoolman connection by trying to fetch spools
      const response = await fetch(`${normalizedUrl}/api/v1/spool`, {
        method: 'HEAD',
        mode: 'cors'
      });
      
      if (response.ok) {
        setTestOk(true);
        setTestMessage('Connection successful!');
      } else {
        setTestOk(false);
        setTestMessage(`HTTP ${response.status}: ${response.statusText}`);
      }
    } catch (err) {
      setTestOk(false);
      setTestMessage(`Connection failed: ${err instanceof Error ? err.message : 'Unknown error'}`);
    } finally {
      setTesting(false);
    }
  };

  const saveSpoolman = async () => {
    try {
      localStorage.setItem('spoolman-base-url', normalizedUrl);
      // TODO: Implement API endpoint to save Spoolman settings server-side
      setError(null);
    } catch (err) {
      setError('Failed to save Spoolman settings');
      console.error('Error saving Spoolman settings:', err);
    }
  };

  const saveFilamentPresets = async () => {
    try {
      await apiClient.saveFilamentPresets(filamentPresets);
      setError(null);
    } catch (err) {
      setError('Failed to save filament presets');
      console.error('Error saving filament presets:', err);
    }
  };

  const addNetworkRange = () => {
    setNetworkRanges([...networkRanges, { cidr: '' }]);
  };

  const removeNetworkRange = (index: number) => {
    setNetworkRanges(networkRanges.filter((_, i) => i !== index));
  };

  const updateNetworkRange = (index: number, cidr: string) => {
    const updated = networkRanges.map((range, i) => i === index ? { cidr } : range);
    setNetworkRanges(updated);
  };

  const addScanPort = () => {
    setScanPorts([...scanPorts, 80]);
  };

  const removeScanPort = (index: number) => {
    setScanPorts(scanPorts.filter((_, i) => i !== index));
  };

  const updateScanPort = (index: number, port: number) => {
    const updated = scanPorts.map((p, i) => i === index ? port : p);
    setScanPorts(updated);
  };

  const saveNetworkSettings = () => {
    try {
      localStorage.setItem('network-ranges', JSON.stringify(networkRanges));
      localStorage.setItem('discovery-timeout', discoveryTimeout.toString());
      localStorage.setItem('max-concurrent-scans', maxConcurrentScans.toString());
      localStorage.setItem('scan-ports', JSON.stringify(scanPorts));
      // TODO: Implement API endpoints to save these server-side
      setError(null);
    } catch (err) {
      setError('Failed to save network settings');
      console.error('Error saving network settings:', err);
    }
  };

  const autoDetectNetworks = async () => {
    setLoadingDynamic(true);
    try {
      // TODO: Implement API endpoint to auto-detect network ranges
      // For now, add some common default ranges
      const defaultRanges = [
        { cidr: '192.168.1.0/24' },
        { cidr: '192.168.0.0/24' },
        { cidr: '10.0.0.0/24' }
      ];
      setNetworkRanges(defaultRanges);
    } catch (err) {
      setError('Failed to auto-detect networks');
      console.error('Error auto-detecting networks:', err);
    } finally {
      setLoadingDynamic(false);
    }
  };

  if (loading) {
    return (
      <div className="flex items-center justify-center h-64">
        <div className="text-pf-text-secondary">Loading settings...</div>
      </div>
    );
  }

  return (
    <div className="space-y-6">
      <div className="flex justify-between items-center">
        <h1 className="text-3xl font-bold text-pf-text-primary font-bebas uppercase">Settings</h1>
      </div>

      {error && (
        <div className="bg-red-900/50 border border-red-700 text-red-100 px-4 py-3 rounded">
          {error}
        </div>
      )}

      {/* Spoolman Configuration */}
      <div className="bg-pf-bg-1 border border-pf-border rounded-xl p-6">
        <h2 className="text-xl font-semibold text-pf-text-primary mb-4">Spoolman Integration</h2>
        
        <div className="space-y-4">
          <div>
            <label className="block text-sm font-medium text-pf-text-primary mb-2">
              Spoolman Base URL
            </label>
            <input
              type="url"
              value={spoolmanBase}
              onChange={(e) => setSpoolmanBase(e.target.value)}
              placeholder="http://spoolman:7912"
              className="w-full px-3 py-2 bg-pf-bg-0 border border-pf-border rounded text-pf-text-primary placeholder-pf-text-secondary"
            />
            <p className="text-sm text-pf-text-secondary mt-1">
              Base URL to your Spoolman instance. No API key required. Example: http://spoolman:8000
            </p>
            {normalizedUrl && (
              <p className="text-sm text-pf-text-secondary mt-1">
                Will save as: <code className="bg-pf-bg-0 px-2 py-1 rounded">{normalizedUrl}</code>
              </p>
            )}
          </div>

          {normalizedUrl && (
            <div className="flex gap-2 items-center">
              <a
                href={normalizedUrl}
                target="_blank"
                rel="noopener noreferrer"
                className="px-4 py-2 bg-pf-bg-0 border border-pf-border rounded text-pf-text-primary hover:bg-pf-bg-2 flex items-center gap-2"
              >
                <ExternalLink className="h-4 w-4" />
                Open Spoolman
              </a>
              <button
                onClick={testSpoolman}
                disabled={testing}
                className="px-4 py-2 bg-blue-600 text-white rounded hover:bg-blue-700 disabled:opacity-50 flex items-center gap-2"
              >
                <TestTube className="h-4 w-4" />
                {testing ? 'Testing...' : 'Test connection'}
              </button>
              {testOk !== null && (
                <span className={`text-sm ${testOk ? 'text-green-400' : 'text-red-400'}`}>
                  {testMessage}
                </span>
              )}
            </div>
          )}

          <div className="flex gap-2">
            <button
              onClick={saveSpoolman}
              disabled={!normalizedUrl}
              className="px-4 py-2 bg-green-600 text-white rounded hover:bg-green-700 disabled:opacity-50 disabled:cursor-not-allowed flex items-center gap-2"
            >
              <Save className="h-4 w-4" />
              Save
            </button>
            <a
              href="/spools"
              className="px-4 py-2 bg-pf-bg-0 border border-pf-border rounded text-pf-text-primary hover:bg-pf-bg-2 flex items-center gap-2"
            >
              View Spools
            </a>
          </div>
        </div>
      </div>

      {/* Filament Temperature Presets */}
      <div className="bg-pf-bg-1 border border-pf-border rounded-xl p-6">
        <h2 className="text-xl font-semibold text-pf-text-primary mb-4">Filament Temperature Presets</h2>
        <p className="text-sm text-pf-text-secondary mb-4">
          Defaults used by preset buttons on the Printers page. Admins can override per material.
        </p>
        
        <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
          <div>
            <label className="block text-sm font-medium text-pf-text-primary mb-2">
              ABS (Hotend / Bed)
            </label>
            <div className="flex gap-2">
              <input
                type="number"
                value={filamentPresets.abs.hotend}
                onChange={(e) => setFilamentPresets(prev => ({ 
                  ...prev, 
                  abs: { ...prev.abs, hotend: Number(e.target.value) }
                }))}
                className="w-24 px-3 py-2 bg-pf-bg-0 border border-pf-border rounded text-pf-text-primary"
              />
              <input
                type="number"
                value={filamentPresets.abs.bed}
                onChange={(e) => setFilamentPresets(prev => ({ 
                  ...prev, 
                  abs: { ...prev.abs, bed: Number(e.target.value) }
                }))}
                className="w-24 px-3 py-2 bg-pf-bg-0 border border-pf-border rounded text-pf-text-primary"
              />
            </div>
          </div>
          
          <div>
            <label className="block text-sm font-medium text-pf-text-primary mb-2">
              ASA (Hotend / Bed)
            </label>
            <div className="flex gap-2">
              <input
                type="number"
                value={filamentPresets.asa.hotend}
                onChange={(e) => setFilamentPresets(prev => ({ 
                  ...prev, 
                  asa: { ...prev.asa, hotend: Number(e.target.value) }
                }))}
                className="w-24 px-3 py-2 bg-pf-bg-0 border border-pf-border rounded text-pf-text-primary"
              />
              <input
                type="number"
                value={filamentPresets.asa.bed}
                onChange={(e) => setFilamentPresets(prev => ({ 
                  ...prev, 
                  asa: { ...prev.asa, bed: Number(e.target.value) }
                }))}
                className="w-24 px-3 py-2 bg-pf-bg-0 border border-pf-border rounded text-pf-text-primary"
              />
            </div>
          </div>
          
          <div>
            <label className="block text-sm font-medium text-pf-text-primary mb-2">
              PLA (Hotend / Bed)
            </label>
            <div className="flex gap-2">
              <input
                type="number"
                value={filamentPresets.pla.hotend}
                onChange={(e) => setFilamentPresets(prev => ({ 
                  ...prev, 
                  pla: { ...prev.pla, hotend: Number(e.target.value) }
                }))}
                className="w-24 px-3 py-2 bg-pf-bg-0 border border-pf-border rounded text-pf-text-primary"
              />
              <input
                type="number"
                value={filamentPresets.pla.bed}
                onChange={(e) => setFilamentPresets(prev => ({ 
                  ...prev, 
                  pla: { ...prev.pla, bed: Number(e.target.value) }
                }))}
                className="w-24 px-3 py-2 bg-pf-bg-0 border border-pf-border rounded text-pf-text-primary"
              />
            </div>
          </div>
          
          <div>
            <label className="block text-sm font-medium text-pf-text-primary mb-2">
              PETG (Hotend / Bed)
            </label>
            <div className="flex gap-2">
              <input
                type="number"
                value={filamentPresets.petg.hotend}
                onChange={(e) => setFilamentPresets(prev => ({ 
                  ...prev, 
                  petg: { ...prev.petg, hotend: Number(e.target.value) }
                }))}
                className="w-24 px-3 py-2 bg-pf-bg-0 border border-pf-border rounded text-pf-text-primary"
              />
              <input
                type="number"
                value={filamentPresets.petg.bed}
                onChange={(e) => setFilamentPresets(prev => ({ 
                  ...prev, 
                  petg: { ...prev.petg, bed: Number(e.target.value) }
                }))}
                className="w-24 px-3 py-2 bg-pf-bg-0 border border-pf-border rounded text-pf-text-primary"
              />
            </div>
          </div>
        </div>

        <button
          onClick={saveFilamentPresets}
          className="mt-4 px-4 py-2 bg-green-600 text-white rounded hover:bg-green-700 flex items-center gap-2"
        >
          <Save className="h-4 w-4" />
          Save Presets
        </button>
      </div>

      {/* Network Discovery Settings */}
      <div className="bg-pf-bg-1 border border-pf-border rounded-xl p-6">
        <h2 className="text-xl font-semibold text-pf-text-primary mb-4">Network Discovery Settings</h2>
        <p className="text-sm text-pf-text-secondary mb-4">
          Configure network ranges and parameters for printer discovery. Leave ranges empty to auto-detect from current network interfaces.
        </p>
        
        <div className="space-y-4">
          <div>
            <label className="block text-sm font-medium text-pf-text-primary mb-2">
              Network Ranges (CIDR notation)
            </label>
            <div className="space-y-2">
              {networkRanges.map((range, index) => (
                <div key={index} className="flex gap-2">
                  <input
                    type="text"
                    value={range.cidr}
                    onChange={(e) => updateNetworkRange(index, e.target.value)}
                    placeholder="192.168.1.0/24"
                    className="flex-1 px-3 py-2 bg-pf-bg-0 border border-pf-border rounded text-pf-text-primary placeholder-pf-text-secondary"
                  />
                  <button
                    onClick={() => removeNetworkRange(index)}
                    className="px-3 py-2 text-red-400 hover:text-red-300"
                    title="Remove this range"
                  >
                    <X className="h-4 w-4" />
                  </button>
                </div>
              ))}
              <button
                onClick={addNetworkRange}
                className="px-4 py-2 bg-pf-bg-0 border border-pf-border rounded text-pf-text-primary hover:bg-pf-bg-2 flex items-center gap-2"
              >
                <Plus className="h-4 w-4" />
                Add Range
              </button>
            </div>
            
            <button
              onClick={autoDetectNetworks}
              disabled={loadingDynamic}
              className="mt-2 px-4 py-2 bg-blue-600 text-white rounded hover:bg-blue-700 disabled:opacity-50 flex items-center gap-2"
            >
              <RefreshCw className="h-4 w-4" />
              {loadingDynamic ? 'Detecting...' : 'Auto-detect Networks'}
            </button>
          </div>

          <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
            <div>
              <label className="block text-sm font-medium text-pf-text-primary mb-2">
                Discovery Timeout (ms)
              </label>
              <input
                type="number"
                value={discoveryTimeout}
                onChange={(e) => setDiscoveryTimeout(Number(e.target.value))}
                min={1000}
                max={10000}
                className="w-full px-3 py-2 bg-pf-bg-0 border border-pf-border rounded text-pf-text-primary"
              />
            </div>
            
            <div>
              <label className="block text-sm font-medium text-pf-text-primary mb-2">
                Max Concurrent Scans
              </label>
              <input
                type="number"
                value={maxConcurrentScans}
                onChange={(e) => setMaxConcurrentScans(Number(e.target.value))}
                min={1}
                max={100}
                className="w-full px-3 py-2 bg-pf-bg-0 border border-pf-border rounded text-pf-text-primary"
              />
            </div>
          </div>

          <div>
            <label className="block text-sm font-medium text-pf-text-primary mb-2">
              Ports to Scan
            </label>
            <div className="space-y-2">
              {scanPorts.map((port, index) => (
                <div key={index} className="flex gap-2">
                  <input
                    type="number"
                    value={port}
                    onChange={(e) => updateScanPort(index, Number(e.target.value))}
                    min={1}
                    max={65535}
                    className="w-24 px-3 py-2 bg-pf-bg-0 border border-pf-border rounded text-pf-text-primary"
                  />
                  <button
                    onClick={() => removeScanPort(index)}
                    className="px-3 py-2 text-red-400 hover:text-red-300"
                    title="Remove this port"
                  >
                    <X className="h-4 w-4" />
                  </button>
                </div>
              ))}
              <button
                onClick={addScanPort}
                className="px-4 py-2 bg-pf-bg-0 border border-pf-border rounded text-pf-text-primary hover:bg-pf-bg-2 flex items-center gap-2"
              >
                <Plus className="h-4 w-4" />
                Add Port
              </button>
            </div>
          </div>

          <button
            onClick={saveNetworkSettings}
            className="px-4 py-2 bg-green-600 text-white rounded hover:bg-green-700 flex items-center gap-2"
          >
            <Save className="h-4 w-4" />
            Save Network Settings
          </button>
        </div>
      </div>
    </div>
  );
}

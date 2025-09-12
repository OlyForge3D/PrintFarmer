import { useState, useEffect } from 'react';
import { useDiagnosticsSummary } from '@/hooks/useHealth';
import { toast } from 'sonner';
import { apiClient } from '@/services/api';
import { usePasswordPolicy } from '@/hooks/usePasswordPolicy';
import { Save, TestTube, Plus, X, ExternalLink, RefreshCw, Edit2, Trash2 } from 'lucide-react';
import type { FilamentType } from '@/types/api';
import { useAuth } from '@/contexts/AuthContext';

interface NetworkRange {
  cidr: string;
}

// (SettingsData interface removed - unused)

export function SettingsPage() {
  const [spoolmanBase, setSpoolmanBase] = useState('');
  const [networkRanges, setNetworkRanges] = useState<NetworkRange[]>([]);
  const [discoveryTimeout, setDiscoveryTimeout] = useState(5000);
  const [maxConcurrentScans, setMaxConcurrentScans] = useState(20);
  const [scanPorts, setScanPorts] = useState<number[]>([80, 7125]);
  const [filamentTypes, setFilamentTypes] = useState<FilamentType[]>([]);
  // Password policy via React Query
  const { data: passwordPolicy, savePolicy, saving, reset: resetPolicy } = usePasswordPolicy();
  const [draftPolicy, setDraftPolicy] = useState({ minLength: 12, requireUppercase: false, requireLowercase: false, requireDigit: false, requireSymbol: false });
  const [policyDirty, setPolicyDirty] = useState(false);
  
  const [testing, setTesting] = useState(false);
  const [testOk, setTestOk] = useState<boolean | null>(null);
  const [testMessage, setTestMessage] = useState('');
  const [loading, setLoading] = useState(true);
  const [loadingDynamic, setLoadingDynamic] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const { data: diagnostics } = useDiagnosticsSummary(45000);
  const { hasRole, isAuthenticated } = useAuth();
  
  // Filament type editing
  const [editingFilamentType, setEditingFilamentType] = useState<FilamentType | null>(null);
  const [newFilamentType, setNewFilamentType] = useState({ name: '', hotend: 210, bed: 60 });
  const [showAddForm, setShowAddForm] = useState(false);

  useEffect(() => {
    loadSettings();
  }, []);

  // Sync draft when policy loads
  useEffect(() => {
    if (passwordPolicy && !policyDirty) {
      setDraftPolicy(passwordPolicy);
    }
  }, [passwordPolicy, policyDirty]);

  const loadSettings = async () => {
    try {
      setLoading(true);
      // Load filament types
      const types = await apiClient.getFilamentTypes();
      setFilamentTypes(types);
      // password policy handled by usePasswordPolicy
      
      // For other settings, we might need to implement API endpoints
      // For now, use default values or localStorage
      const savedSpoolman = localStorage.getItem('spoolman-base-url') || '';
      setSpoolmanBase(savedSpoolman);
      
      // Load network discovery settings from backend
      try {
        const nd = await apiClient.getNetworkDiscoverySettings();
        setNetworkRanges(nd.networkRanges.map(r => ({ cidr: r })));
        setDiscoveryTimeout(nd.timeoutMs);
        setMaxConcurrentScans(nd.maxConcurrentScans);
        setScanPorts(nd.ports);
      } catch {
        // Fallback to any legacy localStorage values (backwards compatibility)
        const savedRanges = localStorage.getItem('network-ranges');
        if (savedRanges) {
          setNetworkRanges(JSON.parse(savedRanges));
        }
        const savedTimeout = localStorage.getItem('discovery-timeout');
        if (savedTimeout) setDiscoveryTimeout(Number(savedTimeout));
        const savedMax = localStorage.getItem('max-concurrent-scans');
        if (savedMax) setMaxConcurrentScans(Number(savedMax));
        const savedPorts = localStorage.getItem('scan-ports');
        if (savedPorts) setScanPorts(JSON.parse(savedPorts));
      }
      
      setError(null);
    } catch (err) {
      setError('Failed to load settings');
      console.error('Error loading settings:', err);
    } finally {
      setLoading(false);
    }
  };

  const authHeader = (): Record<string, string> => {
    const token = localStorage.getItem('auth-token');
    return token ? { Authorization: `Bearer ${token}` } : {} as Record<string, string>;
  };

  const updatePolicyField = (field: string, value: unknown) => {
    setDraftPolicy(prev => ({ ...prev, [field]: value }));
    setPolicyDirty(true);
  };

  const savePasswordPolicy = async () => {
    if (!hasRole('farm_admin') || !draftPolicy) return;
    try {
      await savePolicy(draftPolicy);
      setPolicyDirty(false);
      toast.success('Password policy saved');
    } catch (err) {
      console.error('Failed to save password policy', err);
      toast.error('Failed to save password policy');
    }
  };

  const normalizedUrl = spoolmanBase.trim().replace(/\/$/, '');

  const testSpoolman = async () => {
    if (!normalizedUrl) return;

    setTesting(true);
    setTestOk(null);
    setTestMessage('');

    try {
      // If user is admin attempt to persist config first (required so backend knows URL)
      if (isAuthenticated && hasRole('farm_admin')) {
        const token = localStorage.getItem('auth-token');
        const saveResp = await fetch('/api/spoolman/config', {
          method: 'POST',
          headers: {
            'Content-Type': 'application/json',
            ...(token ? { Authorization: `Bearer ${token}` } : {})
          },
          body: JSON.stringify({ baseUrl: normalizedUrl })
        });
        if (saveResp.status === 401 || saveResp.status === 403) {
          setTestOk(false);
            setTestMessage('Unauthorized: administrator privileges required to set configuration.');
          return;
        }
        if (!saveResp.ok && saveResp.status !== 204) {
          throw new Error(`Failed to persist config (HTTP ${saveResp.status})`);
        }
      } else {
        // Non-admin: give user feedback up-front
        setTestMessage('Note: Running readonly probe (config not persisted; admin rights required).');
      }

      // Use backend proxy endpoint instead of direct browser -> Spoolman (avoids CORS)
      const spoolsResp = await fetch('/api/spoolman/spools', { headers: { 'Accept': 'application/json' } });
      if (!spoolsResp.ok) {
        setTestOk(false);
        setTestMessage(`Backend test failed: HTTP ${spoolsResp.status}`);
        return;
      }
      const data = await spoolsResp.json();
      const count = Array.isArray(data) ? data.length : (Array.isArray(data?.items) ? data.items.length : 0);
      setTestOk(true);
      setTestMessage(`Connection successful (${count} spool${count === 1 ? '' : 's'})`);
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
      // Persist configuration to backend so all server-side features can use it
      const token = localStorage.getItem('auth-token');
      const resp = await fetch('/api/spoolman/config', {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          ...(token ? { Authorization: `Bearer ${token}` } : {})
        },
        body: JSON.stringify({ baseUrl: normalizedUrl })
      });
      if (resp.status === 401 || resp.status === 403) {
        toast.error('Unauthorized: administrator privileges required');
        return;
      }
      if (!resp.ok && resp.status !== 204) {
        throw new Error(`HTTP ${resp.status}`);
      }
      setError(null);
  toast.success('Spoolman settings saved');
      // Optionally re-run a quick connectivity test automatically
      await testSpoolman();
    } catch (err) {
      setError('Failed to save Spoolman settings');
      console.error('Error saving Spoolman settings:', err);
  toast.error('Failed to save Spoolman settings');
    }
  };

  const clearSpoolman = async () => {
    if (!confirm('Clear Spoolman configuration?')) return;
    try {
      const token = localStorage.getItem('auth-token');
      const resp = await fetch('/api/spoolman/config', { method: 'DELETE', headers: token ? { Authorization: `Bearer ${token}` } : undefined });
      if (resp.status === 401 || resp.status === 403) {
        toast.error('Unauthorized: administrator privileges required');
        return;
      }
      if (!resp.ok && resp.status !== 204) throw new Error(`HTTP ${resp.status}`);
      localStorage.removeItem('spoolman-base-url');
      setSpoolmanBase('');
      setTestOk(null);
      setTestMessage('');
      toast.success('Spoolman configuration cleared');
  } catch {
      toast.error('Failed to clear Spoolman configuration');
    }
  };

  // Filament Type Management
  const addFilamentType = async () => {
    if (!newFilamentType.name.trim()) return;
    
    try {
      const created = await apiClient.createFilamentType({
        name: newFilamentType.name.trim(),
        defaultTemperatures: {
          hotend: newFilamentType.hotend,
          bed: newFilamentType.bed
        }
      });
      setFilamentTypes(prev => [...prev, created].sort((a, b) => a.name.localeCompare(b.name)));
      setNewFilamentType({ name: '', hotend: 210, bed: 60 });
      setShowAddForm(false);
      setError(null);
    } catch (err) {
      setError('Failed to add filament type');
      console.error('Error adding filament type:', err);
    }
  };

  const updateFilamentType = async (id: string, name: string, hotend: number, bed: number) => {
    try {
      await apiClient.updateFilamentType(id, { name, defaultTemperatures: { hotend, bed } });
      setFilamentTypes(prev => prev.map(ft => ft.id === id ? { ...ft, name, defaultTemperatures: { hotend, bed } } : ft).sort((a, b) => a.name.localeCompare(b.name)));
      setError(null);
      toast.success('Filament type updated');
    } catch (err) {
      setError('Failed to update filament type');
      console.error('Error updating filament type:', err);
      toast.error('Failed to update filament type');
    }
  };

  const deleteFilamentType = async (id: string) => {
    if (!confirm('Are you sure you want to delete this filament type?')) return;
    try {
      await apiClient.deleteFilamentType(id);
      setFilamentTypes(prev => prev.filter(ft => ft.id !== id));
      setError(null);
    } catch (err) {
      setError('Failed to delete filament type');
      console.error('Error deleting filament type:', err);
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

  const saveNetworkSettings = async () => {
    try {
      const payload = {
        networkRanges: networkRanges.filter(r => r.cidr.trim()).map(r => r.cidr.trim()),
        timeoutMs: discoveryTimeout,
        maxConcurrentScans,
        ports: scanPorts.filter(p => p > 0 && p < 65536)
      };
      await apiClient.saveNetworkDiscoverySettings(payload);
      setError(null);
      toast.success('Network discovery settings saved');
    } catch (err) {
      setError('Failed to save network settings');
      console.error('Error saving network settings:', err);
      toast.error('Failed to save network settings');
    }
  };

  const autoDetectNetworks = async () => {
    setLoadingDynamic(true);
    try {
      const detected = await apiClient.autoDetectNetworkRanges();
      if (detected.length === 0) {
        toast.info('No active network interfaces detected');
      }
      setNetworkRanges(detected.map(cidr => ({ cidr })));
    } catch (err) {
      setError('Failed to auto-detect networks');
      console.error('Error auto-detecting networks:', err);
      toast.error('Failed to auto-detect networks');
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
        {diagnostics && (
          <div className="text-sm text-pf-text-secondary mb-2 flex gap-4 flex-wrap">
            <span>Configured: {diagnostics.spoolman.configured ? 'Yes' : 'No'}</span>
            {diagnostics.spoolman.baseUrl && <span>Base URL: {diagnostics.spoolman.baseUrl}</span>}
            <span>Discovery Ranges: {diagnostics.discovery.ranges.length}</span>
            <span>Scan Ports: {diagnostics.discovery.ports.join(', ')}</span>
          </div>
        )}
        
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
              Base URL to your Spoolman instance. No API key required. Example: http://spoolman:7912
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
            <button
              onClick={clearSpoolman}
              type="button"
              className="px-4 py-2 bg-red-700 text-white rounded hover:bg-red-800 flex items-center gap-2"
            >
              <Trash2 className="h-4 w-4" />
              Clear
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

      {/* Filament Types Management */}
      <div className="bg-pf-bg-1 border border-pf-border rounded-xl p-6">
        <div className="flex justify-between items-center mb-4">
          <div>
            <h2 className="text-xl font-semibold text-pf-text-primary">Filament Types & Temperature Presets</h2>
            <p className="text-sm text-pf-text-secondary">
              Manage filament types and their default temperature settings. These are used as presets for printer controls.
            </p>
          </div>
          <button
            onClick={() => setShowAddForm(true)}
            className="px-4 py-2 bg-green-600 text-white rounded hover:bg-green-700 flex items-center gap-2"
          >
            <Plus className="h-4 w-4" />
            Add Type
          </button>
        </div>
        
        {/* Add new filament type form */}
        {showAddForm && (
          <div className="mb-4 p-4 bg-pf-bg-0 border border-pf-border rounded">
            <h3 className="text-lg font-medium text-pf-text-primary mb-3">Add New Filament Type</h3>
            <div className="grid grid-cols-1 md:grid-cols-4 gap-4">
              <div>
                <label className="block text-sm font-medium text-pf-text-primary mb-2">Name</label>
                <input
                  type="text"
                  value={newFilamentType.name}
                  onChange={(e) => setNewFilamentType(prev => ({ ...prev, name: e.target.value }))}
                  placeholder="e.g., PLA, ABS, PETG"
                  className="w-full px-3 py-2 bg-pf-bg-1 border border-pf-border rounded text-pf-text-primary"
                />
              </div>
              <div>
                <label className="block text-sm font-medium text-pf-text-primary mb-2">Hotend Temp (°C)</label>
                <input
                  type="number"
                  value={newFilamentType.hotend}
                  onChange={(e) => setNewFilamentType(prev => ({ ...prev, hotend: Number(e.target.value) }))}
                  aria-label="New filament hotend temperature"
                  className="w-full px-3 py-2 bg-pf-bg-1 border border-pf-border rounded text-pf-text-primary"
                />
              </div>
              <div>
                <label className="block text-sm font-medium text-pf-text-primary mb-2">Bed Temp (°C)</label>
                <input
                  type="number"
                  value={newFilamentType.bed}
                  onChange={(e) => setNewFilamentType(prev => ({ ...prev, bed: Number(e.target.value) }))}
                  aria-label="New filament bed temperature"
                  className="w-full px-3 py-2 bg-pf-bg-1 border border-pf-border rounded text-pf-text-primary"
                />
              </div>
              <div className="flex items-end gap-2">
                <button
                  onClick={addFilamentType}
                  disabled={!newFilamentType.name.trim()}
                  className="px-4 py-2 bg-green-600 text-white rounded hover:bg-green-700 disabled:opacity-50 flex items-center gap-2"
                >
                  <Save className="h-4 w-4" />
                  Add
                </button>
                <button
                  onClick={() => {
                    setShowAddForm(false);
                    setNewFilamentType({ name: '', hotend: 210, bed: 60 });
                  }}
                  className="px-4 py-2 bg-gray-600 text-white rounded hover:bg-gray-700"
                >
                  Cancel
                </button>
              </div>
            </div>
          </div>
        )}

        {/* Filament types list */}
        <div className="space-y-2">
          {filamentTypes.map((filamentType) => (
            <div key={filamentType.id} className="flex items-center justify-between p-3 bg-pf-bg-0 border border-pf-border rounded">
              {editingFilamentType?.id === filamentType.id ? (
                <div className="flex items-center gap-4 flex-1">
                    <input
                    type="text"
                    value={editingFilamentType.name}
                    onChange={(e) => setEditingFilamentType(prev => prev ? { ...prev, name: e.target.value } : null)}
                      aria-label="Filament type name"
                    className="w-32 px-2 py-1 bg-pf-bg-1 border border-pf-border rounded text-pf-text-primary"
                  />
                  <div className="flex items-center gap-2">
                    <span className="text-sm text-pf-text-secondary">Hotend:</span>
                    <input
                      type="number"
                      value={editingFilamentType.defaultTemperatures.hotend}
                      onChange={(e) => setEditingFilamentType(prev => prev ? {
                        ...prev,
                        defaultTemperatures: { ...prev.defaultTemperatures, hotend: Number(e.target.value) }
                      } : null)}
                      aria-label="Hotend temperature"
                      className="w-16 px-2 py-1 bg-pf-bg-1 border border-pf-border rounded text-pf-text-primary"
                    />
                    <span className="text-sm text-pf-text-secondary">°C</span>
                  </div>
                  <div className="flex items-center gap-2">
                    <span className="text-sm text-pf-text-secondary">Bed:</span>
                    <input
                      type="number"
                      value={editingFilamentType.defaultTemperatures.bed}
                      onChange={(e) => setEditingFilamentType(prev => prev ? {
                        ...prev,
                        defaultTemperatures: { ...prev.defaultTemperatures, bed: Number(e.target.value) }
                      } : null)}
                      aria-label="Bed temperature"
                      className="w-16 px-2 py-1 bg-pf-bg-1 border border-pf-border rounded text-pf-text-primary"
                    />
                    <span className="text-sm text-pf-text-secondary">°C</span>
                  </div>
                  <div className="flex gap-2">
                    <button
                      onClick={() => updateFilamentType(
                        editingFilamentType.id,
                        editingFilamentType.name,
                        editingFilamentType.defaultTemperatures.hotend,
                        editingFilamentType.defaultTemperatures.bed
                      )}
                      className="px-3 py-1 bg-green-600 text-white rounded hover:bg-green-700 text-sm"
                    >
                      Save
                    </button>
                    <button
                      onClick={() => setEditingFilamentType(null)}
                      className="px-3 py-1 bg-gray-600 text-white rounded hover:bg-gray-700 text-sm"
                    >
                      Cancel
                    </button>
                  </div>
                </div>
              ) : (
                <>
                  <div className="flex items-center gap-4">
                    <span className="font-medium text-pf-text-primary">{filamentType.name}</span>
                    <span className="text-sm text-pf-text-secondary">
                      Hotend: {filamentType.defaultTemperatures.hotend}°C, Bed: {filamentType.defaultTemperatures.bed}°C
                    </span>
                  </div>
                  <div className="flex gap-2">
                    <button
                      onClick={() => setEditingFilamentType(filamentType)}
                      className="p-2 text-blue-400 hover:text-blue-300"
                      title="Edit filament type"
                    >
                      <Edit2 className="h-4 w-4" />
                    </button>
                    <button
                      onClick={() => deleteFilamentType(filamentType.id)}
                      className="p-2 text-red-400 hover:text-red-300"
                      title="Delete filament type"
                    >
                      <Trash2 className="h-4 w-4" />
                    </button>
                  </div>
                </>
              )}
            </div>
          ))}
          
          {filamentTypes.length === 0 && (
            <div className="text-center py-8 text-pf-text-secondary">
              No filament types defined. Add some to get started!
            </div>
          )}
        </div>
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
                    aria-label="Discovery timeout"
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
                    aria-label="Max concurrent scans"
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
                    aria-label="Scan port"
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

      {hasRole('farm_admin') && passwordPolicy && (
        <div className="bg-pf-bg-1 border border-pf-border rounded-xl p-6">
          <h2 className="text-xl font-semibold text-pf-text-primary mb-4">Password Policy</h2>
          <p className="text-sm text-pf-text-secondary mb-4">Configure global password requirements for new accounts and admin creation.</p>
          <div className="grid grid-cols-1 md:grid-cols-3 gap-6">
            <div>
              <label className="block text-sm font-medium text-pf-text-primary mb-2">Minimum Length</label>
              <input
                id="pp-minlength"
                type="number"
                min={6}
                max={256}
                aria-label="Minimum password length"
                value={draftPolicy.minLength}
                onChange={(e) => updatePolicyField('minLength', Number(e.target.value))}
                className="w-full px-3 py-2 bg-pf-bg-0 border border-pf-border rounded text-pf-text-primary"
              />
              <p className="text-xs text-pf-text-secondary mt-1">Applies to all new passwords. Existing passwords unaffected.</p>
            </div>
            <div className="space-y-2 col-span-2">
              <div className="flex items-center gap-2">
                <input
                  id="pp-upper"
                  type="checkbox"
                  checked={draftPolicy.requireUppercase}
                  onChange={(e) => updatePolicyField('requireUppercase', e.target.checked)}
                  className="h-4 w-4"
                />
                <label htmlFor="pp-upper" className="text-sm text-pf-text-primary">Require Uppercase Letter</label>
              </div>
              <div className="flex items-center gap-2">
                <input
                  id="pp-lower"
                  type="checkbox"
                  checked={draftPolicy.requireLowercase}
                  onChange={(e) => updatePolicyField('requireLowercase', e.target.checked)}
                  className="h-4 w-4"
                />
                <label htmlFor="pp-lower" className="text-sm text-pf-text-primary">Require Lowercase Letter</label>
              </div>
              <div className="flex items-center gap-2">
                <input
                  id="pp-digit"
                  type="checkbox"
                  checked={draftPolicy.requireDigit}
                  onChange={(e) => updatePolicyField('requireDigit', e.target.checked)}
                  className="h-4 w-4"
                />
                <label htmlFor="pp-digit" className="text-sm text-pf-text-primary">Require Digit</label>
              </div>
              <div className="flex items-center gap-2">
                <input
                  id="pp-symbol"
                  type="checkbox"
                  checked={draftPolicy.requireSymbol}
                  onChange={(e) => updatePolicyField('requireSymbol', e.target.checked)}
                  className="h-4 w-4"
                />
                <label htmlFor="pp-symbol" className="text-sm text-pf-text-primary">Require Symbol</label>
              </div>
            </div>
          </div>
          <div className="mt-6 flex gap-3">
            <button
              onClick={savePasswordPolicy}
              disabled={!policyDirty || saving || draftPolicy.minLength < 6 || draftPolicy.minLength > 256}
              className="px-4 py-2 bg-green-600 text-white rounded hover:bg-green-700 disabled:opacity-50 flex items-center gap-2"
            >
              <Save className="h-4 w-4" />
              {saving ? 'Saving...' : 'Save Policy'}
            </button>
            {policyDirty && !saving && (
              <button
                type="button"
                onClick={() => { setPolicyDirty(false); resetPolicy(); }}
                className="px-4 py-2 bg-gray-600 text-white rounded hover:bg-gray-700"
              >
                Reset
              </button>
            )}
          </div>
        </div>
      )}
    </div>
  );
}
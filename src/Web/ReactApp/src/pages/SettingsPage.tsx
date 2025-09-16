import { useState, useEffect } from 'react';
import { useDiagnosticsSummary } from '@/hooks/useHealth';
import { toast } from 'sonner';
import { apiClient } from '@/services/api';
import { signalRService } from '@/services/signalr';
import { usePasswordPolicy } from '@/hooks/usePasswordPolicy';
import { normalizeSpoolmanBaseUrl, isValidCidr, findOverlappingCidrRanges, suggestCorrectNetworkAddress } from '@/utils/validation';
import { Save, TestTube, Plus, X, ExternalLink, RefreshCw, Edit2, Trash2, AlertCircle, CheckCircle } from 'lucide-react';
import type { FilamentType } from '@/types/api';
import { useAuth } from '@/contexts/AuthContext';

interface NetworkRange {
  cidr: string;
  isValid?: boolean;
  suggestion?: string;
}

interface NetworkValidationState {
  ranges: Array<{
    cidr: string;
    isValid: boolean;
    suggestion?: string;
  }>;
  overlapping: string[];
  hasErrors: boolean;
}

// (SettingsData interface removed - unused)

export function SettingsPage() {
  const [spoolmanBase, setSpoolmanBase] = useState('');
  const [networkRanges, setNetworkRanges] = useState<NetworkRange[]>([]);
  const [networkValidation, setNetworkValidation] = useState<NetworkValidationState>({ ranges: [], overlapping: [], hasErrors: false });
  const [discoveryTimeout, setDiscoveryTimeout] = useState(5000);
  const [maxConcurrentScans, setMaxConcurrentScans] = useState(20);
  const [scanPorts, setScanPorts] = useState<number[]>([80]);
  const [filamentTypes, setFilamentTypes] = useState<FilamentType[]>([]);
  // Password policy via React Query
  const { data: passwordPolicy, savePolicy, saving, reset: resetPolicy } = usePasswordPolicy();
  const [draftPolicy, setDraftPolicy] = useState({ minLength: 12, requireUppercase: false, requireLowercase: false, requireDigit: false, requireSymbol: false });
  const [policyDirty, setPolicyDirty] = useState(false);
  
  // Save feedback state
  const [isSaving, setIsSaving] = useState(false);
  const [saveSuccess, setSaveSuccess] = useState(false);
  
  // SignalR settings
  const [signalrSettings, setSignalrSettings] = useState<{ logLevel: string; consoleLoggingEnabled: boolean }>({ logLevel: 'Information', consoleLoggingEnabled: true });
  const [signalrDirty, setSignalrDirty] = useState(false);
  const [signalrSaving, setSignalrSaving] = useState(false);
  
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
      
      // Load SignalR settings
      try {
        const signalrConfig = await apiClient.getSignalRSettings();
        setSignalrSettings(signalrConfig);
        setSignalrDirty(false);
      } catch (err) {
        console.warn('Failed to load SignalR settings, using defaults:', err);
        setSignalrSettings({ logLevel: 'Information', consoleLoggingEnabled: true });
      }
      
      // Load Spoolman configuration: prefer backend, fallback to localStorage
      try {
        const cfgResp = await fetch('/api/spoolman/config');
        if (cfgResp.ok && cfgResp.status !== 204) {
          const cfg = await cfgResp.json();
          if (cfg?.baseUrl) {
            const normalized = normalizeSpoolmanBaseUrl(cfg.baseUrl);
            setSpoolmanBase(normalized);
            // Sync localStorage if out of date
            if (localStorage.getItem('spoolman-base-url') !== normalized) {
              localStorage.setItem('spoolman-base-url', normalized);
            }
          } else {
            const savedSpoolman = localStorage.getItem('spoolman-base-url') || '';
            setSpoolmanBase(savedSpoolman);
          }
        } else {
          const savedSpoolman = localStorage.getItem('spoolman-base-url') || '';
          setSpoolmanBase(savedSpoolman);
        }
      } catch {
        const savedSpoolman = localStorage.getItem('spoolman-base-url') || '';
        setSpoolmanBase(savedSpoolman);
      }
      
      // Load network discovery settings from backend
      try {
        const nd = await apiClient.getNetworkDiscoverySettings();
        const ranges = nd.networkRanges.map(r => ({ cidr: r }));
        setNetworkRanges(ranges);
        setDiscoveryTimeout(nd.timeoutMs);
        setMaxConcurrentScans(nd.maxConcurrentScans);
        setScanPorts(nd.ports);
        // Validate the loaded ranges
        validateNetworkRanges(ranges);
      } catch {
        // Fallback to any legacy localStorage values (backwards compatibility)
        const savedRanges = localStorage.getItem('network-ranges');
        if (savedRanges) {
          const ranges = JSON.parse(savedRanges);
          setNetworkRanges(ranges);
          validateNetworkRanges(ranges);
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

  const saveSignalRSettings = async () => {
    if (!hasRole('farm_admin')) return;
    try {
      setSignalrSaving(true);
      await apiClient.saveSignalRSettings(signalrSettings);
      setSignalrDirty(false);
      
      // Refresh the SignalR service with new settings
      await signalRService.refreshSettings();
      
      toast.success('SignalR settings saved');
    } catch (err) {
      console.error('Failed to save SignalR settings', err);
      toast.error('Failed to save SignalR settings');
    } finally {
      setSignalrSaving(false);
    }
  };

  const updateSignalRField = (field: string, value: unknown) => {
    setSignalrSettings(prev => ({ ...prev, [field]: value }));
    setSignalrDirty(true);
  };

  const normalizedUrl = normalizeSpoolmanBaseUrl(spoolmanBase);

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
    const updated = networkRanges.filter((_, i) => i !== index);
    setNetworkRanges(updated);
    validateNetworkRanges(updated);
  };

  const updateNetworkRange = (index: number, cidr: string) => {
    const updated = networkRanges.map((range, i) => i === index ? { cidr } : range);
    setNetworkRanges(updated);
    validateNetworkRanges(updated);
  };

  // Network validation logic
  const validateNetworkRanges = (ranges: NetworkRange[]) => {
    const validatedRanges = ranges.map(range => {
      const trimmed = range.cidr.trim();
      if (!trimmed) {
        return { cidr: range.cidr, isValid: true }; // Empty ranges are valid (will be filtered out)
      }
      
      const isValid = isValidCidr(trimmed);
      const suggestion = !isValid ? suggestCorrectNetworkAddress(trimmed) : undefined;
      
      return {
        cidr: range.cidr,
        isValid,
        suggestion: suggestion || undefined // Convert null to undefined
      };
    });

    // Check for overlaps among valid, non-empty ranges
    const nonEmptyValidCidrs = validatedRanges
      .filter(r => r.cidr.trim() && r.isValid)
      .map(r => r.cidr.trim());
    
    const overlapping = findOverlappingCidrRanges(nonEmptyValidCidrs);
    const hasErrors = validatedRanges.some(r => r.cidr.trim() && !r.isValid) || overlapping.length > 0;

    setNetworkValidation({
      ranges: validatedRanges,
      overlapping,
      hasErrors
    });
  };

  // Auto-validate when component mounts and network ranges are loaded
  // (validation is now triggered in the loadData function when ranges are actually loaded)

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
    // Clear previous save state
    setSaveSuccess(false);
    
    // Validate before saving
    if (networkValidation.hasErrors) {
      toast.error('Please fix validation errors before saving');
      return;
    }
    
    const filteredRanges = networkRanges.filter(r => r.cidr.trim()).map(r => r.cidr.trim());
    const filteredPorts = scanPorts.filter(p => p > 0 && p < 65536);
    
    // Additional validation
    if (filteredRanges.length > 0 && filteredPorts.length === 0) {
      toast.error('At least one port is required when network ranges are configured');
      return;
    }

    if (discoveryTimeout < 100 || discoveryTimeout > 30000) {
      toast.error('Discovery timeout must be between 100ms and 30,000ms');
      return;
    }

    if (maxConcurrentScans < 1 || maxConcurrentScans > 100) {
      toast.error('Max concurrent scans must be between 1 and 100');
      return;
    }

    setIsSaving(true);
    try {
      const payload = {
        networkRanges: filteredRanges,
        timeoutMs: discoveryTimeout,
        maxConcurrentScans,
        ports: filteredPorts
      };
      await apiClient.saveNetworkDiscoverySettings(payload);
      setError(null);
      setSaveSuccess(true);
      toast.success(`Network discovery settings saved successfully! ${filteredRanges.length} ranges, ${filteredPorts.length} ports configured.`);
      
      // Clear success indicator after 3 seconds
      setTimeout(() => setSaveSuccess(false), 3000);
    } catch (err) {
      setError('Failed to save network settings');
      console.error('Error saving network settings:', err);
      toast.error('Failed to save network settings');
    } finally {
      setIsSaving(false);
    }
  };

  // autoDetectNetworks removed

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
          Configure network ranges and parameters for printer discovery. Use proper CIDR notation (e.g., 192.168.1.0/24). Leave ranges empty to disable discovery.
        </p>
        
        <div className="space-y-4">
          <div>
            <label className="block text-sm font-medium text-pf-text-primary mb-2">
              Network Ranges (CIDR notation)
            </label>
            <div className="space-y-2">
              {networkRanges.map((range, index) => {
                const validation = networkValidation.ranges[index];
                const isOverlapping = networkValidation.overlapping.includes(range.cidr.trim());
                const hasError = validation && range.cidr.trim() && (!validation.isValid || isOverlapping);
                
                return (
                  <div key={index} className="space-y-1">
                    <div className="flex gap-2">
                      <div className="flex-1 relative">
                        <input
                          type="text"
                          value={range.cidr}
                          onChange={(e) => updateNetworkRange(index, e.target.value)}
                          placeholder="192.168.1.0/24"
                          className={`w-full px-3 py-2 bg-pf-bg-0 border rounded text-pf-text-primary placeholder-pf-text-secondary ${
                            hasError 
                              ? 'border-red-500 focus:ring-red-500' 
                              : validation && range.cidr.trim() && validation.isValid && !isOverlapping
                              ? 'border-green-500 focus:ring-green-500'
                              : 'border-pf-border focus:ring-pf-accent'
                          }`}
                        />
                        {validation && range.cidr.trim() && (
                          <div className="absolute right-8 top-2.5">
                            {validation.isValid && !isOverlapping ? (
                              <CheckCircle className="h-5 w-5 text-green-500" />
                            ) : (
                              <AlertCircle className="h-5 w-5 text-red-500" />
                            )}
                          </div>
                        )}
                      </div>
                      <button
                        onClick={() => removeNetworkRange(index)}
                        className="px-3 py-2 text-red-400 hover:text-red-300"
                        title="Remove this range"
                      >
                        <X className="h-4 w-4" />
                      </button>
                    </div>
                    
                    {/* Validation messages */}
                    {validation && range.cidr.trim() && (
                      <div className="ml-3 text-xs">
                        {!validation.isValid && (
                          <div className="text-red-400 flex items-center gap-1">
                            <AlertCircle className="h-3 w-3" />
                            Invalid CIDR format
                            {validation.suggestion && (
                              <span className="ml-1">
                                - try: <button 
                                  onClick={() => updateNetworkRange(index, validation.suggestion!)} 
                                  className="underline hover:no-underline text-blue-400"
                                >
                                  {validation.suggestion}
                                </button>
                              </span>
                            )}
                          </div>
                        )}
                        {validation.isValid && isOverlapping && (
                          <div className="text-orange-400 flex items-center gap-1">
                            <AlertCircle className="h-3 w-3" />
                            Overlaps with other network ranges
                          </div>
                        )}
                      </div>
                    )}
                  </div>
                );
              })}
              <button
                onClick={addNetworkRange}
                className="px-4 py-2 bg-pf-bg-0 border border-pf-border rounded text-pf-text-primary hover:bg-pf-bg-2 flex items-center gap-2"
              >
                <Plus className="h-4 w-4" />
                Add Range
              </button>
            </div>
            
            {/* Network validation summary */}
            {networkValidation.overlapping.length > 0 && (
              <div className="mt-3 p-3 bg-orange-900/20 border border-orange-700 rounded">
                <div className="text-orange-200 text-sm flex items-center gap-2">
                  <AlertCircle className="h-4 w-4" />
                  Overlapping network ranges detected:
                </div>
                <div className="mt-1 ml-6 text-xs text-orange-300">
                  {networkValidation.overlapping.join(', ')}
                </div>
                <div className="mt-1 ml-6 text-xs text-orange-400">
                  Overlapping ranges may cause duplicate scanning and inefficient discovery.
                </div>
              </div>
            )}
            
            {networkValidation.ranges.some(r => r.cidr.trim() && !r.isValid) && (
              <div className="mt-3 p-3 bg-red-900/20 border border-red-700 rounded">
                <div className="text-red-200 text-sm flex items-center gap-2">
                  <AlertCircle className="h-4 w-4" />
                  Invalid CIDR formats detected. Please fix before saving.
                </div>
              </div>
            )}
            
            {/* Auto-detect removed due to Docker / container network constraints */}
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
            disabled={networkValidation.hasErrors || isSaving}
            className={`px-4 py-2 rounded flex items-center gap-2 transition-colors ${
              networkValidation.hasErrors || isSaving
                ? 'bg-gray-500 text-gray-300 cursor-not-allowed'
                : saveSuccess
                ? 'bg-green-600 text-white hover:bg-green-700'
                : 'bg-green-600 text-white hover:bg-green-700'
            }`}
          >
            {isSaving ? (
              <>
                <RefreshCw className="h-4 w-4 animate-spin" />
                Saving...
              </>
            ) : saveSuccess ? (
              <>
                <CheckCircle className="h-4 w-4" />
                Saved!
              </>
            ) : (
              <>
                <Save className="h-4 w-4" />
                Save Network Settings
              </>
            )}
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

      {hasRole('farm_admin') && (
        <div className="bg-pf-bg-1 border border-pf-border rounded-xl p-6">
          <h2 className="text-xl font-semibold text-pf-text-primary mb-4">SignalR Settings</h2>
          <p className="text-sm text-pf-text-secondary mb-4">Configure SignalR console logging and verbosity level for real-time communication debugging.</p>
          <div className="grid grid-cols-1 md:grid-cols-2 gap-6">
            <div>
              <label className="block text-sm font-medium text-pf-text-primary mb-2">Log Level</label>
              <select
                id="signalr-loglevel"
                value={signalrSettings.logLevel}
                onChange={(e) => updateSignalRField('logLevel', e.target.value)}
                className="w-full px-3 py-2 bg-pf-bg-0 border border-pf-border rounded text-pf-text-primary"
                aria-label="SignalR log level"
              >
                <option value="None">None</option>
                <option value="Critical">Critical</option>
                <option value="Error">Error</option>
                <option value="Warning">Warning</option>
                <option value="Information">Information</option>
                <option value="Debug">Debug</option>
                <option value="Trace">Trace</option>
              </select>
              <p className="text-xs text-pf-text-secondary mt-1">Controls verbosity of SignalR client logging. Changes take effect after page refresh.</p>
            </div>
            <div className="flex flex-col justify-start">
              <label className="block text-sm font-medium text-pf-text-primary mb-2">Console Logging</label>
              <div className="flex items-center gap-2 mt-2">
                <input
                  id="signalr-console"
                  type="checkbox"
                  checked={signalrSettings.consoleLoggingEnabled}
                  onChange={(e) => updateSignalRField('consoleLoggingEnabled', e.target.checked)}
                  className="h-4 w-4"
                />
                <label htmlFor="signalr-console" className="text-sm text-pf-text-primary">Enable Console Logging</label>
              </div>
              <p className="text-xs text-pf-text-secondary mt-1">When disabled, SignalR logging is completely suppressed regardless of level.</p>
            </div>
          </div>
          <div className="mt-6 flex gap-3">
            <button
              onClick={saveSignalRSettings}
              disabled={!signalrDirty || signalrSaving}
              className="px-4 py-2 bg-green-600 text-white rounded hover:bg-green-700 disabled:opacity-50 flex items-center gap-2"
            >
              <Save className="h-4 w-4" />
              {signalrSaving ? 'Saving...' : 'Save Settings'}
            </button>
            {signalrDirty && !signalrSaving && (
              <button
                type="button"
                onClick={() => {
                  setSignalrSettings({ logLevel: 'Information', consoleLoggingEnabled: true });
                  setSignalrDirty(false);
                }}
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
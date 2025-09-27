import { useState, useEffect, useCallback } from 'react';
import { SettingsPagelet, SettingMetadata } from '../components/SettingsPagelet';
// --- Dynamic Settings UI State ---
const [settingsMetadata, setSettingsMetadata] = useState<SettingMetadata[]>([]);
const [settingsValues, setSettingsValues] = useState<Record<string, Record<string, any>>>({});
const [settingsLoading, setSettingsLoading] = useState(true);
const [settingsError, setSettingsError] = useState<string | null>(null);
const [settingsSaving, setSettingsSaving] = useState<Record<string, boolean>>({});
const [settingsDirty, setSettingsDirty] = useState<Record<string, boolean>>({});

// Fetch settings metadata and values
const loadDynamicSettings = useCallback(async () => {
  setSettingsLoading(true);
  setSettingsError(null);
  try {
    const metaResp = await fetch('/api/settings/metadata');
    if (!metaResp.ok) throw new Error('Failed to load settings metadata');
    const metadata: SettingMetadata[] = await metaResp.json();
    setSettingsMetadata(metadata);

    // Fetch current values for each settings class
    const valuesObj: Record<string, Record<string, any>> = {};
    for (const meta of metadata) {
      const valResp = await fetch(`/api/settings/${meta.key}`);
      if (!valResp.ok) throw new Error(`Failed to load settings for ${meta.key}`);
      valuesObj[meta.key] = await valResp.json();
    }
    setSettingsValues(valuesObj);
    setSettingsDirty({});
  } catch (err: any) {
    setSettingsError(err.message || 'Failed to load settings');
  } finally {
    setSettingsLoading(false);
  }
}, []);

useEffect(() => {
  loadDynamicSettings();
}, [loadDynamicSettings]);

const handlePageletChange = (key: string, field: string, value: any) => {
  setSettingsValues(prev => ({
    ...prev,
    [key]: { ...prev[key], [field]: value }
  }));
  setSettingsDirty(prev => ({ ...prev, [key]: true }));
};

const handlePageletSave = async (key: string) => {
  setSettingsSaving(prev => ({ ...prev, [key]: true }));
  setSettingsError(null);
  try {
    const resp = await fetch(`/api/settings/${key}`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(settingsValues[key])
    });
    if (!resp.ok) throw new Error(`Failed to save settings for ${key}`);
    setSettingsDirty(prev => ({ ...prev, [key]: false }));
    // Optionally reload values
    await loadDynamicSettings();
  } catch (err: any) {
    setSettingsError(err.message || `Failed to save settings for ${key}`);
  } finally {
    setSettingsSaving(prev => ({ ...prev, [key]: false }));
  }
};
import { useDiagnosticsSummary } from '@/hooks/useHealth';
import { toast } from 'sonner';
import { apiClient } from '@/services/api';
import { signalRService } from '@/services/harvest-signalr';
import { usePasswordPolicy } from '@/hooks/usePasswordPolicy';
import { normalizeSpoolmanBaseUrl, isValidCidr, findOverlappingCidrRanges, suggestCorrectNetworkAddress } from '@/utils/validation';
import { Save, TestTube, Plus, X, ExternalLink, RefreshCw, Edit2, Trash2, AlertCircle, CheckCircle, Eye, EyeOff } from 'lucide-react';
import type { FilamentType } from '@/types/api';
import { useAuth } from '@/contexts/AuthContext';
import { useTelemetry } from '@/telemetry/useTelemetry';
import { isTelemetryInitialized } from '@/telemetry/config';
import { SpoolmanFilamentImportButton } from '@/components/SpoolmanFilamentImportButton';

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

interface TelemetrySettings {
  enabled: boolean;
  consoleLogging: boolean;
  otlpEndpoint: string;
  samplingRate: number;
  trackUserInteractions: boolean;
  trackApiCalls: boolean;
  trackComponentLifecycle: boolean;
}

// (SettingsData interface removed - unused)

export function SettingsPage() {
  // SystemLogs persistence settings (connected to backend)
  const [logRetentionDays, setLogRetentionDays] = useState<number>(30);
  const [persistedLogTypes, setPersistedLogTypes] = useState<string[]>(['Info', 'Warning', 'Error']);
  const [logSettingsLoading, setLogSettingsLoading] = useState(true);
  const [logSettingsSaving, setLogSettingsSaving] = useState(false);

  useEffect(() => {
    (async () => {
      setLogSettingsLoading(true);
      try {
        const settings = await apiClient.getSystemLogSettings();
        setLogRetentionDays(settings.retentionDays);
        setPersistedLogTypes(settings.persistedLogTypes);
      } catch (err) {
        toast.error('Failed to load log settings');
      } finally {
        setLogSettingsLoading(false);
      }
    })();
  }, []);

  useEffect(() => {
    if (logSettingsLoading) return;
    setLogSettingsSaving(true);
    apiClient.setSystemLogSettings({ retentionDays: logRetentionDays, persistedLogTypes })
      .catch(() => toast.error('Failed to save log settings'))
      .finally(() => setLogSettingsSaving(false));
  }, [logRetentionDays, persistedLogTypes]);
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
  
  // Telemetry settings
  const [telemetrySettings, setTelemetrySettings] = useState<TelemetrySettings>({
    enabled: true,
    consoleLogging: import.meta.env.DEV === true,
    otlpEndpoint: import.meta.env.VITE_OTEL_EXPORTER_OTLP_ENDPOINT || '',
    samplingRate: 1.0,
    trackUserInteractions: true,
    trackApiCalls: true,
    trackComponentLifecycle: true
  });
  const [showOtlpEndpoint, setShowOtlpEndpoint] = useState(false);
  const [telemetryDirty, setTelemetryDirty] = useState(false);
  
  const [testing, setTesting] = useState(false);
  const [testOk, setTestOk] = useState<boolean | null>(null);
  const [testMessage, setTestMessage] = useState('');
  const [loading, setLoading] = useState(true);
  const [, ] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const { data: diagnostics } = useDiagnosticsSummary(45000);
  const { hasRole, isAuthenticated } = useAuth();
  const { trackUserInteraction } = useTelemetry();
  
  // Filament type editing
  const [editingFilamentType, setEditingFilamentType] = useState<FilamentType | null>(null);
  const [newFilamentType, setNewFilamentType] = useState({ name: '', hotend: 210, bed: 60 });
  const [showAddForm, setShowAddForm] = useState(false);

  // Sync draft when policy loads
  useEffect(() => {
    if (passwordPolicy && !policyDirty) {
      setDraftPolicy(passwordPolicy);
    }
  }, [passwordPolicy, policyDirty]);

  const loadSettings = useCallback(async () => {
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
  const ranges = nd.networkRanges.map((r: string) => ({ cidr: r }));
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
  }, []); // useCallback dependency array - empty since we only use setters

  // Load settings on mount
  useEffect(() => {
    loadSettings();
  }, [loadSettings]);

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

  const handleTelemetrySettingChange = (key: keyof TelemetrySettings, value: boolean | string | number) => {
    setTelemetrySettings(prev => ({
      ...prev,
      [key]: value
    }));
    setTelemetryDirty(true);

    trackUserInteraction('setting_change', `telemetry-${key}`, { 
      newValue: value,
      settingType: typeof value
    });
  };

  const saveTelemetrySettings = async () => {
    if (!hasRole('farm_admin')) return;
    
    try {
      trackUserInteraction('save', 'telemetry-settings', { 
        settingsCount: Object.keys(telemetrySettings).length 
      });
      
      // In a real application, these settings would be persisted to backend
      console.log('Telemetry settings saved:', telemetrySettings);
      setTelemetryDirty(false);
      toast.success('Telemetry settings saved');
    } catch (err) {
      console.error('Failed to save telemetry settings', err);
      toast.error('Failed to save telemetry settings');
    }
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


  if (settingsLoading) {
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

      {settingsError && (
        <div className="bg-red-900/50 border border-red-700 text-red-100 px-4 py-3 rounded">
          {settingsError}
        </div>
      )}

      {/* Render a SettingsPagelet for each settings class */}
      {settingsMetadata.map(meta => (
        <SettingsPagelet
          key={meta.key}
          metadata={meta}
          values={settingsValues[meta.key] || {}}
          onChange={(field, value) => handlePageletChange(meta.key, field, value)}
          onSave={() => handlePageletSave(meta.key)}
          isSaving={!!settingsSaving[meta.key]}
          error={settingsError ?? undefined}
        />
      ))}

      <DebugLoggingControls />
    </div>
  );
}

// DebugLoggingControls: Persist PrintFarmerDebug toggles in localStorage
function DebugLoggingControls() {
  const debugKeys = {
    printerCard: 'Printer Card',
    printerHistory: 'Printer History',
    printerRealtime: 'Printer Realtime',
    printerBulkActions: 'Printer Bulk Actions',
    printerSelection: 'Printer Selection',
    printerDashboard: 'Printer Dashboard',
    expandablePrinterCard: 'Expandable Printer Card',
    printerDiscoveryModal: 'Printer Discovery Modal',
    telemetrySettingsPage: 'Telemetry Settings Page',
  };

  // Local state mirrors window.PrintFarmerDebug
  const [debugState, setDebugState] = useState(() => {
    try {
      const raw = localStorage.getItem('PrintFarmerDebug');
      if (raw) return JSON.parse(raw);
    } catch { /* ignore localStorage error */ }
    return window.PrintFarmerDebug || {};
  });

  useEffect(() => {
    if (!window.PrintFarmerDebug) window.PrintFarmerDebug = {};
    Object.assign(window.PrintFarmerDebug, debugState);
    try {
      localStorage.setItem('PrintFarmerDebug', JSON.stringify(debugState));
    } catch { /* ignore localStorage error */ }
  }, [debugState]);

  const handleToggle = (key: string, checked: boolean) => {
    setDebugState((prev: Record<string, boolean>) => ({ ...prev, [key]: checked }));
  };

  const handleReset = () => {
    setDebugState({});
  };

  return (
    <>
      <div className="bg-pf-bg-1 border border-pf-border rounded-xl p-6 mt-8">
        <h2 className="text-xl font-semibold text-pf-text-primary mb-4">Debug Logging Controls</h2>
        <p className="text-sm text-pf-text-secondary mb-4">Enable or disable informational logging for specific UI components. Changes are saved and persist across reloads.</p>
        <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
          {Object.entries(debugKeys).map(([key, label]) => (
            <div key={key} className="flex items-center gap-2">
              <input
                id={`pfdebug-${key}`}
                type="checkbox"
                checked={!!debugState[key]}
                onChange={e => handleToggle(key, e.target.checked)}
                className="h-4 w-4"
              />
              <label htmlFor={`pfdebug-${key}`} className="text-sm text-pf-text-primary">{label}</label>
            </div>
          ))}
        </div>
        <div className="mt-4 flex gap-2">
          <button
            type="button"
            onClick={handleReset}
            className="px-4 py-2 bg-gray-600 text-white rounded hover:bg-gray-700"
          >
            Reset All
          </button>
        </div>
        <p className="text-xs text-pf-text-secondary mt-4">These toggles control live debug logging for development and troubleshooting. Settings are persisted in your browser and will remain after reload.</p>
      </div>

      {/* System Log Persistence settings are now managed via dynamic settings UI above. */}
    </>
  );
}
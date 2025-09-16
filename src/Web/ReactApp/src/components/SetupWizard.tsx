import React, { useState, useEffect } from 'react';
import { Shield, User, Mail, Lock, Eye, EyeOff, CheckCircle, Network, Server, Thermometer, Layers, AlertTriangle, Info } from 'lucide-react';
import { useSpoolman as useSpoolmanContext } from '@/contexts/SpoolmanHooks';
import { useAuth } from '@/contexts/AuthContext';
import { isValidCidr, normalizeUrl, normalizeSpoolmanBaseUrl } from '@/utils/validation';
import { apiClient } from '@/services/api';

interface SetupWizardProps {
  onComplete: () => void;
}

export function SetupWizard({ onComplete }: SetupWizardProps) {
  const [loading, setLoading] = useState(true);
  const [needsSetup, setNeedsSetup] = useState(false);
  const [submitting, setSubmitting] = useState(false);
  const [showPassword, setShowPassword] = useState(false);
  const [globalError, setGlobalError] = useState<string | null>(null);
  const { login } = useAuth();
  const [step, setStep] = useState(0); // 0 Account, 1 Network, 2 Spoolman, 3 Filament Presets, 4 Summary
  const [adminCreated, setAdminCreated] = useState(false);

  // Step: Account
  const [formData, setFormData] = useState({
    username: '',
    email: '',
    password: '',
    confirmPassword: '',
    firstName: '',
    lastName: '',
  });
  const [fieldErrors, setFieldErrors] = useState<{[K in keyof typeof formData]?: string}>({});

  // Step: Network
  const [networkRanges, setNetworkRanges] = useState<string[]>([]);
  const [discoveryTimeout, setDiscoveryTimeout] = useState(5000);
  const [maxConcurrentScans, setMaxConcurrentScans] = useState(20);
  const [scanPorts, setScanPorts] = useState<number[]>([80]);
  const [networkErrors, setNetworkErrors] = useState<string | null>(null);

  // Step: Spoolman
  const [spoolmanEnabled, setSpoolmanEnabled] = useState(false);
  const [spoolmanUrl, setSpoolmanUrl] = useState('');
  const [testingSpoolman, setTestingSpoolman] = useState(false);
  const [spoolmanTestResult, setSpoolmanTestResult] = useState<string | null>(null);
  const [spoolmanTestOk, setSpoolmanTestOk] = useState<boolean | null>(null);
  const [spoolmanVersion, setSpoolmanVersion] = useState<string | null>(null);
  const [spoolmanEndpoint, setSpoolmanEndpoint] = useState<string | null>(null);
  const [spoolmanErrorCategory, setSpoolmanErrorCategory] = useState<string | null>(null);
  const { setEnabled: setSpoolmanEnabledCtx, setBaseUrl: setSpoolmanBaseUrlCtx, updateProbeSuccess: updateSpoolmanSuccessCtx, updateProbeFailure: updateSpoolmanFailureCtx } = useSpoolmanContext();

  // Friendly mapping for error categories coming from backend probe
  const spoolmanErrorMeta: Record<string, { label: string; hint: string }> = {
    timeout: { label: 'Connection timed out', hint: 'The server did not respond within 5 seconds. Verify the host/port or network reachability.' },
    dns_failure: { label: 'Hostname could not be resolved', hint: 'Check the spelling or try using an IP address instead of a hostname.' },
    connection_refused: { label: 'Connection refused', hint: 'The service rejected the connection. Ensure Spoolman is running and the port is correct.' },
    tls_error: { label: 'TLS/SSL handshake failed', hint: 'If using a self-signed certificate, try http:// temporarily or trust the certificate.' },
    network_error: { label: 'Network error', hint: 'A low-level network failure occurred. Confirm Docker/network settings and that there is no firewall blocking access.' },
    http_error: { label: 'HTTP error', hint: 'The server responded with an HTTP-level failure. Check Spoolman logs for details.' },
    unknown: { label: 'Unknown error', hint: 'An unexpected error occurred. Verify the URL and try again.' }
  };

  const getSpoolmanFriendly = (cat: string | null) => {
    if (!cat) return null;
    return spoolmanErrorMeta[cat]?.label || cat;
  };

  // Step: Filament Presets
  interface FilamentPresetEditable { id?: string; name: string; hotend: number; bed: number; enabled: boolean; }
  const [filamentPresets, setFilamentPresets] = useState<FilamentPresetEditable[]>([]);
  const [loadingPresets, setLoadingPresets] = useState(false);
  const [presetError, setPresetError] = useState<string | null>(null);

  useEffect(() => { checkSetupStatus(); }, []);

  // Load filament types when entering presets step first time
  useEffect(() => {
    if (step === 3 && filamentPresets.length === 0) {
      void loadFilamentTypes();
    }
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [step]);

  // If setup not required, immediately signal completion (must be a hook, not conditional call inside render)
  useEffect(() => {
    if (!loading && !needsSetup) {
      onComplete();
    }
  }, [loading, needsSetup, onComplete]);

  const checkSetupStatus = async () => {
    try {
      const response = await fetch('/api/setup/status');
      if (response.ok) {
        const data = await response.json();
        setNeedsSetup(data.needsSetup);
      } else {
        setGlobalError('Failed to check setup status');
      }
    } catch (err) {
      setGlobalError('Error checking setup status');
      console.error('Setup status check error:', err);
    } finally {
      setLoading(false);
    }
  };
  const passwordPolicy = { minLength: 8, recommendUpper: true, recommendLower: true, recommendDigit: true, recommendSymbol: true };

  const validateAccount = () => {
    const errs: typeof fieldErrors = {};
    if (formData.username.trim().length < 3) errs.username = 'At least 3 characters';
    if (!formData.email || !/^[^@\s]+@[^@\s]+\.[^@\s]+$/.test(formData.email)) errs.email = 'Invalid email address';
    if (formData.password.length < passwordPolicy.minLength) errs.password = `Min ${passwordPolicy.minLength} characters`;
    if (formData.password !== formData.confirmPassword) errs.confirmPassword = 'Passwords do not match';
    if (!formData.firstName.trim()) errs.firstName = 'Required';
    if (!formData.lastName.trim()) errs.lastName = 'Required';
    setFieldErrors(errs);
    return Object.keys(errs).length === 0;
  };
  const nextFromAccount = () => {
    if (!validateAccount()) return;
    setStep(1);
  };

  // Network helpers
  const addNetworkRange = () => setNetworkRanges(r => [...r, '']);
  const updateNetworkRange = (i: number, cidr: string) => setNetworkRanges(r => r.map((x, idx) => idx === i ? cidr : x));
  const removeNetworkRange = (i: number) => setNetworkRanges(r => r.filter((_, idx) => idx !== i));

  const validateNetwork = () => {
    const filtered = networkRanges.filter(r => r.trim());
    for (const cidr of filtered) {
      if (!isValidCidr(cidr.trim())) {
        setNetworkErrors(`Invalid CIDR: ${cidr}`);
        return false;
      }
    }
    setNetworkErrors(null);
    return true;
  };

  const nextFromNetwork = () => {
    if (!validateNetwork()) return;
    setStep(2);
  };

  // Spoolman
  const testSpoolman = async () => {
    if (!spoolmanUrl) return;
    const normalized = normalizeUrl(spoolmanUrl);
    setTestingSpoolman(true);
    setSpoolmanTestResult(null);
    setSpoolmanTestOk(null);
    setSpoolmanVersion(null);
    setSpoolmanEndpoint(null);
    setSpoolmanErrorCategory(null);
    try {
      if (!/^https?:\/\//i.test(normalized)) {
        setSpoolmanTestOk(false);
        setSpoolmanTestResult('URL must start with http:// or https://');
        return;
      }
      const resp = await fetch('/api/spoolman/test', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ baseUrl: normalized }) });
      if (!resp.ok) {
        setSpoolmanTestOk(false);
        setSpoolmanTestResult(`Test request failed: HTTP ${resp.status}`);
        return;
      }
      const data = await resp.json();
      if (data.success) {
        setSpoolmanTestOk(true);
        const parts: string[] = ['Reachable'];
        if (data.version) { parts.push(`v${data.version}`); setSpoolmanVersion(data.version); }
        if (data.endpointTried) { parts.push(`endpoint ${data.endpointTried}`); setSpoolmanEndpoint(data.endpointTried); }
        setSpoolmanTestResult(parts.join(' · '));
        updateSpoolmanSuccessCtx({ version: data.version, endpoint: data.endpointTried });
      } else {
        setSpoolmanTestOk(false);
        if (data.errorCategory) setSpoolmanErrorCategory(data.errorCategory);
        // Prefer friendly label + server message if distinct
        if (data.errorCategory) {
          const label = getSpoolmanFriendly(data.errorCategory);
          const serverMsg: string | undefined = data.message;
          if (serverMsg && label && !serverMsg.toLowerCase().includes(label.toLowerCase())) {
            setSpoolmanTestResult(`${label}: ${serverMsg}`);
          } else if (label) {
            setSpoolmanTestResult(label + (serverMsg ? `: ${serverMsg}` : ''));
          } else {
            setSpoolmanTestResult(serverMsg || 'Unreachable');
          }
          updateSpoolmanFailureCtx({ errorCategory: data.errorCategory, message: data.message });
        } else {
          setSpoolmanTestResult(data.message || 'Unreachable');
        }
      }
    } catch (e) {
      setSpoolmanTestOk(false);
      setSpoolmanTestResult(e instanceof Error ? e.message : 'Test failed');
    } finally { setTestingSpoolman(false); }
  };

  const nextFromSpoolman = () => {
  if (spoolmanEnabled) {
      if (!spoolmanUrl || !/^https?:\/\//i.test(spoolmanUrl)) {
        setSpoolmanTestOk(false); setSpoolmanTestResult('Enter a valid URL'); return;
      }
    }
    setSpoolmanTestResult(null);
    setStep(3);
  };

  // Filament presets
  const loadFilamentTypes = async () => {
    setLoadingPresets(true);
    setPresetError(null);
    try {
      const list = await apiClient.getFilamentTypes();
      // Merge with default canonical suggestions in case DB empty
      const defaults = ['PLA','ABS','PETG','ASA','PC','PCTG','TPU','Wood'];
      const existingNames = new Set(list.map(f => f.name));
      const merged: FilamentPresetEditable[] = [
        ...list.map<FilamentPresetEditable>(f => ({ id: f.id, name: f.name, hotend: f.defaultTemperatures.hotend, bed: f.defaultTemperatures.bed, enabled: true })),
        ...defaults.filter(d => !existingNames.has(d)).map<FilamentPresetEditable>(d => ({ name: d, hotend: d === 'PLA' ? 205 : d === 'ABS' ? 230 : d === 'PETG' ? 240 : d === 'ASA' ? 245 : d === 'PC' ? 260 : d === 'PCTG' ? 235 : d === 'TPU' ? 220 : 210, bed: d === 'ABS' || d === 'ASA' ? 100 : d === 'PC' ? 110 : d === 'PETG' ? 85 : d === 'PCTG' ? 80 : d === 'Wood' ? 65 : 60, enabled: true }))
      ].sort((a,b) => a.name.localeCompare(b.name));
      setFilamentPresets(merged);
    } catch {
      setPresetError('Failed to load filament presets');
    } finally { setLoadingPresets(false); }
  };

  const togglePreset = (name: string) => setFilamentPresets(p => p.map(f => f.name === name ? { ...f, enabled: !f.enabled } : f));
  const updatePresetTemp = (name: string, field: 'hotend' | 'bed', value: number) => setFilamentPresets(p => p.map(f => f.name === name ? { ...f, [field]: value } : f));

  const nextFromPresets = () => { setStep(4); };

  const goBack = () => setStep(s => Math.max(0, s - 1));

  // Final submission orchestrating all steps
  const finalizeSetup = async () => {
    if (submitting) return;
    setSubmitting(true);
    setGlobalError(null);
    try {
      // 1. Ensure admin exists & login
      if (!adminCreated) {
        const resp = await fetch('/api/setup/initial-admin', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ username: formData.username, email: formData.email, password: formData.password, firstName: formData.firstName, lastName: formData.lastName }) });
        if (!resp.ok) {
          const txt = await resp.text();
          throw new Error(txt || 'Admin creation failed');
        }
        const result = await resp.json();
        if (!(result.success && result.token)) throw new Error(result.error || 'Admin creation failed');
        localStorage.setItem('auth-token', result.token);
        await login({ username: formData.username, password: formData.password });
        setAdminCreated(true);
      }

      // 2. Save network settings
      const netPayload = { networkRanges: networkRanges.filter(r => r.trim()).map(r => r.trim()), timeoutMs: discoveryTimeout, maxConcurrentScans, ports: scanPorts.filter(p => p > 0 && p < 65536) };
      await apiClient.saveNetworkDiscoverySettings(netPayload);

      // 3. Spoolman config (optional)
      if (spoolmanEnabled && spoolmanUrl) {
        const normalized = normalizeSpoolmanBaseUrl(spoolmanUrl);
        const token = localStorage.getItem('auth-token');
        const saveResp = await fetch('/api/spoolman/config', { method: 'POST', headers: { 'Content-Type': 'application/json', ...(token ? { Authorization: `Bearer ${token}` } : {}) }, body: JSON.stringify({ baseUrl: normalized }) });
        if (!saveResp.ok && saveResp.status !== 204) throw new Error('Failed to save Spoolman config');
        // Keep localStorage synchronized so Settings page reflects wizard-entered value immediately
        localStorage.setItem('spoolman-base-url', normalized);
      }

      // 4. Filament presets (create/update & delete unselected)
      if (filamentPresets.length) {
        const enabled = filamentPresets.filter(f => f.enabled);
        const payload: Record<string, { hotend: number; bed: number }> = {};
        enabled.forEach(f => { payload[f.name] = { hotend: f.hotend, bed: f.bed }; });
        await fetch('/api/filamenttype/presets', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ presets: payload }) });
        const disabledIds = filamentPresets.filter(f => !f.enabled && f.id).map(f => f.id!);
        for (const id of disabledIds) {
          try { await fetch(`/api/filamenttype/${id}`, { method: 'DELETE' }); } catch {/* ignore individual failures */}
        }
      }

      onComplete();
    } catch (e) {
      setGlobalError(e instanceof Error ? e.message : 'Setup failed');
    } finally { setSubmitting(false); }
  };

  const handleInputChange = (field: keyof typeof formData, value: string) => {
    setFormData(prev => ({ ...prev, [field]: value }));
    if (Object.keys(fieldErrors).length) setFieldErrors({});
    if (globalError) setGlobalError(null);
  };

  if (loading) {
    return (
      <div className="min-h-screen bg-pf-bg-0 flex items-center justify-center">
  <div className="pf-animate-spin rounded-full h-8 w-8 border-b-2 border-pf-accent"></div>
      </div>
    );
  }

  if (!needsSetup) return null;

  // --- Step renderers ---
  const renderAccountStep = () => (
    <div className="space-y-6">
      {/* Name Fields */}
      <div className="grid grid-cols-2 gap-4">
        <div>
          <label htmlFor="firstName" className="block text-sm font-medium text-pf-text-primary mb-2"><User className="inline h-4 w-4 mr-1"/>First Name *</label>
          <input id="firstName" type="text" value={formData.firstName} onChange={e => handleInputChange('firstName', e.target.value)} className="w-full px-3 py-2 border border-pf-border rounded-md focus:outline-none focus:ring-2 focus:ring-pf-accent bg-pf-bg-2 text-pf-text-primary" autoComplete="given-name" disabled={submitting} />
          {fieldErrors.firstName && <p className="text-xs text-red-500" role="alert">{fieldErrors.firstName}</p>}
        </div>
        <div>
          <label htmlFor="lastName" className="block text-sm font-medium text-pf-text-primary mb-2">Last Name *</label>
          <input id="lastName" type="text" value={formData.lastName} onChange={e => handleInputChange('lastName', e.target.value)} className="w-full px-3 py-2 border border-pf-border rounded-md focus:outline-none focus:ring-2 focus:ring-pf-accent bg-pf-bg-2 text-pf-text-primary" autoComplete="family-name" disabled={submitting} />
          {fieldErrors.lastName && <p className="text-xs text-red-500" role="alert">{fieldErrors.lastName}</p>}
        </div>
      </div>
      <div>
        <label htmlFor="username" className="block text-sm font-medium text-pf-text-primary mb-2"><User className="inline h-4 w-4 mr-1"/>Username *</label>
        <input id="username" value={formData.username} onChange={e => handleInputChange('username', e.target.value)} className="w-full px-3 py-2 border border-pf-border rounded-md focus:outline-none focus:ring-2 focus:ring-pf-accent bg-pf-bg-2 text-pf-text-primary" autoComplete="username" disabled={submitting} />
        {fieldErrors.username && <p className="text-xs text-red-500" role="alert">{fieldErrors.username}</p>}
      </div>
      <div>
        <label htmlFor="email" className="block text-sm font-medium text-pf-text-primary mb-2"><Mail className="inline h-4 w-4 mr-1"/>Email *</label>
        <input id="email" type="email" value={formData.email} onChange={e => handleInputChange('email', e.target.value)} className="w-full px-3 py-2 border border-pf-border rounded-md focus:outline-none focus:ring-2 focus:ring-pf-accent bg-pf-bg-2 text-pf-text-primary" autoComplete="email" disabled={submitting} />
        {fieldErrors.email && <p className="text-xs text-red-500" role="alert">{fieldErrors.email}</p>}
      </div>
      <div>
        <label htmlFor="password" className="block text-sm font-medium text-pf-text-primary mb-2"><Lock className="inline h-4 w-4 mr-1"/>Password *</label>
        <div className="relative">
          <input id="password" type={showPassword ? 'text':'password'} value={formData.password} onChange={e => handleInputChange('password', e.target.value)} autoComplete="new-password" className="w-full px-3 py-2 border border-pf-border rounded-md focus:outline-none focus:ring-2 focus:ring-pf-accent bg-pf-bg-2 text-pf-text-primary pr-10" disabled={submitting} />
          <button type="button" onClick={() => setShowPassword(p => !p)} className="absolute right-3 top-1/2 -translate-y-1/2 text-pf-text-tertiary" disabled={submitting}>{showPassword ? <EyeOff className="h-4 w-4"/> : <Eye className="h-4 w-4"/>}</button>
        </div>
        <ul className="mt-2 text-xs space-y-0.5">
          <li className={formData.password.length >= passwordPolicy.minLength ? 'text-green-500':'text-pf-text-tertiary'}>Min {passwordPolicy.minLength} characters</li>
          <li className={/[A-Z]/.test(formData.password)?'text-green-500':'text-pf-text-tertiary'}>Uppercase (recommended)</li>
          <li className={/[a-z]/.test(formData.password)?'text-green-500':'text-pf-text-tertiary'}>Lowercase (recommended)</li>
          <li className={/[0-9]/.test(formData.password)?'text-green-500':'text-pf-text-tertiary'}>Digit (recommended)</li>
          <li className={/[^A-Za-z0-9]/.test(formData.password)?'text-green-500':'text-pf-text-tertiary'}>Symbol (recommended)</li>
        </ul>
        {fieldErrors.password && <p className="text-xs text-red-500" role="alert">{fieldErrors.password}</p>}
      </div>
      <div>
        <label htmlFor="confirmPassword" className="block text-sm font-medium text-pf-text-primary mb-2"><Lock className="inline h-4 w-4 mr-1"/>Confirm Password *</label>
        <input id="confirmPassword" type="password" value={formData.confirmPassword} onChange={e => handleInputChange('confirmPassword', e.target.value)} autoComplete="new-password" className="w-full px-3 py-2 border border-pf-border rounded-md focus:outline-none focus:ring-2 focus:ring-pf-accent bg-pf-bg-2 text-pf-text-primary" disabled={submitting} />
        {fieldErrors.confirmPassword && <p className="text-xs text-red-500" role="alert">{fieldErrors.confirmPassword}</p>}
      </div>
      <div className="flex justify-end">
        <button type="button" onClick={nextFromAccount} disabled={submitting} className="px-4 py-2 bg-pf-accent text-white rounded hover:bg-pf-accent-dark flex items-center gap-2"><CheckCircle className="h-4 w-4"/>Next</button>
      </div>
    </div>
  );

  const renderNetworkStep = () => (
    <div className="space-y-6">
      <div>
        <h2 className="text-lg font-semibold flex items-center gap-2"><Network className="h-5 w-5"/>Network Discovery</h2>
        <p className="text-sm text-pf-text-secondary">Provide CIDR ranges to scan for printers (e.g. 192.168.1.0/24). Leave empty to disable discovery.</p>
      </div>
      <div className="space-y-2">
        {networkRanges.map((r,i) => (
          <div key={i} className="flex gap-2">
            <input value={r} onChange={e => updateNetworkRange(i, e.target.value)} placeholder="192.168.1.0/24" className="flex-1 px-3 py-2 bg-pf-bg-2 border border-pf-border rounded" />
            <button type="button" onClick={() => removeNetworkRange(i)} className="px-3 py-2 text-red-400" aria-label="Remove range">×</button>
          </div>
        ))}
        <button type="button" onClick={addNetworkRange} className="px-3 py-2 bg-pf-bg-2 border border-pf-border rounded text-sm">Add Range</button>
        {networkErrors && <p className="text-xs text-red-500" role="alert">{networkErrors}</p>}
      </div>
      <div className="grid grid-cols-3 gap-4">
        <div>
          <label htmlFor="nw-timeout" className="block text-xs mb-1">Timeout (ms)</label>
          <input id="nw-timeout" name="nw-timeout" type="number" value={discoveryTimeout} placeholder="5000" onChange={e => setDiscoveryTimeout(Number(e.target.value))} className="w-full px-2 py-1 bg-pf-bg-2 border border-pf-border rounded" />
        </div>
        <div>
          <label htmlFor="nw-concurrent" className="block text-xs mb-1">Max Concurrent</label>
          <input id="nw-concurrent" name="nw-concurrent" type="number" value={maxConcurrentScans} placeholder="20" onChange={e => setMaxConcurrentScans(Number(e.target.value))} className="w-full px-2 py-1 bg-pf-bg-2 border border-pf-border rounded" />
        </div>
        <div>
          <label htmlFor="nw-ports" className="block text-xs mb-1">Ports (comma separated)</label>
          <input id="nw-ports" name="nw-ports" type="text" value={scanPorts.join(',')} placeholder="80" onChange={e => setScanPorts(e.target.value.split(',').map(p => Number(p.trim())).filter(n => !isNaN(n)))} className="w-full px-2 py-1 bg-pf-bg-2 border border-pf-border rounded" />
        </div>
      </div>
      <div className="flex justify-between">
        <button type="button" onClick={goBack} className="px-4 py-2 bg-pf-bg-2 border border-pf-border rounded">Back</button>
        <button type="button" onClick={nextFromNetwork} className="px-4 py-2 bg-pf-accent text-white rounded">Next</button>
      </div>
    </div>
  );

  const renderSpoolmanStep = () => (
    <div className="space-y-6">
      <div>
        <h2 className="text-lg font-semibold flex items-center gap-2"><Server className="h-5 w-5"/>Spoolman Integration</h2>
        <p className="text-sm text-pf-text-secondary">Optionally connect to a Spoolman instance now or later in Settings.</p>
      </div>
      <div className="flex items-center gap-2">
  <input id="useSpoolman" type="checkbox" checked={spoolmanEnabled} onChange={e => { setSpoolmanEnabled(e.target.checked); setSpoolmanEnabledCtx(e.target.checked); if (e.target.checked && spoolmanUrl) setSpoolmanBaseUrlCtx(spoolmanUrl); }} />
        <label htmlFor="useSpoolman" className="text-sm">Enable Spoolman</label>
      </div>
  {spoolmanEnabled && (
        <div className="space-y-2">
          <input
            type="url"
            value={spoolmanUrl}
            onChange={e => { setSpoolmanUrl(e.target.value); setSpoolmanBaseUrlCtx(e.target.value || null); }}
            placeholder="http://spoolman:7912"
            className="w-full px-3 py-2 bg-pf-bg-2 border border-pf-border rounded"
          />
          <button
            type="button"
            onClick={testSpoolman}
            disabled={testingSpoolman}
            className="px-3 py-2 bg-blue-600 text-white rounded text-sm"
          >
            {testingSpoolman ? 'Testing...' : 'Test URL'}
          </button>
          {spoolmanTestResult && (
            <p className={`text-xs ${spoolmanTestOk ? 'text-green-500':'text-red-500'}`}>{spoolmanTestResult}</p>
          )}
          {!spoolmanTestOk && spoolmanErrorCategory && (
            <div className="relative text-xs text-red-400 bg-red-950/30 border border-red-800/40 rounded p-2 flex gap-2 group">
              <AlertTriangle className="h-4 w-4 shrink-0" />
              <div className="space-y-1">
                <div className="font-semibold">{getSpoolmanFriendly(spoolmanErrorCategory)}</div>
                {spoolmanErrorMeta[spoolmanErrorCategory]?.hint && (
                  <div className="opacity-80 leading-snug">{spoolmanErrorMeta[spoolmanErrorCategory].hint}</div>
                )}
                {/* Info icon with tooltip raw message */}
                {spoolmanTestResult && (
                  <div className="flex items-center gap-1 text-pf-text-tertiary">
                    <Info className="h-3 w-3" />
                    <span className="truncate max-w-[220px]" title={spoolmanTestResult}>{spoolmanTestResult}</span>
                  </div>
                )}
              </div>
            </div>
          )}
        </div>
      )}
      <div className="flex justify-between">
        <button type="button" onClick={goBack} className="px-4 py-2 bg-pf-bg-2 border border-pf-border rounded">Back</button>
        <button type="button" onClick={nextFromSpoolman} className="px-4 py-2 bg-pf-accent text-white rounded">Next</button>
      </div>
    </div>
  );

  const renderFilamentStep = () => (
    <div className="space-y-6">
      <div>
        <h2 className="text-lg font-semibold flex items-center gap-2"><Thermometer className="h-5 w-5"/>Filament Presets</h2>
        <p className="text-sm text-pf-text-secondary">Select which material presets to enable. You can edit temperatures or manage later.</p>
      </div>
      {loadingPresets && <div className="text-sm text-pf-text-secondary">Loading presets...</div>}
      {presetError && <div className="text-sm text-red-500" role="alert">{presetError}</div>}
      <div className="space-y-2 max-h-64 overflow-auto pr-1">
        {filamentPresets.map(fp => (
          <div key={fp.name} className={`flex items-center gap-3 p-2 border border-pf-border rounded bg-pf-bg-2 ${!fp.enabled ? 'opacity-60':''}`}> 
            <input type="checkbox" checked={fp.enabled} onChange={() => togglePreset(fp.name)} aria-label={`Enable ${fp.name}`} />
            <span className="w-20 font-medium">{fp.name}</span>
            <label className="text-xs text-pf-text-secondary">Hotend</label>
            <input type="number" value={fp.hotend} aria-label={`${fp.name} hotend temperature`} placeholder="Hotend" onChange={e => updatePresetTemp(fp.name,'hotend',Number(e.target.value))} className="w-20 px-2 py-1 bg-pf-bg-1 border border-pf-border rounded" />
            <label className="text-xs text-pf-text-secondary">Bed</label>
            <input type="number" value={fp.bed} aria-label={`${fp.name} bed temperature`} placeholder="Bed" onChange={e => updatePresetTemp(fp.name,'bed',Number(e.target.value))} className="w-20 px-2 py-1 bg-pf-bg-1 border border-pf-border rounded" />
          </div>
        ))}
        {filamentPresets.length === 0 && !loadingPresets && (
          <div className="text-sm text-pf-text-secondary">No presets found.</div>
        )}
      </div>
      <div className="flex justify-between">
        <button type="button" onClick={goBack} className="px-4 py-2 bg-pf-bg-2 border border-pf-border rounded">Back</button>
        <button type="button" onClick={nextFromPresets} className="px-4 py-2 bg-pf-accent text-white rounded">Next</button>
      </div>
    </div>
  );

  const renderSummaryStep = () => {
    const enabledPresets = filamentPresets.filter(f => f.enabled).length;
    return (
      <div className="space-y-6">
        <div>
          <h2 className="text-lg font-semibold flex items-center gap-2"><Layers className="h-5 w-5"/>Summary</h2>
          <p className="text-sm text-pf-text-secondary">Review your initial configuration before finishing setup.</p>
        </div>
        <div className="text-sm space-y-2">
          <div><strong>Admin:</strong> {formData.username} ({formData.email})</div>
          <div><strong>Network Ranges:</strong> {networkRanges.filter(r=>r.trim()).length ? networkRanges.filter(r=>r.trim()).join(', ') : 'None (discovery disabled)'}</div>
          <div><strong>Spoolman:</strong> {spoolmanEnabled ? (
            <span>
              {normalizeSpoolmanBaseUrl(spoolmanUrl)} {spoolmanTestOk && spoolmanVersion && (
                <span className="text-green-500">(v{spoolmanVersion}{spoolmanEndpoint ? ` · ${spoolmanEndpoint}`:''})</span>
              )}
              {!spoolmanTestOk && spoolmanErrorCategory && (
                <span className="text-red-400 inline-flex items-center gap-1">
                  (last test: {getSpoolmanFriendly(spoolmanErrorCategory)}{getSpoolmanFriendly(spoolmanErrorCategory) !== spoolmanErrorCategory ? ` [${spoolmanErrorCategory}]` : ''})
                  {spoolmanTestResult && (
                    <span className="text-pf-text-tertiary" title={spoolmanTestResult}>
                      <Info className="h-3 w-3" />
                    </span>
                  )}
                </span>
              )}
            </span>
          ) : 'Not configured'}</div>
          <div><strong>Filament Presets Enabled:</strong> {enabledPresets}</div>
        </div>
        {globalError && <div className="text-sm text-red-500" role="alert">{globalError}</div>}
        <div className="flex justify-between">
          <button type="button" onClick={goBack} className="px-4 py-2 bg-pf-bg-2 border border-pf-border rounded" disabled={submitting}>Back</button>
          <button type="button" onClick={finalizeSetup} disabled={submitting} className="px-4 py-2 bg-green-600 text-white rounded flex items-center gap-2">{submitting ? (<><div className="pf-animate-spin h-4 w-4 border-b-2 border-white rounded-full"></div>Finishing...</>) : (<><CheckCircle className="h-4 w-4"/>Finish Setup</> )}</button>
        </div>
      </div>
    );
  };

  return (
    <div className="min-h-screen bg-pf-bg-0 flex items-center justify-center p-4">
      <div className="w-full max-w-2xl bg-pf-bg-1 border border-pf-border shadow-xl rounded-xl p-8">
        <div className="flex items-center gap-4 mb-6">
          <div className="flex items-center justify-center w-14 h-14 bg-pf-accent bg-opacity-15 rounded-full">
            <Shield className="h-7 w-7 text-pf-accent" />
          </div>
          <div>
            <h1 className="text-2xl font-bold text-pf-text-primary">Welcome to PrintFarmer</h1>
            <p className="text-pf-text-secondary text-sm">Initial configuration wizard</p>
          </div>
        </div>
        <div className="mb-4 flex items-center gap-2 text-xs flex-wrap">
          {['Account','Network','Spoolman','Filament','Summary'].map((label, idx) => (
            <div key={label} className={`px-2 py-1 rounded ${idx===step ? 'bg-pf-accent text-white':'bg-pf-bg-2 text-pf-text-secondary'}`}>{idx+1}. {label}</div>
          ))}
        </div>
        {globalError && step !== 4 && <div className="mb-4 text-sm text-red-500" role="alert">{globalError}</div>}
        {step === 0 && renderAccountStep()}
        {step === 1 && renderNetworkStep()}
        {step === 2 && renderSpoolmanStep()}
        {step === 3 && renderFilamentStep()}
        {step === 4 && renderSummaryStep()}
        <div className="mt-6 text-center text-xs text-pf-text-tertiary">You can change these settings later in the Settings page.</div>
      </div>
    </div>
  );
}
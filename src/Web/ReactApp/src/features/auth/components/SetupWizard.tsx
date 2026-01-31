/* eslint-disable local/pf-no-raw-html-controls */
import React, { useState, useEffect, useActionState } from 'react';
import { useFormStatus } from 'react-dom';
import { AccountIcon, EmailIcon, LockIcon, EyeIcon, EyeOffIcon, CheckCircleIcon, NetworkIcon, ServerIcon, LayersIcon, InfoIcon, WiFiIcon, SearchIcon, AlertIcon } from '@/common/components/icons/MdiIcons';
import { useSpoolman as useSpoolmanContext } from '@/contexts/SpoolmanHooks';
import { useAuth } from '@/features/auth/hooks/useAuth';
import { Button } from '@/common/components/ui';
import { useHealthStatus } from '@/common/hooks/useApi';
import { useSpoolmanNetworkScan } from '@/common/hooks/useSpoolmanNetworkScan';
import { isValidCidr, normalizeUrl, normalizeSpoolmanBaseUrl } from '@/common/utils/validation';
import { apiClient } from '@/services/api';

// Move password policy outside component to prevent unnecessary re-renders
const passwordPolicy = { 
  minLength: 8, 
  recommendUpper: true, 
  recommendLower: true, 
  recommendDigit: true, 
  recommendSymbol: true 
};

/**
 * React 19 Form State for Setup Account Step
 */
interface SetupAccountFormState {
  errors: {[K in keyof SetupFormData]?: string};
  fieldErrors?: Record<string, string>;
}

interface SetupFormData {
  username: string;
  email: string;
  password: string;
  confirmPassword: string;
  firstName: string;
  lastName: string;
}

/**
 * React 19 Action: Validates account creation form data
 * Note: Actual API call happens in finalizeSetup, this just validates
 */
async function setupAccountAction(
  prevState: SetupAccountFormState,
  formData: FormData
): Promise<SetupAccountFormState> {
  const username = (formData.get('username') as string)?.trim() || '';
  const email = (formData.get('email') as string)?.trim() || '';
  const password = formData.get('password') as string;
  const confirmPassword = formData.get('confirmPassword') as string;

  const errors: SetupAccountFormState['errors'] = {};

  // Validation
  if (!username) errors.username = 'Username is required';
  if (!email) errors.email = 'Email is required';
  else if (!/^[^@\s]+@[^@\s]+\.[^@\s]+$/.test(email)) errors.email = 'Invalid email format';
  if (!password) errors.password = 'Password is required';
  else if (password.length < 8) errors.password = 'Password must be at least 8 characters';
  if (password !== confirmPassword) errors.confirmPassword = 'Passwords do not match';

  if (Object.keys(errors).length > 0) {
    return { errors };
  }

  return { errors };
}

/**
 * Setup Account Submit Button using React 19 useFormStatus
 * Note: Not currently used - account submission uses nextFromAccount handler
 */
// eslint-disable-next-line @typescript-eslint/no-unused-vars
function SetupAccountSubmitButton({ isDisabled }: { isDisabled: boolean }) {
  const { pending } = useFormStatus();

  return (
    <Button
      type="submit"
      variant="primary"
      disabled={pending || isDisabled}
      className="w-full"
    >
      {pending ? 'Creating Account...' : 'Create Account'}
    </Button>
  );
}

interface SetupWizardProps {
  onComplete: () => void;
}

export function SetupWizard({ onComplete }: SetupWizardProps) {
  const [initializing, setInitializing] = useState(true);
  const [loading, setLoading] = useState(true);
  const [needsSetup, setNeedsSetup] = useState(false);
  const [submitting, setSubmitting] = useState(false);
  const [showPassword, setShowPassword] = useState(false);
  const [globalError, setGlobalError] = useState<string | null>(null);
  const { login } = useAuth();
  const [step, setStep] = useState(0); // 0 Account, 1 Network, 2 Spoolman, 3 Summary
  const [adminCreated, setAdminCreated] = useState(false);

  // Check initialization status
  const { data: healthStatus, isLoading: healthLoading, refetch: refetchHealth } = useHealthStatus();

  // Step: Account
  const [formData, setFormData] = useState({
    username: '',
    email: '',
    password: '',
    confirmPassword: '',
    firstName: '',
    lastName: '',
  });
  
  // React 19 useActionState for account form validation
  // Note: Not currently used - account submission uses nextFromAccount handler
  // eslint-disable-next-line @typescript-eslint/no-unused-vars
  const [accountFormState, accountFormAction] = useActionState(setupAccountAction, {
    errors: {},
  });

  // Step: Network
  // Network Discovery Settings state (migrated from DTO to settings class)
  const [networkDiscoverySettings, setNetworkDiscoverySettings] = useState<import("@/types/NetworkDiscoverySettings").NetworkDiscoverySettings | null>(null);
  // Fetch network discovery settings from backend on mount
  useEffect(() => {
    // The unified settings API expects the AppSetting "key" (AppSettingAttribute.Key),
    // which for NetworkDiscovery is "NetworkDiscovery" (not the class name).
    apiClient.getSettings<import("@/types/NetworkDiscoverySettings").NetworkDiscoverySettings>('NetworkDiscovery')
      .then(settings => setNetworkDiscoverySettings(settings))
      .catch(() => {
        // fallback to server canonical defaults if fetch fails
        setNetworkDiscoverySettings({
          enableDiscovery: true,
          discoverySubnets: ["10.0.0.0/24","10.0.5.0/24"],
          clientTimeoutMs: 200,
          requestDelayMs: 100,
          maxConcurrentRequests: 20,
          maxRetries: 2,
          ports: [80],
        });
      });
  }, []);
  // Additional UI state for advanced fields (if needed)
  // (Removed unused discoveryTimeout, maxConcurrentScans, scanPorts)
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

  // Synchronize context from local state (prevents infinite re-render)
  useEffect(() => { setSpoolmanEnabledCtx(spoolmanEnabled); }, [spoolmanEnabled, setSpoolmanEnabledCtx]);
  useEffect(() => { setSpoolmanBaseUrlCtx(spoolmanUrl); }, [spoolmanUrl, setSpoolmanBaseUrlCtx]);

  // Fetch Spoolman settings from backend on mount (pre-populate from deployment config)
  useEffect(() => {
    apiClient.getSettings<import("@/types/SpoolmanSettings").SpoolmanSettings>('Spoolman')
      .then(settings => {
        if (settings?.baseUrl) {
          setSpoolmanUrl(settings.baseUrl);
          setSpoolmanEnabled(true);
        }
      })
      .catch(() => {
        // Silently fail - user can configure manually
      });
  }, []);

  // Network scanning for Spoolman discovery
  const { isScanning, results: scanResults, error: scanError, scanNetwork, reset: resetScan, availableInstances } = useSpoolmanNetworkScan();

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

  useEffect(() => { checkSetupStatus(); }, []);

  // Returns a friendly label for a Spoolman error category
  const getSpoolmanFriendly = (cat: string | null): string => {
    if (!cat) return '';
    return spoolmanErrorMeta[cat]?.label || cat;
  };

  // Monitor initialization status
  useEffect(() => {
    if (!healthLoading && healthStatus) {
      if (healthStatus.kind === 'detailed' && healthStatus.startup) {
        const { ready, failed } = healthStatus.startup;
        
        if (failed) {
          setGlobalError(`System initialization failed: ${healthStatus.startup.failureMessage || 'Unknown error'}`);
          setInitializing(false);
        } else if (ready) {
          setInitializing(false);
        } else {
          // Still initializing, continue polling
          setTimeout(() => refetchHealth(), 1000);
        }
      } else {
        // Basic health status or no startup info, assume ready
        setInitializing(false);
      }
    }
  }, [healthStatus, healthLoading, refetchHealth]);

  const addNetworkRange = () => {
    if (!networkDiscoverySettings) return;
    setNetworkDiscoverySettings(prev => prev ? { ...prev, discoverySubnets: [...prev.discoverySubnets, ""] } : prev);
  };
  const updateNetworkRange = (idx: number, value: string) => {
    if (!networkDiscoverySettings) return;
    setNetworkDiscoverySettings(prev => {
      if (!prev) return prev;
      const arr = [...prev.discoverySubnets];
      arr[idx] = value;
      return { ...prev, discoverySubnets: arr };
    });
  };
  const removeNetworkRange = (idx: number) => {
    if (!networkDiscoverySettings) return;
    setNetworkDiscoverySettings(prev => {
      if (!prev) return prev;
      const arr = [...prev.discoverySubnets];
      arr.splice(idx, 1);
      return { ...prev, discoverySubnets: arr };
    });
  };
  const checkSetupStatus = async () => {
    try {
      const data = await apiClient.getSetupStatus() as { needsSetup: boolean };
      setNeedsSetup(data.needsSetup);
    } catch (err) {
      setGlobalError('Error checking setup status');
      console.error('Setup status check error:', err);
    } finally {
      setLoading(false);
    }
  };

  const validateAccount = () => {
    const errs: SetupAccountFormState['errors'] = {};
    if (formData.username.trim().length < 3) errs.username = 'At least 3 characters';
    if (!formData.email || !/^[^@\s]+@[^@\s]+\.[^@\s]+$/.test(formData.email)) errs.email = 'Invalid email address';
    if (formData.password.length < passwordPolicy.minLength) errs.password = `Min ${passwordPolicy.minLength} characters`;
    if (formData.password !== formData.confirmPassword) errs.confirmPassword = 'Passwords do not match';
    if (!formData.firstName.trim()) errs.firstName = 'Required';
    if (!formData.lastName.trim()) errs.lastName = 'Required';
    return errs;
  };
  const nextFromAccount = () => {
    const errors = validateAccount();
    if (Object.keys(errors).length > 0) return;
    setStep(1);
  };

  // (all helpers now use networkDiscoverySettings)

  const validateNetwork = () => {
    if (!networkDiscoverySettings) return false;
    const filtered = networkDiscoverySettings.discoverySubnets.filter((r: string) => r.trim());
    for (const cidr of filtered) {
      if (!isValidCidr(cidr.trim())) {
        setNetworkErrors(`Invalid CIDR: ${cidr}`);
        return false;
      }
    }
    setNetworkErrors(null);
    return true;
  };

  const nextFromNetwork = async () => {
    if (!validateNetwork()) return;
    setNetworkErrors(null);
    try {
      // Compose payload for API (settings class)
      if (!networkDiscoverySettings) return;
      const netPayload: import("@/types/NetworkDiscoverySettings").NetworkDiscoverySettings = {
        ...networkDiscoverySettings,
        discoverySubnets: networkDiscoverySettings.discoverySubnets.filter((r: string) => r.trim()).map((r: string) => r.trim()),
      };
      await apiClient.saveSettings('NetworkDiscovery', netPayload);
      setStep(2);
    } catch (e) {
      setNetworkErrors(e instanceof Error ? e.message : 'Failed to save network settings');
    }
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
      const data = (await apiClient.testSpoolmanConnection()) as unknown as { success?: boolean; version?: string; endpointTried?: string; errorCategory?: string; message?: string };
      if (data.success) {
        setSpoolmanTestOk(true);
        const parts: string[] = ['Reachable'];
        if (data.version) { parts.push(`v${data.version}`); setSpoolmanVersion(data.version); }
        if (data.endpointTried) { parts.push(`endpoint ${data.endpointTried}`); setSpoolmanEndpoint(data.endpointTried); }
        setSpoolmanTestResult(parts.join(' · '));
        updateSpoolmanSuccessCtx({ version: data.version || null, endpoint: data.endpointTried || null });
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
          updateSpoolmanFailureCtx({ errorCategory: data.errorCategory || '', message: data.message || '' });
        } else {
          setSpoolmanTestResult((data.message as string) || 'Unreachable');
        }
      }
    } catch (e) {
      setSpoolmanTestOk(false);
      setSpoolmanTestResult(e instanceof Error ? e.message : 'Test failed');
    } finally { setTestingSpoolman(false); }
  };

  const selectSpoolmanInstance = (url: string) => {
    setSpoolmanUrl(url);
    setSpoolmanBaseUrlCtx(url);
    // Auto-test the selected instance
    testSpoolman();
  };

  const handleNetworkScan = async () => {
    resetScan();
    await scanNetwork();
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

  const goBack = () => setStep(s => Math.max(0, s - 1));

  // Final submission orchestrating all steps
  const finalizeSetup = async () => {
    if (submitting) return;
    setSubmitting(true);
    setGlobalError(null);
    try {
      // 1. Ensure admin exists & login
      if (!adminCreated) {
        const result = await apiClient.createInitialAdmin({ 
          username: formData.username, 
          email: formData.email, 
          password: formData.password, 
          firstName: formData.firstName, 
          lastName: formData.lastName 
        });
        const adminResult = result as unknown as { success?: boolean; token?: string; error?: string };
        if (!(adminResult.success && adminResult.token)) throw new Error((adminResult.error as string) || 'Admin creation failed');
        localStorage.setItem('auth-token', (adminResult.token as string));
        await login({ username: formData.username, password: formData.password });
        setAdminCreated(true);
      }

      // 2. Save network settings
      if (!networkDiscoverySettings) return;
      const netPayload: import("@/types/NetworkDiscoverySettings").NetworkDiscoverySettings = {
        ...networkDiscoverySettings,
        discoverySubnets: networkDiscoverySettings.discoverySubnets.filter((r: string) => r.trim()).map((r: string) => r.trim()),
      };
  await apiClient.saveSettings('NetworkDiscovery', netPayload);

      // 3. Spoolman config (optional)
        if (spoolmanEnabled && spoolmanUrl) {
        const normalized = normalizeSpoolmanBaseUrl(spoolmanUrl);
        const token = localStorage.getItem('auth-token');
          const win = window as unknown as { PrintFarmerDebug?: Record<string, unknown> };
          if (win.PrintFarmerDebug?.setupWizard) {
            console.log('[SetupWizard] JWT token before Spoolman config request:', token);
          }
        await apiClient.saveSpoolmanConfig({ baseUrl: normalized });
        // Keep localStorage synchronized so Settings page reflects wizard-entered value immediately
        localStorage.setItem('spoolman-base-url', normalized);
      }

      onComplete();
    } catch (e) {
      setGlobalError(e instanceof Error ? e.message : 'Setup failed');
    } finally { setSubmitting(false); }
  };

  const handleInputChange = (field: keyof typeof formData, value: string) => {
    setFormData(prev => ({ ...prev, [field]: value }));
    if (Object.keys(accountFormState.errors).length) {
      // Errors would be cleared on form submission
    }
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
  // ...existing code...
  // Remove duplicate addNetworkRange, updateNetworkRange, removeNetworkRange below (keep only top-level)
  // ...existing code...
  const renderAccountStep = () => (
    <form className="space-y-6" onSubmit={(e) => { e.preventDefault(); nextFromAccount(); }}>
      {/* Name Fields */}
      <div className="grid grid-cols-2 gap-4">
        <div>
          <label htmlFor="firstName" className="block text-sm font-medium text-pf-text-primary mb-2"><AccountIcon className="inline h-4 w-4 mr-1"/>First Name *</label>
          <input id="firstName" type="text" value={formData.firstName} onChange={e => handleInputChange('firstName', e.target.value)} className="w-full px-3 py-2 border border-pf-border rounded-md focus:outline-hidden focus:ring-2 focus:ring-pf-accent bg-pf-bg-2 text-pf-text-primary" autoComplete="given-name" disabled={submitting} />
          {accountFormState.errors.firstName && <p className="text-xs text-red-500" role="alert">{accountFormState.errors.firstName}</p>}
        </div>
        <div>
          <label htmlFor="lastName" className="block text-sm font-medium text-pf-text-primary mb-2">Last Name *</label>
          <input id="lastName" type="text" value={formData.lastName} onChange={e => handleInputChange('lastName', e.target.value)} className="w-full px-3 py-2 border border-pf-border rounded-md focus:outline-hidden focus:ring-2 focus:ring-pf-accent bg-pf-bg-2 text-pf-text-primary" autoComplete="family-name" disabled={submitting} />
          {accountFormState.errors.lastName && <p className="text-xs text-red-500" role="alert">{accountFormState.errors.lastName}</p>}
        </div>
      </div>
      <div>
        <label htmlFor="username" className="block text-sm font-medium text-pf-text-primary mb-2"><AccountIcon className="inline h-4 w-4 mr-1"/>Username *</label>
        <input id="username" type="text" name="username" value={formData.username} onChange={e => handleInputChange('username', e.target.value)} className="w-full px-3 py-2 border border-pf-border rounded-md focus:outline-hidden focus:ring-2 focus:ring-pf-accent bg-pf-bg-2 text-pf-text-primary" autoComplete="username" disabled={submitting} />
        {accountFormState.errors.username && <p className="text-xs text-red-500" role="alert">{accountFormState.errors.username}</p>}
      </div>
      <div>
        <label htmlFor="email" className="block text-sm font-medium text-pf-text-primary mb-2"><EmailIcon className="inline h-4 w-4 mr-1"/>Email *</label>
        <input id="email" type="email" name="email" value={formData.email} onChange={e => handleInputChange('email', e.target.value)} className="w-full px-3 py-2 border border-pf-border rounded-md focus:outline-hidden focus:ring-2 focus:ring-pf-accent bg-pf-bg-2 text-pf-text-primary" autoComplete="email" disabled={submitting} />
        {accountFormState.errors.email && <p className="text-xs text-red-500" role="alert">{accountFormState.errors.email}</p>}
      </div>
      <div>
        <label htmlFor="password" className="block text-sm font-medium text-pf-text-primary mb-2"><LockIcon className="inline h-4 w-4 mr-1"/>Password *</label>
        <div className="relative">
          <input id="password" type={showPassword ? 'text':'password'} name="password" value={formData.password} onChange={e => handleInputChange('password', e.target.value)} autoComplete="new-password" className="w-full px-3 py-2 border border-pf-border rounded-md focus:outline-hidden focus:ring-2 focus:ring-pf-accent bg-pf-bg-2 text-pf-text-primary pr-10" disabled={submitting} />
          <Button
            type="button"
            onClick={() => setShowPassword(p => !p)}
            variant="subtle"
            size="sm"
            className="absolute right-3 top-1/2 -translate-y-1/2 p-0! h-auto!"
            disabled={submitting}
            title={showPassword ? 'Hide password' : 'Show password'}
          >
            {showPassword ? <EyeOffIcon className="h-4 w-4" /> : <EyeIcon className="h-4 w-4" />}
          </Button>
        </div>
        <ul className="mt-2 text-xs space-y-0.5">
          <li className={formData.password.length >= passwordPolicy.minLength ? 'text-green-500':'text-pf-text-tertiary'}>Min {passwordPolicy.minLength} characters</li>
          <li className={/[A-Z]/.test(formData.password)?'text-green-500':'text-pf-text-tertiary'}>Uppercase (recommended)</li>
          <li className={/[a-z]/.test(formData.password)?'text-green-500':'text-pf-text-tertiary'}>Lowercase (recommended)</li>
          <li className={/[0-9]/.test(formData.password)?'text-green-500':'text-pf-text-tertiary'}>Digit (recommended)</li>
          <li className={/[^A-Za-z0-9]/.test(formData.password)?'text-green-500':'text-pf-text-tertiary'}>Symbol (recommended)</li>
        </ul>
        {accountFormState.errors.password && <p className="text-xs text-red-500" role="alert">{accountFormState.errors.password}</p>}
      </div>
      <div>
        <label htmlFor="confirmPassword" className="block text-sm font-medium text-pf-text-primary mb-2"><LockIcon className="inline h-4 w-4 mr-1"/>Confirm Password *</label>
        <input id="confirmPassword" type="password" name="confirmPassword" value={formData.confirmPassword} onChange={e => handleInputChange('confirmPassword', e.target.value)} autoComplete="new-password" className="w-full px-3 py-2 border border-pf-border rounded-md focus:outline-hidden focus:ring-2 focus:ring-pf-accent bg-pf-bg-2 text-pf-text-primary" disabled={submitting} />
        {accountFormState.errors.confirmPassword && <p className="text-xs text-red-500" role="alert">{accountFormState.errors.confirmPassword}</p>}
      </div>
      <div className="flex justify-end">
        <Button
          type="submit"
          disabled={submitting}
          variant="primary"
          iconLeft={<CheckCircleIcon className="h-4 w-4" />}
        >
          Next
        </Button>
      </div>
    </form>
  );

  // ...existing code...
  const renderNetworkStep = () => (
    <div className="space-y-6">
      <div>
        <h2 className="text-lg font-semibold flex items-center gap-2"><NetworkIcon className="h-5 w-5"/>Network Discovery</h2>
        <p className="text-sm text-pf-text-secondary">Provide CIDR ranges to scan for printers (e.g. 192.168.1.0/24). Leave empty to disable discovery.</p>
      </div>
      <div className="space-y-2">
        {networkDiscoverySettings?.discoverySubnets.map((r: string, i: number) => (
          <div key={i} className="flex gap-2">
            <input value={r} onChange={e => updateNetworkRange(i, e.target.value)} placeholder="192.168.1.0/24" className="flex-1 px-3 py-2 bg-pf-bg-2 border border-pf-border rounded-sm" />
            <Button
              type="button"
              onClick={() => removeNetworkRange(i)}
              variant="danger"
              size="sm"
              aria-label="Remove range"
            >
              ×
            </Button>
          </div>
        ))}
        <Button
          type="button"
          onClick={addNetworkRange}
          variant="secondary"
          size="sm"
        >
          Add Range
        </Button>
        {networkErrors && <p className="text-xs text-red-500" role="alert">{networkErrors}</p>}
      </div>
      {/* Advanced network scan settings */}
      <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
        <div>
          <label className="block text-sm font-medium text-pf-text-primary mb-1" htmlFor="clientTimeoutMs">Client Timeout (ms)</label>
          <input
            id="clientTimeoutMs"
            type="number"
            className="w-full px-3 py-2 border border-pf-border rounded-md focus:outline-hidden focus:ring-2 focus:ring-pf-accent bg-pf-bg-2 text-pf-text-primary"
            value={networkDiscoverySettings?.clientTimeoutMs ?? 0}
            onChange={e => setNetworkDiscoverySettings(s => s ? { ...s, clientTimeoutMs: Number(e.target.value) } : s)}
            min={100}
            max={10000}
          />
        </div>
        <div>
          <label className="block text-sm font-medium text-pf-text-primary mb-1" htmlFor="requestDelayMs">Request Delay (ms)</label>
          <input
            id="requestDelayMs"
            type="number"
            className="w-full px-3 py-2 border border-pf-border rounded-md focus:outline-hidden focus:ring-2 focus:ring-pf-accent bg-pf-bg-2 text-pf-text-primary"
            value={networkDiscoverySettings?.requestDelayMs ?? 0}
            onChange={e => setNetworkDiscoverySettings(s => s ? { ...s, requestDelayMs: Number(e.target.value) } : s)}
            min={0}
            max={2000}
          />
        </div>
        <div>
          <label className="block text-sm font-medium text-pf-text-primary mb-1" htmlFor="maxConcurrentRequests">Max Concurrent Requests</label>
          <input
            id="maxConcurrentRequests"
            type="number"
            className="w-full px-3 py-2 border border-pf-border rounded-md focus:outline-hidden focus:ring-2 focus:ring-pf-accent bg-pf-bg-2 text-pf-text-primary"
            value={networkDiscoverySettings?.maxConcurrentRequests ?? 0}
            onChange={e => setNetworkDiscoverySettings(s => s ? { ...s, maxConcurrentRequests: Number(e.target.value) } : s)}
            min={1}
            max={64}
          />
        </div>
        <div>
          <label className="block text-sm font-medium text-pf-text-primary mb-1" htmlFor="maxRetries">Max Retries</label>
          <input
            id="maxRetries"
            type="number"
            className="w-full px-3 py-2 border border-pf-border rounded-md focus:outline-hidden focus:ring-2 focus:ring-pf-accent bg-pf-bg-2 text-pf-text-primary"
            value={networkDiscoverySettings?.maxRetries ?? 0}
            onChange={e => setNetworkDiscoverySettings(s => s ? { ...s, maxRetries: Number(e.target.value) } : s)}
            min={0}
            max={10}
          />
        </div>
      </div>
      <div className="flex justify-between">
        <Button type="button" onClick={goBack} variant="secondary">
          Back
        </Button>
        <Button
          type="button"
          onClick={nextFromNetwork}
          variant="primary"
        >
          Next
        </Button>
      </div>
    </div>
  );

  const renderSpoolmanStep = () => (
    <div className="space-y-6">
      <div>
        <h2 className="text-lg font-semibold flex items-center gap-2"><ServerIcon className="h-5 w-5"/>Spoolman Integration</h2>
        <p className="text-sm text-pf-text-secondary">Optionally connect to a Spoolman instance now or later in Settings.</p>
      </div>
      <div className="flex items-center gap-2">
        <input id="useSpoolman" type="checkbox" checked={spoolmanEnabled} onChange={e => { setSpoolmanEnabled(e.target.checked); setSpoolmanEnabledCtx(e.target.checked); if (e.target.checked && spoolmanUrl) setSpoolmanBaseUrlCtx(spoolmanUrl); }} />
        <label htmlFor="useSpoolman" className="text-sm">Enable Spoolman</label>
      </div>
  {spoolmanEnabled && (
        <div className="space-y-4">
          {/* Network Discovery Section */}
          <div className="bg-pf-bg-1 border border-pf-border rounded-lg p-4 space-y-3">
            <div className="flex items-center justify-between">
              <div className="flex items-center gap-2">
                <WiFiIcon className="h-4 w-4 text-pf-accent" />
                <span className="text-sm font-medium">Network Discovery</span>
              </div>
              <button
                type="button"
                onClick={handleNetworkScan}
                disabled={isScanning}
                className="flex items-center gap-2 px-3 py-1.5 bg-pf-accent text-white rounded-sm text-sm hover:bg-pf-accent-dark disabled:opacity-50"
              >
                <SearchIcon className="h-4 w-4" />
                {isScanning ? 'Scanning...' : 'Scan Network'}
              </button>
            </div>
            
            {scanError && (
              <div className="text-xs text-red-400 bg-red-950/30 border border-red-800/40 rounded-sm p-2">
                {scanError}
              </div>
            )}
            
            {availableInstances.length > 0 && (
              <div className="space-y-2">
                <p className="text-xs text-pf-text-secondary">Found {availableInstances.length} Spoolman instance(s):</p>
                <div className="space-y-1 max-h-32 overflow-y-auto">
                  {availableInstances.map((instance, index) => (
                    <button
                      key={index}
                      type="button"
                      onClick={() => selectSpoolmanInstance(instance.url)}
                      className="w-full text-left p-2 bg-pf-bg-2 hover:bg-pf-bg-3 border border-pf-border rounded-sm text-xs flex items-center justify-between transition-colors"
                    >
                      <div>
                        <div className="font-medium">{instance.url}</div>
                        {instance.version && (
                          <div className="text-pf-text-tertiary">v{instance.version}</div>
                        )}
                      </div>
                      {instance.responseTime && (
                        <div className="text-pf-text-tertiary">{instance.responseTime}ms</div>
                      )}
                    </button>
                  ))}
                </div>
              </div>
            )}
            
            {scanResults.length > 0 && availableInstances.length === 0 && (
              <div className="text-xs text-orange-400 bg-orange-950/30 border border-orange-800/40 rounded-sm p-2">
                Found {scanResults.length} address(es) but no Spoolman instances were responding
              </div>
            )}
          </div>

          {/* Manual URL Input */}
          <div className="space-y-2">
            <label className="text-sm text-pf-text-secondary">Or enter URL manually:</label>
            <input
              type="url"
              value={spoolmanUrl}
              onChange={e => setSpoolmanUrl(e.target.value)}
              placeholder="http://spoolman:7912"
              className="w-full px-3 py-2 bg-pf-bg-2 border border-pf-border rounded-sm"
            />
            <div className="flex gap-2">
              <button
                type="button"
                onClick={testSpoolman}
                disabled={testingSpoolman}
                className="px-3 py-2 bg-blue-600 text-white rounded-sm text-sm hover:bg-blue-700 disabled:opacity-50"
              >
                {testingSpoolman ? 'Testing...' : 'Test URL'}
              </button>
            </div>
          </div>

          {spoolmanTestResult && (
            <p className={`text-xs ${spoolmanTestOk ? 'text-green-500':'text-red-500'}`}>{spoolmanTestResult}</p>
          )}
          {!spoolmanTestOk && spoolmanErrorCategory && (
            <div className="relative text-xs text-red-400 bg-red-950/30 border border-red-800/40 rounded-sm p-2 flex gap-2 group">
              <AlertIcon className="h-4 w-4 shrink-0" />
              <div className="space-y-1">
                <div className="font-semibold">{getSpoolmanFriendly(spoolmanErrorCategory)}</div>
                {spoolmanErrorMeta[spoolmanErrorCategory]?.hint && (
                  <div className="opacity-80 leading-snug">{spoolmanErrorMeta[spoolmanErrorCategory].hint}</div>
                )}
                {/* Info icon with tooltip raw message */}
                {spoolmanTestResult && (
                  <div className="flex items-center gap-1 text-pf-text-tertiary">
                    <InfoIcon className="h-3 w-3" />
                    <span className="truncate max-w-[220px]" title={spoolmanTestResult}>{spoolmanTestResult}</span>
                  </div>
                )}
              </div>
            </div>
          )}
        </div>
      )}
      <div className="flex justify-between">
        <Button type="button" onClick={goBack} variant="secondary">
          Back
        </Button>
        <Button
          type="button"
          onClick={nextFromSpoolman}
          variant="primary"
        >
          Next
        </Button>
      </div>
    </div>
  );

  const renderSummaryStep = () => {
    return (
      <div className="space-y-6">
        <div>
          <h2 className="text-lg font-semibold flex items-center gap-2"><LayersIcon className="h-5 w-5"/>Summary</h2>
          <p className="text-sm text-pf-text-secondary">Review your initial configuration before finishing setup.</p>
        </div>
        <div className="text-sm space-y-2">
          <div><strong>Admin:</strong> {formData.username} ({formData.email})</div>
          <div><strong>Network Ranges:</strong> {networkDiscoverySettings?.discoverySubnets.filter((r: string) => r.trim()).length ? networkDiscoverySettings?.discoverySubnets.filter((r: string) => r.trim()).join(', ') : 'None (discovery disabled)'}</div>
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
                      <InfoIcon className="h-3 w-3" />
                    </span>
                  )}
                </span>
              )}
            </span>
          ) : 'Not configured'}</div>
        </div>
        {globalError && <div className="text-sm text-red-500" role="alert">{globalError}</div>}
        <div className="flex justify-between">
          <Button
            type="button"
            onClick={goBack}
            variant="secondary"
            disabled={submitting}
          >
            Back
          </Button>
          <Button
            type="button"
            onClick={finalizeSetup}
            disabled={submitting}
            variant="success"
            iconLeft={submitting ? undefined : <CheckCircleIcon className="h-4 w-4" />}
          >
            {submitting ? (
              <>
                <div className="pf-animate-spin h-4 w-4 border-b-2 border-white rounded-full"></div>
                <span>Finishing...</span>
              </>
            ) : (
              'Finish Setup'
            )}
          </Button>
        </div>
      </div>
    );
  };

  return (
    <div className="min-h-screen bg-pf-bg-0 flex items-center justify-center p-4">
      <div className="w-full max-w-2xl bg-pf-bg-1 border border-pf-border shadow-xl rounded-xl p-8">
        {initializing ? (
          // Show initialization spinner
          (<div className="text-center py-16">
            <div className="flex items-center justify-center gap-4 mb-6">
              <div className="flex items-center gap-3">
                <img
                  src="/printfarmer-logo.svg"
                  alt="PrintFarmer Logo"
                  className="h-14 w-14"
                />
                <div className="flex flex-col items-start">
                  <h1 className="text-2xl font-bold text-pf-text-primary">Welcome to PrintFarmer</h1>
                  <p className="text-pf-text-secondary text-sm">Initializing system...</p>
                </div>
              </div>
            </div>
            <div className="flex items-center justify-center gap-3 mb-4">
              <div className="pf-animate-spin h-6 w-6 border-b-2 border-pf-accent rounded-full"></div>
              <span className="text-pf-text-secondary">
                {healthStatus?.kind === 'detailed' && healthStatus.startup 
                  ? `Phase: ${healthStatus.startup.phase}` 
                  : 'Starting up...'}
              </span>
            </div>
            {globalError && (
              <div className="mt-4 p-4 bg-red-50 border border-red-200 rounded-lg">
                <div className="text-sm text-red-600">{globalError}</div>
              </div>
            )}
          </div>)
        ) : (
          // Show setup wizard once initialized
          (<>
            <div className="flex items-center gap-4 mb-6">
              <div className="flex items-center gap-3">
                <img
                  src="/printfarmer-logo.svg"
                  alt="PrintFarmer Logo"
                  className="h-14 w-14"
                />
                <div className="flex flex-col items-start">
                  <h1 className="text-2xl font-bold text-pf-text-primary">Welcome to PrintFarmer</h1>
                  <p className="text-pf-text-secondary text-sm">Initial configuration wizard</p>
                </div>
              </div>
            </div>
            <div className="mb-4 flex items-center gap-2 text-xs flex-wrap">
              {['Account','Network','Spoolman','Summary'].map((label, idx) => (
                <div key={label} className={`px-2 py-1 rounded-sm ${idx===step ? 'bg-pf-accent text-white':'bg-pf-bg-2 text-pf-text-secondary'}`}>{idx+1}. {label}</div>
              ))}
            </div>
            {globalError && step !== 3 && <div className="mb-4 text-sm text-red-500" role="alert">{globalError}</div>}
            {step === 0 && renderAccountStep()}
            {step === 1 && renderNetworkStep()}
            {step === 2 && renderSpoolmanStep()}
            {step === 3 && renderSummaryStep()}
            <div className="mt-6 text-center text-xs text-pf-text-tertiary">You can change these settings later in the Settings page.</div>
          </>)
        )}
      </div>
    </div>
  );
}
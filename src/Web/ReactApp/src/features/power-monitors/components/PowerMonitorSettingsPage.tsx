import { useState, useEffect, useRef } from 'react';
import { toast } from 'sonner';
import type { CostTrackingSettings } from '@/types/api';
import { apiClient } from '@/services/api';
import { PageTemplate } from '@/common/components/PageTemplate';
import { Button, FormField, Input, Select, Toggle, Badge } from '@/common/components/ui';
import { Modal } from '@/common/components/modals/Modal';
import { DeleteIcon, EditIcon, PlusIcon, SettingsIcon } from '@/common/components/icons/MdiIcons';
import { usePrintersFast } from '@/common/hooks/useApi';
import {
  usePowerMonitors,
  useCreatePowerMonitor,
  useUpdatePowerMonitor,
  useDeletePowerMonitor,
  useTestPowerMonitorConnection,
} from '@/features/power-monitors/hooks/usePowerMonitors';
import {
  POWER_MONITOR_PROVIDERS,
  type PowerMonitor,
  type PowerMonitorProvider,
  type PowerMonitorTestResult,
} from '@/features/power-monitors/types';

export function PowerMonitorSettingsPage() {
  const { data: monitors = [], isLoading, isError } = usePowerMonitors();
  const { data: printers = [] } = usePrintersFast(true);

  const createMutation = useCreatePowerMonitor();
  const updateMutation = useUpdatePowerMonitor();
  const deleteMutation = useDeletePowerMonitor();
  const testMutation = useTestPowerMonitorConnection();

  const [showModal, setShowModal] = useState(false);
  const [editing, setEditing] = useState<PowerMonitor | null>(null);

  // Form state
  const [printerId, setPrinterId] = useState('');
  const [provider, setProvider] = useState<PowerMonitorProvider>('Kasa');
  const [deviceAddress, setDeviceAddress] = useState('');
  const [electricityRate, setElectricityRate] = useState('');
  const [enabled, setEnabled] = useState(true);
  const [testResult, setTestResult] = useState<PowerMonitorTestResult | null>(null);
  const [testingConnection, setTestingConnection] = useState(false);

  const [fallbackRate, setFallbackRate] = useState('');
  const [savingFallback, setSavingFallback] = useState(false);
  const costSettingsRef = useRef<CostTrackingSettings | null>(null);

  useEffect(() => {
    apiClient.getCostTrackingSettings().then((settings) => {
      costSettingsRef.current = settings;
      setFallbackRate(settings.electricityRatePerKwh > 0 ? settings.electricityRatePerKwh.toString() : '');
    }).catch(() => {
      // Non-fatal: farm-wide rate section degrades gracefully
    });
  }, []);

  const resetForm = () => {
    setPrinterId('');
    setProvider('Kasa');
    setDeviceAddress('');
    setElectricityRate('');
    setEnabled(true);
    setTestResult(null);
    setTestingConnection(false);
  };

  const openCreate = () => {
    setEditing(null);
    resetForm();
    setShowModal(true);
  };

  const openEdit = (monitor: PowerMonitor) => {
    setEditing(monitor);
    setPrinterId(monitor.printerId);
    setProvider(monitor.provider);
    setDeviceAddress(monitor.deviceAddress);
    setElectricityRate(monitor.electricityRatePerKwh?.toString() ?? '');
    setEnabled(monitor.enabled);
    setTestResult(null);
    setShowModal(true);
  };

  const handleSave = () => {
    if (!printerId) {
      toast.error('Please select a printer');
      return;
    }
    if (!deviceAddress.trim()) {
      toast.error('Device address is required');
      return;
    }

    const dto = {
      printerId,
      provider,
      deviceAddress: deviceAddress.trim(),
      electricityRatePerKwh: electricityRate ? Number(electricityRate) : undefined,
      enabled,
    };

    if (editing) {
      updateMutation.mutate(
        { id: editing.id, dto },
        {
          onSuccess: () => {
            toast.success('Power monitor updated');
            setShowModal(false);
          },
          onError: (err) => toast.error(`Failed to update: ${err.message}`),
        }
      );
    } else {
      createMutation.mutate(dto, {
        onSuccess: () => {
          toast.success('Power monitor created');
          setShowModal(false);
        },
        onError: (err) => toast.error(`Failed to create: ${err.message}`),
      });
    }
  };

  const handleDelete = (monitor: PowerMonitor) => {
    if (!confirm(`Delete power monitor for ${monitor.printerName ?? monitor.printerId}?`)) return;
    deleteMutation.mutate(monitor.id, {
      onSuccess: () => toast.success('Power monitor deleted'),
      onError: (err) => toast.error(`Failed to delete: ${err.message}`),
    });
  };

  const handleTestConnection = () => {
    if (!deviceAddress.trim()) {
      toast.error('Enter a device address first');
      return;
    }
    setTestingConnection(true);
    setTestResult(null);
    testMutation.mutate(
      { provider, deviceAddress: deviceAddress.trim() },
      {
        onSuccess: (result) => {
          setTestResult(result);
          setTestingConnection(false);
        },
        onError: (err) => {
          setTestResult({ success: false, message: err.message });
          setTestingConnection(false);
        },
      }
    );
  };

  const handleSaveFallbackRate = async () => {
    setSavingFallback(true);
    try {
      const rate = Number(fallbackRate);
      if (isNaN(rate) || rate < 0) {
        toast.error('Enter a valid rate');
        return;
      }
      const current = costSettingsRef.current ?? await apiClient.getCostTrackingSettings();
      costSettingsRef.current = current;
      await apiClient.updateCostTrackingSettings({ ...current, electricityRatePerKwh: rate });
      toast.success('Farm-wide fallback rate saved');
    } catch {
      toast.error('Failed to save farm-wide rate');
    } finally {
      setSavingFallback(false);
    }
  };

  const printerName = (id: string) =>
    printers.find((p) => p.id === id)?.name ?? id;

  return (
    <PageTemplate
      title="Power Monitors"
      subtitle="Manage smart plug power monitors for energy tracking"
      icon={SettingsIcon}
      titleActions={
        <Button variant="primary" onClick={openCreate}>
          <PlusIcon className="w-4 h-4 mr-1" />
          Add Monitor
        </Button>
      }
    >
      {/* Farm-wide fallback rate */}
      <div className="mb-6 p-4 rounded-lg border border-pf-border bg-pf-card">
        <h3 className="text-sm font-medium text-pf-text-primary mb-2">
          Farm-Wide Fallback Electricity Rate
        </h3>
        <p className="text-xs text-pf-text-secondary mb-3">
          Used when a printer has no per-monitor rate configured.
        </p>
        <div className="flex items-end gap-3">
          <FormField label="Rate (USD/kWh)" className="w-48">
            <Input
              type="number"
              step="0.01"
              min="0"
              value={fallbackRate}
              onChange={(e) => setFallbackRate(e.target.value)}
              placeholder="0.12"
            />
          </FormField>
          <Button
            variant="secondary"
            onClick={handleSaveFallbackRate}
            disabled={savingFallback}
          >
            {savingFallback ? 'Saving...' : 'Save'}
          </Button>
        </div>
      </div>

      {/* Monitor list */}
      {isLoading ? (
        <div className="text-center text-pf-text-secondary py-8">Loading power monitors...</div>
      ) : isError ? (
        <div className="text-center text-pf-error py-8">
          Failed to load power monitors.
        </div>
      ) : monitors.length === 0 ? (
        <div className="text-center text-pf-text-secondary py-12">
          <p className="mb-2">No power monitors configured yet.</p>
          <Button variant="primary" onClick={openCreate}>
            <PlusIcon className="w-4 h-4 mr-1" />
            Add Your First Monitor
          </Button>
        </div>
      ) : (
        <div className="space-y-3">
          {monitors.map((monitor) => (
            <div
              key={monitor.id}
              className="flex items-center justify-between p-4 rounded-lg border border-pf-border bg-pf-card"
            >
              <div className="flex items-center gap-4">
                <div>
                  <div className="font-medium text-pf-text-primary">
                    {monitor.printerName ?? printerName(monitor.printerId)}
                  </div>
                  <div className="text-xs text-pf-text-secondary">
                    {monitor.provider} · {monitor.deviceAddress}
                    {monitor.electricityRatePerKwh != null && (
                      <> · ${monitor.electricityRatePerKwh}/kWh</>
                    )}
                  </div>
                </div>
                <Badge variant={monitor.enabled ? 'success' : 'default'} size="sm">
                  {monitor.enabled ? 'Enabled' : 'Disabled'}
                </Badge>
              </div>
              <div className="flex items-center gap-2">
                <Button variant="subtle" onClick={() => openEdit(monitor)} aria-label="Edit">
                  <EditIcon className="w-4 h-4" />
                </Button>
                <Button variant="subtle" onClick={() => handleDelete(monitor)} aria-label="Delete">
                  <DeleteIcon className="w-4 h-4 text-pf-error" />
                </Button>
              </div>
            </div>
          ))}
        </div>
      )}

      {/* Add/Edit Modal */}
      {showModal && (
        <Modal
          title={editing ? 'Edit Power Monitor' : 'Add Power Monitor'}
          onClose={() => setShowModal(false)}
        >
          <div className="space-y-4">
            <FormField label="Printer">
              <Select
                value={printerId}
                onChange={(e) => setPrinterId(e.target.value)}
                aria-label="Select printer"
              >
                <option value="">— Select a printer —</option>
                {printers.map((p) => (
                  <option key={p.id} value={p.id}>
                    {p.name}
                  </option>
                ))}
              </Select>
            </FormField>

            <FormField label="Provider">
              <Select
                value={provider}
                onChange={(e) => setProvider(e.target.value as PowerMonitorProvider)}
                aria-label="Select provider"
              >
                {POWER_MONITOR_PROVIDERS.map((opt) => (
                  <option key={opt.value} value={opt.value}>
                    {opt.label}
                  </option>
                ))}
              </Select>
            </FormField>

            <FormField label="Device Address" helper="IP address or hostname of the smart plug">
              <Input
                value={deviceAddress}
                onChange={(e) => setDeviceAddress(e.target.value)}
                placeholder="192.168.1.100"
              />
            </FormField>

            <FormField
              label="Electricity Rate (USD/kWh)"
              helper="Leave empty to use farm-wide fallback"
            >
              <Input
                type="number"
                step="0.01"
                min="0"
                value={electricityRate}
                onChange={(e) => setElectricityRate(e.target.value)}
                placeholder="0.12"
              />
            </FormField>

            <div className="flex items-center gap-3">
              <Toggle
                checked={enabled}
                onChange={(e) => setEnabled(e.target.checked)}
                label="Enabled"
              />
            </div>

            {/* Test Connection */}
            <div className="pt-2 border-t border-pf-border">
              <Button
                variant="secondary"
                onClick={handleTestConnection}
                disabled={testingConnection || !deviceAddress.trim()}
              >
                {testingConnection ? 'Testing...' : 'Test Connection'}
              </Button>
              {testResult && (
                <div
                  className={`mt-2 text-sm ${testResult.success ? 'text-pf-success' : 'text-pf-error'}`}
                >
                  {testResult.success
                    ? `✓ Connected${testResult.currentWatts != null ? ` — ${testResult.currentWatts}W` : ''}`
                    : `✗ ${testResult.message ?? 'Connection failed'}`}
                </div>
              )}
            </div>

            {/* Actions */}
            <div className="flex justify-end gap-3 pt-4 border-t border-pf-border">
              <Button variant="secondary" onClick={() => setShowModal(false)}>
                Cancel
              </Button>
              <Button
                variant="primary"
                onClick={handleSave}
                disabled={createMutation.isPending || updateMutation.isPending}
              >
                {createMutation.isPending || updateMutation.isPending ? 'Saving...' : 'Save'}
              </Button>
            </div>
          </div>
        </Modal>
      )}
    </PageTemplate>
  );
}

if (!window.PrintFarmerDebug) {
  window.PrintFarmerDebug = {};
}
import React, { useState } from 'react';
import './printerDiscovery.css';
import { useStartDiscoveryStream, useCancelDiscoveryStream, useCreatePrinter, useManufacturers, useModels } from '@/hooks/useApi';
import { useDiscoveryStream, useSignalRConnection } from '@/hooks/useSignalR';
import { PrinterBackend } from '@/types/api';
import moonrakerIcon from '@/assets/moonraker.svg';
import octoprintIcon from '@/assets/octoprint.svg';
import { X, Search } from 'lucide-react';
import { renderUnknown } from '@/utils/renderUnknown';
import { Button } from '@/components/ui/Button';
import { Checkbox } from '@/components/ui/Checkbox';
import { Select } from '@/components/ui/Select';
import { Label } from '@/components/ui/Label';
import { Alert } from '@/components/ui/Alert';
import { ProgressBar } from '@/components/ui/ProgressBar';

interface PrinterDiscoveryModalProps {
  isOpen: boolean;
  onClose: () => void;
  onSuccess?: () => void;
}

interface PrinterConfiguration {
  manufacturerId?: string;
  modelId?: string;
  newManufacturerName?: string;
  newModelName?: string;
}

export function PrinterDiscoveryModal({ isOpen, onClose, onSuccess }: PrinterDiscoveryModalProps) {
  if ((window as unknown as { PrintFarmerDebug?: Record<string, unknown> }).PrintFarmerDebug?.printerDiscoveryModal) {
    try {
      const pf = (window as unknown as { PrintFarmerDebug?: Record<string, unknown> }).PrintFarmerDebug;
      if (pf?.printerDiscoveryModal === true) {
        console.log('[PrintFarmer] PrinterDiscoveryModal rendered with isOpen:', isOpen, 'at', new Date().toISOString());
      }
    } catch {
      // ignore
    }
  }
  const [sessionId, setSessionId] = useState<string | null>(null);
  const [selectedPrinters, setSelectedPrinters] = useState<Set<string>>(new Set());
  const [printerConfigs, setPrinterConfigs] = useState<Record<string, PrinterConfiguration>>({});
  const [selectedBackends, setSelectedBackends] = useState<Set<PrinterBackend>>(new Set([PrinterBackend.Moonraker, PrinterBackend.PrusaLink]));

  const startDiscoveryMutation = useStartDiscoveryStream();
  const cancelDiscoveryMutation = useCancelDiscoveryStream();
  const createPrinterMutation = useCreatePrinter();
  const { isConnected: isSignalRConnected } = useSignalRConnection('printer');
  const [startError, setStartError] = useState<string | null>(null);
  
  // Load manufacturers and models
  const { data: manufacturers = [] } = useManufacturers();
  const { data: allModels = [] } = useModels();
  
  // Use the discovery stream hook to listen for real-time updates
  const { progress, foundPrinters, completed, resetDiscovery, isActive, isCompleted } = useDiscoveryStream(sessionId || undefined);

  // Debug logging for state changes
  React.useEffect(() => {
    console.log('[PrinterDiscoveryModal] State changed:', {
      sessionId,
      isActive,
      isCompleted,
      foundPrintersCount: foundPrinters.length,
      progressStatus: progress?.status,
      completedPrinters: completed?.totalPrintersFound,
    });
  }, [sessionId, isActive, isCompleted, foundPrinters.length, progress?.status, completed?.totalPrintersFound]);

  // When modal is being closed, reset session so a new discovery can start fresh next open
  if (!isOpen) {
    if (sessionId) {
      setSessionId(null);
    }
    return null;
  }

  const handleStartDiscovery = async () => {
    try {
      if ((window as unknown as { PrintFarmerDebug?: Record<string, unknown> }).PrintFarmerDebug?.printerDiscoveryModal) {
        if (typeof window !== 'undefined' && (window as unknown as { PrintFarmerDebug?: Record<string, unknown> }).PrintFarmerDebug?.printerDiscoveryModal) {
          console.log('[PrintFarmer] PrinterDiscoveryModal: handleStartDiscovery called - starting network scan');
        }
      }
      resetDiscovery(); // Clear previous results
      const backends = selectedBackends.size > 0 ? Array.from(selectedBackends) : undefined;
      const result = await startDiscoveryMutation.mutateAsync({ backends, autoRegister: false });
      if (!result || !('sessionId' in result) || !result.sessionId) {
        console.error('[PrintFarmer] startDiscovery returned no sessionId', result);
        setSessionId(null);
        setStartError('Discovery API did not return a session id.');
        return;
      }
      if (window.PrintFarmerDebug?.printerDiscoveryModal) {
        console.log('[PrintFarmer] PrinterDiscoveryModal: Discovery stream started with sessionId:', result.sessionId);
      }
      setSessionId(result.sessionId);
      setSelectedPrinters(new Set());
    } catch (error) {
      console.error('Failed to start discovery stream:', error);
    }
  };

  const handleCancelDiscovery = async () => {
    if (!sessionId) return;
    try {
      await cancelDiscoveryMutation.mutateAsync(sessionId);
      setSessionId(null);
      resetDiscovery();
    } catch (error) {
      console.error('Failed to cancel discovery stream:', error);
    }
  };

  const handleSelectAll = () => {
    if (selectedPrinters.size === foundPrinters.length) {
      setSelectedPrinters(new Set());
    } else {
      setSelectedPrinters(new Set(foundPrinters.map(p => p.serverUrl)));
    }
  };

  const handleToggleSelection = (serverUrl: string) => {
    const newSelected = new Set(selectedPrinters);
    if (newSelected.has(serverUrl)) {
      newSelected.delete(serverUrl);
    } else {
      newSelected.add(serverUrl);
    }
    setSelectedPrinters(newSelected);
  };

  const getFilteredModels = (manufacturerId?: string) => {
    if (!manufacturerId) return [];
    return allModels.filter(m => m.manufacturerId === manufacturerId);
  };

  const handleAddSelected = async () => {
    const printersToAdd = foundPrinters.filter(p => selectedPrinters.has(p.serverUrl));
    
    try {
      for (const printer of printersToAdd) {
        const config = printerConfigs[printer.serverUrl] || {};
        await createPrinterMutation.mutateAsync({
          name: printer.name || `${printer.manufacturer || 'Unknown'} Printer`,
          serverUrl: printer.serverUrl,
          backend: printer.backend,
          notes: `Auto-discovered at ${printer.ipAddress}:${printer.backendPort ?? 'unknown'}`,
          manufacturerId: config.manufacturerId,
          modelId: config.modelId,
          newManufacturerName: config.newManufacturerName,
          newModelName: config.newModelName,
          backendPort: printer.backendPort ?? undefined,
          frontendPort: printer.frontendPort ?? undefined,
          cameraStreamUrl: printer.cameraStreamUrl ?? undefined,
          cameraSnapshotUrl: printer.cameraSnapshotUrl ?? undefined,
        });
      }
      
      onSuccess?.();
      onClose();
    } catch (error) {
      console.error('Failed to add printers:', error);
    }
  };

  const getBackendIcon = (backend: PrinterBackend) => {
    switch (backend) {
      case PrinterBackend.Moonraker:
        return <img src={moonrakerIcon} alt="Moonraker" title="Moonraker" className="inline h-6 w-6 align-middle" />;
      case PrinterBackend.PrusaLink:
        return <span title="PrusaLink" aria-label="PrusaLink" role="img">🔗</span>;
      case PrinterBackend.SDCP:
        return <span title="SDCP" aria-label="SDCP" role="img">📡</span>;
      case PrinterBackend.OctoPrint:
        return <img src={octoprintIcon} alt="OctoPrint" title="OctoPrint" className="inline h-6 w-6 align-middle" />;
      default:
        return <span title="Other" aria-label="Other" role="img">🖨️</span>;
    }
  };

  const getBackendColor = (backend: PrinterBackend) => {
    switch (backend) {
      case PrinterBackend.Moonraker: return 'bg-pf-accent-bg text-pf-text-primary border border-pf-border-medium';
      case PrinterBackend.PrusaLink: return 'bg-pf-warning text-pf-bg-0 border border-pf-border-medium';
      case PrinterBackend.SDCP: return 'bg-pf-accent text-pf-bg-0 border border-pf-border-medium';
      default: return 'bg-pf-bg-2 text-pf-text-primary border border-pf-border';
    }
  };

  // Determine if scan has been run (either completed or has results)
  const hasScanRun = isCompleted || foundPrinters.length > 0;

  return (
    <div className="fixed inset-0 z-50 overflow-y-auto">
      <div className="flex items-end justify-center min-h-screen pt-4 px-4 pb-20 text-center sm:block sm:p-0">
        <div className="fixed inset-0 bg-black bg-opacity-75 transition-opacity" onClick={onClose} />

        <span className="hidden sm:inline-block sm:align-middle sm:h-screen">&#8203;</span>

        <div className="relative inline-block align-bottom bg-pf-bg-1 rounded-lg px-4 pt-5 pb-4 text-left overflow-hidden shadow-xl transform transition-all sm:my-8 sm:align-middle sm:max-w-lg sm:w-full sm:p-6 lg:max-w-4xl border border-pf-border">
          {/* Close X button in corner */}
          <div className="absolute top-0 right-0 pt-4 pr-4">
            <Button
              variant="subtle"
              size="sm"
              aria-label="Close discovery modal"
              onClick={onClose}
            >
              <X className="h-5 w-5" />
            </Button>
          </div>

          {/* Modal content */}
          <div className="sm:flex sm:items-start">
            <div className="w-full">
              <div className="text-center sm:text-left">
                <h3 className="text-lg leading-6 font-medium text-pf-text-primary mb-4">
                  Discover Printers
                </h3>
                
                <div className="mb-6">
                  <p className="text-sm text-pf-text-secondary mb-4">
                    Scan your network for compatible 3D printers
                  </p>
                  
                  {/* Backend selection */}
                  <div className="mb-4">
                    <Label className="mb-2">Select backends to scan:</Label>
                    <div className="flex flex-wrap gap-3">
                      {[
                        { value: PrinterBackend.Moonraker, label: 'Moonraker' },
                        { value: PrinterBackend.PrusaLink, label: 'PrusaLink' },
                        { value: PrinterBackend.SDCP, label: 'SDCP' },
                        { value: PrinterBackend.OctoPrint, label: 'OctoPrint' }
                      ].map(backend => (
                        <Checkbox
                          key={backend.value}
                          label={backend.label}
                          checked={selectedBackends.has(backend.value)}
                          onChange={(e) => {
                            const newBackends = new Set(selectedBackends);
                            if (e.target.checked) {
                              newBackends.add(backend.value);
                            } else {
                              newBackends.delete(backend.value);
                            }
                            setSelectedBackends(newBackends);
                          }}
                          disabled={!!isActive}
                        />
                      ))}
                    </div>
                  </div>
                  
                  {selectedBackends.size === 0 && (
                    <Alert type="error" className="mt-2">Please select at least one backend to scan</Alert>
                  )}
                  {startError && (
                    <Alert type="error" className="mt-2">{startError}</Alert>
                  )}
                  <div className="mt-2 text-xs text-pf-text-tertiary">SignalR: {isSignalRConnected ? 'connected' : 'disconnected'}</div>
                </div>

                {/* Progress bar during active scan */}
                {isActive && progress && (
                  <div className="text-center py-4 mb-4">
                    <ProgressBar 
                      value={Math.round(progress.progressPercentage)} 
                      label="Network discovery progress"
                      showPercent={true}
                      className="mb-2"
                    />
                    
                    <p className="text-xs text-pf-text-tertiary mb-2">Session: {progress.sessionId}</p>
                    {progress.networkRanges && progress.networkRanges.length > 0 && (
                      <p className="text-xs text-pf-text-tertiary mb-2">
                        Networks: {progress.networkRanges.join(', ')} {progress.autoDetectedNetworks && '(auto-detected)'}
                      </p>
                    )}
                    <p className="text-sm text-pf-text-secondary mb-2">
                      Scanning {progress.currentNetwork} - {progress.currentIp}
                    </p>
                    <p className="text-xs text-pf-text-tertiary">
                      {progress.scannedIps} of {progress.totalIps} IPs scanned • {progress.printersFound} printers found
                    </p>
                  </div>
                )}

                {/* Error message */}
                {startDiscoveryMutation.error && (
                  <Alert type="error" className="mb-4">
                    Failed to start network scan: {startDiscoveryMutation.error.message}
                  </Alert>
                )}

                {/* Found printers list */}
                {foundPrinters.length > 0 && (
                  <div className="space-y-4">
                    {/* Debug panel (gated) */}
                    {window.PrintFarmerDebug?.printerDiscoveryDisplay && (
                      <div className="mb-2 p-2 bg-pf-bg-0 border border-pf-border rounded text-xs text-pf-text-tertiary">
                        {renderUnknown({ foundPrinters, progress })}
                      </div>
                    )}
                    <div className="flex items-center justify-between">
                      <h4 className="text-md font-medium text-pf-text-primary">
                        Found {foundPrinters.length} printer{foundPrinters.length !== 1 ? 's' : ''}
                      </h4>
                      <Button variant="subtle" size="sm" onClick={handleSelectAll}>
                        {selectedPrinters.size === foundPrinters.length ? 'Deselect All' : 'Select All'}
                      </Button>
                    </div>

                    <div className="max-h-96 overflow-y-auto space-y-2 border border-pf-border rounded-md p-2 bg-pf-bg-0">
                      {foundPrinters.map((printer) => {
                        const config = printerConfigs[printer.serverUrl];
                        const hasConfig = !!(config?.manufacturerId || config?.newManufacturerName);
                        const hasModelConfig = !!(config?.modelId || config?.newModelName);
                        
                        return (
                          <div
                            key={printer.serverUrl}
                            className={`p-4 border rounded-lg transition-all ${
                              selectedPrinters.has(printer.serverUrl)
                                ? 'border-pf-accent bg-pf-accent-light'
                                : 'border-pf-border hover:border-pf-border-hover hover:bg-pf-bg-2'
                            }`}
                          >
                            <div className="flex items-start justify-between">
                              <div className="flex-1">
                                <div className="flex items-center space-x-2 mb-1">
                                  <span className="text-lg">{getBackendIcon(printer.backend)}</span>
                                  <h5 className="font-medium text-pf-text-primary">
                                    {printer.name || `${printer.manufacturer || 'Unknown'} Printer`}
                                  </h5>
                                  <span className={`inline-flex items-center px-2 py-0.5 rounded-full text-xs font-medium ${getBackendColor(printer.backend)}`}>
                                    {PrinterBackend[printer.backend]}
                                  </span>
                                  {hasConfig && (
                                    <span className={`inline-flex items-center px-2 py-0.5 rounded-full text-xs font-medium ${
                                      hasModelConfig ? 'bg-pf-success-bg text-pf-status-online-text border border-pf-status-online-border' : 'bg-pf-warning text-pf-warning-text border border-pf-border-medium'
                                    }`}>
                                      {hasModelConfig ? 'Fully Configured' : 'Manufacturer Set'}
                                    </span>
                                  )}
                                </div>
                                
                                <p className="text-sm text-pf-text-secondary mb-2">
                                  {printer.ipAddress}:{printer.backendPort ?? 'unknown'} • {printer.serverUrl}
                                </p>
                                
                                {/* Inline Manufacturer and Model Selection */}
                                <div className="space-y-2 mb-2">
                                  <div className="grid grid-cols-1 md:grid-cols-2 gap-2">
                                    {/* Manufacturer Selection */}
                                    <div>
                                      <Select
                                        value={config?.manufacturerId || ''}
                                        onChange={(e) => {
                                          const newConfig = { 
                                            ...config, 
                                            manufacturerId: e.target.value || undefined,
                                            modelId: undefined,
                                            newManufacturerName: undefined 
                                          };
                                          setPrinterConfigs(prev => ({ ...prev, [printer.serverUrl]: newConfig }));
                                          
                                          if (e.target.value && (config?.modelId || config?.newModelName)) {
                                            setSelectedPrinters(prev => new Set([...prev, printer.serverUrl]));
                                          }
                                        }}
                                        className="w-full text-xs"
                                        aria-label="Select manufacturer"
                                      >
                                        <option value="">Select manufacturer...</option>
                                        {manufacturers.filter(m => m.name.toLowerCase() !== 'unknown').map(m => (
                                          <option key={m.id} value={m.id}>{m.name}</option>
                                        ))}
                                      </Select>
                                    </div>

                                    {/* Model Selection */}
                                    <div>
                                      <Select
                                        value={config?.modelId || ''}
                                        onChange={(e) => {
                                          const newConfig = { 
                                            ...config, 
                                            modelId: e.target.value || undefined,
                                            newModelName: undefined 
                                          };
                                          setPrinterConfigs(prev => ({ ...prev, [printer.serverUrl]: newConfig }));
                                          
                                          if ((config?.manufacturerId || config?.newManufacturerName) && e.target.value) {
                                            setSelectedPrinters(prev => new Set([...prev, printer.serverUrl]));
                                          }
                                        }}
                                        className="w-full text-xs"
                                        aria-label="Select model"
                                        disabled={!config?.manufacturerId}
                                      >
                                        <option value="">Select model...</option>
                                        {config?.manufacturerId && getFilteredModels(config.manufacturerId)
                                          .filter(m => m.name.toLowerCase() !== 'unknown')
                                          .map(m => (
                                            <option key={m.id} value={m.id}>{m.name}</option>
                                          ))
                                        }
                                      </Select>
                                    </div>
                                  </div>

                                  {/* Show selected model capabilities preview */}
                                  {config?.modelId && (() => {
                                    const selectedModel = allModels.find(m => m.id === config.modelId);
                                    if (selectedModel) {
                                      return (
                                        <div className="text-xs text-pf-text-tertiary bg-pf-bg-2 rounded p-2">
                                          <p className="font-medium mb-1">Model Capabilities:</p>
                                          <div className="grid grid-cols-2 gap-1">
                                            {selectedModel.defaultNozzleDiameter && (
                                              <span>Nozzle: {selectedModel.defaultNozzleDiameter}mm</span>
                                            )}
                                            {selectedModel.maxX && selectedModel.maxY && selectedModel.maxZ && (
                                              <span>Build: {selectedModel.maxX}×{selectedModel.maxY}×{selectedModel.maxZ}</span>
                                            )}
                                            {selectedModel.hasHeatedBed && <span>Heated Bed</span>}
                                            {selectedModel.hasEnclosure && <span>Enclosure</span>}
                                            {selectedModel.multiMaterial && <span>Multi-Material</span>}
                                            {selectedModel.supportsAutoLeveling && <span>Auto-Leveling</span>}
                                            {selectedModel.maxHotendTemp && (
                                              <span>Max Hotend: {selectedModel.maxHotendTemp}°C</span>
                                            )}
                                            {selectedModel.maxBedTemp && (
                                              <span>Max Bed: {selectedModel.maxBedTemp}°C</span>
                                            )}
                                          </div>
                                        </div>
                                      );
                                    }
                                    return null;
                                  })()}
                                </div>
                                
                                {/* Configuration info (legacy, keeping for custom names) */}
                                {config && (config.newManufacturerName || config.newModelName) && (
                                  <div className="text-xs text-pf-text-tertiary mb-2">
                                    {config.newManufacturerName && (
                                      <p>Custom Manufacturer: {config.newManufacturerName}</p>
                                    )}
                                    {config.newModelName && (
                                      <p>Custom Model: {config.newModelName}</p>
                                    )}
                                  </div>
                                )}
                                
                                {/* Auto-detected info */}
                                {(printer.manufacturer || printer.model || printer.firmware) && (
                                  <div className="text-xs text-pf-text-tertiary space-y-0.5">
                                    {printer.manufacturer && <p>Auto-detected: {printer.manufacturer}</p>}
                                    {printer.model && <p>Model: {printer.model}</p>}
                                    {printer.firmware && <p>Firmware: {printer.firmware} {printer.version}</p>}
                                  </div>
                                )}
                              </div>
                              
                              <div className="flex items-center">
                                <Checkbox
                                  aria-label={`Select printer ${printer.name || printer.serverUrl}`}
                                  checked={selectedPrinters.has(printer.serverUrl)}
                                  onChange={(e) => {
                                    e.stopPropagation();
                                    handleToggleSelection(printer.serverUrl);
                                  }}
                                />
                              </div>
                            </div>
                          </div>
                        );
                      })}
                    </div>
                  </div>
                )}

                {/* Initial state - before any scan */}
                {!isActive && foundPrinters.length === 0 && !isCompleted && (
                  <div className="text-center py-8 text-pf-text-secondary">
                    Click "Start Scan" to search for printers on your network
                  </div>
                )}

                {/* Scan complete with 0 new printers found */}
                {isCompleted && foundPrinters.length === 0 && completed && (
                  <div className="text-center py-8">
                    <div className="text-pf-text-primary font-medium mb-2">
                      Scan Complete
                    </div>
                    <p className="text-sm text-pf-text-secondary mb-2">
                      {completed.totalPrintersExcluded > 0
                        ? `No new printers found. ${completed.totalPrintersExcluded} printer${completed.totalPrintersExcluded !== 1 ? 's' : ''} already registered.`
                        : 'No printers were found on the network.'}
                    </p>
                    <p className="text-xs text-pf-text-tertiary">
                      Scan duration: {(() => {
                        const parts = String(completed.duration).split(':');
                        if (parts.length === 3) {
                          const seconds = parseFloat(parts[2]);
                          return `${seconds.toFixed(1)}s`;
                        }
                        return completed.duration;
                      })()}
                    </p>
                  </div>
                )}
              </div>
            </div>
          </div>

          {/* Footer with action buttons - always at bottom, aligned right */}
          <div className="flex items-center justify-end space-x-3 pt-4 mt-4 border-t border-pf-border">
            {/* During scan: Cancel Scan button */}
            {isActive && (
              <Button
                variant="danger"
                onClick={handleCancelDiscovery}
                disabled={cancelDiscoveryMutation.isPending}
              >
                Cancel Scan
              </Button>
            )}

            {/* Before scan or after scan: Close button */}
            {!isActive && (
              <Button variant="secondary" onClick={onClose}>
                Close
              </Button>
            )}

            {/* After scan with printers: Add Selected button */}
            {foundPrinters.length > 0 && !isActive && (
              <Button
                variant="primary"
                onClick={handleAddSelected}
                disabled={selectedPrinters.size === 0 || createPrinterMutation.isPending}
              >
                {createPrinterMutation.isPending 
                  ? 'Adding...' 
                  : `Add ${selectedPrinters.size} Selected Printer${selectedPrinters.size !== 1 ? 's' : ''}`
                }
              </Button>
            )}

            {/* Scan button - changes label based on state */}
            {!isActive && (
              <Button
                variant="primary"
                onClick={handleStartDiscovery}
                disabled={startDiscoveryMutation.isPending || selectedBackends.size === 0}
                iconLeft={<Search className="h-4 w-4" />}
              >
                {hasScanRun ? 'Scan Again' : 'Start Scan'}
              </Button>
            )}
          </div>
        </div>
      </div>
    </div>
  );
}

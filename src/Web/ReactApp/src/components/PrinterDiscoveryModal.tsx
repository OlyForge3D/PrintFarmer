import React, { useState, useEffect } from 'react';
import './printerDiscovery.css';
import { useStartDiscoveryStream, useCreatePrinter, useManufacturers, useModels } from '@/hooks/useApi';
import { useDiscoveryStream } from '@/hooks/useSignalR';
import { PrinterBackend } from '@/types/api';
import { signalRService } from '@/services/signalr';
import { X, Search } from 'lucide-react';

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
  console.log('PrinterDiscoveryModal rendered with isOpen:', isOpen, 'at', new Date().toISOString());
  const [sessionId, setSessionId] = useState<string | null>(null);
  const [selectedPrinters, setSelectedPrinters] = useState<Set<string>>(new Set());
  const [printerConfigs, setPrinterConfigs] = useState<Record<string, PrinterConfiguration>>({});

  const startDiscoveryMutation = useStartDiscoveryStream();
  const createPrinterMutation = useCreatePrinter();
  
  // Load manufacturers and models
  const { data: manufacturers = [] } = useManufacturers();
  const { data: allModels = [] } = useModels();
  
  // Use the discovery stream hook to listen for real-time updates
  const { progress, foundPrinters, resetDiscovery, isActive } = useDiscoveryStream(sessionId || undefined);

  // Ensure SignalR connection when modal opens so we can receive events immediately
  useEffect(() => {
    if (isOpen) {
      signalRService.connect();
    }
  }, [isOpen]);

  // When modal is being closed, reset session so a new discovery can start fresh next open
  if (!isOpen) {
    if (sessionId) {
      setSessionId(null);
    }
    return null;
  }

  const handleStartDiscovery = async () => {
    try {
      console.log('handleStartDiscovery called - starting network scan');
      resetDiscovery(); // Clear previous results
      const result = await startDiscoveryMutation.mutateAsync();
      console.log('Discovery stream started with sessionId:', result.sessionId);
      setSessionId(result.sessionId);
      setSelectedPrinters(new Set());
    } catch (error) {
      console.error('Failed to start discovery stream:', error);
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
          notes: `Auto-discovered at ${printer.ipAddress}:${printer.port}`,
          manufacturerId: config.manufacturerId,
          modelId: config.modelId,
          newManufacturerName: config.newManufacturerName,
          newModelName: config.newModelName,
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
      case PrinterBackend.Moonraker: return '🌙';
      case PrinterBackend.PrusaLink: return '🔗';
      case PrinterBackend.SDCP: return '📡';
      default: return '🖨️';
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

  return (
    <>
    <div className="fixed inset-0 z-50 overflow-y-auto">
      <div className="flex items-end justify-center min-h-screen pt-4 px-4 pb-20 text-center sm:block sm:p-0">
        <div className="fixed inset-0 bg-black bg-opacity-75 transition-opacity" onClick={onClose} />

        <span className="hidden sm:inline-block sm:align-middle sm:h-screen">&#8203;</span>

        <div className="relative inline-block align-bottom bg-pf-bg-1 rounded-lg px-4 pt-5 pb-4 text-left overflow-hidden shadow-xl transform transition-all sm:my-8 sm:align-middle sm:max-w-lg sm:w-full sm:p-6 lg:max-w-4xl border border-pf-border">
          <div className="absolute top-0 right-0 pt-4 pr-4">
            <button
              type="button"
              aria-label="Close discovery modal"
              title="Close"
              className="bg-pf-bg-1 rounded-md text-pf-text-secondary hover:text-pf-text-primary focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-pf-accent"
              onClick={onClose}
            >
              <X className="h-6 w-6" />
            </button>
          </div>

          <div className="sm:flex sm:items-start">
            <div className="w-full">
              <div className="text-center sm:text-left">
                <h3 className="text-lg leading-6 font-medium text-pf-text-primary mb-4">
                  Discover Printers
                </h3>
                
                <div className="mb-6">
                  <p className="text-sm text-pf-text-secondary mb-4">
                    Scan your network for compatible 3D printers (Moonraker, PrusaLink, and SDCP)
                  </p>
                  
                  <button
                    onClick={handleStartDiscovery}
                    disabled={startDiscoveryMutation.isPending || !!isActive}
                    className="inline-flex items-center px-4 py-2 border border-transparent text-sm font-medium rounded-md shadow-sm text-white bg-pf-accent hover:bg-pf-accent-hover focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-pf-accent disabled:opacity-50 disabled:cursor-not-allowed"
                  >
                    <Search className="h-4 w-4 mr-2" />
                    {isActive ? 'Scanning...' : 'Start Network Scan'}
                  </button>
                </div>

                {isActive && progress && (() => {
                  const valueNow: number = Math.round(progress.progressPercentage);
                  // valueNow used for aria-valuenow; linter workaround by casting
                  return (
                  <div className="text-center py-4 mb-4">
                    {(() => {
                      const ariaProps = {
                        role: 'progressbar',
                        'aria-label': 'Network discovery progress',
                        'aria-valuemin': 0,
                        'aria-valuemax': 100,
                        'aria-valuenow': valueNow,
                        'data-progress': valueNow,
                      } as const;
                      return (
                        <div
                          {...ariaProps}
                          className="w-full bg-pf-bg-2 rounded-full h-2 mb-2 overflow-hidden pf-progress-bar border border-pf-border"
                        >
                          {(() => {
                            const pct = Math.min(100, Math.max(0, valueNow));
                            const step = pct >= 99 ? 100 : pct >= 75 ? 75 : pct >= 50 ? 50 : pct >= 25 ? 25 : 0;
                            return <ProgressFill pct={pct} step={step} />;
                          })()}
                        </div>
                      );
                    })()}
                    
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
                  );
                })()}

                {startDiscoveryMutation.error && (
                  <div className="mb-4 p-3 bg-pf-error border border-pf-error-border rounded-md">
                    <p className="text-sm text-pf-error-text">
                      Failed to start network scan: {startDiscoveryMutation.error.message}
                    </p>
                  </div>
                )}

                {foundPrinters.length > 0 && (
                  <div className="space-y-4">
                    <div className="flex items-center justify-between">
                      <h4 className="text-md font-medium text-pf-text-primary">
                        Found {foundPrinters.length} printer{foundPrinters.length !== 1 ? 's' : ''}
                      </h4>
                      <button
                        onClick={handleSelectAll}
                        className="text-sm text-pf-accent hover:text-pf-accent-hover"
                      >
                        {selectedPrinters.size === foundPrinters.length ? 'Deselect All' : 'Select All'}
                      </button>
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
                                  {printer.ipAddress}:{printer.port} • {printer.serverUrl}
                                </p>
                                
                                {/* Inline Manufacturer and Model Selection */}
                                <div className="space-y-2 mb-2">
                                  <div className="grid grid-cols-1 md:grid-cols-2 gap-2">
                                    {/* Manufacturer Selection */}
                                    <div>
                                      <select
                                        value={config?.manufacturerId || ''}
                                        onChange={(e) => {
                                          const newConfig = { 
                                            ...config, 
                                            manufacturerId: e.target.value || undefined,
                                            modelId: undefined, // Reset model when manufacturer changes
                                            newManufacturerName: undefined 
                                          };
                                          setPrinterConfigs(prev => ({ ...prev, [printer.serverUrl]: newConfig }));
                                          
                                          // Auto-select checkbox when manufacturer is set (and model was already set)
                                          if (e.target.value && (config?.modelId || config?.newModelName)) {
                                            setSelectedPrinters(prev => new Set([...prev, printer.serverUrl]));
                                          }
                                        }}
                                        className="w-full px-2 py-1 text-xs border border-pf-border rounded bg-pf-bg-0 text-pf-text-primary focus:ring-1 focus:ring-pf-accent"
                                        title="Select manufacturer"
                                      >
                                        <option value="">Select manufacturer...</option>
                                        {manufacturers.filter(m => m.name.toLowerCase() !== 'unknown').map(m => (
                                          <option key={m.id} value={m.id}>{m.name}</option>
                                        ))}
                                      </select>
                                    </div>

                                    {/* Model Selection */}
                                    <div>
                                      <select
                                        value={config?.modelId || ''}
                                        onChange={(e) => {
                                          const newConfig = { 
                                            ...config, 
                                            modelId: e.target.value || undefined,
                                            newModelName: undefined 
                                          };
                                          setPrinterConfigs(prev => ({ ...prev, [printer.serverUrl]: newConfig }));
                                          
                                          // Auto-select checkbox when both manufacturer and model are set
                                          if ((config?.manufacturerId || config?.newManufacturerName) && e.target.value) {
                                            setSelectedPrinters(prev => new Set([...prev, printer.serverUrl]));
                                          }
                                        }}
                                        className="w-full px-2 py-1 text-xs border border-pf-border rounded bg-pf-bg-0 text-pf-text-primary focus:ring-1 focus:ring-pf-accent"
                                        title="Select model"
                                        disabled={!config?.manufacturerId}
                                      >
                                        <option value="">Select model...</option>
                                        {config?.manufacturerId && getFilteredModels(config.manufacturerId)
                                          .filter(m => m.name.toLowerCase() !== 'unknown')
                                          .map(m => (
                                            <option key={m.id} value={m.id}>{m.name}</option>
                                          ))
                                        }
                                      </select>
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
                                <input
                                  type="checkbox"
                                  aria-label={`Select printer ${printer.name || printer.serverUrl}`}
                                  title={`Select printer ${printer.name || printer.serverUrl}`}
                                  checked={selectedPrinters.has(printer.serverUrl)}
                                  onChange={(e) => {
                                    e.stopPropagation();
                                    handleToggleSelection(printer.serverUrl);
                                  }}
                                  className="h-4 w-4 text-pf-accent focus:ring-pf-accent border-pf-border rounded"
                                />
                              </div>
                            </div>
                          </div>
                        );
                      })}
                    </div>

                    <div className="flex items-center justify-end space-x-3 pt-4 border-t border-pf-border">
                      <button
                        onClick={onClose}
                        className="px-4 py-2 border border-pf-border rounded-md text-sm font-medium text-pf-text-primary bg-pf-bg-1 hover:bg-pf-bg-2 focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-pf-accent"
                      >
                        Cancel
                      </button>
                      <button
                        onClick={handleAddSelected}
                        disabled={selectedPrinters.size === 0 || createPrinterMutation.isPending}
                        className="px-4 py-2 border border-transparent rounded-md shadow-sm text-sm font-medium text-white bg-pf-accent hover:bg-pf-accent-hover focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-pf-accent disabled:opacity-50 disabled:cursor-not-allowed"
                      >
                        {createPrinterMutation.isPending 
                          ? 'Adding...' 
                          : `Add ${selectedPrinters.size} Selected Printer${selectedPrinters.size !== 1 ? 's' : ''}`
                        }
                      </button>
                    </div>
                  </div>
                )}

                {!isActive && foundPrinters.length === 0 && !startDiscoveryMutation.error && !sessionId && (
                  <div className="text-center py-8 text-pf-text-secondary">
                    Click "Start Network Scan" to search for printers on your network
                  </div>
                )}
              </div>
            </div>
          </div>
        </div>
      </div>
    </div>
    </>
  );
}

// Separate component to avoid inline style lint for CSS variable usage
const ProgressFill: React.FC<{ pct: number; step: number }> = ({ pct, step }) => {
  const ref = React.useRef<HTMLDivElement | null>(null);
  React.useEffect(() => {
    if (ref.current) {
      ref.current.style.setProperty('--pf-progress', pct + '%');
    }
  }, [pct]);
  return <div ref={ref} className={`pf-progress-fill step-${step} bg-pf-accent h-2 rounded-full transition-all duration-300`} aria-hidden="true" />;
};
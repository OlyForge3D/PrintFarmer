import { useState } from 'react';
import { useStartDiscoveryStream, useCreatePrinter } from '@/hooks/useApi';
import { useDiscoveryStream } from '@/hooks/useSignalR';
import { DiscoveredPrinterDto, PrinterBackend } from '@/types/api';
import { X, Search } from 'lucide-react';

interface PrinterDiscoveryModalProps {
  isOpen: boolean;
  onClose: () => void;
  onSuccess?: () => void;
}

export function PrinterDiscoveryModal({ isOpen, onClose, onSuccess }: PrinterDiscoveryModalProps) {
  console.log('PrinterDiscoveryModal rendered with isOpen:', isOpen, 'at', new Date().toISOString());
  const [sessionId, setSessionId] = useState<string | null>(null);
  const [selectedPrinters, setSelectedPrinters] = useState<Set<string>>(new Set());

  const startDiscoveryMutation = useStartDiscoveryStream();
  const createPrinterMutation = useCreatePrinter();
  
  // Use the discovery stream hook to listen for real-time updates
  const { progress, foundPrinters, completed, resetDiscovery, isActive, isCompleted } = useDiscoveryStream(sessionId || undefined);

  if (!isOpen) return null;

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

  const handleAddSelected = async () => {
    const printersToAdd = foundPrinters.filter(p => selectedPrinters.has(p.serverUrl));
    
    try {
      for (const printer of printersToAdd) {
        await createPrinterMutation.mutateAsync({
          name: printer.name || `${printer.manufacturer || 'Unknown'} Printer`,
          serverUrl: printer.serverUrl,
          backend: printer.backend,
          notes: `Auto-discovered at ${printer.ipAddress}:${printer.port}`,
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
      case PrinterBackend.Moonraker: return 'bg-purple-100 text-purple-800';
      case PrinterBackend.PrusaLink: return 'bg-orange-100 text-orange-800';
      case PrinterBackend.SDCP: return 'bg-blue-100 text-blue-800';
      default: return 'bg-gray-100 text-gray-800';
    }
  };

  return (
    <div className="fixed inset-0 z-50 overflow-y-auto">
      <div className="flex items-end justify-center min-h-screen pt-4 px-4 pb-20 text-center sm:block sm:p-0">
        <div className="fixed inset-0 bg-gray-500 bg-opacity-75 transition-opacity" onClick={onClose} />

        <span className="hidden sm:inline-block sm:align-middle sm:h-screen">&#8203;</span>

        <div className="relative inline-block align-bottom bg-white rounded-lg px-4 pt-5 pb-4 text-left overflow-hidden shadow-xl transform transition-all sm:my-8 sm:align-middle sm:max-w-lg sm:w-full sm:p-6 lg:max-w-4xl">
          <div className="absolute top-0 right-0 pt-4 pr-4">
            <button
              type="button"
              className="bg-white rounded-md text-gray-400 hover:text-gray-500 focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-blue-500"
              onClick={onClose}
            >
              <X className="h-6 w-6" />
            </button>
          </div>

          <div className="sm:flex sm:items-start">
            <div className="w-full">
              <div className="text-center sm:text-left">
                <h3 className="text-lg leading-6 font-medium text-gray-900 mb-4">
                  Discover Printers (Debug: {Math.random().toString(36).substr(2, 5)})
                </h3>
                
                <div className="mb-6">
                  <p className="text-sm text-gray-600 mb-4">
                    Scan your network for compatible 3D printers (Moonraker, PrusaLink, and SDCP)
                  </p>
                  
                                    <button
                    onClick={handleStartDiscovery}
                    disabled={startDiscoveryMutation.isPending || !!isActive}
                    className="inline-flex items-center px-4 py-2 border border-transparent text-sm font-medium rounded-md shadow-sm text-white bg-blue-600 hover:bg-blue-700 focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-blue-500 disabled:opacity-50 disabled:cursor-not-allowed"
                  >
                    <Search className="h-4 w-4 mr-2" />
                    {isActive ? 'Scanning...' : 'Start Network Scan'}
                  </button>
                </div>

                {isActive && progress && (
                  <div className="text-center py-4 mb-4">
                    <div className="w-full bg-gray-200 rounded-full h-2 mb-4">
                      <div 
                        className="bg-blue-600 h-2 rounded-full transition-all duration-300" 
                        style={{ width: `${progress.progressPercentage}%` }}
                      />
                    </div>
                    <p className="text-sm text-gray-600 mb-2">
                      Scanning {progress.currentNetwork} - {progress.currentIp}
                    </p>
                    <p className="text-xs text-gray-500">
                      {progress.scannedIps} of {progress.totalIps} IPs scanned • {progress.printersFound} printers found
                    </p>
                  </div>
                )}

                {startDiscoveryMutation.error && (
                  <div className="mb-4 p-3 bg-red-50 border border-red-200 rounded-md">
                    <p className="text-sm text-red-800">
                      Failed to start network scan: {startDiscoveryMutation.error.message}
                    </p>
                  </div>
                )}

                {foundPrinters.length > 0 && (
                  <div className="space-y-4">
                    <div className="flex items-center justify-between">
                      <h4 className="text-md font-medium text-gray-900">
                        Found {foundPrinters.length} printer{foundPrinters.length !== 1 ? 's' : ''}
                      </h4>
                      <button
                        onClick={handleSelectAll}
                        className="text-sm text-blue-600 hover:text-blue-800"
                      >
                        {selectedPrinters.size === foundPrinters.length ? 'Deselect All' : 'Select All'}
                      </button>
                    </div>

                    <div className="max-h-96 overflow-y-auto space-y-2 border rounded-md p-2">
                      {foundPrinters.map((printer) => (
                        <div
                          key={printer.serverUrl}
                          className={`p-4 border rounded-lg cursor-pointer transition-all ${
                            selectedPrinters.has(printer.serverUrl)
                              ? 'border-blue-500 bg-blue-50'
                              : 'border-gray-200 hover:border-gray-300 hover:bg-gray-50'
                          }`}
                          onClick={() => handleToggleSelection(printer.serverUrl)}
                        >
                          <div className="flex items-start justify-between">
                            <div className="flex-1">
                              <div className="flex items-center space-x-2 mb-1">
                                <span className="text-lg">{getBackendIcon(printer.backend)}</span>
                                <h5 className="font-medium text-gray-900">
                                  {printer.name || `${printer.manufacturer || 'Unknown'} Printer`}
                                </h5>
                                <span className={`inline-flex items-center px-2 py-0.5 rounded-full text-xs font-medium ${getBackendColor(printer.backend)}`}>
                                  {PrinterBackend[printer.backend]}
                                </span>
                              </div>
                              
                              <p className="text-sm text-gray-600 mb-1">
                                {printer.ipAddress}:{printer.port} • {printer.serverUrl}
                              </p>
                              
                              {(printer.manufacturer || printer.model || printer.firmware) && (
                                <div className="text-xs text-gray-500 space-y-0.5">
                                  {printer.manufacturer && <p>Manufacturer: {printer.manufacturer}</p>}
                                  {printer.model && <p>Model: {printer.model}</p>}
                                  {printer.firmware && <p>Firmware: {printer.firmware} {printer.version}</p>}
                                </div>
                              )}
                            </div>
                            
                            <div className="flex items-center">
                              <input
                                type="checkbox"
                                checked={selectedPrinters.has(printer.serverUrl)}
                                onChange={() => handleToggleSelection(printer.serverUrl)}
                                className="h-4 w-4 text-blue-600 focus:ring-blue-500 border-gray-300 rounded"
                              />
                            </div>
                          </div>
                        </div>
                      ))}
                    </div>

                    <div className="flex items-center justify-end space-x-3 pt-4 border-t border-gray-200">
                      <button
                        onClick={onClose}
                        className="px-4 py-2 border border-gray-300 rounded-md text-sm font-medium text-gray-700 bg-white hover:bg-gray-50 focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-blue-500"
                      >
                        Cancel
                      </button>
                      <button
                        onClick={handleAddSelected}
                        disabled={selectedPrinters.size === 0 || createPrinterMutation.isPending}
                        className="px-4 py-2 border border-transparent rounded-md shadow-sm text-sm font-medium text-white bg-blue-600 hover:bg-blue-700 focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-blue-500 disabled:opacity-50 disabled:cursor-not-allowed"
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
                  <div className="text-center py-8 text-gray-500">
                    Click "Start Network Scan" to search for printers on your network
                  </div>
                )}
              </div>
            </div>
          </div>
        </div>
      </div>
    </div>
  );
}
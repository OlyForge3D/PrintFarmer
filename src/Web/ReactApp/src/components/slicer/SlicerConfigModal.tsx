/* eslint-disable local/pf-no-raw-html-controls */
import React, { useState } from 'react';
// No MdiIcons used in this component
import { CloseIcon, CheckCircleIcon, AlertCircleIcon } from '@/components/icons/MdiIcons';
import { slicerService, SlicerProfile, SliceRequest, SlicingProgress } from '@/services/slicerService';

interface AvailablePrinter {
  id: string;
  name: string;
  backend: string;
  isReachable: boolean;
}

interface SliceCompleteResult {
  jobId: string;
  gcodeUrl: string;
  printTime: number;
  filamentUsed: number;
}

interface SlicerConfigModalProps {
  isOpen: boolean;
  onClose: () => void;
  // Either a file to upload and slice, or an uploaded model to slice
  modelFile?: File;
  modelId?: string;
  modelName?: string;
  availablePrinters: AvailablePrinter[];
  onSliceComplete?: (result: SliceCompleteResult) => void;
}

const DEFAULT_PROFILES: Record<string, SlicerProfile> = {
  'draft': {
    layerHeight: 0.3,
    infillPercentage: 10,
    printSpeed: 60,
    nozzleTemperature: 210,
    bedTemperature: 60,
    supports: false,
    material: 'PLA',
    quality: 'draft'
  },
  'standard': {
    layerHeight: 0.2,
    infillPercentage: 20,
    printSpeed: 50,
    nozzleTemperature: 210,
    bedTemperature: 60,
    supports: false,
    material: 'PLA',
    quality: 'standard'
  },
  'fine': {
    layerHeight: 0.15,
    infillPercentage: 25,
    printSpeed: 40,
    nozzleTemperature: 210,
    bedTemperature: 60,
    supports: true,
    material: 'PLA',
    quality: 'fine'
  }
};

export const SlicerConfigModal: React.FC<SlicerConfigModalProps> = ({
  isOpen,
  onClose,
  modelFile,
  modelId,
  modelName,
  availablePrinters,
  onSliceComplete
}) => {
  const [selectedPrinter, setSelectedPrinter] = useState<AvailablePrinter | null>(null);
  const [selectedSlicer, setSelectedSlicer] = useState<'prusaslicer' | 'orcaslicer'>('prusaslicer');
  const [profile, setProfile] = useState<SlicerProfile>(DEFAULT_PROFILES.standard);
  const [isSlicing, setIsSlicing] = useState(false);
  const [slicingProgress, setSlicingProgress] = useState<SlicingProgress | null>(null);
  const [validationResult, setValidationResult] = useState<{ valid: boolean; issues?: string[] } | null>(null);

  // Future: load available profiles when UI exposes profile selection beyond defaults
  // const { data: availableProfiles } = useQuery({
  //   queryKey: ['slicer-profiles', selectedPrinter?.id],
  //   queryFn: () => selectedPrinter ? slicerService.getAvailableProfiles(selectedPrinter.id) : [],
  //   enabled: !!selectedPrinter
  // });

  // Validate model when modal opens
  React.useEffect(() => {
    if (isOpen && modelFile) {
      slicerService.validateModel(modelFile)
        .then(setValidationResult)
        .catch(error => {
          setValidationResult({ valid: false, issues: [`Validation failed: ${error.message}`] });
        });
    }
  }, [isOpen, modelFile]);

  const handleSlice = async () => {
    if (!selectedPrinter) return;

    setIsSlicing(true);
    setSlicingProgress({ jobId: '', progress: 0, status: 'queued' });

    try {
      let result;
      
      if (modelFile) {
        // Slice a new file upload
        const sliceRequest: SliceRequest = {
          modelFile,
          slicerEngine: selectedSlicer,
          printerId: selectedPrinter.id,
          profile
        };
        result = await slicerService.sliceModel(sliceRequest);
      } else if (modelId) {
        // Slice an already uploaded model
        result = await slicerService.sliceUploadedModel(
          modelId,
          selectedSlicer,
          selectedPrinter.id,
          profile
        );
      } else {
        throw new Error('No model file or model ID provided');
      }

      // Subscribe to progress updates
      const progressSource = slicerService.subscribeToSlicingProgress(
        result.jobId,
        (progress) => {
          setSlicingProgress(progress);
          if (progress.status === 'completed') {
            progressSource.close();
            setIsSlicing(false);
            onSliceComplete?.(result);
            onClose();
          } else if (progress.status === 'error') {
            progressSource.close();
            setIsSlicing(false);
            alert(`Slicing failed: ${progress.message}`);
          }
        }
      );

      // Set initial job ID
      setSlicingProgress(prev => prev ? { ...prev, jobId: result.jobId } : null);

    } catch (error) {
      console.error('Slicing failed:', error);
      setIsSlicing(false);
      setSlicingProgress(null);
      alert(`Slicing failed: ${error instanceof Error ? error.message : 'Unknown error'}`);
    }
  };

  const updateProfile = (updates: Partial<SlicerProfile>) => {
    setProfile(prev => ({ ...prev, ...updates }));
  };

  if (!isOpen) return null;

  return (
    <div className="fixed inset-0 bg-black bg-opacity-50 flex items-center justify-center z-50 p-4">
      <div className="bg-white rounded-lg shadow-xl max-w-4xl w-full max-h-[90vh] overflow-y-auto">
        {/* Header */}
        <div className="flex items-center justify-between p-6 border-b">
          <h2 className="text-xl font-semibold">Configure Slicing</h2>
          <button 
            onClick={onClose}
            disabled={isSlicing}
            className="p-1 hover:bg-gray-100 rounded"
            aria-label="Close slicing configuration"
            title="Close"
          >
            <CloseIcon className="w-5 h-5" />
          </button>
        </div>

        <div className="p-6 space-y-6">
          {/* Model info */}
          <div className="bg-gray-50 rounded-lg p-4">
            <h4 className="font-medium mb-2">Model Information</h4>
            <div className="grid grid-cols-2 gap-4 text-sm">
              <div>
                <span className="text-gray-600">File:</span>
                <span className="ml-2 font-medium">
                  {modelFile ? modelFile.name : (modelName || 'Unknown Model')}
                </span>
              </div>
              <div>
                <span className="text-gray-600">Size:</span>
                <span className="ml-2 font-medium">
                  {modelFile ? `${(modelFile.size / 1024 / 1024).toFixed(1)} MB` : 'Unknown'}
                </span>
              </div>
            </div>
            
            {/* Validation status */}
            {validationResult && (
              <div className={`mt-3 flex items-start space-x-2 text-sm ${
                validationResult.valid ? 'text-green-700' : 'text-red-700'
              }`}>
                {validationResult.valid ? (
                  <CheckCircleIcon className="w-4 h-4 flex-shrink-0 mt-0.5" />
                ) : (
                  <AlertCircleIcon className="w-4 h-4 flex-shrink-0 mt-0.5" />
                )}
                <div>
                  {validationResult.valid ? (
                    <span>Model validation passed</span>
                  ) : (
                    <div>
                      <div className="font-medium">Validation issues:</div>
                      <ul className="list-disc list-inside mt-1">
                        {validationResult.issues?.map((issue, index) => (
                          <li key={index}>{issue}</li>
                        ))}
                      </ul>
                    </div>
                  )}
                </div>
              </div>
            )}
          </div>

          {/* Printer selection */}
          <div>
            <label className="block text-sm font-medium text-gray-700 mb-2">
              Select Printer
            </label>
            <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
              {availablePrinters.map(printer => (
                <div
                  key={printer.id}
                  onClick={() => setSelectedPrinter(printer)}
                  className={`p-3 border rounded-lg cursor-pointer transition-colors ${
                    selectedPrinter?.id === printer.id
                      ? 'border-blue-500 bg-blue-50'
                      : 'border-gray-200 hover:border-gray-300'
                  }`}
                >
                  <div className="flex items-center justify-between">
                    <div>
                      <h5 className="font-medium">{printer.name}</h5>
                      <p className="text-sm text-gray-500">{printer.backend}</p>
                    </div>
                    <div className={`w-3 h-3 rounded-full ${
                      printer.isReachable ? 'bg-green-500' : 'bg-red-500'
                    }`} />
                  </div>
                </div>
              ))}
            </div>
          </div>

          {/* Slicer engine selection */}
          <div>
            <label className="block text-sm font-medium text-gray-700 mb-2">
              Slicer Engine
            </label>
            <div className="flex space-x-4">
              <label className="flex items-center">
                <input
                  type="radio"
                  name="slicer"
                  value="prusaslicer"
                  checked={selectedSlicer === 'prusaslicer'}
                  onChange={(e) => setSelectedSlicer(e.target.value as 'prusaslicer')}
                  className="mr-2"
                />
                <span className="flex items-center">
                  PrusaSlicer
                  <span className="ml-2 px-2 py-1 text-xs bg-orange-100 text-orange-800 rounded">
                    Reliable
                  </span>
                </span>
              </label>
              
              <label className="flex items-center">
                <input
                  type="radio"
                  name="slicer"
                  value="orcaslicer"
                  checked={selectedSlicer === 'orcaslicer'}
                  onChange={(e) => setSelectedSlicer(e.target.value as 'orcaslicer')}
                  className="mr-2"
                />
                <span className="flex items-center">
                  OrcaSlicer
                  <span className="ml-2 px-2 py-1 text-xs bg-purple-100 text-purple-800 rounded">
                    Advanced
                  </span>
                </span>
              </label>
            </div>
          </div>

          {/* Profile settings */}
          <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
            <div>
              <label className="block text-sm font-medium text-gray-700 mb-1">
                Quality
              </label>
              <select
                aria-label="Print quality preset"
                title="Quality"
                value={profile.quality}
                onChange={(e) => {
                  const quality = e.target.value as 'draft' | 'standard' | 'fine';
                  setProfile(DEFAULT_PROFILES[quality]);
                }}
                className="w-full px-3 py-2 border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500"
              >
                <option value="draft">Draft (0.3mm)</option>
                <option value="standard">Standard (0.2mm)</option>
                <option value="fine">Fine (0.15mm)</option>
              </select>
            </div>

            <div>
              <label className="block text-sm font-medium text-gray-700 mb-1">
                Material
              </label>
              <select
                aria-label="Material type"
                title="Material"
                value={profile.material}
                onChange={(e) => updateProfile({
                  material: e.target.value,
                  nozzleTemperature: e.target.value === 'PLA' ? 210 : e.target.value === 'PETG' ? 240 : 250,
                  bedTemperature: e.target.value === 'PLA' ? 60 : e.target.value === 'PETG' ? 80 : 90
                })}
                className="w-full px-3 py-2 border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500"
              >
                <option value="PLA">PLA</option>
                <option value="PETG">PETG</option>
                <option value="ABS">ABS</option>
                <option value="ASA">ASA</option>
              </select>
            </div>

            <div>
              <label className="block text-sm font-medium text-gray-700 mb-1">
                Infill ({profile.infillPercentage}%)
              </label>
              <input
                aria-label="Infill percentage"
                title="Infill percentage"
                type="range"
                min="0"
                max="100"
                step="5"
                value={profile.infillPercentage}
                onChange={(e) => updateProfile({ infillPercentage: parseInt(e.target.value) })}
                className="w-full h-2 bg-gray-200 rounded-lg appearance-none cursor-pointer"
              />
            </div>

            <div>
              <label className="block text-sm font-medium text-gray-700 mb-1">
                Print Speed ({profile.printSpeed}mm/s)
              </label>
              <input
                aria-label="Print speed"
                title="Print speed"
                type="range"
                min="20"
                max="100"
                step="5"
                value={profile.printSpeed}
                onChange={(e) => updateProfile({ printSpeed: parseInt(e.target.value) })}
                className="w-full h-2 bg-gray-200 rounded-lg appearance-none cursor-pointer"
              />
            </div>

            <div>
              <label className="block text-sm font-medium text-gray-700 mb-1">
                Nozzle Temperature (°C)
              </label>
              <input
                aria-label="Nozzle temperature"
                title="Nozzle temperature"
                type="number"
                min="180"
                max="300"
                value={profile.nozzleTemperature}
                onChange={(e) => updateProfile({ nozzleTemperature: parseInt(e.target.value) })}
                className="w-full px-3 py-2 border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500"
              />
            </div>

            <div>
              <label className="block text-sm font-medium text-gray-700 mb-1">
                Bed Temperature (°C)
              </label>
              <input
                aria-label="Bed temperature"
                title="Bed temperature"
                type="number"
                min="0"
                max="120"
                value={profile.bedTemperature}
                onChange={(e) => updateProfile({ bedTemperature: parseInt(e.target.value) })}
                className="w-full px-3 py-2 border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500"
              />
            </div>
          </div>

          {/* Support structures */}
          <div>
            <label className="flex items-center">
              <input
                aria-label="Enable support structures"
                title="Generate support structures"
                type="checkbox"
                checked={profile.supports}
                onChange={(e) => updateProfile({ supports: e.target.checked })}
                className="mr-2"
              />
              <span className="text-sm font-medium text-gray-700">Generate support structures</span>
            </label>
          </div>

          {/* Slicing progress */}
          {slicingProgress && (
            <div className="bg-blue-50 rounded-lg p-4">
              <div className="flex items-center justify-between mb-2">
                <span className="text-sm font-medium text-blue-900">
                  {slicingProgress.status === 'queued' ? 'Queued for slicing...' :
                   slicingProgress.status === 'slicing' ? 'Slicing in progress...' :
                   slicingProgress.status === 'completed' ? 'Slicing completed!' :
                   'Slicing failed'}
                </span>
                <span className="text-sm text-blue-700">{Math.round(slicingProgress.progress)}%</span>
              </div>
              <div className="w-full bg-blue-200 rounded-full h-2 overflow-hidden">
                {(() => {
                  const pct = Math.max(0, Math.min(100, Math.round(slicingProgress.progress)));
                  const step = Math.round(pct / 5) * 5;
                  const widthClass = `w-[${step}%]` as const;
                  return <>
                    <span className="sr-only">Slicing progress {pct}%</span>
                    <div className={`h-2 bg-blue-600 transition-all duration-300 ${widthClass}`} aria-hidden="true" />
                  </>;
                })()}
              </div>
              {slicingProgress.message && (
                <div className="mt-2 text-sm text-blue-700">{slicingProgress.message}</div>
              )}
            </div>
          )}

          {/* Actions */}
          <div className="flex items-center justify-end space-x-3 pt-4 border-t">
            <button 
              onClick={onClose} 
              disabled={isSlicing}
              className="px-4 py-2 text-gray-700 bg-gray-100 hover:bg-gray-200 rounded-md disabled:opacity-50"
            >
              Cancel
            </button>
            <button
              onClick={handleSlice}
              disabled={!selectedPrinter || isSlicing || (validationResult?.valid === false)}
              className="px-4 py-2 text-white bg-blue-600 hover:bg-blue-700 rounded-md disabled:opacity-50"
            >
              {isSlicing ? 'Slicing...' : 'Slice & Queue Print'}
            </button>
          </div>
        </div>
      </div>
    </div>
  );
};
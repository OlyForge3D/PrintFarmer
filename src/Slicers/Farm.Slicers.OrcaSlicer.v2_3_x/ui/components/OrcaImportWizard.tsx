import React, { useState } from 'react';
import { Upload, FileJson, CheckCircle, AlertCircle, ArrowLeft, ArrowRight } from 'lucide-react';
import { useMutation } from '@tanstack/react-query';
import { orcaProfilesService } from '../services/orcaProfilesService';
import { OrcaBundlePreview } from '../types/orcaProfiles';

type WizardStep = 'upload' | 'preview' | 'review' | 'import' | 'complete';

export const OrcaImportWizard: React.FC = () => {
    const [currentStep, setCurrentStep] = useState<WizardStep>('upload');
    const [bundleJson, setBundleJson] = useState<string>('');
    const [preview, setPreview] = useState<OrcaBundlePreview | null>(null);
    const [selectedPrinters, setSelectedPrinters] = useState<Set<string>>(new Set());
    const [selectedFilaments, setSelectedFilaments] = useState<Set<string>>(new Set());
    const [selectedProcesses, setSelectedProcesses] = useState<Set<string>>(new Set());

    const previewMutation = useMutation({
        mutationFn: (json: string) => orcaProfilesService.previewBundle(json),
        onSuccess: (data) => {
            setPreview(data);
            // Select all presets by default
            setSelectedPrinters(new Set(data.printers.map((p) => p.name)));
            setSelectedFilaments(new Set(data.filaments.map((f) => f.name)));
            setSelectedProcesses(new Set(data.processes.map((p) => p.name)));
            setCurrentStep('preview');
        },
    });

    const importMutation = useMutation({
        mutationFn: () =>
            orcaProfilesService.importBundle({
                bundleJson,
                importPrinters: selectedPrinters.size > 0,
                importFilaments: selectedFilaments.size > 0,
                importProcesses: selectedProcesses.size > 0,
            }),
        onSuccess: () => {
            setCurrentStep('complete');
        },
    });

    const handleFileUpload = (event: React.ChangeEvent<HTMLInputElement>) => {
        const file = event.target.files?.[0];
        if (file) {
            const reader = new FileReader();
            reader.onload = (e) => {
                const content = e.target?.result as string;
                setBundleJson(content);
            };
            reader.readAsText(file);
        }
    };

    const handlePreview = () => {
        if (bundleJson) {
            previewMutation.mutate(bundleJson);
        }
    };

    const handleImport = () => {
        importMutation.mutate();
    };

    const renderStepIndicator = () => {
        const steps = [
            { id: 'upload', label: 'Upload' },
            { id: 'preview', label: 'Preview' },
            { id: 'review', label: 'Review' },
            { id: 'import', label: 'Import' },
        ];

        const stepIndex = steps.findIndex((s) => s.id === currentStep);

        return (
            <div className="flex items-center justify-center mb-8">
                {steps.map((step, index) => (
                    <React.Fragment key={step.id}>
                        <div className="flex items-center">
                            <div
                                className={`flex items-center justify-center w-10 h-10 rounded-full font-semibold ${index <= stepIndex
                                        ? 'bg-blue-600 text-white'
                                        : 'bg-gray-200 text-gray-600'
                                    }`}
                            >
                                {index + 1}
                            </div>
                            <span
                                className={`ml-2 font-medium ${index <= stepIndex ? 'text-blue-600' : 'text-gray-500'
                                    }`}
                            >
                                {step.label}
                            </span>
                        </div>
                        {index < steps.length - 1 && (
                            <div
                                className={`w-16 h-1 mx-4 ${index < stepIndex ? 'bg-blue-600' : 'bg-gray-200'
                                    }`}
                            />
                        )}
                    </React.Fragment>
                ))}
            </div>
        );
    };

    const renderUploadStep = () => (
        <div className="max-w-2xl mx-auto">
            <div className="text-center mb-6">
                <FileJson className="w-16 h-16 mx-auto mb-4 text-blue-600" />
                <h2 className="text-2xl font-bold mb-2">Upload OrcaSlicer Bundle</h2>
                <p className="text-gray-600">
                    Select a config bundle JSON file exported from OrcaSlicer to import presets.
                </p>
            </div>

            <div className="border-2 border-dashed border-gray-300 rounded-lg p-8 text-center hover:border-blue-500 transition-colors">
                <input
                    type="file"
                    accept=".json"
                    onChange={handleFileUpload}
                    className="hidden"
                    id="bundle-upload"
                />
                <label htmlFor="bundle-upload" className="cursor-pointer">
                    <Upload className="w-12 h-12 mx-auto mb-4 text-gray-400" />
                    <p className="text-lg font-medium mb-2">
                        {bundleJson ? 'File loaded' : 'Click to select bundle file'}
                    </p>
                    <p className="text-sm text-gray-500">
                        Supports OrcaSlicer config bundle JSON format
                    </p>
                </label>
            </div>

            {bundleJson && (
                <div className="mt-6">
                    <button
                        onClick={handlePreview}
                        disabled={previewMutation.isPending}
                        className="w-full bg-blue-600 text-white px-6 py-3 rounded-lg font-semibold hover:bg-blue-700 disabled:bg-gray-400 disabled:cursor-not-allowed transition-colors"
                    >
                        {previewMutation.isPending ? (
                            <span className="flex items-center justify-center">
                                <div className="animate-spin rounded-full h-5 w-5 border-b-2 border-white mr-2" />
                                Parsing bundle...
                            </span>
                        ) : (
                            'Preview Bundle'
                        )}
                    </button>
                </div>
            )}

            {previewMutation.isError && (
                <div className="mt-4 p-4 bg-red-50 border border-red-200 rounded-lg">
                    <div className="flex items-start">
                        <AlertCircle className="w-5 h-5 text-red-600 mr-2 mt-0.5" />
                        <div>
                            <p className="font-semibold text-red-800">Failed to parse bundle</p>
                            <p className="text-sm text-red-700 mt-1">
                                {previewMutation.error instanceof Error
                                    ? previewMutation.error.message
                                    : 'Invalid bundle format'}
                            </p>
                        </div>
                    </div>
                </div>
            )}
        </div>
    );

    const renderPreviewStep = () => {
        if (!preview) return null;

        return (
            <div className="max-w-4xl mx-auto">
                <h2 className="text-2xl font-bold mb-6">Bundle Preview</h2>

                <div className="grid grid-cols-1 md:grid-cols-3 gap-6 mb-8">
                    <div className="bg-blue-50 p-6 rounded-lg border border-blue-200">
                        <h3 className="font-semibold text-blue-900 mb-2">Printers</h3>
                        <p className="text-3xl font-bold text-blue-600">{preview.printers.length}</p>
                        <p className="text-sm text-blue-700 mt-1">printer presets</p>
                    </div>

                    <div className="bg-green-50 p-6 rounded-lg border border-green-200">
                        <h3 className="font-semibold text-green-900 mb-2">Filaments</h3>
                        <p className="text-3xl font-bold text-green-600">{preview.filaments.length}</p>
                        <p className="text-sm text-green-700 mt-1">filament presets</p>
                    </div>

                    <div className="bg-purple-50 p-6 rounded-lg border border-purple-200">
                        <h3 className="font-semibold text-purple-900 mb-2">Processes</h3>
                        <p className="text-3xl font-bold text-purple-600">{preview.processes.length}</p>
                        <p className="text-sm text-purple-700 mt-1">process presets</p>
                    </div>
                </div>

                <div className="space-y-6">
                    {/* Printer Presets */}
                    {preview.printers.length > 0 && (
                        <div className="bg-white border border-gray-200 rounded-lg p-6">
                            <h3 className="text-lg font-semibold mb-4 flex items-center">
                                <input
                                    type="checkbox"
                                    checked={selectedPrinters.size === preview.printers.length}
                                    onChange={(e) => {
                                        if (e.target.checked) {
                                            setSelectedPrinters(new Set(preview.printers.map((p) => p.name)));
                                        } else {
                                            setSelectedPrinters(new Set());
                                        }
                                    }}
                                    className="mr-3"
                                    aria-label="Select all printer presets"
                                />
                                Printer Presets ({preview.printers.length})
                            </h3>
                            <div className="space-y-2">
                                {preview.printers.map((printer) => (
                                    <label
                                        key={printer.name}
                                        className="flex items-start p-3 hover:bg-gray-50 rounded cursor-pointer"
                                    >
                                        <input
                                            type="checkbox"
                                            checked={selectedPrinters.has(printer.name)}
                                            onChange={(e) => {
                                                const newSet = new Set(selectedPrinters);
                                                if (e.target.checked) {
                                                    newSet.add(printer.name);
                                                } else {
                                                    newSet.delete(printer.name);
                                                }
                                                setSelectedPrinters(newSet);
                                            }}
                                            className="mt-1 mr-3"
                                        />
                                        <div className="flex-1">
                                            <p className="font-medium">{printer.name}</p>
                                            <p className="text-sm text-gray-600">
                                                {printer.manufacturer} • {printer.bedWidth}x{printer.bedDepth}x
                                                {printer.maxZHeight}mm • {printer.nozzleDiameter}mm nozzle
                                            </p>
                                        </div>
                                    </label>
                                ))}
                            </div>
                        </div>
                    )}

                    {/* Filament Presets */}
                    {preview.filaments.length > 0 && (
                        <div className="bg-white border border-gray-200 rounded-lg p-6">
                            <h3 className="text-lg font-semibold mb-4 flex items-center">
                                <input
                                    type="checkbox"
                                    checked={selectedFilaments.size === preview.filaments.length}
                                    onChange={(e) => {
                                        if (e.target.checked) {
                                            setSelectedFilaments(new Set(preview.filaments.map((f) => f.name)));
                                        } else {
                                            setSelectedFilaments(new Set());
                                        }
                                    }}
                                    className="mr-3"
                                    aria-label="Select all filament presets"
                                />
                                Filament Presets ({preview.filaments.length})
                            </h3>
                            <div className="space-y-2">
                                {preview.filaments.map((filament) => (
                                    <label
                                        key={filament.name}
                                        className="flex items-start p-3 hover:bg-gray-50 rounded cursor-pointer"
                                    >
                                        <input
                                            type="checkbox"
                                            checked={selectedFilaments.has(filament.name)}
                                            onChange={(e) => {
                                                const newSet = new Set(selectedFilaments);
                                                if (e.target.checked) {
                                                    newSet.add(filament.name);
                                                } else {
                                                    newSet.delete(filament.name);
                                                }
                                                setSelectedFilaments(newSet);
                                            }}
                                            className="mt-1 mr-3"
                                        />
                                        <div className="flex-1">
                                            <p className="font-medium">{filament.name}</p>
                                            <p className="text-sm text-gray-600">
                                                {filament.filamentType}
                                                {filament.nozzleTemperature &&
                                                    ` • ${filament.nozzleTemperature}°C nozzle`}
                                                {filament.bedTemperature && ` • ${filament.bedTemperature}°C bed`}
                                            </p>
                                        </div>
                                    </label>
                                ))}
                            </div>
                        </div>
                    )}

                    {/* Process Presets */}
                    {preview.processes.length > 0 && (
                        <div className="bg-white border border-gray-200 rounded-lg p-6">
                            <h3 className="text-lg font-semibold mb-4 flex items-center">
                                <input
                                    type="checkbox"
                                    checked={selectedProcesses.size === preview.processes.length}
                                    onChange={(e) => {
                                        if (e.target.checked) {
                                            setSelectedProcesses(new Set(preview.processes.map((p) => p.name)));
                                        } else {
                                            setSelectedProcesses(new Set());
                                        }
                                    }}
                                    className="mr-3"
                                    aria-label="Select all process presets"
                                />
                                Process Presets ({preview.processes.length})
                            </h3>
                            <div className="space-y-2">
                                {preview.processes.map((process) => (
                                    <label
                                        key={process.name}
                                        className="flex items-start p-3 hover:bg-gray-50 rounded cursor-pointer"
                                    >
                                        <input
                                            type="checkbox"
                                            checked={selectedProcesses.has(process.name)}
                                            onChange={(e) => {
                                                const newSet = new Set(selectedProcesses);
                                                if (e.target.checked) {
                                                    newSet.add(process.name);
                                                } else {
                                                    newSet.delete(process.name);
                                                }
                                                setSelectedProcesses(newSet);
                                            }}
                                            className="mt-1 mr-3"
                                        />
                                        <div className="flex-1">
                                            <p className="font-medium">{process.name}</p>
                                            <p className="text-sm text-gray-600">
                                                {process.layerHeight}mm layer • {process.infillPercentage}% infill
                                                {process.quality && ` • ${process.quality} quality`}
                                            </p>
                                        </div>
                                    </label>
                                ))}
                            </div>
                        </div>
                    )}
                </div>

                <div className="mt-8 flex justify-between">
                    <button
                        onClick={() => setCurrentStep('upload')}
                        className="px-6 py-2 border border-gray-300 rounded-lg font-medium hover:bg-gray-50 transition-colors flex items-center"
                    >
                        <ArrowLeft className="w-4 h-4 mr-2" />
                        Back
                    </button>
                    <button
                        onClick={handleImport}
                        disabled={
                            importMutation.isPending ||
                            (selectedPrinters.size === 0 &&
                                selectedFilaments.size === 0 &&
                                selectedProcesses.size === 0)
                        }
                        className="px-6 py-2 bg-blue-600 text-white rounded-lg font-semibold hover:bg-blue-700 disabled:bg-gray-400 disabled:cursor-not-allowed transition-colors flex items-center"
                    >
                        {importMutation.isPending ? (
                            <>
                                <div className="animate-spin rounded-full h-4 w-4 border-b-2 border-white mr-2" />
                                Importing...
                            </>
                        ) : (
                            <>
                                Import Selected
                                <ArrowRight className="w-4 h-4 ml-2" />
                            </>
                        )}
                    </button>
                </div>

                {importMutation.isError && (
                    <div className="mt-4 p-4 bg-red-50 border border-red-200 rounded-lg">
                        <div className="flex items-start">
                            <AlertCircle className="w-5 h-5 text-red-600 mr-2 mt-0.5" />
                            <div>
                                <p className="font-semibold text-red-800">Import failed</p>
                                <p className="text-sm text-red-700 mt-1">
                                    {importMutation.error instanceof Error
                                        ? importMutation.error.message
                                        : 'Failed to import presets'}
                                </p>
                            </div>
                        </div>
                    </div>
                )}
            </div>
        );
    };

    const renderCompleteStep = () => (
        <div className="max-w-2xl mx-auto text-center">
            <CheckCircle className="w-20 h-20 mx-auto mb-6 text-green-600" />
            <h2 className="text-3xl font-bold mb-4">Import Complete!</h2>
            <p className="text-lg text-gray-600 mb-8">
                Your OrcaSlicer presets have been successfully imported.
            </p>

            <div className="bg-green-50 border border-green-200 rounded-lg p-6 mb-8">
                <div className="grid grid-cols-3 gap-4">
                    <div>
                        <p className="text-2xl font-bold text-green-600">{selectedPrinters.size}</p>
                        <p className="text-sm text-gray-700">Printers</p>
                    </div>
                    <div>
                        <p className="text-2xl font-bold text-green-600">{selectedFilaments.size}</p>
                        <p className="text-sm text-gray-700">Filaments</p>
                    </div>
                    <div>
                        <p className="text-2xl font-bold text-green-600">{selectedProcesses.size}</p>
                        <p className="text-sm text-gray-700">Processes</p>
                    </div>
                </div>
            </div>

            <div className="flex gap-4 justify-center">
                <button
                    onClick={() => {
                        setCurrentStep('upload');
                        setBundleJson('');
                        setPreview(null);
                        setSelectedPrinters(new Set());
                        setSelectedFilaments(new Set());
                        setSelectedProcesses(new Set());
                    }}
                    className="px-6 py-2 border border-gray-300 rounded-lg font-medium hover:bg-gray-50 transition-colors"
                >
                    Import Another Bundle
                </button>
                <button
                    onClick={() => (window.location.href = '/profiles')}
                    className="px-6 py-2 bg-blue-600 text-white rounded-lg font-semibold hover:bg-blue-700 transition-colors"
                >
                    View Profiles
                </button>
            </div>
        </div>
    );

    return (
        <div className="min-h-screen bg-gray-50 py-12 px-4">
            <div className="max-w-6xl mx-auto">
                <div className="mb-8">
                    <h1 className="text-3xl font-bold text-gray-900 mb-2">
                        Import OrcaSlicer Profiles
                    </h1>
                    <p className="text-gray-600">
                        Import printer, filament, and process presets from OrcaSlicer config bundles.
                    </p>
                </div>

                {currentStep !== 'complete' && renderStepIndicator()}

                <div className="bg-white rounded-lg shadow-lg p-8">
                    {currentStep === 'upload' && renderUploadStep()}
                    {currentStep === 'preview' && renderPreviewStep()}
                    {currentStep === 'complete' && renderCompleteStep()}
                </div>
            </div>
        </div>
    );
};

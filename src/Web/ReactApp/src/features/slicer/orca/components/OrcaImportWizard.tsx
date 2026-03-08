/* eslint-disable local/pf-no-raw-html-controls -- File input with custom label styling and complex checkbox lists require raw controls */
/**
 * ⚠️ IMPORTANT: This component uses PrintFarmer Design System
 * 
 * DESIGN SYSTEM REQUIREMENTS for workspace package UI:
 * - Use CSS variables from src/Web/ReactApp/src/styles/theme.css (pf-* variables)
 * - Import PrintFarmer components: PageTemplate, Button, Alert, FormField, Select
 * - Reference: src/Web/ReactApp/src/pages/ImportOfficialProfilesPage.tsx for correct styling patterns
 * - Do NOT use generic Tailwind colors (bg-pf-accent-bg, text-pf-error, etc)
 * - Use pf-* classes: bg-pf-panel, text-pf-text-primary, border-pf-border, etc.
 * - For interactive states: bg-pf-accent-2 (primary), bg-pf-error (danger), bg-pf-accent (success)
 */

'use client';

import React, { useState } from 'react';
import { UploadIcon, CheckCircleIcon, FileJsonIcon, AlertCircleIcon, ArrowLeftIcon, ArrowRightIcon } from '@/common/components/icons/MdiIcons';
import { useMutation } from '@tanstack/react-query';
import { orcaProfilesService } from '../services/orcaProfilesService';
import type { OrcaBundlePreview } from '../types/orcaProfiles';

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
                selectedPrinters: Array.from(selectedPrinters),
                selectedFilaments: Array.from(selectedFilaments),
                selectedProcesses: Array.from(selectedProcesses),
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
                                className={`flex items-center justify-center w-10 h-10 rounded-full font-semibold transition-colors ${
                                    index <= stepIndex
                                        ? 'bg-pf-accent-2 text-pf-text-primary'
                                        : 'bg-pf-border text-pf-text-secondary'
                                }`}
                            >
                                {index + 1}
                            </div>
                            <span
                                className={`ml-2 font-medium transition-colors ${
                                    index <= stepIndex ? 'text-pf-accent-2' : 'text-pf-text-muted'
                                }`}
                            >
                                {step.label}
                            </span>
                        </div>
                        {index < steps.length - 1 && (
                            <div
                                className={`w-16 h-1 mx-4 transition-colors ${
                                    index < stepIndex ? 'bg-pf-accent-2' : 'bg-pf-border-light'
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
                <FileJsonIcon className="w-16 h-16 mx-auto mb-4 text-pf-accent-2" />
                <h2 className="text-2xl font-bold mb-2 text-pf-text-primary">Upload OrcaSlicer Bundle</h2>
                <p className="text-pf-text-secondary">
                    Select a config bundle JSON file exported from OrcaSlicer to import presets.
                </p>
            </div>

            <div className="border-2 border-dashed border-pf-border-medium rounded-lg p-8 text-center hover:border-pf-accent-2 transition-colors bg-pf-bg-2">
                <input
                    type="file"
                    accept=".json"
                    onChange={handleFileUpload}
                    className="hidden"
                    id="bundle-upload"
                />
                <label htmlFor="bundle-upload" className="cursor-pointer">
                    <UploadIcon className="w-12 h-12 mx-auto mb-4 text-pf-text-muted" />
                    <p className="text-lg font-medium mb-2 text-pf-text-primary">
                        {bundleJson ? 'File loaded' : 'Click to select bundle file'}
                    </p>
                    <p className="text-sm text-pf-text-secondary">
                        Supports OrcaSlicer config bundle JSON format
                    </p>
                </label>
            </div>

            {bundleJson && (
                <div className="mt-6">
                    <button
                        onClick={handlePreview}
                        disabled={previewMutation.isPending}
                        className="w-full bg-pf-accent-2 text-pf-text-primary px-6 py-3 rounded-lg font-semibold hover:opacity-90 disabled:bg-pf-border disabled:cursor-not-allowed transition-colors"
                    >
                        {previewMutation.isPending ? (
                            <span className="flex items-center justify-center">
                                <div className="animate-spin rounded-full h-5 w-5 border-b-2 border-current mr-2" />
                                Parsing bundle...
                            </span>
                        ) : (
                            'Preview Bundle'
                        )}
                    </button>
                </div>
            )}

            {previewMutation.isError && (
                <div className="mt-4 p-4 bg-pf-error-bg border border-pf-error rounded-lg">
                    <div className="flex items-start">
                        <AlertCircleIcon className="w-5 h-5 text-pf-error mr-2 mt-0.5" />
                        <div>
                            <p className="font-semibold text-pf-error">Failed to parse bundle</p>
                            <p className="text-sm text-pf-error mt-1">
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
                <h2 className="text-2xl font-bold mb-6 text-pf-text-primary">Bundle Preview</h2>

                <div className="grid grid-cols-1 md:grid-cols-3 gap-6 mb-8">
                    <div className="bg-pf-panel p-6 rounded-lg border border-pf-border">
                        <h3 className="font-semibold text-pf-text-primary mb-2">Printers</h3>
                        <p className="text-3xl font-bold text-pf-accent-2">{preview.printers.length}</p>
                        <p className="text-sm text-pf-text-secondary mt-1">printer presets</p>
                    </div>

                    <div className="bg-pf-panel p-6 rounded-lg border border-pf-border">
                        <h3 className="font-semibold text-pf-text-primary mb-2">Filaments</h3>
                        <p className="text-3xl font-bold text-pf-accent">{preview.filaments.length}</p>
                        <p className="text-sm text-pf-text-secondary mt-1">filament presets</p>
                    </div>

                    <div className="bg-pf-panel p-6 rounded-lg border border-pf-border">
                        <h3 className="font-semibold text-pf-text-primary mb-2">Processes</h3>
                        <p className="text-3xl font-bold text-pf-accent-2">{preview.processes.length}</p>
                        <p className="text-sm text-pf-text-secondary mt-1">process presets</p>
                    </div>
                </div>

                <div className="space-y-6">
                    {/* Printer Presets */}
                    {preview.printers.length > 0 && (
                        <div className="bg-pf-panel border border-pf-border rounded-lg p-6">
                            <h3 className="text-lg font-semibold mb-4 flex items-center text-pf-text-primary">
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
                                    className="mr-3 w-4 h-4 rounded border-pf-border bg-pf-bg-1 cursor-pointer accent-pf-accent-2"
                                    aria-label="Select all printer presets"
                                />
                                Printer Presets ({preview.printers.length})
                            </h3>
                            <div className="space-y-2">
                                {preview.printers.map((printer) => (
                                    <label
                                        key={printer.name}
                                        className="flex items-start p-3 hover:bg-pf-bg-2 rounded cursor-pointer transition-colors"
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
                                            className="mt-1 mr-3 w-4 h-4 rounded border-pf-border bg-pf-bg-1 cursor-pointer accent-pf-accent-2"
                                        />
                                        <div className="flex-1">
                                            <p className="font-medium text-pf-text-primary">{printer.name}</p>
                                            <p className="text-sm text-pf-text-secondary">
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
                        <div className="bg-pf-panel border border-pf-border rounded-lg p-6">
                            <h3 className="text-lg font-semibold mb-4 flex items-center text-pf-text-primary">
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
                                    className="mr-3 w-4 h-4 rounded border-pf-border bg-pf-bg-1 cursor-pointer accent-pf-accent-2"
                                    aria-label="Select all filament presets"
                                />
                                Filament Presets ({preview.filaments.length})
                            </h3>
                            <div className="space-y-2">
                                {preview.filaments.map((filament) => (
                                    <label
                                        key={filament.name}
                                        className="flex items-start p-3 hover:bg-pf-bg-2 rounded cursor-pointer transition-colors"
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
                                            className="mt-1 mr-3 w-4 h-4 rounded border-pf-border bg-pf-bg-1 cursor-pointer accent-pf-accent-2"
                                        />
                                        <div className="flex-1">
                                            <p className="font-medium text-pf-text-primary">{filament.name}</p>
                                            <p className="text-sm text-pf-text-secondary">
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
                        <div className="bg-pf-panel border border-pf-border rounded-lg p-6">
                            <h3 className="text-lg font-semibold mb-4 flex items-center text-pf-text-primary">
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
                                    className="mr-3 w-4 h-4 rounded border-pf-border bg-pf-bg-1 cursor-pointer accent-pf-accent-2"
                                    aria-label="Select all process presets"
                                />
                                Process Presets ({preview.processes.length})
                            </h3>
                            <div className="space-y-2">
                                {preview.processes.map((process) => (
                                    <label
                                        key={process.name}
                                        className="flex items-start p-3 hover:bg-pf-bg-2 rounded cursor-pointer transition-colors"
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
                                            className="mt-1 mr-3 w-4 h-4 rounded border-pf-border bg-pf-bg-1 cursor-pointer accent-pf-accent-2"
                                        />
                                        <div className="flex-1">
                                            <p className="font-medium text-pf-text-primary">{process.name}</p>
                                            <p className="text-sm text-pf-text-secondary">
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
                        className="px-6 py-2 border border-pf-border rounded-lg font-medium hover:bg-pf-bg-2 transition-colors flex items-center text-pf-text-primary"
                    >
                        <ArrowLeftIcon className="w-4 h-4 mr-2" />
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
                        className="px-6 py-2 bg-pf-accent-2 text-pf-text-primary rounded-lg font-semibold hover:opacity-90 disabled:bg-pf-border disabled:cursor-not-allowed transition-colors flex items-center"
                    >
                        {importMutation.isPending ? (
                            <>
                                <div className="animate-spin rounded-full h-4 w-4 border-b-2 border-current mr-2" />
                                Importing...
                            </>
                        ) : (
                            <>
                                Import Selected
                                <ArrowRightIcon className="w-4 h-4 ml-2" />
                            </>
                        )}
                    </button>
                </div>

                {importMutation.isError && (
                    <div className="mt-4 p-4 bg-pf-error-bg border border-pf-error rounded-lg">
                        <div className="flex items-start">
                            <AlertCircleIcon className="w-5 h-5 text-pf-error mr-2 mt-0.5" />
                            <div>
                                <p className="font-semibold text-pf-error">Import failed</p>
                                <p className="text-sm text-pf-error mt-1">
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
            <CheckCircleIcon className="w-20 h-20 mx-auto mb-6 text-pf-accent" />
            <h2 className="text-3xl font-bold mb-4 text-pf-text-primary">Import Complete!</h2>
            <p className="text-lg text-pf-text-secondary mb-8">
                Your OrcaSlicer presets have been successfully imported.
            </p>

            <div className="bg-pf-panel border border-pf-border rounded-lg p-6 mb-8">
                <div className="grid grid-cols-3 gap-4">
                    <div>
                        <p className="text-2xl font-bold text-pf-accent">{selectedPrinters.size}</p>
                        <p className="text-sm text-pf-text-secondary">Printers</p>
                    </div>
                    <div>
                        <p className="text-2xl font-bold text-pf-accent">{selectedFilaments.size}</p>
                        <p className="text-sm text-pf-text-secondary">Filaments</p>
                    </div>
                    <div>
                        <p className="text-2xl font-bold text-pf-accent">{selectedProcesses.size}</p>
                        <p className="text-sm text-pf-text-secondary">Processes</p>
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
                    className="px-6 py-2 border border-pf-border rounded-lg font-medium hover:bg-pf-bg-2 transition-colors text-pf-text-primary"
                >
                    Import Another Bundle
                </button>
                <button
                    onClick={() => (window.location.href = '/profiles')}
                    className="px-6 py-2 bg-pf-accent-2 text-pf-text-primary rounded-lg font-semibold hover:opacity-90 transition-colors"
                >
                    View Profiles
                </button>
            </div>
        </div>
    );

    return (
        <div className="min-h-screen bg-pf-bg-0 py-12 px-4">
            <div className="max-w-6xl mx-auto">
                <div className="mb-8">
                    <h1 className="text-3xl font-bold text-pf-text-primary mb-2">
                        Import OrcaSlicer Profiles
                    </h1>
                    <p className="text-pf-text-secondary">
                        Import printer, filament, and process presets from OrcaSlicer config bundles.
                    </p>
                </div>

                {currentStep !== 'complete' && renderStepIndicator()}

                <div className="bg-pf-panel rounded-lg border border-pf-border shadow-lg p-8">
                    {currentStep === 'upload' && renderUploadStep()}
                    {currentStep === 'preview' && renderPreviewStep()}
                    {currentStep === 'complete' && renderCompleteStep()}
                </div>
            </div>
        </div>
    );
};

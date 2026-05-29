import React, { useState, useMemo } from 'react';
import { useNavigate } from 'react-router';
import { useQuery } from '@tanstack/react-query';
import { AlertCircleIcon, LayersIcon } from '@/common/components/icons/MdiIcons';
import { PageTemplate } from '@/common/components/PageTemplate';
import { Button, Select, FormField, Badge, Spinner } from '@/common/components/ui';
import { Download, ArrowRight, Printer, Settings2, LinkIcon } from 'lucide-react';
import { officialProfilesService } from '@/services/officialProfilesService';
import { PrinterBackend } from '@/types/api';
import { apiClient } from '@/services/api';
import type { MachineProfileDto } from '@/features/tasks/components/profile-wizard';

interface PrinterListItem {
    id: string;
    name: string;
    backend: PrinterBackend;
    modelId?: string;
    modelName?: string;
}

function getBackendName(backend: PrinterBackend | string): string {
    if (typeof backend === 'string') return backend;
    switch (backend) {
        case PrinterBackend.Moonraker: return 'Moonraker';
        case PrinterBackend.PrusaLink: return 'PrusaLink';
        case PrinterBackend.SDCP: return 'SDCP';
        case PrinterBackend.OctoPrint: return 'OctoPrint';
        case PrinterBackend.FlashForge: return 'FlashForge';
        default: return `Unknown (${backend})`;
    }
}

export const ImportOfficialProfilesPage: React.FC = () => {
    const navigate = useNavigate();
    const [selectedPrinterId, setSelectedPrinterId] = useState<string>('');

    // Fetch registered printers
    const { data: printers = [] } = useQuery({
        queryKey: ['printers-for-profile-import'],
        queryFn: async () => {
            const printerList = await apiClient.getPrinters();
            return printerList.map(p => ({
                id: p.id || '',
                name: p.name || 'Unknown',
                backend: p.backend ?? 0,
                modelId: p.modelId,
                modelName: p.modelName,
            } as PrinterListItem));
        },
        staleTime: 30_000,
    });

    const selectedPrinter = printers.find(p => p.id === selectedPrinterId);
    const modelId = selectedPrinter?.modelId;

    // Fetch machine profiles from OrcaSlicer worker for the printer's model
    const {
        data: machineProfiles = [],
        isLoading: machineProfilesLoading,
        error: machineProfilesError,
    } = useQuery({
        queryKey: ['machine-profiles-for-model', modelId],
        queryFn: async () => {
            if (!modelId) return [];
            const res = await apiClient.get<MachineProfileDto[]>(`/slicer/profiles/machine/for-model/${modelId}`);
            return res.data;
        },
        enabled: !!modelId,
        retry: false,
        staleTime: 60_000,
    });

    // Fetch already-imported profile names
    const { data: importedNames } = useQuery({
        queryKey: ['imported-profile-names', modelId],
        queryFn: async () => {
            if (!modelId) return null;
            return await officialProfilesService.getImportedProfileNamesForModel(modelId);
        },
        enabled: !!modelId,
        staleTime: 30_000,
    });

    const importedMachineSet = useMemo(
        () => new Set(importedNames?.machineProfileNames ?? []),
        [importedNames],
    );

    const alreadyImportedCount = useMemo(
        () => machineProfiles.filter(p => importedMachineSet.has(p.name)).length,
        [machineProfiles, importedMachineSet],
    );

    // Group machine profiles by nozzle diameter
    const groupedByNozzle = useMemo(() => {
        const groups = new Map<string, MachineProfileDto[]>();
        for (const profile of machineProfiles) {
            const key = profile.nozzleDiameter != null
                ? `${profile.nozzleDiameter}mm nozzle`
                : 'Default';
            const list = groups.get(key) ?? [];
            list.push(profile);
            groups.set(key, list);
        }
        return Array.from(groups.entries()).sort(([a], [b]) => a.localeCompare(b));
    }, [machineProfiles]);

    const handleStartWizard = () => {
        if (modelId) {
            navigate(`/profiles/import?modelId=${modelId}`);
        }
    };

    return (
        <PageTemplate
            title="Import Official Profiles"
            subtitle="Import OrcaSlicer profiles for your registered printers"
            icon={Download}
        >
            <div className="grid md:grid-cols-4 gap-6">
                {/* Left: Printer Selection */}
                <div className="md:col-span-1">
                    <div className="card bg-pf-panel border border-pf-border rounded-sm shadow-sm p-4 sticky top-20">
                        <h3 className="font-semibold mb-4">Select Printer</h3>

                        <FormField label="Printer">
                            <Select
                                value={selectedPrinterId}
                                onChange={e => setSelectedPrinterId(e.target.value)}
                            >
                                <option value="">-- Choose Printer --</option>
                                {printers.map(p => (
                                    <option key={p.id} value={p.id}>
                                        {p.name} ({getBackendName(p.backend)})
                                    </option>
                                ))}
                            </Select>
                        </FormField>

                        {selectedPrinter && (
                            <div className="mt-4 p-3 bg-pf-background rounded-sm text-sm space-y-1">
                                <p className="text-pf-text-muted">
                                    Printer: <span className="font-medium text-pf-text-primary">{selectedPrinter.name}</span>
                                </p>
                                {selectedPrinter.modelName && (
                                    <p className="text-pf-text-muted">
                                        Model: <span className="font-medium text-pf-text-primary">{selectedPrinter.modelName}</span>
                                    </p>
                                )}
                            </div>
                        )}

                        {selectedPrinter && modelId && machineProfiles.length > 0 && (
                            <Button
                                type="button"
                                variant="primary"
                                className="w-full mt-6"
                                onClick={handleStartWizard}
                                iconRight={<ArrowRight className="w-4 h-4" />}
                            >
                                Start Import Wizard
                            </Button>
                        )}
                    </div>
                </div>

                {/* Right: Profile Preview */}
                <div className="md:col-span-3">
                    {!selectedPrinterId ? (
                        <div className="card bg-pf-panel border border-pf-border rounded-sm shadow-sm p-8 text-center">
                            <Printer className="w-12 h-12 text-pf-text-muted mx-auto mb-4" />
                            <p className="text-pf-text-muted">Select a printer to see available profiles</p>
                        </div>
                    ) : !modelId ? (
                        <div className="card bg-pf-panel border border-pf-border rounded-sm shadow-sm p-8 text-center">
                            <LinkIcon className="w-12 h-12 text-pf-text-muted mx-auto mb-4" />
                            <p className="text-pf-text-primary font-medium mb-2">No Printer Model Linked</p>
                            <p className="text-pf-text-muted text-sm mb-4">
                                This printer is not associated with a catalog model. Link it to a printer model to import official profiles.
                            </p>
                            <Button
                                variant="secondary"
                                onClick={() => navigate(`/printers/${selectedPrinterId}/edit`)}
                            >
                                Edit Printer
                            </Button>
                        </div>
                    ) : machineProfilesError ? (
                        <div className="card bg-pf-error/10 border border-pf-error rounded-sm shadow-sm p-8 text-center">
                            <AlertCircleIcon className="w-12 h-12 text-pf-error mx-auto mb-4" />
                            <p className="text-pf-error font-medium mb-2">Failed to Load Profiles</p>
                            <p className="text-pf-error text-sm">{(machineProfilesError as Error).message}</p>
                            <p className="text-pf-error text-xs mt-3 italic">
                                The OrcaSlicer worker service may not be running. Check the server logs.
                            </p>
                        </div>
                    ) : machineProfilesLoading ? (
                        <div className="card bg-pf-panel border border-pf-border rounded-sm shadow-sm p-8 text-center">
                            <Spinner size="lg" />
                            <p className="text-pf-text-muted mt-4">Loading profiles from OrcaSlicer worker…</p>
                        </div>
                    ) : machineProfiles.length === 0 ? (
                        <div className="card bg-pf-panel border border-pf-border rounded-sm shadow-sm p-8 text-center">
                            <AlertCircleIcon className="w-12 h-12 text-pf-text-muted mx-auto mb-4" />
                            <p className="text-pf-text-muted">No official profiles found for this printer model</p>
                        </div>
                    ) : (
                        <div className="space-y-4">
                            {/* Summary banner */}
                            <div className="card bg-pf-accent-bg border border-pf-accent/30 rounded-sm shadow-sm p-4 flex flex-col sm:flex-row items-start sm:items-center gap-4">
                                <LayersIcon className="w-8 h-8 text-pf-accent shrink-0" />
                                <div className="flex-1">
                                    <p className="font-semibold text-pf-text-primary">
                                        {machineProfiles.length} machine profile{machineProfiles.length !== 1 ? 's' : ''} available
                                        {selectedPrinter?.modelName && (
                                            <> for <span className="text-pf-accent">{selectedPrinter.modelName}</span></>
                                        )}
                                    </p>
                                    <p className="text-sm text-pf-text-secondary mt-1">
                                        {alreadyImportedCount > 0
                                            ? `${alreadyImportedCount} already imported. `
                                            : ''}
                                        Use the import wizard to select machine, filament, and process profiles.
                                    </p>
                                </div>
                                <Button
                                    variant="primary"
                                    onClick={handleStartWizard}
                                    iconRight={<ArrowRight className="w-4 h-4" />}
                                >
                                    Import Wizard
                                </Button>
                            </div>

                            {/* Machine profiles grouped by nozzle */}
                            {groupedByNozzle.map(([group, profiles]) => (
                                <div key={group} className="card bg-pf-panel border border-pf-border rounded-sm shadow-sm">
                                    <div className="card-header bg-pf-bg-2 p-3 flex items-center gap-2">
                                        <Settings2 className="w-4 h-4 text-pf-text-muted" />
                                        <h4 className="font-semibold text-sm">{group}</h4>
                                        <Badge variant="default" size="sm">{profiles.length}</Badge>
                                    </div>
                                    <div className="card-body p-3">
                                        <div className="space-y-2">
                                            {profiles.map(profile => {
                                                const isImported = importedMachineSet.has(profile.name);
                                                return (
                                                    <div
                                                        key={profile.name}
                                                        className="flex items-center gap-3 p-2 rounded-sm"
                                                    >
                                                        <div className="flex-1">
                                                            <p className="text-sm font-medium">{profile.name}</p>
                                                            {profile.manufacturer && (
                                                                <p className="text-xs text-pf-text-muted">
                                                                    {profile.manufacturer}
                                                                </p>
                                                            )}
                                                        </div>
                                                        {isImported && (
                                                            <Badge variant="success" size="sm">Imported</Badge>
                                                        )}
                                                    </div>
                                                );
                                            })}
                                        </div>
                                    </div>
                                </div>
                            ))}
                        </div>
                    )}
                </div>
            </div>
        </PageTemplate>
    );
};

export default ImportOfficialProfilesPage;

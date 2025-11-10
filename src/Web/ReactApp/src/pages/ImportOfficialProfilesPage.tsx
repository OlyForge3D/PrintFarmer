import React, { useState, useMemo } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { Download, Check, AlertCircle } from 'lucide-react';
import { PageTemplate } from '@/components/PageTemplate';
import { Button } from '@/components/ui/Button';
import { Alert } from '@/components/ui/Alert';
import { Select } from '@/components/ui/Select';
import { FormField } from '@/components/ui/FormField';
import officialProfilesService from '@/services/officialProfilesService';
import { getApiBaseUrl } from '@/utils/apiUrlHelpers';

interface PrinterListItem {
    id: string;
    name: string;
    backend: number; // 0=Moonraker, 1=PrusaLink, 2=SDCP, 3=OctoPrint
    modelId?: string;
    modelName?: string;
}

function getBackendName(backend: number | string): string {
    if (typeof backend === 'string') return backend;
    switch (backend) {
        case 0: return 'Moonraker';
        case 1: return 'PrusaLink';
        case 2: return 'SDCP';
        case 3: return 'OctoPrint';
        default: return `Unknown (${backend})`;
    }
}

interface AvailableProfile {
    id: string;
    name: string;
    material: string;
    quality: string;
    layerHeight: number;
    infillPercentage: number;
    isSystem: boolean;
    slicerType: string;
}

export const ImportOfficialProfilesPage: React.FC = () => {
    const qc = useQueryClient();

    // State
    const [selectedPrinterId, setSelectedPrinterId] = useState<string>('');
    const [selectedProfileIds, setSelectedProfileIds] = useState<Set<string>>(new Set());
    const [makePublic, setMakePublic] = useState(false);
    const [importMessage, setImportMessage] = useState<string | null>(null);
    const [importError, setImportError] = useState<string | null>(null);

    // Fetch registered printers
    const { data: printers = [] } = useQuery({
        queryKey: ['printers-for-profile-import'],
        queryFn: async () => {
            const baseUrl = getApiBaseUrl();
            const token = localStorage.getItem('auth-token');
            const headers: HeadersInit = { 'Content-Type': 'application/json' };
            if (token) headers['Authorization'] = `Bearer ${token}`;

            const res = await fetch(`${baseUrl}/printers`, { headers });
            if (!res.ok) throw new Error('Failed to load printers');

            const json = await res.json() as unknown[];
            return json.map(p => {
                const printer = p as { id?: string; name?: string; backend?: number; modelId?: string; modelName?: string };
                return {
                    id: printer.id || '',
                    name: printer.name || 'Unknown',
                    backend: printer.backend ?? 0,
                    modelId: printer.modelId,
                    modelName: printer.modelName
                } as PrinterListItem;
            });
        },
        staleTime: 30_000
    });

  // Fetch available profiles from OrcaSlicer worker
  // These are the actual profiles from the OrcaSlicer installation,
  // not previously imported/system profiles
  const { data: officialProfiles = [], isLoading: profilesLoading } = useQuery({
    queryKey: ["official-profiles-from-worker"],
    queryFn: async () => {
      try {
        const profiles = await officialProfilesService.getAvailableProfilesFromWorker();
        return profiles;
      } catch (error) {
        console.error("Failed to fetch profiles from worker:", error);
        throw error;
      }
    },
  });

  // Group profiles by material and quality
  const groupedProfiles = useMemo(() => {
    const groups: { [key: string]: AvailableProfile[] } = {};
    officialProfiles.forEach((profile: AvailableProfile) => {
      const key = `${profile.material} • ${profile.quality}`;
      if (!groups[key]) groups[key] = [];
      groups[key].push(profile);
    });
    return Object.entries(groups).sort();
  }, [officialProfiles]);

    // Import mutation
    const importMutation = useMutation({
        mutationFn: async () => {
            if (!selectedPrinterId || selectedProfileIds.size === 0) {
                throw new Error('Please select a printer and at least one profile');
            }

            const result = await officialProfilesService.bulkImportProfilesForPrinter(
                selectedPrinterId,
                {
                    profileIds: Array.from(selectedProfileIds),
                    makePublic
                }
            );

            return result;
        },
        onSuccess: (result) => {
            setImportMessage(
                `Successfully imported ${result.imported} profile(s) for ${result.printerName}. ` +
                `${result.duplicated} were already imported.`
            );
            setImportError(null);
            setSelectedProfileIds(new Set());
            qc.invalidateQueries({ queryKey: ['slicerProfilesExtended'] });
        },
        onError: (err: Error) => {
            setImportError(err.message);
            setImportMessage(null);
        }
    });

    const toggleProfileSelection = (profileId: string) => {
        const newSet = new Set(selectedProfileIds);
        if (newSet.has(profileId)) {
            newSet.delete(profileId);
        } else {
            newSet.add(profileId);
        }
        setSelectedProfileIds(newSet);
    };

    const selectAllProfiles = () => {
        setSelectedProfileIds(new Set(officialProfiles.map((p: AvailableProfile) => p.id)));
    };

    const clearSelection = () => {
        setSelectedProfileIds(new Set());
    };

    const selectedPrinter = printers.find(p => p.id === selectedPrinterId);

    return (
        <PageTemplate
            title="Import Official Profiles"
            subtitle="Import system OrcaSlicer profiles for your registered printers"
            icon={Download}
            maxWidth="max-w-4xl"
        >
            <div className="grid md:grid-cols-4 gap-6">
                {/* Left: Printer Selection */}
                <div className="md:col-span-1">
                    <div className="card bg-pf-panel border border-pf-border rounded shadow p-4 sticky top-20">
                        <h3 className="font-semibold mb-4">Select Printer</h3>

                        <FormField label="Printer">
                            <Select
                                value={selectedPrinterId}
                                onChange={e => {
                                    setSelectedPrinterId(e.target.value);
                                    setSelectedProfileIds(new Set());
                                    setImportMessage(null);
                                    setImportError(null);
                                }}
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
                            <div className="mt-4 p-3 bg-pf-background rounded text-sm space-y-1">
                                <p className="text-pf-text-muted">Printer: <span className="font-medium text-pf-text">{selectedPrinter.name}</span></p>
                                {selectedPrinter.modelName && (
                                    <p className="text-pf-text-muted">Model: <span className="font-medium text-pf-text">{selectedPrinter.modelName}</span></p>
                                )}
                            </div>
                        )}

                        {selectedPrinterId && (
                            <>
                                <div className="mt-6 p-3 bg-pf-background rounded text-sm">
                                    <p className="text-pf-text-muted mb-2">Selected: {selectedProfileIds.size} profile(s)</p>
                                    <div className="space-y-2">
                                        <Button
                                            type="button"
                                            variant="secondary"
                                            size="sm"
                                            className="w-full"
                                            onClick={selectAllProfiles}
                                            disabled={officialProfiles.length === 0}
                                        >
                                            Select All
                                        </Button>
                                        <Button
                                            type="button"
                                            variant="secondary"
                                            size="sm"
                                            className="w-full"
                                            onClick={clearSelection}
                                        >
                                            Clear
                                        </Button>
                                    </div>
                                </div>

                                <label className="inline-flex items-center gap-2 mt-4 text-sm cursor-pointer">
                                    <input
                                        type="checkbox"
                                        checked={makePublic}
                                        onChange={e => setMakePublic(e.target.checked)}
                                    />
                                    <span>Make public</span>
                                </label>

                                <Button
                                    type="button"
                                    variant="primary"
                                    className="w-full mt-4"
                                    onClick={() => importMutation.mutate()}
                                    loading={importMutation.isPending}
                                    disabled={selectedProfileIds.size === 0}
                                >
                                    Import Selected
                                </Button>

                                {importError && <Alert type="error" className="mt-4">{importError}</Alert>}
                                {importMessage && <Alert type="success" className="mt-4">{importMessage}</Alert>}
                            </>
                        )}
                    </div>
                </div>

                {/* Right: Profile List */}
                <div className="md:col-span-3">
                    {!selectedPrinterId ? (
                        <div className="card bg-pf-panel border border-pf-border rounded shadow p-8 text-center">
                            <AlertCircle className="w-12 h-12 text-pf-text-muted mx-auto mb-4" />
                            <p className="text-pf-text-muted">Select a printer to see available profiles</p>
                        </div>
                    ) : profilesLoading ? (
                        <div className="card bg-pf-panel border border-pf-border rounded shadow p-8 text-center">
                            <p className="text-pf-text-muted">Loading profiles...</p>
                        </div>
                    ) : officialProfiles.length === 0 ? (
                        <div className="card bg-pf-panel border border-pf-border rounded shadow p-8 text-center">
                            <AlertCircle className="w-12 h-12 text-pf-text-muted mx-auto mb-4" />
                            <p className="text-pf-text-muted">No official profiles available</p>
                        </div>
                    ) : (
                        <div className="space-y-4">
                            {groupedProfiles.map(([group, profiles]) => (
                                <div key={group} className="card bg-pf-panel border border-pf-border rounded shadow">
                                    <div className="card-header bg-pf-hover p-3">
                                        <h4 className="font-semibold text-sm">{group}</h4>
                                    </div>
                                    <div className="card-body p-3">
                                        <div className="space-y-2">
                                            {profiles.map(profile => (
                                                <label
                                                    key={profile.id}
                                                    className="flex items-center gap-3 p-2 hover:bg-pf-hover rounded cursor-pointer transition-colors"
                                                >
                                                    <input
                                                        type="checkbox"
                                                        checked={selectedProfileIds.has(profile.id)}
                                                        onChange={() => toggleProfileSelection(profile.id)}
                                                        className="w-4 h-4"
                                                    />
                                                    <div className="flex-1">
                                                        <p className="text-sm font-medium">{profile.name}</p>
                                                        <p className="text-xs text-pf-text-muted">
                                                            {profile.layerHeight}mm • {profile.infillPercentage}% infill
                                                        </p>
                                                    </div>
                                                    {selectedProfileIds.has(profile.id) && (
                                                        <Check className="w-5 h-5 text-pf-success" />
                                                    )}
                                                </label>
                                            ))}
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

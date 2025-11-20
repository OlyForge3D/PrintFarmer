import React, { useState, useMemo } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import slicerProfilesService, { SlicerProfileListItem, ImportSlicerProfileRequest, SlicerProfileExtended, SlicerProfileExportDto } from '@/services/slicerProfilesService';
import { officialProfilesService } from '@/services/officialProfilesService';
import { orcaProfilesService } from '@farm/slicers-orcaslicer-v2_3_1';
import { slicerRegistry } from '@/services/slicerRegistry';
import { Settings, Download, Upload, Search, Filter } from 'lucide-react';
import { PageTemplate } from '@/components/PageTemplate';
import { Button } from '@/components/ui/Button';
import { Alert } from '@/components/ui/Alert';
import { FormField } from '@/components/ui/FormField';
import { Input } from '@/components/ui/Input';
import { Select } from '@/components/ui/Select';

export const SlicerProfilesPage: React.FC = () => {
  const qc = useQueryClient();
  const navigate = useNavigate();

  // Form state
  const [rawJson, setRawJson] = useState('');
  const [name, setName] = useState('');
  const [description, setDescription] = useState('');
  const [slicerType, setSlicerType] = useState('');
  const [allowSystemOverride, setAllowSystemOverride] = useState(false);
  const [setDefault, setSetDefault] = useState(false);
  const [isPublic, setIsPublic] = useState(true);

  // Filtering and search state
  const [searchQuery, setSearchQuery] = useState('');
  const [filterEngine, setFilterEngine] = useState<string>('all');
  const [filterQuality, setFilterQuality] = useState<string>('all');
  const [filterSource, setFilterSource] = useState<string>('all');
  const [showFilters, setShowFilters] = useState(false);

  // UI state
  const [importError, setImportError] = useState<string | null>(null);
  const [exportingId, setExportingId] = useState<string | null>(null);
  const [exportingBundle, setExportingBundle] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  const [isReseedingProfiles, setIsReseedingProfiles] = useState(false);

  // Fetch available slicers
  const { data: availableSlicers = [] } = useQuery({
    queryKey: ['slicers-available'],
    queryFn: () => slicerRegistry.getSlicers(),
    staleTime: 10_000,
    refetchInterval: 15_000,
  });

  // Extract slicer names for the dropdown
  const slicerNames = useMemo(() => {
    return availableSlicers
      .map(s => s.slicerType || s.name || '')
      .filter((v, i, arr) => v && arr.indexOf(v) === i)
      .sort();
  }, [availableSlicers]);

  // Set initial slicer type to first available
  React.useEffect(() => {
    if (!slicerType && slicerNames.length > 0) {
      setSlicerType(slicerNames[0]);
    }
  }, [slicerNames, slicerType]);

  const { data: profiles, isLoading, error } = useQuery<SlicerProfileListItem[], Error>({
    queryKey: ['slicerProfilesExtended'],
    queryFn: async () => slicerProfilesService.listExtended(),
    staleTime: 10_000
  });

  const importMutation = useMutation<SlicerProfileExtended, Error, ImportSlicerProfileRequest>({
    mutationFn: async (payload) => {
      return slicerProfilesService.importProfile(payload);
    },
    onSuccess: (res) => {
      setMessage(res ? (res.isDefault ? 'Imported and set as default.' : 'Profile imported.') : 'Imported.');
      setImportError(null);
      setRawJson('');
      setName('');
      setDescription('');
      setAllowSystemOverride(false);
      setSetDefault(false);
      qc.invalidateQueries({ queryKey: ['slicerProfilesExtended'] });
    },
    onError: (err) => {
      setImportError(err.message);
    }
  });

  const setDefaultMutation = useMutation<void, Error, string>({
    mutationFn: async (id) => slicerProfilesService.setDefault(id),
    onSuccess: () => {
      setMessage('Default profile updated.');
      qc.invalidateQueries({ queryKey: ['slicerProfilesExtended'] });
    },
    onError: (err) => setMessage(`Failed to set default: ${err.message}`)
  });

  const exportProfile = async (id: string) => {
    setExportingId(id);
    try {
      const data: SlicerProfileExportDto = await slicerProfilesService.exportProfile(id);
      const blob = new Blob([data.rawJson], { type: 'application/json' });
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = `${data.name.replace(/\s+/g, '_')}_${data.hash.substring(0, 8)}.json`;
      a.click();
      URL.revokeObjectURL(url);
      setMessage('Profile exported.');
    } catch (e) {
      setMessage(e instanceof Error ? e.message : 'Export failed');
    } finally {
      setExportingId(null);
    }
  };

  const exportOrcaBundle = async () => {
    setExportingBundle(true);
    try {
      const bundleJson = await orcaProfilesService.exportBundle({
        includeProcessProfiles: true,
        includeMetadata: true
      });

      const blob = new Blob([bundleJson], { type: 'application/json' });
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      const timestamp = new Date().toISOString().split('T')[0];
      a.download = `printfarmer_orca_bundle_${timestamp}.json`;
      a.click();
      URL.revokeObjectURL(url);
      setMessage('OrcaSlicer bundle exported successfully.');
    } catch (e) {
      setMessage(e instanceof Error ? e.message : 'Bundle export failed');
    } finally {
      setExportingBundle(false);
    }
  };

  // Filtered and searched profiles
  const filteredProfiles = useMemo(() => {
    if (!profiles) return [];

    return profiles.filter(p => {
      // Search filter
      if (searchQuery) {
        const query = searchQuery.toLowerCase();
        const matchesSearch =
          p.name.toLowerCase().includes(query) ||
          p.material?.toLowerCase().includes(query) ||
          p.slicerType.toLowerCase().includes(query);
        if (!matchesSearch) return false;
      }

      // Engine filter
      if (filterEngine !== 'all' && filterEngine !== '' && p.slicerType !== filterEngine) {
        return false;
      }

      // Quality filter
      if (filterQuality !== 'all' && filterQuality !== '' && p.quality.toLowerCase() !== filterQuality.toLowerCase()) {
        return false;
      }

      // Source filter
      if (filterSource !== 'all' && filterSource !== '') {
        if (filterSource === 'default' && !p.isDefault) return false;
        if (filterSource === 'system' && !p.isSystem) return false;
        if (filterSource === 'public' && !p.isPublic) return false;
        if (filterSource === 'imported' && p.isSystem) return false;
      }

      return true;
    });
  }, [profiles, searchQuery, filterEngine, filterQuality, filterSource]);

  const onImport = (e: React.FormEvent) => {
    e.preventDefault();
    if (!rawJson.trim()) {
      setImportError('Raw profile JSON is required');
      return;
    }
    importMutation.mutate({
      rawJson: rawJson,
      name: name || undefined,
      description: description || undefined,
      slicerType,
      allowSystemOverride,
      setDefault,
      isPublic
    });
    // Mutation handles pending state; removed local importing flag
  };

  return (
    <PageTemplate
      title="Slicer Profiles"
      subtitle="Manage imported slicer profiles (OrcaSlicer / PrusaSlicer)"
      icon={Settings}
      maxWidth="max-w-6xl"
    >
      {/* OrcaSlicer Quick Actions */}
      <div className="mb-6 bg-pf-panel rounded-lg shadow p-4">
        <h3 className="text-lg font-semibold mb-4 flex items-center gap-2">
          <Settings className="w-5 h-5" />
          OrcaSlicer Integration
        </h3>
        <div className="flex flex-wrap gap-3">
          <Button
            variant="primary"
            onClick={() => navigate('/profiles/import/orca')}
            className="flex items-center gap-2"
          >
            <Upload className="w-4 h-4" />
            Import from OrcaSlicer
          </Button>
          <Button
            variant="primary"
            onClick={() => navigate('/profiles/import/official')}
            className="flex items-center gap-2"
          >
            <Download className="w-4 h-4" />
            Import for Printers
          </Button>
          <Button
            variant="secondary"
            onClick={exportOrcaBundle}
            loading={exportingBundle}
            className="flex items-center gap-2"
          >
            <Download className="w-4 h-4" />
            Export to OrcaSlicer
          </Button>
          <Button
            variant="secondary"
            onClick={async () => {
              setIsReseedingProfiles(true);
              try {
                const result = await officialProfilesService.forceReseedSystemProfilesFromWorker();
                console.log('Force reseed result:', result);
                
                if (result.imported === 0) {
                  let details = result.message || 'No profiles available from worker';
                  if (result.orcaslicerVersion) {
                    details += ` (OrcaSlicer version: ${result.orcaslicerVersion})`;
                  }
                  setMessage(`⚠️ ${details}`);
                } else {
                  setMessage(`✅ System profiles updated: ${result.imported} profile(s) imported from OrcaSlicer worker${result.orcaslicerVersion ? ` (v${result.orcaslicerVersion})` : ''}.`);
                  qc.invalidateQueries({ queryKey: ['slicerProfilesExtended'] });
                }
              } catch (error) {
                const errorMsg = error instanceof Error ? error.message : 'Failed to reseed system profiles';
                console.error('Force reseed error:', error);
                setMessage(`❌ ${errorMsg}`);
              } finally {
                setIsReseedingProfiles(false);
              }
            }}
            loading={isReseedingProfiles}
            className="flex items-center gap-2"
          >
            <Upload className="w-4 h-4" />
            Force Reseed System Profiles
          </Button>
          <div className="flex-1" />
          <p className="text-sm text-pf-text-muted self-center">
            Import/export profiles directly from OrcaSlicer config bundles or reseed system profiles
          </p>
        </div>
      </div>

      <div className="grid md:grid-cols-3 gap-6">
        <div className="md:col-span-1 space-y-4">
          <form onSubmit={onImport} className="bg-pf-panel rounded shadow p-4 flex flex-col gap-4">
            <h3 className="text-lg font-semibold">Import Profile</h3>
            <FormField label="Raw Profile JSON" required helper="Paste raw slicer profile JSON exported from your slicer.">
              <textarea
                className="border rounded p-2 h-48 font-mono text-xs focus:outline-none focus:ring-2 focus:ring-blue-500"
                placeholder={'{\n  "layer_height": 0.2, ...\n}'}
                value={rawJson}
                onChange={(e: React.ChangeEvent<HTMLTextAreaElement>) => setRawJson(e.target.value)}
              />
            </FormField>
            <FormField label="Name" helper="Optional; derived automatically if left blank.">
              <Input
                type="text"
                placeholder="Profile name"
                value={name}
                onChange={e => setName(e.target.value)}
              />
            </FormField>
            <FormField label="Description">
              <Input
                type="text"
                placeholder="Description"
                value={description}
                onChange={e => setDescription(e.target.value)}
              />
            </FormField>
            <FormField label="Slicer Engine" required>
              <Select
                aria-label="Slicer engine"
                value={slicerType}
                onChange={e => setSlicerType(e.target.value)}
              >
                {slicerNames.map(s => <option key={s}>{s}</option>)}
              </Select>
            </FormField>
            <div className="flex flex-col gap-2 text-sm">
              <label className="inline-flex items-center gap-2">
                <input type="checkbox" checked={allowSystemOverride} onChange={e => setAllowSystemOverride(e.target.checked)} />
                <span>Allow system override</span>
              </label>
              <label className="inline-flex items-center gap-2">
                <input type="checkbox" checked={setDefault} onChange={e => setSetDefault(e.target.checked)} />
                <span>Set as default after import</span>
              </label>
              <label className="inline-flex items-center gap-2">
                <input type="checkbox" checked={isPublic} onChange={e => setIsPublic(e.target.checked)} />
                <span>Public (visible to other users)</span>
              </label>
            </div>
            {importError && <Alert type="error">{importError}</Alert>}
            <Button type="submit" loading={importMutation.isPending} variant="primary">Import Profile</Button>
          </form>
          {message && <Alert type="success">{message}</Alert>}
        </div>
        <div className="md:col-span-2">
          <div className="bg-pf-panel rounded shadow">
            {/* Header with Search and Filters */}
            <div className="p-4 border-b border-pf-border">
              <div className="flex items-center gap-4 mb-4">
                <div className="flex-1 relative">
                  <Search className="absolute left-3 top-1/2 -translate-y-1/2 w-4 h-4 text-pf-text-muted" />
                  <input
                    type="text"
                    placeholder="Search profiles by name, material, or slicer..."
                    value={searchQuery}
                    onChange={(e) => setSearchQuery(e.target.value)}
                    className="w-full pl-10 pr-4 py-2 bg-pf-background border border-pf-border rounded-lg focus:ring-2 focus:ring-pf-primary focus:border-transparent"
                  />
                </div>
                <Button
                  variant={showFilters ? 'primary' : 'secondary'}
                  onClick={() => setShowFilters(!showFilters)}
                  className="flex items-center gap-2"
                  size="sm"
                >
                  <Filter className="w-4 h-4" />
                  Filters
                </Button>
              </div>

              {/* Filter Controls */}
              {showFilters && (
                <div className="grid grid-cols-3 gap-3 p-3 bg-pf-background rounded-lg">
                  <div>
                    <label className="block text-sm font-medium mb-1">Engine</label>
                    <Select
                      value={filterEngine}
                      onChange={(e) => setFilterEngine(e.target.value)}
                      aria-label="Filter by engine"
                    >
                      <option value="all">All Engines</option>
                      {slicerNames.map(s => <option key={s} value={s}>{s}</option>)}
                    </Select>
                  </div>
                  <div>
                    <label className="block text-sm font-medium mb-1">Quality</label>
                    <Select
                      value={filterQuality}
                      onChange={(e) => setFilterQuality(e.target.value)}
                      aria-label="Filter by quality"
                    >
                      <option value="all">All Qualities</option>
                      <option value="draft">Draft</option>
                      <option value="standard">Standard</option>
                      <option value="fine">Fine</option>
                    </Select>
                  </div>
                  <div>
                    <label className="block text-sm font-medium mb-1">Source</label>
                    <Select
                      value={filterSource}
                      onChange={(e) => setFilterSource(e.target.value)}
                      aria-label="Filter by source"
                    >
                      <option value="all">All Sources</option>
                      <option value="default">Default</option>
                      <option value="system">System</option>
                      <option value="public">Public</option>
                      <option value="imported">Imported</option>
                    </Select>
                  </div>
                </div>
              )}

              {/* Active Filter Summary */}
              <div className="flex items-center justify-between mt-3">
                <div className="flex items-center gap-4">
                  <p className="text-sm text-pf-text-muted">
                    Showing {filteredProfiles.length} of {profiles?.length || 0} profiles
                  </p>
                  <Button variant="secondary" size="sm" onClick={() => qc.invalidateQueries({ queryKey: ['slicerProfilesExtended'] })}>Refresh</Button>
                </div>
                {(searchQuery || filterEngine !== 'all' || filterQuality !== 'all' || filterSource !== 'all') && (
                  <button
                    onClick={() => {
                      setSearchQuery('');
                      setFilterEngine('all');
                      setFilterQuality('all');
                      setFilterSource('all');
                    }}
                    className="text-sm text-pf-primary hover:underline"
                  >
                    Clear filters
                  </button>
                )}
              </div>
            </div>

            {/* Profiles Table */}
            <div className="p-4">
              {error && <Alert type="error">{error.message}</Alert>}
              {isLoading && <div>Loading profiles...</div>}
              {!isLoading && filteredProfiles.length === 0 && profiles && profiles.length > 0 && (
                <div className="text-pf-text-muted text-sm">No profiles match your filters.</div>
              )}
              {!isLoading && profiles && profiles.length === 0 && <div className="text-pf-text-muted text-sm">No profiles imported yet.</div>}
              {!isLoading && filteredProfiles.length > 0 && (
                <div className="overflow-x-auto">
                  <table className="min-w-full text-sm">
                    <thead>
                      <tr className="bg-pf-bg-1 text-left">
                        <th className="p-2">Name</th>
                        <th className="p-2">Engine</th>
                        <th className="p-2">Material</th>
                        <th className="p-2">Quality</th>
                        <th className="p-2">Layer</th>
                        <th className="p-2">Infill</th>
                        <th className="p-2">Flags</th>
                        <th className="p-2">Actions</th>
                      </tr>
                    </thead>
                    <tbody>
                      {filteredProfiles.map(p => (
                        <tr key={p.id} className="border-t border-pf-border hover:bg-pf-bg-1">
                          <td className="p-2 font-medium">{p.name}</td>
                          <td className="p-2">{p.slicerType}</td>
                          <td className="p-2">{p.material}</td>
                          <td className="p-2">{p.quality}</td>
                          <td className="p-2">{p.layerHeight.toFixed(2)}mm</td>
                          <td className="p-2">{p.infillPercentage}%</td>
                          <td className="p-2">
                            <div className="flex flex-col text-xs gap-1">
                              {p.isDefault && <span className="px-2 py-0.5 bg-pf-accent-bg text-pf-text-primary rounded">Default</span>}
                              {p.isSystem && <span className="px-2 py-0.5 bg-pf-bg-2 text-pf-text-primary rounded">System</span>}
                              {p.isPublic && <span className="px-2 py-0.5 bg-pf-success-bg text-pf-text-primary rounded">Public</span>}
                            </div>
                          </td>
                          <td className="p-2">
                            <div className="flex gap-2">
                              <Button
                                onClick={() => setDefaultMutation.mutate(p.id)}
                                loading={setDefaultMutation.isPending}
                                size="sm"
                                variant="primary"
                              >Set Default</Button>
                              <Button
                                onClick={() => exportProfile(p.id)}
                                loading={exportingId === p.id}
                                size="sm"
                                variant="secondary"
                              >{exportingId === p.id ? 'Exporting...' : 'Export'}</Button>
                            </div>
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}
            </div>
          </div>
        </div>
      </div>
    </PageTemplate>
  );
};

export default SlicerProfilesPage;

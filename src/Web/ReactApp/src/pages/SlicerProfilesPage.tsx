import React, { useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import slicerProfilesService, { SlicerProfileListItem, ImportSlicerProfileRequest, SlicerProfileExtended, SlicerProfileExportDto } from '@/services/slicerProfilesService';
import { Settings } from 'lucide-react';
import { PageTemplate } from '@/components/PageTemplate';
import { Button } from '@/components/ui/Button';
import { Alert } from '@/components/ui/Alert';
import { FormField } from '@/components/ui/FormField';
import { Input } from '@/components/ui/Input';
import { Select } from '@/components/ui/Select';

export const SlicerProfilesPage: React.FC = () => {
  const qc = useQueryClient();
  const [rawJson, setRawJson] = useState('');
  const [name, setName] = useState('');
  const [description, setDescription] = useState('');
  const [slicerType, setSlicerType] = useState('PrusaSlicer');
  const [allowSystemOverride, setAllowSystemOverride] = useState(false);
  const [setDefault, setSetDefault] = useState(false);
  const [isPublic, setIsPublic] = useState(true);
  const [importError, setImportError] = useState<string | null>(null);
  // importing flag no longer needed (mutation provides pending state)
  const [exportingId, setExportingId] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);

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
      a.download = `${data.name.replace(/\s+/g, '_')}_${data.hash.substring(0,8)}.json`;
      a.click();
      URL.revokeObjectURL(url);
      setMessage('Profile exported.');
    } catch (e) {
      setMessage(e instanceof Error ? e.message : 'Export failed');
    } finally {
      setExportingId(null);
    }
  };

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
      subtitle="Manage imported slicer profiles (PrusaSlicer / OrcaSlicer / others)"
      icon={Settings}
      maxWidth="max-w-6xl"
    >
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
                {['PrusaSlicer','OrcaSlicer','Cura','SuperSlicer'].map(s => <option key={s}>{s}</option>)}
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
          <div className="bg-pf-panel rounded shadow p-4">
            <div className="flex justify-between items-center mb-4">
              <h3 className="font-semibold text-lg">Profiles</h3>
              <Button variant="secondary" size="sm" onClick={() => qc.invalidateQueries({ queryKey: ['slicerProfilesExtended'] })}>Refresh</Button>
            </div>
            {error && <Alert type="error">{error.message}</Alert>}
            {isLoading && <div>Loading profiles...</div>}
            {!isLoading && profiles && profiles.length === 0 && <div className="text-pf-text-muted text-sm">No profiles imported yet.</div>}
            {!isLoading && profiles && profiles.length > 0 && (
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
                    {profiles.map(p => (
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
    </PageTemplate>
  );
};

export default SlicerProfilesPage;

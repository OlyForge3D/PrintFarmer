import React from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { slicerRegistry, SlicerDto } from '@/services/slicerRegistry';
import { SlicerConfirmModal } from '@/components/SlicerConfirmModal';
import { Trash2, Server } from 'lucide-react';
import { PageTemplate } from '@/components/PageTemplate';
import { ProtectedRoute } from '@/components/auth/ProtectedRoute';
import { toast } from 'sonner';

function SlicerRow({ s, onRequestDeregister }: { s: SlicerDto; onRequestDeregister?: (s: SlicerDto) => void }) {
  const queryClient = useQueryClient();
  const mutation = useMutation<void, Error, string>({
    mutationFn: (id: string) => slicerRegistry.deregisterSlicer(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['slicers'] });
      toast.success('Deregistered');
    },
    onError: (err) => toast.error(`Failed to deregister: ${err.message}`)
  });

  const [showDetails, setShowDetails] = React.useState(false);

  return (
    <div>
      <div className="pf-card pf-flex pf-items-center pf-justify-between pf-p-3 pf-mb-2">
        <div className="pf-flex pf-items-center pf-gap-3">
          <Server className="w-6 h-6 text-pf-text-secondary" />
          <div>
            <div className="pf-text-lg pf-font-medium">{s.name}</div>
            <div className="pf-text-sm pf-text-muted">{s.slicerType || 'unknown'} • {s.version || 'n/a'}</div>
            <div className="pf-text-sm">{s.host}</div>
          </div>
        </div>
        <div className="pf-flex pf-gap-2 pf-items-center">
          <div className="pf-text-sm pf-text-muted">{s.status || 'unknown'}</div>
          {s.uiManifestUrl && (
            <button
              className="px-2 py-1 border rounded bg-pf-panel text-sm"
              onClick={() => window.open(s.uiManifestUrl, '_blank', 'noopener')}
              aria-label={`Open UI ${s.name}`}
              title="Open UI"
            >
              Open UI
            </button>
          )}
          <button
            className="px-2 py-1 border rounded bg-pf-panel text-sm"
            onClick={() => setShowDetails(v => !v)}
            aria-expanded={showDetails}
            aria-controls={`slicer-details-${s.id}`}
          >
            {showDetails ? 'Hide' : 'Details'}
          </button>
          <button
            className="flex items-center gap-2 px-3 py-1 border rounded bg-red-600 text-white"
            onClick={() => onRequestDeregister ? onRequestDeregister(s) : mutation.mutate(s.id)}
            disabled={mutation.status === 'pending'}
            aria-label={`Deregister ${s.name}`}
          >
            <Trash2 className="w-4 h-4" />
            {mutation.status === 'pending' ? '...' : 'Deregister'}
          </button>
        </div>
      </div>

      {showDetails && (
        <div id={`slicer-details-${s.id}`} className="pf-p-3 pf-mb-3 pf-bg-panel border border-pf-border rounded">
          <div className="pf-grid pf-grid-cols-2 pf-gap-3 pf-mb-2">
            <div><strong>Last seen</strong><div className="pf-text-sm">{s.lastSeen ?? 'n/a'}</div></div>
            <div><strong>Max concurrent jobs</strong><div className="pf-text-sm">{s.maxConcurrentJobs ?? 'n/a'}</div></div>
            <div><strong>Tags</strong><div className="pf-text-sm">{(s.tags && s.tags.length > 0) ? s.tags.join(', ') : 'none'}</div></div>
            <div><strong>Host</strong><div className="pf-text-sm">{s.host ?? 'n/a'}</div></div>
          </div>
          <div>
            <strong>Capabilities</strong>
            <div className="pf-mt-2 pf-text-sm pf-overflow-auto">
              {s.capabilitiesJson ? (
                (() => {
                  try {
                    const parsed = JSON.parse(s.capabilitiesJson as unknown as string);
                    return <pre className="text-xs">{JSON.stringify(parsed, null, 2)}</pre>;
                  } catch {
                    return <pre className="text-xs">{s.capabilitiesJson}</pre>;
                  }
                })()
              ) : (
                <div className="pf-text-muted">No capabilities published</div>
              )}
            </div>
          </div>
        </div>
      )}
    </div>
  );
}

export default function SlicersAdminPage() {
  const { data, isLoading, error } = useQuery<SlicerDto[]>({ queryKey: ['slicers'], queryFn: () => slicerRegistry.getSlicers(), staleTime: 30000 });

  const [confirmOpen, setConfirmOpen] = React.useState(false);
  const [selected, setSelected] = React.useState<SlicerDto | null>(null);

  const openConfirm = (s: SlicerDto) => {
    setSelected(s);
    setConfirmOpen(true);
  };

  const handleConfirm = async () => {
    if (!selected) return;
    try {
      await slicerRegistry.deregisterSlicer(selected.id);
      toast.success('Deregistered');
    } catch (err) {
      const msg = err instanceof Error ? err.message : String(err);
      toast.error(`Failed to deregister: ${msg}`);
    } finally {
      setConfirmOpen(false);
      setSelected(null);
    }
  };

  return (
    <ProtectedRoute requiredRole="farm_admin">
      <PageTemplate title="Admin: Slicers" subtitle="Manage registered slicer services" maxWidth="max-w-3xl">
        <div className="space-y-4">
          {isLoading ? (
            <div className="pf-p-4">Loading slicers...</div>
          ) : error ? (
            <div className="pf-p-4">Failed to load slicers.</div>
          ) : (data && data.length > 0) ? (
            <div>
              {data.map((s) => (
                <div key={s.id} onDoubleClick={() => openConfirm(s)}>
                  <SlicerRow s={s} />
                </div>
              ))}
            </div>
          ) : (
            <div className="pf-text-muted">No registered slicer services found.</div>
          )}
        </div>

        <SlicerConfirmModal
          isOpen={confirmOpen}
          slicer={selected ? { id: selected.id, name: selected.name } : null}
          onConfirm={handleConfirm}
          onCancel={() => setConfirmOpen(false)}
        />
      </PageTemplate>
    </ProtectedRoute>
  );
}

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
    <div className="gap-md flex flex-col">
      <div className="card">
        <div className="card-body">
          <div className="flex items-center justify-between">
            <div className="flex items-center gap-3">
              <Server className="w-6 h-6 text-pf-text-secondary" />
              <div>
                <div className="text-lg font-medium text-pf-text-primary">{s.name}</div>
                <div className="text-sm text-pf-text-secondary">{s.slicerType || 'unknown'} • {s.version || 'n/a'}</div>
                <div className="text-sm text-pf-text-secondary">{s.host}</div>
              </div>
            </div>
            <div className="flex gap-2 items-center flex-wrap justify-end">
              <div className="text-sm text-pf-text-secondary">{s.status || 'unknown'}</div>
              {s.uiManifestUrl && (
                <button
                  className="btn-base btn-sm btn-secondary"
                  onClick={() => window.open(s.uiManifestUrl, '_blank', 'noopener')}
                  aria-label={`Open UI ${s.name}`}
                  title="Open UI"
                >
                  Open UI
                </button>
              )}
              <button
                className="btn-base btn-sm btn-secondary"
                onClick={() => setShowDetails(v => !v)}
                aria-expanded={showDetails}
                aria-controls={`slicer-details-${s.id}`}
              >
                {showDetails ? 'Hide' : 'Details'}
              </button>
              <button
                className="btn-base btn-sm btn-danger flex items-center gap-2"
                onClick={() => onRequestDeregister ? onRequestDeregister(s) : mutation.mutate(s.id)}
                disabled={mutation.status === 'pending'}
                aria-label={`Deregister ${s.name}`}
              >
                <Trash2 className="w-4 h-4" />
                {mutation.status === 'pending' ? '...' : 'Deregister'}
              </button>
            </div>
          </div>
        </div>
      </div>

      {showDetails && (
        <div id={`slicer-details-${s.id}`} className="card">
          <div className="card-body gap-md">
            <div className="grid grid-cols-2 gap-md">
              <div>
                <div className="form-label">Last seen</div>
                <div className="text-sm text-pf-text-secondary">{s.lastSeen ?? 'n/a'}</div>
              </div>
              <div>
                <div className="form-label">Max concurrent jobs</div>
                <div className="text-sm text-pf-text-secondary">{s.maxConcurrentJobs ?? 'n/a'}</div>
              </div>
              <div>
                <div className="form-label">Tags</div>
                <div className="text-sm text-pf-text-secondary">{(s.tags && s.tags.length > 0) ? s.tags.join(', ') : 'none'}</div>
              </div>
              <div>
                <div className="form-label">Host</div>
                <div className="text-sm text-pf-text-secondary">{s.host ?? 'n/a'}</div>
              </div>
            </div>
            <div>
              <div className="form-label mb-2">Capabilities</div>
              <div className="text-sm text-pf-text-secondary overflow-auto">
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
                  <div className="text-pf-text-secondary">No capabilities published</div>
                )}
              </div>
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
        <div className="gap-md flex flex-col">
          {isLoading ? (
            <div className="card">
              <div className="card-body">
                <div className="text-pf-text-secondary">Loading slicers...</div>
              </div>
            </div>
          ) : error ? (
            <div className="alert-base alert-error">
              <div className="alert-title">Error</div>
              <div>Failed to load slicers.</div>
            </div>
          ) : (data && data.length > 0) ? (
            <div className="gap-md flex flex-col">
              {data.map((s) => (
                <div key={s.id} onDoubleClick={() => openConfirm(s)}>
                  <SlicerRow s={s} />
                </div>
              ))}
            </div>
          ) : (
            <div className="card">
              <div className="card-body">
                <div className="text-pf-text-secondary">No registered slicer services found.</div>
              </div>
            </div>
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

import { useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import { Button, Input, Select, FormField, Card, Spinner, Badge } from '@/common/components/ui';
import { PlusIcon, DeleteIcon } from '@/common/components/icons/MdiIcons';
import { apiClient } from '@/services/api';
import type {
  MaterialClusterDto,
  FilamentTypeDto,
} from '@/types/api';

const CLUSTER_QUERY_KEY = ['material-clusters'];

export function MaterialClustersTab() {
  const queryClient = useQueryClient();
  const [showCreateForm, setShowCreateForm] = useState(false);
  const [newName, setNewName] = useState('');
  const [newDescription, setNewDescription] = useState('');
  const [addingToClusterId, setAddingToClusterId] = useState<string | null>(null);
  const [selectedFilamentId, setSelectedFilamentId] = useState('');

  const { data: clusters = [], isLoading, error } = useQuery({
    queryKey: CLUSTER_QUERY_KEY,
    queryFn: () => apiClient.getMaterialClusters(),
    staleTime: 30_000,
  });

  const { data: filamentTypes = [] } = useQuery({
    queryKey: ['filament-types'],
    queryFn: () => apiClient.getFilamentTypes(),
    staleTime: 300_000,
  });

  const createMutation = useMutation({
    mutationFn: () =>
      apiClient.createMaterialCluster({
        name: newName.trim(),
        description: newDescription.trim() || undefined,
      }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: CLUSTER_QUERY_KEY });
      toast.success('Cluster created');
      setNewName('');
      setNewDescription('');
      setShowCreateForm(false);
    },
    onError: (err: Error) => toast.error(`Failed to create cluster: ${err.message}`),
  });

  const deleteMutation = useMutation({
    mutationFn: (id: string) => apiClient.deleteMaterialCluster(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: CLUSTER_QUERY_KEY });
      toast.success('Cluster deleted');
    },
    onError: (err: Error) => toast.error(`Failed to delete cluster: ${err.message}`),
  });

  const addMemberMutation = useMutation({
    mutationFn: ({ clusterId, filamentTypeId }: { clusterId: string; filamentTypeId: string }) =>
      apiClient.addMaterialClusterMember(clusterId, filamentTypeId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: CLUSTER_QUERY_KEY });
      toast.success('Material added to cluster');
      setSelectedFilamentId('');
      setAddingToClusterId(null);
    },
    onError: (err: Error) => toast.error(`Failed to add member: ${err.message}`),
  });

  const removeMemberMutation = useMutation({
    mutationFn: ({ clusterId, filamentTypeId }: { clusterId: string; filamentTypeId: string }) =>
      apiClient.removeMaterialClusterMember(clusterId, filamentTypeId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: CLUSTER_QUERY_KEY });
      toast.success('Material removed from cluster');
    },
    onError: (err: Error) => toast.error(`Failed to remove member: ${err.message}`),
  });

  const handleCreate = () => {
    if (!newName.trim()) {
      toast.error('Cluster name is required');
      return;
    }
    createMutation.mutate();
  };

  const handleAddMember = (clusterId: string) => {
    if (!selectedFilamentId) {
      toast.error('Select a filament type');
      return;
    }
    addMemberMutation.mutate({ clusterId, filamentTypeId: selectedFilamentId });
  };

  // Build a set of filament type IDs already in a cluster (for filtering the dropdown)
  const getMemberIds = (cluster: MaterialClusterDto): Set<string> =>
    new Set(cluster.members.map((m) => m.filamentTypeId));

  if (isLoading) {
    return (
      <div className="flex justify-center py-12">
        <Spinner size="lg" />
      </div>
    );
  }

  if (error) {
    return (
      <div className="p-4 text-pf-error">
        Failed to load material clusters: {String(error)}
      </div>
    );
  }

  return (
    <div className="space-y-6">
      <div className="flex items-center justify-between">
        <div>
          <p className="text-sm text-pf-text-secondary">
            Group equivalent materials from different vendors so auto-dispatch can pick the right spool
            when an exact match is unavailable.
          </p>
        </div>
        <Button
          variant="primary"
          size="sm"
          iconLeft={<PlusIcon className="h-4 w-4" />}
          onClick={() => setShowCreateForm(true)}
        >
          New Cluster
        </Button>
      </div>

      {showCreateForm && (
        <Card>
          <Card.Body className="space-y-3">
            <FormField label="Cluster Name" htmlFor="cluster-name" required>
              <Input
                id="cluster-name"
                value={newName}
                onChange={(e) => setNewName(e.target.value)}
                placeholder='e.g. "PLA+ Equivalents"'
              />
            </FormField>
            <FormField label="Description" htmlFor="cluster-desc">
              <Input
                id="cluster-desc"
                value={newDescription}
                onChange={(e) => setNewDescription(e.target.value)}
                placeholder="Optional description"
              />
            </FormField>
            <div className="flex gap-2">
              <Button
                variant="primary"
                size="sm"
                loading={createMutation.isPending}
                onClick={handleCreate}
              >
                Create
              </Button>
              <Button
                variant="secondary"
                size="sm"
                onClick={() => {
                  setShowCreateForm(false);
                  setNewName('');
                  setNewDescription('');
                }}
              >
                Cancel
              </Button>
            </div>
          </Card.Body>
        </Card>
      )}

      {clusters.length === 0 && !showCreateForm && (
        <div className="text-center py-12 text-pf-text-secondary">
          <p className="text-lg font-medium">No material clusters yet</p>
          <p className="mt-1 text-sm">
            Create a cluster to group equivalent filament types together.
          </p>
        </div>
      )}

      <div className="space-y-4">
        {clusters.map((cluster: MaterialClusterDto) => {
          const memberIds = getMemberIds(cluster);
          const availableFilaments = filamentTypes.filter(
            (ft: FilamentTypeDto) => !memberIds.has(ft.id)
          );

          return (
            <Card key={cluster.id}>
              <Card.Header className="flex items-center justify-between">
                <div>
                  <h3 className="text-base font-semibold text-pf-text-primary">
                    {cluster.name}
                  </h3>
                  {cluster.description && (
                    <p className="text-sm text-pf-text-secondary mt-0.5">
                      {cluster.description}
                    </p>
                  )}
                </div>
                <div className="flex items-center gap-2">
                  <Badge variant="default" size="sm">
                    {cluster.members.length} material{cluster.members.length !== 1 ? 's' : ''}
                  </Badge>
                  <Button
                    variant="danger"
                    size="sm"
                    onClick={() => deleteMutation.mutate(cluster.id)}
                    loading={deleteMutation.isPending}
                    iconLeft={<DeleteIcon className="h-4 w-4" />}
                  >
                    Delete
                  </Button>
                </div>
              </Card.Header>
              <Card.Body>
                {cluster.members.length > 0 ? (
                  <div className="flex flex-wrap gap-2 mb-3">
                    {cluster.members.map((member) => (
                      <span
                        key={member.filamentTypeId}
                        className="inline-flex items-center gap-1.5 rounded-full bg-pf-accent-bg px-3 py-1 text-sm text-pf-text-primary"
                      >
                        {member.filamentTypeName}
                        <Button
                          variant="unstyled"
                          size="sm"
                          className="ml-1 text-pf-text-secondary hover:text-pf-error transition-colors p-0 leading-none"
                          aria-label={`Remove ${member.filamentTypeName}`}
                          onClick={() =>
                            removeMemberMutation.mutate({
                              clusterId: cluster.id,
                              filamentTypeId: member.filamentTypeId,
                            })
                          }
                        >
                          ×
                        </Button>
                      </span>
                    ))}
                  </div>
                ) : (
                  <p className="text-sm text-pf-text-secondary mb-3">
                    No materials in this cluster yet. Add filament types below.
                  </p>
                )}

                {addingToClusterId === cluster.id ? (
                  <div className="flex items-end gap-2">
                    <FormField label="Add Material" htmlFor={`add-member-${cluster.id}`}>
                      <Select
                        id={`add-member-${cluster.id}`}
                        value={selectedFilamentId}
                        onChange={(e) => setSelectedFilamentId(e.target.value)}
                      >
                        <option value="">Select a filament type…</option>
                        {availableFilaments.map((ft: FilamentTypeDto) => (
                          <option key={ft.id} value={ft.id}>
                            {ft.name}
                          </option>
                        ))}
                      </Select>
                    </FormField>
                    <Button
                      variant="primary"
                      size="sm"
                      loading={addMemberMutation.isPending}
                      onClick={() => handleAddMember(cluster.id)}
                    >
                      Add
                    </Button>
                    <Button
                      variant="secondary"
                      size="sm"
                      onClick={() => {
                        setAddingToClusterId(null);
                        setSelectedFilamentId('');
                      }}
                    >
                      Cancel
                    </Button>
                  </div>
                ) : (
                  <Button
                    variant="subtle"
                    size="sm"
                    iconLeft={<PlusIcon className="h-4 w-4" />}
                    onClick={() => setAddingToClusterId(cluster.id)}
                  >
                    Add Material
                  </Button>
                )}
              </Card.Body>
            </Card>
          );
        })}
      </div>
    </div>
  );
}

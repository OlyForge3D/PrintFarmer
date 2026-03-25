import { useState } from 'react';
import { toast } from 'sonner';
import {
  Button,
  Input,
  FormField,
  Card,
  Badge,
  Spinner,
  Alert,
} from '@/common/components/ui';
import {
  PlusIcon,
  DeleteIcon,
  CheckIcon,
  CloseIcon,
  TestIcon,
  EditIcon,
} from '@/common/components/icons/MdiIcons';
import { Modal } from '@/common/components/modals/Modal';
import {
  useObicoServers,
  useCreateObicoServer,
  useUpdateObicoServer,
  useDeleteObicoServer,
  useTestObicoServerHealth,
} from '@/common/hooks/useApi';
import type { ObicoServer, CreateObicoServerRequest, UpdateObicoServerRequest } from '@/types/api';

interface ServerFormData {
  name: string;
  url: string;
  maxConcurrentAnalyses: string;
}

const DEFAULT_FORM_DATA: ServerFormData = {
  name: '',
  url: '',
  maxConcurrentAnalyses: '5',
};

export function ObicoServersSection() {
  const { data: servers = [], isLoading } = useObicoServers();
  const createMutation = useCreateObicoServer();
  const updateMutation = useUpdateObicoServer();
  const deleteMutation = useDeleteObicoServer();
  const testHealthMutation = useTestObicoServerHealth();

  const [isAddModalOpen, setIsAddModalOpen] = useState(false);
  const [isEditModalOpen, setIsEditModalOpen] = useState(false);
  const [isDeleteModalOpen, setIsDeleteModalOpen] = useState(false);
  const [selectedServer, setSelectedServer] = useState<ObicoServer | null>(null);
  const [formData, setFormData] = useState<ServerFormData>(DEFAULT_FORM_DATA);
  const [testingServerId, setTestingServerId] = useState<string | null>(null);
  const [healthResults, setHealthResults] = useState<Record<string, { healthy: boolean; latencyMs: number; message?: string }>>({});

  const handleOpenAddModal = () => {
    setFormData(DEFAULT_FORM_DATA);
    setIsAddModalOpen(true);
  };

  const handleOpenEditModal = (server: ObicoServer) => {
    setSelectedServer(server);
    setFormData({
      name: server.name,
      url: server.url,
      maxConcurrentAnalyses: String(server.maxConcurrentAnalyses),
    });
    setIsEditModalOpen(true);
  };

  const handleOpenDeleteModal = (server: ObicoServer) => {
    setSelectedServer(server);
    setIsDeleteModalOpen(true);
  };

  const handleCloseAddModal = () => {
    setIsAddModalOpen(false);
    setFormData(DEFAULT_FORM_DATA);
  };

  const handleCloseEditModal = () => {
    setIsEditModalOpen(false);
    setSelectedServer(null);
    setFormData(DEFAULT_FORM_DATA);
  };

  const handleCloseDeleteModal = () => {
    setIsDeleteModalOpen(false);
    setSelectedServer(null);
  };

  const handleCreate = async () => {
    if (!formData.name.trim() || !formData.url.trim()) {
      toast.error('Name and URL are required');
      return;
    }

    const request: CreateObicoServerRequest = {
      name: formData.name.trim(),
      url: formData.url.trim(),
      maxConcurrentAnalyses: parseInt(formData.maxConcurrentAnalyses, 10) || 5,
    };

    const created = await createMutation.mutateAsync(request);
    handleCloseAddModal();

    // Auto-verify connectivity after creation
    try {
      const result = await testHealthMutation.mutateAsync(created.id);
      setHealthResults(prev => ({ ...prev, [created.id]: result }));
      if (result.healthy) {
        toast.success(`Server created and verified (${result.latencyMs}ms)`);
      } else {
        toast.warning(`Server created but unreachable: ${result.message || 'Connection failed'}. Check the URL and ensure the Obico ML API is running.`);
      }
    } catch {
      toast.warning('Server created but connectivity check failed. Use the Test button to verify later.');
    }
  };

  const handleUpdate = async () => {
    if (!selectedServer) return;
    if (!formData.name.trim() || !formData.url.trim()) {
      toast.error('Name and URL are required');
      return;
    }

    const request: UpdateObicoServerRequest = {
      name: formData.name.trim(),
      url: formData.url.trim(),
      maxConcurrentAnalyses: parseInt(formData.maxConcurrentAnalyses, 10) || 5,
    };

    await updateMutation.mutateAsync({ id: selectedServer.id, data: request });
    handleCloseEditModal();

    // Auto-verify connectivity if URL changed
    if (formData.url.trim() !== selectedServer.url) {
      try {
        const result = await testHealthMutation.mutateAsync(selectedServer.id);
        setHealthResults(prev => ({ ...prev, [selectedServer.id]: result }));
        if (result.healthy) {
          toast.success(`Server updated and verified (${result.latencyMs}ms)`);
        } else {
          toast.warning(`Server updated but unreachable at new URL: ${result.message || 'Connection failed'}`);
        }
      } catch {
        toast.warning('Server updated but connectivity check failed. Use the Test button to verify.');
      }
    }
  };

  const handleDelete = async () => {
    if (!selectedServer) return;
    await deleteMutation.mutateAsync(selectedServer.id);
    handleCloseDeleteModal();
  };

  const handleTestHealth = async (serverId: string) => {
    setTestingServerId(serverId);
    try {
      const result = await testHealthMutation.mutateAsync(serverId);
      setHealthResults(prev => ({ ...prev, [serverId]: result }));
      if (result.healthy) {
        toast.success(`Server is healthy (${result.latencyMs}ms)`);
      } else {
        toast.error(`Server is unhealthy: ${result.message || 'Unknown error'}`);
      }
    } finally {
      setTestingServerId(null);
    }
  };

  const handleToggleEnabled = async (server: ObicoServer) => {
    const enabling = !server.isEnabled;
    await updateMutation.mutateAsync({
      id: server.id,
      data: { isEnabled: enabling },
    });

    // Auto-verify when enabling a server
    if (enabling) {
      try {
        const result = await testHealthMutation.mutateAsync(server.id);
        setHealthResults(prev => ({ ...prev, [server.id]: result }));
        if (!result.healthy) {
          toast.warning(`Server enabled but unreachable: ${result.message || 'Connection failed'}. Print failure detection will not work until the server is reachable.`);
        }
      } catch {
        toast.warning('Server enabled but connectivity check failed.');
      }
    }
  };

  // Identify misconfigured servers: enabled but known-unhealthy
  const misconfiguredServers = servers.filter(
    s => s.isEnabled && healthResults[s.id] && !healthResults[s.id].healthy,
  );

  if (isLoading) {
    return (
      <div className="flex items-center justify-center py-12">
        <Spinner size="lg" />
      </div>
    );
  }

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between">
        <div>
          <h3 className="text-lg font-semibold text-pf-text-primary">Registered Obico ML Servers</h3>
          <p className="text-sm text-pf-text-secondary">
            Add pooled Obico ML servers here. Printers can use one of these servers or fall back to the global Obico Failure Detection settings above.
          </p>
        </div>
        <Button variant="primary" onClick={handleOpenAddModal} iconLeft={<PlusIcon />}>
          Add Server
        </Button>
      </div>

      {misconfiguredServers.length > 0 && (
        <Alert variant="warning" title="Obico server misconfiguration detected">
          {misconfiguredServers.length === 1
            ? `"${misconfiguredServers[0].name}" is enabled but unreachable. Print failure detection will not work for printers assigned to this server.`
            : `${misconfiguredServers.length} servers are enabled but unreachable: ${misconfiguredServers.map(s => s.name).join(', ')}. Print failure detection will not work for printers assigned to these servers.`}
        </Alert>
      )}

      {servers.length === 0 ? (
        <Alert variant="info" title="No servers configured">
          Add an Obico ML server to enable AI-powered print failure detection for your printers.
        </Alert>
      ) : (
        <div className="grid gap-3">
          {servers.map(server => {
            const healthResult = healthResults[server.id];
            return (
              <Card key={server.id}>
                <Card.Body className="flex items-center justify-between gap-4">
                  <div className="flex-1 min-w-0">
                    <div className="flex items-center gap-2 mb-1">
                      <h4 className="text-base font-semibold text-pf-text-primary truncate">
                        {server.name}
                      </h4>
                      <Badge
                        variant={server.isEnabled ? 'success' : 'default'}
                        size="sm"
                      >
                        {server.isEnabled ? 'Enabled' : 'Disabled'}
                      </Badge>
                      {healthResult && (
                        <Badge
                          variant={healthResult.healthy ? 'success' : 'error'}
                          size="sm"
                        >
                          {healthResult.healthy ? (
                            <><CheckIcon className="w-3 h-3" /> {healthResult.latencyMs}ms</>
                          ) : (
                            <><CloseIcon className="w-3 h-3" /> Unhealthy</>
                          )}
                        </Badge>
                      )}
                    </div>
                    <p className="text-sm text-pf-text-secondary truncate">{server.url}</p>
                    <p className="text-xs text-pf-text-tertiary mt-1">
                      Max concurrent analyses: {server.maxConcurrentAnalyses}
                    </p>
                  </div>
                  <div className="flex items-center gap-2">
                    <Button
                      variant="ghost"
                      size="sm"
                      onClick={() => handleTestHealth(server.id)}
                      loading={testingServerId === server.id}
                      iconLeft={<TestIcon />}
                      title="Test connection"
                    >
                      Test
                    </Button>
                    <Button
                      variant={server.isEnabled ? 'subtle' : 'success'}
                      size="sm"
                      onClick={() => handleToggleEnabled(server)}
                      disabled={updateMutation.isPending}
                    >
                      {server.isEnabled ? 'Disable' : 'Enable'}
                    </Button>
                    <Button
                      variant="ghost"
                      size="sm"
                      onClick={() => handleOpenEditModal(server)}
                      iconLeft={<EditIcon />}
                      title="Edit server"
                    >
                      Edit
                    </Button>
                    <Button
                      variant="danger"
                      size="sm"
                      onClick={() => handleOpenDeleteModal(server)}
                      iconLeft={<DeleteIcon />}
                      title="Delete server"
                    >
                      Delete
                    </Button>
                  </div>
                </Card.Body>
              </Card>
            );
          })}
        </div>
      )}

      {/* Add Server Modal */}
      <Modal
        isOpen={isAddModalOpen}
        onClose={handleCloseAddModal}
        title="Add Obico ML Server"
        size="md"
        footer={
          <>
            <Button variant="ghost" onClick={handleCloseAddModal}>
              Cancel
            </Button>
            <Button
              variant="primary"
              onClick={handleCreate}
              loading={createMutation.isPending}
            >
              Create
            </Button>
          </>
        }
      >
        <div className="space-y-4">
          <FormField label="Server Name" htmlFor="server-name" required>
            <Input
              id="server-name"
              value={formData.name}
              onChange={e => setFormData(prev => ({ ...prev, name: e.target.value }))}
              placeholder="e.g., Primary Obico Server"
            />
          </FormField>

          <FormField label="Server URL" htmlFor="server-url" required>
            <Input
              id="server-url"
              type="url"
              value={formData.url}
              onChange={e => setFormData(prev => ({ ...prev, url: e.target.value }))}
              placeholder="https://obico.example.com"
            />
          </FormField>

          <FormField
            label="Max Concurrent Analyses"
            htmlFor="max-concurrent"
            helper="Maximum number of printers this server can analyze simultaneously"
          >
            <Input
              id="max-concurrent"
              type="number"
              min="1"
              max="50"
              value={formData.maxConcurrentAnalyses}
              onChange={e => setFormData(prev => ({ ...prev, maxConcurrentAnalyses: e.target.value }))}
            />
          </FormField>
        </div>
      </Modal>

      {/* Edit Server Modal */}
      <Modal
        isOpen={isEditModalOpen}
        onClose={handleCloseEditModal}
        title={`Edit ${selectedServer?.name}`}
        size="md"
        footer={
          <>
            <Button variant="ghost" onClick={handleCloseEditModal}>
              Cancel
            </Button>
            <Button
              variant="primary"
              onClick={handleUpdate}
              loading={updateMutation.isPending}
            >
              Save Changes
            </Button>
          </>
        }
      >
        <div className="space-y-4">
          <FormField label="Server Name" htmlFor="edit-server-name" required>
            <Input
              id="edit-server-name"
              value={formData.name}
              onChange={e => setFormData(prev => ({ ...prev, name: e.target.value }))}
              placeholder="e.g., Primary Obico Server"
            />
          </FormField>

          <FormField label="Server URL" htmlFor="edit-server-url" required>
            <Input
              id="edit-server-url"
              type="url"
              value={formData.url}
              onChange={e => setFormData(prev => ({ ...prev, url: e.target.value }))}
              placeholder="https://obico.example.com"
            />
          </FormField>

          <FormField
            label="Max Concurrent Analyses"
            htmlFor="edit-max-concurrent"
            helper="Maximum number of printers this server can analyze simultaneously"
          >
            <Input
              id="edit-max-concurrent"
              type="number"
              min="1"
              max="50"
              value={formData.maxConcurrentAnalyses}
              onChange={e => setFormData(prev => ({ ...prev, maxConcurrentAnalyses: e.target.value }))}
            />
          </FormField>
        </div>
      </Modal>

      {/* Delete Confirmation Modal */}
      <Modal
        isOpen={isDeleteModalOpen}
        onClose={handleCloseDeleteModal}
        title="Delete Obico Server"
        size="sm"
        footer={
          <>
            <Button variant="ghost" onClick={handleCloseDeleteModal}>
              Cancel
            </Button>
            <Button
              variant="danger"
              onClick={handleDelete}
              loading={deleteMutation.isPending}
            >
              Delete
            </Button>
          </>
        }
      >
        <p className="text-pf-text-secondary">
          Are you sure you want to delete <strong>{selectedServer?.name}</strong>?
          {servers.length > 0 && (
            <span className="block mt-2 text-pf-warning">
              Warning: Printers assigned to this server will fall back to the default configuration.
            </span>
          )}
        </p>
      </Modal>
    </div>
  );
}

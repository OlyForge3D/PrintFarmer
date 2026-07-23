import React, { useState } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { DeleteIcon, EditIcon, PlusIcon } from '@/common/components/icons/MdiIcons';
import { Modal } from '@/common/components/modals/Modal';
import { PageTemplate } from '@/common/components/PageTemplate';
import { Button, Input, FormField } from '@/common/components/ui';
import { apiClient } from '@/services/api';
import { useBedTypes, queryKeys } from '@/common/hooks/useApi';
import { toast } from 'sonner';
import type { BedType, CreateBedTypeRequest, UpdateBedTypeRequest } from '@/types/api';

export function BedTypeAdminPage() {
  const queryClient = useQueryClient();
  const { data: bedTypes = [], isLoading } = useBedTypes();

  const [showModal, setShowModal] = useState(false);
  const [editingType, setEditingType] = useState<BedType | null>(null);
  const [name, setName] = useState('');
  const [description, setDescription] = useState('');
  const [color, setColor] = useState('#6366f1');

  const openCreate = () => {
    setEditingType(null);
    setName('');
    setDescription('');
    setColor('#6366f1');
    setShowModal(true);
  };

  const openEdit = (bt: BedType) => {
    setEditingType(bt);
    setName(bt.name);
    setDescription(bt.description ?? '');
    setColor(bt.color ?? '#6366f1');
    setShowModal(true);
  };

  const createMutation = useMutation({
    mutationFn: (dto: CreateBedTypeRequest) => apiClient.createBedType(dto),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: queryKeys.bedTypes });
      toast.success('Bed type created');
      setShowModal(false);
    },
    onError: (err: Error) => toast.error(`Failed to create: ${err.message}`),
  });

  const updateMutation = useMutation({
    mutationFn: ({ id, dto }: { id: string; dto: UpdateBedTypeRequest }) =>
      apiClient.updateBedType(id, dto),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: queryKeys.bedTypes });
      toast.success('Bed type updated');
      setShowModal(false);
    },
    onError: (err: Error) => toast.error(`Failed to update: ${err.message}`),
  });

  const deleteMutation = useMutation({
    mutationFn: (id: string) => apiClient.deleteBedType(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: queryKeys.bedTypes });
      toast.success('Bed type deleted');
    },
    onError: (err: Error) => toast.error(`Failed to delete: ${err.message}`),
  });

  const handleSave = () => {
    const trimmed = name.trim();
    if (!trimmed) {
      toast.error('Name is required');
      return;
    }
    const dto = { name: trimmed, description: description.trim() || undefined, color };
    if (editingType) {
      updateMutation.mutate({ id: editingType.id, dto });
    } else {
      createMutation.mutate(dto);
    }
  };

  const isSaving = createMutation.isPending || updateMutation.isPending;

  return (
    <PageTemplate
      title="Bed Types"
      subtitle="Manage printer bed surface types"
      actions={
        <Button variant="primary" onClick={openCreate} iconLeft={<PlusIcon className="h-4 w-4" />}>
          Add Bed Type
        </Button>
      }
    >
      {isLoading ? (
        <div className="text-pf-text-secondary p-8 text-center">Loading…</div>
      ) : bedTypes.length === 0 ? (
        <div className="text-pf-text-secondary p-8 text-center">
          No bed types configured. Click &quot;Add Bed Type&quot; to create one.
        </div>
      ) : (
        <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
          {bedTypes.map(bt => (
            <div
              key={bt.id}
              className="rounded-lg border border-pf-border bg-pf-card p-4 flex items-start gap-3"
            >
              <div
                className="w-4 h-4 rounded-full mt-1 shrink-0"
                style={{ backgroundColor: bt.color ?? '#6366f1' }}
              />
              <div className="flex-1 min-w-0">
                <div className="font-medium text-pf-text-primary truncate">{bt.name}</div>
                {bt.description && (
                  <div className="text-xs text-pf-text-secondary mt-0.5 truncate">{bt.description}</div>
                )}
                {bt.isSystem && (
                  <span className="text-xs text-pf-text-secondary italic">System default</span>
                )}
              </div>
              <div className="flex gap-1 shrink-0">
                <Button
                  variant="ghost"
                  size="sm"
                  onClick={() => openEdit(bt)}
                  title="Edit"
                  aria-label={`Edit ${bt.name}`}
                  iconCenter={<EditIcon className="h-4 w-4" />}
                />
                <Button
                  variant="ghost"
                  size="sm"
                  onClick={() => {
                    if (confirm(`Delete "${bt.name}"?`)) {
                      deleteMutation.mutate(bt.id);
                    }
                  }}
                  title="Delete"
                  aria-label={`Delete ${bt.name}`}
                  className="text-pf-error"
                  iconCenter={<DeleteIcon className="h-4 w-4" />}
                />
              </div>
            </div>
          ))}
        </div>
      )}

      <Modal
        isOpen={showModal}
        onClose={() => setShowModal(false)}
        title={editingType ? 'Edit Bed Type' : 'New Bed Type'}
        size="sm"
        footer={
          <>
            <Button variant="secondary" onClick={() => setShowModal(false)}>Cancel</Button>
            <Button variant="primary" onClick={handleSave} loading={isSaving}>
              {editingType ? 'Save' : 'Create'}
            </Button>
          </>
        }
      >
        <div className="flex flex-col gap-4">
          <FormField label="Name" htmlFor="bt-name" required>
            <Input
              id="bt-name"
              value={name}
              onChange={e => setName(e.target.value)}
              placeholder="e.g. PEI Smooth"
            />
          </FormField>
          <FormField label="Description" htmlFor="bt-desc">
            <Input
              id="bt-desc"
              value={description}
              onChange={e => setDescription(e.target.value)}
              placeholder="Optional description"
            />
          </FormField>
          <FormField label="Badge Color" htmlFor="bt-color">
            <div className="flex items-center gap-3">
              <input
                id="bt-color"
                type="color"
                value={color}
                onChange={e => setColor(e.target.value)}
                className="w-10 h-10 rounded cursor-pointer border border-pf-border"
              />
              <Input
                value={color}
                onChange={e => setColor(e.target.value)}
                className="w-28 font-mono text-sm"
                placeholder="#hex"
              />
            </div>
          </FormField>
        </div>
      </Modal>
    </PageTemplate>
  );
}

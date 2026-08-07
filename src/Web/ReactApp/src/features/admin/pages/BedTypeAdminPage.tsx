import React, { useState } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { DeleteIcon, EditIcon, PlusIcon } from '@/common/components/icons/MdiIcons';
import { Modal } from '@/common/components/modals/Modal';
import { PageTemplate } from '@/common/components/PageTemplate';
import type { EmbeddablePageProps } from '@/common/components/EmbeddablePageProps';
import { Button, Input, FormField } from '@/common/components/ui';
import { apiClient } from '@/services/api';
import { useBedTypes, queryKeys } from '@/common/hooks/useApi';
import {
  AdminEmpty,
  AdminError,
  AdminLoading,
  AdminSaveBar,
  adminToast,
  useDirtyState,
} from '@/common/components/admin';
import type { BedType, CreateBedTypeRequest, UpdateBedTypeRequest } from '@/types/api';

const DEFAULT_FORM = {
  name: '',
  description: '',
  color: '#6366f1',
};

export function BedTypeAdminPage({ embedded = false }: EmbeddablePageProps) {
  const queryClient = useQueryClient();
  const { data: bedTypes = [], isLoading, error, refetch } = useBedTypes();

  const [showModal, setShowModal] = useState(false);
  const [editingType, setEditingType] = useState<BedType | null>(null);
  const form = useDirtyState(DEFAULT_FORM);

  const openCreate = () => {
    setEditingType(null);
    form.markPristine(DEFAULT_FORM);
    setShowModal(true);
  };

  const openEdit = (bt: BedType) => {
    setEditingType(bt);
    form.markPristine({
      name: bt.name,
      description: bt.description ?? '',
      color: bt.color ?? '#6366f1',
    });
    setShowModal(true);
  };

  const createMutation = useMutation({
    mutationFn: (dto: CreateBedTypeRequest) => apiClient.createBedType(dto),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: queryKeys.bedTypes });
      form.markPristine(form.values);
      adminToast.success('Bed type created');
      setShowModal(false);
    },
    onError: (err: Error) => adminToast.error(`Failed to create: ${err.message}`),
  });

  const updateMutation = useMutation({
    mutationFn: ({ id, dto }: { id: string; dto: UpdateBedTypeRequest }) =>
      apiClient.updateBedType(id, dto),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: queryKeys.bedTypes });
      form.markPristine(form.values);
      adminToast.success('Bed type updated');
      setShowModal(false);
    },
    onError: (err: Error) => adminToast.error(`Failed to update: ${err.message}`),
  });

  const deleteMutation = useMutation({
    mutationFn: (id: string) => apiClient.deleteBedType(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: queryKeys.bedTypes });
      adminToast.success('Bed type deleted');
    },
    onError: (err: Error) => adminToast.error(`Failed to delete: ${err.message}`),
  });

  const handleSave = () => {
    const trimmed = form.values.name.trim();
    if (!trimmed) {
      adminToast.error('Name is required');
      return;
    }
    const dto = {
      name: trimmed,
      description: form.values.description.trim() || undefined,
      color: form.values.color,
    };
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
      embedded={embedded}
    >
      {isLoading ? (
        <AdminLoading variant="card-grid" label="Loading bed types" rows={3} />
      ) : error ? (
        <AdminError
          title="Couldn't load bed types"
          description="Try loading the bed type list again."
          error={error}
          onRetry={() => void refetch()}
        />
      ) : bedTypes.length === 0 ? (
        <AdminEmpty
          title="No bed types configured"
          description="Create a bed type to make it available for printer configuration."
          action={<Button variant="primary" onClick={openCreate}>Add Bed Type</Button>}
        />
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
                  className="text-pf-error-text"
                  iconCenter={<DeleteIcon className="h-4 w-4" />}
                />
              </div>
            </div>
          ))}
        </div>
      )}

      <Modal
        isOpen={showModal}
        onClose={() => {
          form.reset();
          setShowModal(false);
        }}
        title={editingType ? 'Edit Bed Type' : 'New Bed Type'}
        size="sm"
        footer={(
          <AdminSaveBar
            isDirty={form.isDirty}
            changeCount={form.changedCount}
            changedLabels={form.changedKeys.map(key => ({
              name: 'Name',
              description: 'Description',
              color: 'Badge color',
            })[key])}
            onDiscard={() => {
              form.reset();
              setShowModal(false);
            }}
            onSave={handleSave}
            isSaving={isSaving}
            saveLabel={editingType ? 'Save' : 'Create'}
            discardLabel="Cancel"
            className="-mx-6 -my-4"
          />
        )}
      >
        <div className="flex flex-col gap-4">
          <FormField label="Name" htmlFor="bt-name" required>
            <Input
              id="bt-name"
              value={form.values.name}
              onChange={e => form.setValue('name', e.target.value)}
              placeholder="e.g. PEI Smooth"
            />
          </FormField>
          <FormField label="Description" htmlFor="bt-desc">
            <Input
              id="bt-desc"
              value={form.values.description}
              onChange={e => form.setValue('description', e.target.value)}
              placeholder="Optional description"
            />
          </FormField>
          <FormField label="Badge Color" htmlFor="bt-color">
            <div className="flex items-center gap-3">
              <input
                id="bt-color"
                type="color"
                value={form.values.color}
                onChange={e => form.setValue('color', e.target.value)}
                className="w-10 h-10 rounded cursor-pointer border border-pf-border"
              />
              <Input
                value={form.values.color}
                onChange={e => form.setValue('color', e.target.value)}
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

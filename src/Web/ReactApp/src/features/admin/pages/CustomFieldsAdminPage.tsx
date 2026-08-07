import React, { useState } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { DeleteIcon, EditIcon, PlusIcon } from '@/common/components/icons/MdiIcons';
import { Modal } from '@/common/components/modals/Modal';
import { PageTemplate } from '@/common/components/PageTemplate';
import type { EmbeddablePageProps } from '@/common/components/EmbeddablePageProps';
import { Button, Input, Select, FormField, Textarea, Toggle } from '@/common/components/ui';
import { apiClient } from '@/services/api';
import { useCustomFieldDefinitions, queryKeys } from '@/common/hooks/useApi';
import {
  AdminEmpty,
  AdminError,
  AdminLoading,
  AdminSaveBar,
  adminToast,
  useDirtyState,
} from '@/common/components/admin';
import type {
  CustomFieldDefinition,
  CustomFieldEntityType,
  CustomFieldType,
  CreateCustomFieldDefinitionRequest,
  UpdateCustomFieldDefinitionRequest,
} from '@/types/api';

const FIELD_TYPES: { value: CustomFieldType; label: string }[] = [
  { value: 'Text', label: 'Text' },
  { value: 'Number', label: 'Number' },
  { value: 'Boolean', label: 'Boolean' },
  { value: 'Date', label: 'Date' },
  { value: 'Select', label: 'Select (dropdown)' },
];

const DEFAULT_FORM = {
  fieldName: '',
  fieldKey: '',
  fieldType: 'Text' as CustomFieldType,
  options: '',
  isRequired: false,
  sortOrder: 0,
  description: '',
  defaultValue: '',
};

function toKebabCase(s: string): string {
  return s
    .trim()
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-|-$/g, '');
}

export function CustomFieldsAdminPage({ embedded = false }: EmbeddablePageProps) {
  const queryClient = useQueryClient();
  const [activeTab, setActiveTab] = useState<CustomFieldEntityType>('Printer');

  const { data: definitions = [], isLoading, error, refetch } = useCustomFieldDefinitions(activeTab);

  const [showModal, setShowModal] = useState(false);
  const [editing, setEditing] = useState<CustomFieldDefinition | null>(null);
  const form = useDirtyState(DEFAULT_FORM);
  const [autoKey, setAutoKey] = useState(true);

  const resetForm = () => {
    form.markPristine(DEFAULT_FORM);
    setAutoKey(true);
  };

  const openCreate = () => {
    setEditing(null);
    resetForm();
    setShowModal(true);
  };

  const openEdit = (def: CustomFieldDefinition) => {
    setEditing(def);
    form.markPristine({
      fieldName: def.fieldName,
      fieldKey: def.fieldKey,
      fieldType: def.fieldType,
      options: def.options ? parseOptionsToLines(def.options) : '',
      isRequired: def.isRequired,
      sortOrder: def.sortOrder,
      description: def.description ?? '',
      defaultValue: def.defaultValue ?? '',
    });
    setAutoKey(false);
    setShowModal(true);
  };

  const handleFieldNameChange = (name: string) => {
    form.setValue('fieldName', name);
    if (autoKey && !editing) {
      form.setValue('fieldKey', toKebabCase(name));
    }
  };

  const createMutation = useMutation({
    mutationFn: (dto: CreateCustomFieldDefinitionRequest) => apiClient.createCustomFieldDefinition(dto),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: queryKeys.customFieldDefinitions(activeTab) });
      form.markPristine(form.values);
      adminToast.success('Custom field created');
      setShowModal(false);
    },
    onError: (err: Error) => adminToast.error(`Failed to create: ${err.message}`),
  });

  const updateMutation = useMutation({
    mutationFn: ({ id, dto }: { id: string; dto: UpdateCustomFieldDefinitionRequest }) =>
      apiClient.updateCustomFieldDefinition(id, dto),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: queryKeys.customFieldDefinitions(activeTab) });
      form.markPristine(form.values);
      adminToast.success('Custom field updated');
      setShowModal(false);
    },
    onError: (err: Error) => adminToast.error(`Failed to update: ${err.message}`),
  });

  const deleteMutation = useMutation({
    mutationFn: (id: string) => apiClient.deleteCustomFieldDefinition(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: queryKeys.customFieldDefinitions(activeTab) });
      adminToast.success('Custom field deleted');
    },
    onError: (err: Error) => adminToast.error(`Failed to delete: ${err.message}`),
  });

  const handleSave = () => {
    const trimmedName = form.values.fieldName.trim();
    const trimmedKey = form.values.fieldKey.trim();
    if (!trimmedName) { adminToast.error('Field name is required'); return; }
    if (!trimmedKey) { adminToast.error('Field key is required'); return; }

    const optionsJson = form.values.fieldType === 'Select' && form.values.options.trim()
      ? JSON.stringify(form.values.options.trim().split('\n').map(o => o.trim()).filter(Boolean))
      : undefined;

    if (editing) {
      const dto: UpdateCustomFieldDefinitionRequest = {
        fieldName: trimmedName,
        fieldKey: trimmedKey,
        fieldType: form.values.fieldType,
        options: optionsJson,
        isRequired: form.values.isRequired,
        sortOrder: form.values.sortOrder,
        description: form.values.description.trim() || undefined,
        defaultValue: form.values.defaultValue.trim() || undefined,
      };
      updateMutation.mutate({ id: editing.id, dto });
    } else {
      const dto: CreateCustomFieldDefinitionRequest = {
        entityType: activeTab,
        fieldName: trimmedName,
        fieldKey: trimmedKey,
        fieldType: form.values.fieldType,
        options: optionsJson,
        isRequired: form.values.isRequired,
        sortOrder: form.values.sortOrder,
        description: form.values.description.trim() || undefined,
        defaultValue: form.values.defaultValue.trim() || undefined,
      };
      createMutation.mutate(dto);
    }
  };

  const isSaving = createMutation.isPending || updateMutation.isPending;

  return (
    <PageTemplate
      title="Custom Fields"
      subtitle="Define custom metadata fields for printers and users"
      actions={
        <Button variant="primary" onClick={openCreate} iconLeft={<PlusIcon className="h-4 w-4" />}>
          Add Field
        </Button>
      }
      embedded={embedded}
    >
      <div className="mb-6 flex gap-2">
        {(['Printer', 'User'] as const).map(tab => (
          <Button
            key={tab}
            variant={activeTab === tab ? 'primary' : 'secondary'}
            size="sm"
            onClick={() => setActiveTab(tab)}
          >
            {tab} Fields
          </Button>
        ))}
      </div>

      {isLoading ? (
        <AdminLoading variant="table" cols={6} label={`Loading ${activeTab.toLowerCase()} custom fields`} />
      ) : error ? (
        <AdminError
          title="Couldn't load custom fields"
          description={`Try loading the ${activeTab.toLowerCase()} field definitions again.`}
          error={error}
          onRetry={() => void refetch()}
        />
      ) : definitions.length === 0 ? (
        <AdminEmpty
          title={`No ${activeTab.toLowerCase()} custom fields`}
          description={`Create a field to store additional metadata for ${activeTab.toLowerCase()}s.`}
          action={<Button variant="primary" onClick={openCreate}>Add Field</Button>}
        />
      ) : (
        <div className="overflow-x-auto rounded-lg border border-pf-border">
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b border-pf-border bg-pf-bg-1 text-left text-pf-text-secondary">
                <th className="px-4 py-2 font-medium">Name</th>
                <th className="px-4 py-2 font-medium">Key</th>
                <th className="px-4 py-2 font-medium">Type</th>
                <th className="px-4 py-2 font-medium">Required</th>
                <th className="px-4 py-2 font-medium">Order</th>
                <th className="px-4 py-2 font-medium text-right">Actions</th>
              </tr>
            </thead>
            <tbody>
              {definitions.map(def => (
                <tr key={def.id} className="border-b border-pf-border last:border-0">
                  <td className="px-4 py-2 text-pf-text-primary font-medium">{def.fieldName}</td>
                  <td className="px-4 py-2 font-mono text-xs text-pf-text-secondary">{def.fieldKey}</td>
                  <td className="px-4 py-2 text-pf-text-secondary">{def.fieldType}</td>
                  <td className="px-4 py-2 text-pf-text-secondary">{def.isRequired ? 'Yes' : 'No'}</td>
                  <td className="px-4 py-2 text-pf-text-secondary">{def.sortOrder}</td>
                  <td className="px-4 py-2 text-right">
                    <div className="flex justify-end gap-1">
                      <Button
                        variant="ghost"
                        size="sm"
                        onClick={() => openEdit(def)}
                        title="Edit"
                        aria-label={`Edit ${def.fieldName}`}
                        iconCenter={<EditIcon className="h-4 w-4" />}
                      />
                      <Button
                        variant="ghost"
                        size="sm"
                        onClick={() => {
                          if (confirm(`Delete "${def.fieldName}"? All stored values will be lost.`)) {
                            deleteMutation.mutate(def.id);
                          }
                        }}
                        title="Delete"
                        aria-label={`Delete ${def.fieldName}`}
                        className="text-pf-error-text"
                        iconCenter={<DeleteIcon className="h-4 w-4" />}
                      />
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      <Modal
        isOpen={showModal}
        onClose={() => {
          form.reset();
          setShowModal(false);
        }}
        title={editing ? 'Edit Custom Field' : 'New Custom Field'}
        size="md"
      >
        <div className="flex flex-col gap-4">
          <FormField label="Field Name" htmlFor="cf-name" required>
            <Input
              id="cf-name"
              value={form.values.fieldName}
              onChange={e => handleFieldNameChange(e.target.value)}
              placeholder="e.g. Department"
            />
          </FormField>
          <FormField label="Field Key" htmlFor="cf-key" required helper="Unique identifier (kebab-case)">
            <Input
              id="cf-key"
              value={form.values.fieldKey}
              onChange={e => { form.setValue('fieldKey', e.target.value); setAutoKey(false); }}
              placeholder="e.g. department"
              className="font-mono text-sm"
            />
          </FormField>
          <FormField label="Type" htmlFor="cf-type" required>
            <Select
              id="cf-type"
              value={form.values.fieldType}
              onChange={e => form.setValue('fieldType', e.target.value as CustomFieldType)}
            >
              {FIELD_TYPES.map(ft => (
                <option key={ft.value} value={ft.value}>{ft.label}</option>
              ))}
            </Select>
          </FormField>
          {form.values.fieldType === 'Select' && (
            <FormField label="Options" htmlFor="cf-options" helper="One option per line">
              <Textarea
                id="cf-options"
                value={form.values.options}
                onChange={e => form.setValue('options', e.target.value)}
                rows={4}
                placeholder={"Option A\nOption B\nOption C"}
              />
            </FormField>
          )}
          <FormField label="Description" htmlFor="cf-desc">
            <Input
              id="cf-desc"
              value={form.values.description}
              onChange={e => form.setValue('description', e.target.value)}
              placeholder="Optional help text"
            />
          </FormField>
          <FormField label="Default Value" htmlFor="cf-default">
            <Input
              id="cf-default"
              value={form.values.defaultValue}
              onChange={e => form.setValue('defaultValue', e.target.value)}
              placeholder="Optional default"
            />
          </FormField>
          <div className="flex items-center gap-6">
            <FormField label="Sort Order" htmlFor="cf-sort" inline>
              <Input
                id="cf-sort"
                type="number"
                value={String(form.values.sortOrder)}
                onChange={e => form.setValue('sortOrder', parseInt(e.target.value) || 0)}
                className="w-20"
              />
            </FormField>
            <div className="flex items-center gap-2">
              <Toggle checked={form.values.isRequired} onChange={value => form.setValue('isRequired', value)} />
              <span className="text-sm text-pf-text-primary">Required</span>
            </div>
          </div>
          <AdminSaveBar
            isDirty={form.isDirty}
            changeCount={form.changedCount}
            changedLabels={form.changedKeys.map(key => ({
              fieldName: 'Field name',
              fieldKey: 'Field key',
              fieldType: 'Type',
              options: 'Options',
              isRequired: 'Required',
              sortOrder: 'Sort order',
              description: 'Description',
              defaultValue: 'Default value',
            })[key])}
            onDiscard={() => {
              form.reset();
              setShowModal(false);
            }}
            onSave={handleSave}
            isSaving={isSaving}
            saveLabel={editing ? 'Save' : 'Create'}
            discardLabel="Cancel"
            className="-mx-6 -mb-6 mt-2"
          />
        </div>
      </Modal>
    </PageTemplate>
  );
}

function parseOptionsToLines(optionsJson: string): string {
  try {
    const arr: string[] = JSON.parse(optionsJson);
    return Array.isArray(arr) ? arr.join('\n') : optionsJson;
  } catch {
    return optionsJson;
  }
}

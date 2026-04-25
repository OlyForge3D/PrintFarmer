import React, { useState } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { DeleteIcon, EditIcon, PlusIcon } from '@/common/components/icons/MdiIcons';
import { Modal } from '@/common/components/modals/Modal';
import { PageTemplate } from '@/common/components/PageTemplate';
import { Button, Input, Select, FormField, Textarea, Toggle } from '@/common/components/ui';
import { apiClient } from '@/services/api';
import { useCustomFieldDefinitions, queryKeys } from '@/common/hooks/useApi';
import { toast } from 'sonner';
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

function toKebabCase(s: string): string {
  return s
    .trim()
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-|-$/g, '');
}

export function CustomFieldsAdminPage() {
  const queryClient = useQueryClient();
  const [activeTab, setActiveTab] = useState<CustomFieldEntityType>('Printer');

  const { data: definitions = [], isLoading } = useCustomFieldDefinitions(activeTab);

  const [showModal, setShowModal] = useState(false);
  const [editing, setEditing] = useState<CustomFieldDefinition | null>(null);
  const [fieldName, setFieldName] = useState('');
  const [fieldKey, setFieldKey] = useState('');
  const [fieldType, setFieldType] = useState<CustomFieldType>('Text');
  const [options, setOptions] = useState('');
  const [isRequired, setIsRequired] = useState(false);
  const [sortOrder, setSortOrder] = useState(0);
  const [description, setDescription] = useState('');
  const [defaultValue, setDefaultValue] = useState('');
  const [autoKey, setAutoKey] = useState(true);

  const resetForm = () => {
    setFieldName('');
    setFieldKey('');
    setFieldType('Text');
    setOptions('');
    setIsRequired(false);
    setSortOrder(0);
    setDescription('');
    setDefaultValue('');
    setAutoKey(true);
  };

  const openCreate = () => {
    setEditing(null);
    resetForm();
    setShowModal(true);
  };

  const openEdit = (def: CustomFieldDefinition) => {
    setEditing(def);
    setFieldName(def.fieldName);
    setFieldKey(def.fieldKey);
    setFieldType(def.fieldType);
    setOptions(def.options ? parseOptionsToLines(def.options) : '');
    setIsRequired(def.isRequired);
    setSortOrder(def.sortOrder);
    setDescription(def.description ?? '');
    setDefaultValue(def.defaultValue ?? '');
    setAutoKey(false);
    setShowModal(true);
  };

  const handleFieldNameChange = (name: string) => {
    setFieldName(name);
    if (autoKey && !editing) {
      setFieldKey(toKebabCase(name));
    }
  };

  const createMutation = useMutation({
    mutationFn: (dto: CreateCustomFieldDefinitionRequest) => apiClient.createCustomFieldDefinition(dto),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: queryKeys.customFieldDefinitions(activeTab) });
      toast.success('Custom field created');
      setShowModal(false);
    },
    onError: (err: Error) => toast.error(`Failed to create: ${err.message}`),
  });

  const updateMutation = useMutation({
    mutationFn: ({ id, dto }: { id: string; dto: UpdateCustomFieldDefinitionRequest }) =>
      apiClient.updateCustomFieldDefinition(id, dto),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: queryKeys.customFieldDefinitions(activeTab) });
      toast.success('Custom field updated');
      setShowModal(false);
    },
    onError: (err: Error) => toast.error(`Failed to update: ${err.message}`),
  });

  const deleteMutation = useMutation({
    mutationFn: (id: string) => apiClient.deleteCustomFieldDefinition(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: queryKeys.customFieldDefinitions(activeTab) });
      toast.success('Custom field deleted');
    },
    onError: (err: Error) => toast.error(`Failed to delete: ${err.message}`),
  });

  const handleSave = () => {
    const trimmedName = fieldName.trim();
    const trimmedKey = fieldKey.trim();
    if (!trimmedName) { toast.error('Field name is required'); return; }
    if (!trimmedKey) { toast.error('Field key is required'); return; }

    const optionsJson = fieldType === 'Select' && options.trim()
      ? JSON.stringify(options.trim().split('\n').map(o => o.trim()).filter(Boolean))
      : undefined;

    if (editing) {
      const dto: UpdateCustomFieldDefinitionRequest = {
        fieldName: trimmedName,
        fieldKey: trimmedKey,
        fieldType,
        options: optionsJson,
        isRequired,
        sortOrder,
        description: description.trim() || undefined,
        defaultValue: defaultValue.trim() || undefined,
      };
      updateMutation.mutate({ id: editing.id, dto });
    } else {
      const dto: CreateCustomFieldDefinitionRequest = {
        entityType: activeTab,
        fieldName: trimmedName,
        fieldKey: trimmedKey,
        fieldType,
        options: optionsJson,
        isRequired,
        sortOrder,
        description: description.trim() || undefined,
        defaultValue: defaultValue.trim() || undefined,
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
        <div className="text-pf-text-secondary p-8 text-center">Loading…</div>
      ) : definitions.length === 0 ? (
        <div className="text-pf-text-secondary p-8 text-center">
          No custom fields defined for {activeTab.toLowerCase()}s. Click &quot;Add Field&quot; to create one.
        </div>
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
                        className="text-pf-error"
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
        onClose={() => setShowModal(false)}
        title={editing ? 'Edit Custom Field' : 'New Custom Field'}
        size="md"
        footer={
          <>
            <Button variant="secondary" onClick={() => setShowModal(false)}>Cancel</Button>
            <Button variant="primary" onClick={handleSave} loading={isSaving}>
              {editing ? 'Save' : 'Create'}
            </Button>
          </>
        }
      >
        <div className="flex flex-col gap-4">
          <FormField label="Field Name" htmlFor="cf-name" required>
            <Input
              id="cf-name"
              value={fieldName}
              onChange={e => handleFieldNameChange(e.target.value)}
              placeholder="e.g. Department"
            />
          </FormField>
          <FormField label="Field Key" htmlFor="cf-key" required helper="Unique identifier (kebab-case)">
            <Input
              id="cf-key"
              value={fieldKey}
              onChange={e => { setFieldKey(e.target.value); setAutoKey(false); }}
              placeholder="e.g. department"
              className="font-mono text-sm"
            />
          </FormField>
          <FormField label="Type" htmlFor="cf-type" required>
            <Select
              id="cf-type"
              value={fieldType}
              onChange={e => setFieldType(e.target.value as CustomFieldType)}
            >
              {FIELD_TYPES.map(ft => (
                <option key={ft.value} value={ft.value}>{ft.label}</option>
              ))}
            </Select>
          </FormField>
          {fieldType === 'Select' && (
            <FormField label="Options" htmlFor="cf-options" helper="One option per line">
              <Textarea
                id="cf-options"
                value={options}
                onChange={e => setOptions(e.target.value)}
                rows={4}
                placeholder={"Option A\nOption B\nOption C"}
              />
            </FormField>
          )}
          <FormField label="Description" htmlFor="cf-desc">
            <Input
              id="cf-desc"
              value={description}
              onChange={e => setDescription(e.target.value)}
              placeholder="Optional help text"
            />
          </FormField>
          <FormField label="Default Value" htmlFor="cf-default">
            <Input
              id="cf-default"
              value={defaultValue}
              onChange={e => setDefaultValue(e.target.value)}
              placeholder="Optional default"
            />
          </FormField>
          <div className="flex items-center gap-6">
            <FormField label="Sort Order" htmlFor="cf-sort" inline>
              <Input
                id="cf-sort"
                type="number"
                value={String(sortOrder)}
                onChange={e => setSortOrder(parseInt(e.target.value) || 0)}
                className="w-20"
              />
            </FormField>
            <div className="flex items-center gap-2">
              <Toggle checked={isRequired} onChange={setIsRequired} />
              <span className="text-sm text-pf-text-primary">Required</span>
            </div>
          </div>
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

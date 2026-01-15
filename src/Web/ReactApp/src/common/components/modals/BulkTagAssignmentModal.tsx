import { CheckIcon } from '@/common/components/icons/MdiIcons';
import React, { useState, useEffect } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiClient } from '@/services/api';
import { Button, Checkbox } from '@/common/components/ui';
import { Modal } from './Modal';
import { TagSelector } from '@/components/TagSelector';

interface BulkTagAssignmentModalProps {
    isOpen: boolean;
    onClose: () => void;
    initialSelectedModelIds?: string[];
}

interface ModelOption {
    id: string;
    name: string;
    fileName: string;
}

interface TagOption {
    id: string;
    name: string;
    color?: string;
    description?: string;
}

export const BulkTagAssignmentModal: React.FC<BulkTagAssignmentModalProps> = ({
    isOpen,
    onClose,
    initialSelectedModelIds = []
}) => {
    const queryClient = useQueryClient();
    const [selectedModelIds, setSelectedModelIds] = useState<string[]>(initialSelectedModelIds);
    const [selectedTags, setSelectedTags] = useState<TagOption[]>([]);
    const [selectAllModels, setSelectAllModels] = useState(false);

    // Update selected model IDs when initialSelectedModelIds changes
    useEffect(() => {
        setSelectedModelIds(initialSelectedModelIds);
    }, [initialSelectedModelIds]);

    // Fetch all models
    const { data: models = [] } = useQuery<ModelOption[]>({
        queryKey: ['all-models-bulk'],
        queryFn: async () => {
          const result = await apiClient.get3DModels();
          return (result as unknown as ModelOption[]) || [];
        },
        enabled: isOpen,
        staleTime: 5 * 60 * 1000,
        gcTime: 10 * 60 * 1000
    });

    // Bulk assign tags mutation
    const assignTagsMutation = useMutation({
        mutationFn: async () => {
            if (selectedModelIds.length === 0 || selectedTags.length === 0) {
                throw new Error('Please select at least one model and one tag');
            }

            return apiClient.bulkAssignTags(selectedModelIds, selectedTags.map(t => t.id));
        },
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['models-search'] });
            queryClient.invalidateQueries({ queryKey: ['model-detail'] });
            setSelectedModelIds([]);
            setSelectedTags([]);
            setSelectAllModels(false);
            onClose();
        }
    });

    // Handle select all models
    const handleSelectAllModels = (checked: boolean) => {
        setSelectAllModels(checked);
        if (checked) {
            if (models && Array.isArray(models)) setSelectedModelIds(models.map((m: ModelOption) => (m as unknown as { id: string }).id));
        } else {
            setSelectedModelIds([]);
        }
    };

    if (!isOpen) return null;

    const isLoading = assignTagsMutation.isPending;
    const canSubmit = selectedModelIds.length > 0 && selectedTags.length > 0 && !isLoading;

    return (
        <Modal
            isOpen={isOpen}
            onClose={onClose}
            title="Bulk Tag Assignment"
            isDisabled={isLoading}
            footer={
                <div className="flex justify-end gap-3 w-full">
                    <Button
                        onClick={onClose}
                        disabled={isLoading}
                        variant="secondary"
                    >
                        Cancel
                    </Button>
                    <Button
                        onClick={() => assignTagsMutation.mutate()}
                        disabled={!canSubmit}
                        variant="primary"
                        loading={isLoading}
                    >
                        Assign Tags ({selectedModelIds.length} models, {selectedTags.length} tags)
                    </Button>
                </div>
            }
        >
            {/* Models Selection */}
            <div className="space-y-6">
                <div>
                    <div className="flex items-center justify-between mb-3">
                        <h3 className="text-lg font-medium text-pf-text-primary">
                            Select Models ({selectedModelIds.length} selected)
                        </h3>
                        <label className="flex items-center gap-2 text-sm text-pf-text-secondary cursor-pointer hover:text-pf-text-primary">
                        <Checkbox
                            checked={selectAllModels}
                            onChange={(e) => handleSelectAllModels(e.target.checked)}
                            disabled={isLoading}
                                className="rounded"
                            />
                            Select All
                        </label>
                    </div>
                    <div className="bg-pf-bg-2 rounded-lg border border-pf-border p-4 max-h-48 overflow-y-auto space-y-2">
                        {models && Array.isArray(models) && models.length > 0 ? (
                            models.map((model: ModelOption) => (
                                <label
                                    key={model.id}
                                    className={`flex items-center gap-3 p-2 rounded transition-colors ${selectedModelIds.includes(model.id) ? 'bg-pf-bg-2' : 'hover:bg-pf-bg-secondary'}`}
                                >
                                    <Checkbox
                                        checked={selectedModelIds.includes(model.id)}
                                        onChange={(e) => {
                                            if (e.target.checked) {
                                                setSelectedModelIds(prev => [...prev, model.id]);
                                            } else {
                                                setSelectedModelIds(prev => prev.filter(id => id !== model.id));
                                                setSelectAllModels(false);
                                            }
                                        }}
                                        disabled={isLoading}
                                        className="rounded"
                                    />
                                    <div className="flex-1">
                                        <div className="text-pf-text-primary font-medium">{model.name}</div>
                                        <div className="text-sm text-pf-text-tertiary">{model.fileName}</div>
                                    </div>
                                </label>
                            ))
                        ) : (
                            <p className="text-pf-text-secondary text-center py-4">No models available</p>
                        )}
                    </div>
                </div>

                {/* Tags Selection */}
                <div>
                    <h3 className="text-lg font-medium text-pf-text-primary mb-3">
                        Select Tags ({selectedTags.length} selected)
                    </h3>
                    <TagSelector
                        selectedTags={selectedTags}
                        onTagsChange={setSelectedTags}
                        isSaving={isLoading}
                        placeholder="Search or create tags..."
                    />
                </div>

                {/* Error Message */}
                {assignTagsMutation.isError && (
                    <div className="p-4 bg-pf-error bg-opacity-10 border border-pf-error text-pf-error rounded-lg">
                        {assignTagsMutation.error instanceof Error
                            ? assignTagsMutation.error.message
                            : 'Failed to assign tags'}
                    </div>
                )}

                {/* Success Message */}
                {assignTagsMutation.isSuccess && (
                    <div className="p-4 bg-pf-success bg-opacity-10 border border-pf-success text-pf-success rounded-lg flex items-center gap-2">
                        <CheckIcon className="w-5 h-5" />
                        Tags assigned successfully!
                    </div>
                )}
                </div>
            </Modal>
    );
};

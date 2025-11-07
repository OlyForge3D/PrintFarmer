import React, { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { X, Check, Loader } from 'lucide-react';
import { getApiBaseUrl, getAuthHeaders } from '@/utils/apiUrlHelpers';

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
    const [selectedTagIds, setSelectedTagIds] = useState<string[]>([]);
    const [selectAllModels, setSelectAllModels] = useState(false);
    const [selectAllTags, setSelectAllTags] = useState(false);

    // Fetch all models
    const { data: models = [] } = useQuery<ModelOption[]>({
        queryKey: ['all-models-bulk'],
        queryFn: async () => {
            const response = await fetch(`${getApiBaseUrl()}/3d-models`, {
                headers: getAuthHeaders()
            });
            if (!response.ok) throw new Error('Failed to fetch models');
            return response.json();
        },
        enabled: isOpen,
        staleTime: 5 * 60 * 1000,
        gcTime: 10 * 60 * 1000
    });

    // Fetch all tags
    const { data: allTags = [] } = useQuery<TagOption[]>({
        queryKey: ['all-tags-bulk'],
        queryFn: async () => {
            const response = await fetch(`${getApiBaseUrl()}/3d-models/tags`, {
                headers: getAuthHeaders()
            });
            if (!response.ok) throw new Error('Failed to fetch tags');
            return response.json();
        },
        enabled: isOpen,
        staleTime: 5 * 60 * 1000,
        gcTime: 10 * 60 * 1000
    });

    // Bulk assign tags mutation
    const assignTagsMutation = useMutation({
        mutationFn: async () => {
            if (selectedModelIds.length === 0 || selectedTagIds.length === 0) {
                throw new Error('Please select at least one model and one tag');
            }

            const response = await fetch(
                `${getApiBaseUrl()}/3d-models/bulk/assign-tags`,
                {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        ...getAuthHeaders()
                    },
                    body: JSON.stringify({
                        modelIds: selectedModelIds,
                        tagIds: selectedTagIds
                    })
                }
            );

            if (!response.ok) {
                const error = await response.json();
                throw new Error(error.message || 'Failed to assign tags');
            }

            return response.json();
        },
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['models-search'] });
            queryClient.invalidateQueries({ queryKey: ['model-detail'] });
            setSelectedModelIds([]);
            setSelectedTagIds([]);
            setSelectAllModels(false);
            setSelectAllTags(false);
            onClose();
        }
    });

    // Handle select all models
    const handleSelectAllModels = (checked: boolean) => {
        setSelectAllModels(checked);
        if (checked) {
            setSelectedModelIds(models.map(m => m.id));
        } else {
            setSelectedModelIds([]);
        }
    };

    // Handle select all tags
    const handleSelectAllTags = (checked: boolean) => {
        setSelectAllTags(checked);
        if (checked) {
            setSelectedTagIds(allTags.map(t => t.id));
        } else {
            setSelectedTagIds([]);
        }
    };

    if (!isOpen) return null;

    const isLoading = assignTagsMutation.isPending;
    const canSubmit = selectedModelIds.length > 0 && selectedTagIds.length > 0 && !isLoading;

    return (
        <div className="fixed inset-0 bg-black bg-opacity-50 flex items-center justify-center z-50 p-4">
            <div className="bg-pf-bg-0 rounded-lg shadow-xl border border-pf-border max-w-2xl w-full max-h-[90vh] overflow-y-auto">
                {/* Header */}
                <div className="sticky top-0 bg-pf-bg-1 border-b border-pf-border px-6 py-4 flex items-center justify-between">
                    <h2 className="text-xl font-semibold text-pf-text-primary">Bulk Tag Assignment</h2>
                    <button
                        onClick={onClose}
                        disabled={isLoading}
                        className="text-pf-text-tertiary hover:text-pf-text-primary disabled:opacity-50"
                    >
                        <X className="w-6 h-6" />
                    </button>
                </div>

                {/* Content */}
                <div className="p-6 space-y-6">
                    {/* Models Selection */}
                    <div>
                        <div className="flex items-center justify-between mb-3">
                            <h3 className="text-lg font-medium text-pf-text-primary">
                                Select Models ({selectedModelIds.length} selected)
                            </h3>
                            <label className="flex items-center gap-2 text-sm text-pf-text-secondary cursor-pointer hover:text-pf-text-primary">
                                <input
                                    type="checkbox"
                                    checked={selectAllModels}
                                    onChange={(e) => handleSelectAllModels(e.target.checked)}
                                    disabled={isLoading}
                                    className="rounded"
                                />
                                Select All
                            </label>
                        </div>
                        <div className="bg-pf-bg-2 rounded-lg border border-pf-border p-4 max-h-48 overflow-y-auto space-y-2">
                            {models.length > 0 ? (
                                models.map(model => (
                                    <label
                                        key={model.id}
                                        className="flex items-center gap-3 p-2 hover:bg-pf-bg-1 rounded cursor-pointer"
                                    >
                                        <input
                                            type="checkbox"
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
                        <div className="flex items-center justify-between mb-3">
                            <h3 className="text-lg font-medium text-pf-text-primary">
                                Select Tags ({selectedTagIds.length} selected)
                            </h3>
                            <label className="flex items-center gap-2 text-sm text-pf-text-secondary cursor-pointer hover:text-pf-text-primary">
                                <input
                                    type="checkbox"
                                    checked={selectAllTags}
                                    onChange={(e) => handleSelectAllTags(e.target.checked)}
                                    disabled={isLoading}
                                    className="rounded"
                                />
                                Select All
                            </label>
                        </div>
                        <div className="bg-pf-bg-2 rounded-lg border border-pf-border p-4 max-h-48 overflow-y-auto space-y-2">
                            {allTags.length > 0 ? (
                                allTags.map(tag => (
                                    <label
                                        key={tag.id}
                                        className="flex items-center gap-3 p-2 hover:bg-pf-bg-1 rounded cursor-pointer"
                                    >
                                        <input
                                            type="checkbox"
                                            checked={selectedTagIds.includes(tag.id)}
                                            onChange={(e) => {
                                                if (e.target.checked) {
                                                    setSelectedTagIds(prev => [...prev, tag.id]);
                                                } else {
                                                    setSelectedTagIds(prev => prev.filter(id => id !== tag.id));
                                                    setSelectAllTags(false);
                                                }
                                            }}
                                            disabled={isLoading}
                                            className="rounded"
                                        />
                                        <div className="flex items-center gap-2 flex-1">
                                            {tag.color && (
                                                <div
                                                    className="w-4 h-4 rounded"
                                                    style={{ backgroundColor: tag.color }}
                                                />
                                            )}
                                            <div>
                                                <div className="text-pf-text-primary font-medium">{tag.name}</div>
                                                {tag.description && (
                                                    <div className="text-sm text-pf-text-tertiary">{tag.description}</div>
                                                )}
                                            </div>
                                        </div>
                                    </label>
                                ))
                            ) : (
                                <p className="text-pf-text-secondary text-center py-4">No tags available</p>
                            )}
                        </div>
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
                            <Check className="w-5 h-5" />
                            Tags assigned successfully!
                        </div>
                    )}
                </div>

                {/* Footer */}
                <div className="sticky bottom-0 bg-pf-bg-1 border-t border-pf-border px-6 py-4 flex items-center justify-end gap-3">
                    <button
                        onClick={onClose}
                        disabled={isLoading}
                        className="px-4 py-2 bg-pf-bg-2 border border-pf-border rounded hover:bg-pf-bg-0 disabled:opacity-50"
                    >
                        Cancel
                    </button>
                    <button
                        onClick={() => assignTagsMutation.mutate()}
                        disabled={!canSubmit}
                        className="flex items-center gap-2 px-4 py-2 bg-pf-accent text-white rounded hover:bg-pf-success-hover disabled:opacity-50"
                    >
                        {isLoading && <Loader className="w-4 h-4 animate-spin" />}
                        Assign Tags ({selectedModelIds.length} models, {selectedTagIds.length} tags)
                    </button>
                </div>
            </div>
        </div>
    );
};

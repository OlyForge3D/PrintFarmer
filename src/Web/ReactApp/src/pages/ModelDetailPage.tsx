import React, { useState } from 'react';
import { useParams, useNavigate } from 'react-router-dom';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { CloseIcon, ArrowLeftIcon, TagIcon, EditIcon, SaveIcon, PlusIcon } from '@/components/icons/MdiIcons';
import { PageTemplate } from '@/components/PageTemplate';
import { TagEditor } from '@/components/TagEditor';
import { Button, Input, FormField } from '@/components/ui';
import { getApiBaseUrl, getAuthHeaders } from '@/utils/apiUrlHelpers';

interface ModelDetail {
    id: string;
    name: string;
    fileName: string;
    fileSize: number;
    fileType: string;
    uploadedAt: string;
    url: string;
    thumbnailUrl?: string;
    tags?: Array<{ id: string; name: string; color?: string; description?: string }>;
}

interface TagOption {
    id: string;
    name: string;
    color?: string;
    description?: string;
}

export const ModelDetailPage: React.FC = () => {
    const { modelId } = useParams<{ modelId: string }>();
    const navigate = useNavigate();
    const queryClient = useQueryClient();
    const [selectedTagIds, setSelectedTagIds] = useState<string[]>([]);
    const [newTags, setNewTags] = useState<{ name: string; color?: string; description?: string }[]>([]);
    const [isEditingName, setIsEditingName] = useState(false);
    const [editedName, setEditedName] = useState('');
    const [isEditingTags, setIsEditingTags] = useState(false);

    // Fetch model details
    const { data: model, isLoading: modelLoading } = useQuery<ModelDetail>({
        queryKey: ['model-detail', modelId],
        queryFn: async () => {
            const response = await fetch(
                `${getApiBaseUrl()}/3d-models/${modelId}/details`,
                { headers: getAuthHeaders() }
            );
            if (!response.ok) throw new Error('Failed to fetch model');
            return response.json();
        },
        enabled: !!modelId,
        staleTime: 5 * 60 * 1000,
        gcTime: 10 * 60 * 1000
    });

    // Fetch all available tags
    const { data: allTags = [] } = useQuery<TagOption[]>({
        queryKey: ['model-tags'],
        queryFn: async () => {
            const response = await fetch(`${getApiBaseUrl()}/3d-models/tags`, {
                headers: getAuthHeaders()
            });
            if (!response.ok) throw new Error('Failed to fetch tags');
            return response.json();
        },
        staleTime: 5 * 60 * 1000,
        gcTime: 10 * 60 * 1000
    });

    // Update model name mutation
    const updateNameMutation = useMutation({
        mutationFn: async (newName: string) => {
            const response = await fetch(
                `${getApiBaseUrl()}/3d-models/${modelId}`,
                {
                    method: 'PUT',
                    headers: {
                        'Content-Type': 'application/json',
                        ...getAuthHeaders()
                    },
                    body: JSON.stringify({ name: newName })
                }
            );
            if (!response.ok) throw new Error('Failed to update model name');
        },
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['model-detail', modelId] });
            setIsEditingName(false);
        }
    });

    // Update model tags mutation
    const updateTagsMutation = useMutation({
        mutationFn: async (payload: {
            tagIds: string[];
            newTags?: { name: string; color?: string; description?: string }[];
        }) => {
            // First, create any new tags
            let finalTagIds = payload.tagIds.filter(id => !id.startsWith('temp-'));

            if (payload.newTags && payload.newTags.length > 0) {
                for (const newTag of payload.newTags) {
                    try {
                        const createResponse = await fetch(
                            `${getApiBaseUrl()}/3d-models/tags`,
                            {
                                method: 'POST',
                                headers: {
                                    'Content-Type': 'application/json',
                                    ...getAuthHeaders()
                                },
                                body: JSON.stringify({
                                    name: newTag.name,
                                    color: newTag.color,
                                    description: newTag.description
                                })
                            }
                        );
                        if (createResponse.ok) {
                            const createdTag = await createResponse.json();
                            // Add the new tag ID to the list
                            if (createdTag.id && !finalTagIds.includes(createdTag.id)) {
                                finalTagIds = [...finalTagIds, createdTag.id];
                            }
                        } else {
                            const errorText = await createResponse.text();
                            console.error(`Failed to create tag "${newTag.name}":`, createResponse.status, errorText);
                            throw new Error(`Failed to create tag: ${errorText}`);
                        }
                    } catch (error) {
                        console.error('Failed to create tag:', error);
                        throw error;
                    }
                }
            }

            // Then update the model with all tag IDs (only real IDs, no temp IDs)
            const response = await fetch(
                `${getApiBaseUrl()}/3d-models/${modelId}/tags`,
                {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        ...getAuthHeaders()
                    },
                    body: JSON.stringify({ tagIds: finalTagIds })
                }
            );
            if (!response.ok) {
                const errorText = await response.text();
                throw new Error(`Failed to update tags: ${errorText}`);
            }
        },
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['model-detail', modelId] });
            queryClient.invalidateQueries({ queryKey: ['model-tags'] });
            setIsEditingTags(false);
        }
    });

    // Initialize selected tags and name when model loads
    React.useEffect(() => {
        if (model?.tags) {
            setSelectedTagIds(model.tags.map(t => t.id));
        }
        if (model?.name) {
            setEditedName(model.name);
        }
    }, [model]);

    if (modelLoading) {
        return (
            <PageTemplate
                title="Model Detail"
                subtitle="View and manage model details"
                icon={TagIcon}
                maxWidth="max-w-4xl"
            >
                <div className="flex items-center justify-center h-64">
                    <div className="pf-animate-spin rounded-full h-12 w-12 border-b-2 border-pf-accent"></div>
                </div>
            </PageTemplate>
        );
    }

    if (!model) {
        return (
            <PageTemplate
                title="Model Not Found"
                subtitle="The requested model could not be found"
                icon={TagIcon}
                maxWidth="max-w-4xl"
            >
                <div className="text-center py-8">
                    <p className="text-pf-text-secondary mb-4">The model you're looking for doesn't exist.</p>
                    <Button variant="primary" onClick={() => navigate('/models')}>
                        Back to Models
                    </Button>
                </div>
            </PageTemplate>
        );
    }

    const formatFileSize = (bytes: number) => {
        if (bytes === 0) return '0 Bytes';
        const k = 1024;
        const sizes = ['Bytes', 'KB', 'MB', 'GB'];
        const i = Math.floor(Math.log(bytes) / Math.log(k));
        return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + ' ' + sizes[i];
    };

    const currentTags = model.tags || [];

    // Check if there are unsaved changes: either tag selection changed or new tags were added
    const hasUnsavedChanges =
        selectedTagIds.length !== currentTags.length ||
        selectedTagIds.some(id => !id.startsWith('temp-') && !currentTags.some(t => t.id === id)) ||
        currentTags.some(t => !selectedTagIds.includes(t.id)) ||
        newTags.length > 0;

    return (
        <PageTemplate
            title={model.name}
            subtitle="Model details and tag management"
            icon={TagIcon}
            maxWidth="max-w-4xl"
        >
            {/* Header */}
            <div className="flex items-center justify-between mb-6">
                <Button 
                    variant="subtle" 
                    onClick={() => navigate('/models')}
                    className="flex items-center gap-2"
                >
                    <ArrowLeftIcon className="w-5 h-5" />
                    Back to Models
                </Button>
            </div>

            {/* Model Preview and Info */}
            <div className="grid grid-cols-1 md:grid-cols-3 gap-6 mb-8">
                {/* Thumbnail */}
                <div className="md:col-span-1">
                    <div className="bg-pf-bg-1 rounded-lg border border-pf-border aspect-square flex items-center justify-center overflow-hidden">
                        {model.thumbnailUrl ? (
                            <img
                                src={model.thumbnailUrl}
                                alt={model.name}
                                className="w-full h-full object-contain"
                            />
                        ) : (
                            <div className="text-pf-text-tertiary">
                                <TagIcon className="w-12 h-12" />
                            </div>
                        )}
                    </div>
                </div>

                {/* Model Info */}
                <div className="md:col-span-2">
                    <div className="bg-pf-bg-1 rounded-lg border border-pf-border p-6 space-y-4">
                        {/* Model Name - Editable */}
                        <div>
                            <FormField label="Model Name">
                                {isEditingName ? (
                                    <div className="flex gap-2 mt-2">
                                        <Input
                                            type="text"
                                            value={editedName}
                                            onChange={(e) => setEditedName(e.target.value)}
                                            placeholder="Model name"
                                        />
                                        <Button
                                            variant="primary"
                                            size="sm"
                                            onClick={() => {
                                                if (editedName.trim() && editedName !== model?.name) {
                                                    updateNameMutation.mutate(editedName.trim());
                                                } else {
                                                    setIsEditingName(false);
                                                }
                                            }}
                                            disabled={updateNameMutation.isPending}
                                        >
                                            <SaveIcon className="w-4 h-4" />
                                        </Button>
                                        <Button
                                            variant="secondary"
                                            size="sm"
                                            onClick={() => {
                                                setEditedName(model?.name || '');
                                                setIsEditingName(false);
                                            }}
                                        >
                                            <CloseIcon className="w-4 h-4" />
                                        </Button>
                                    </div>
                                ) : (
                                    <div className="flex items-center justify-between">
                                        <p className="text-pf-text-primary font-medium">{model.name}</p>
                                        <Button
                                            variant="subtle"
                                            size="sm"
                                            onClick={() => setIsEditingName(true)}
                                            className="p-1"
                                        >
                                            <EditIcon className="w-4 h-4" />
                                        </Button>
                                    </div>
                                )}
                            </FormField>
                        </div>

                        <div>
                            <label className="text-sm text-pf-text-tertiary">File Name</label>
                            <p className="text-pf-text-primary font-medium">{model.fileName}</p>
                        </div>

                        <div className="grid grid-cols-2 gap-4">
                            <div>
                                <label className="text-sm text-pf-text-tertiary">File Type</label>
                                <p className="text-pf-text-primary font-medium">{model.fileType}</p>
                            </div>
                            <div>
                                <label className="text-sm text-pf-text-tertiary">File Size</label>
                                <p className="text-pf-text-primary font-medium">{formatFileSize(model.fileSize)}</p>
                            </div>
                        </div>

                        <div>
                            <label className="text-sm text-pf-text-tertiary">Uploaded</label>
                            <p className="text-pf-text-primary font-medium">
                                {new Date(model.uploadedAt).toLocaleString()}
                            </p>
                        </div>

                        <div>
                            <a
                                href={model.url}
                                download
                                className="inline-flex items-center gap-2 px-4 py-2 bg-pf-accent text-white rounded hover:bg-pf-success-hover"
                            >
                                Download File
                            </a>
                        </div>
                    </div>
                </div>
            </div>

            {/* Tags Section */}
            <div className="bg-pf-bg-1 rounded-lg border border-pf-border p-6">
                <h3 className="text-lg font-medium text-pf-text-primary mb-4 flex items-center gap-2">
                    <TagIcon className="w-5 h-5" />
                    Tags
                </h3>

                {isEditingTags ? (
                    <div className="space-y-4">
                        <TagEditor
                            allTags={allTags}
                            selectedTagIds={selectedTagIds}
                            onTagsChange={(tagIds, newTags) => {
                                setSelectedTagIds(tagIds);
                                if (newTags && newTags.length > 0) {
                                    // Accumulate new tags that haven't been created yet
                                    setNewTags(prev => {
                                        const accumulated = [...prev];
                                        for (const newTag of newTags) {
                                            // Check if we already have this tag
                                            if (!accumulated.some(t => t.name === newTag.name)) {
                                                accumulated.push(newTag);
                                            }
                                        }
                                        return accumulated;
                                    });
                                }
                            }}
                            placeholder="Search and add tags..."
                        />

                        {/* Save Button */}
                        <div className="flex gap-2">
                            <Button
                                type="button"
                                onClick={() =>
                                    updateTagsMutation.mutate({ tagIds: selectedTagIds, newTags })
                                }
                                disabled={updateTagsMutation.isPending || !hasUnsavedChanges}
                                variant="primary"
                                iconLeft={<SaveIcon className="w-4 h-4" />}
                            >
                                {updateTagsMutation.isPending ? 'Saving...' : 'Save Tags'}
                            </Button>
                            <Button
                                type="button"
                                onClick={() => {
                                    setSelectedTagIds(currentTags.map(t => t.id));
                                    setNewTags([]);
                                    setIsEditingTags(false);
                                }}
                                variant="secondary"
                            >
                                Cancel
                            </Button>
                        </div>
                    </div>
                ) : (
                    <div>
                        {currentTags.length > 0 || isEditingTags ? (
                            <div className="flex flex-wrap gap-2 items-center">
                                {currentTags.map(tag => (
                                    <div
                                        key={tag.id}
                                        className="flex items-center gap-1 px-3 py-1 rounded-full text-white text-sm"
                                        style={{ backgroundColor: tag.color || '#6366f1' }}
                                    >
                                        <span>{tag.name}</span>
                                        <Button
                                            type="button"
                                            onClick={() => {
                                                setSelectedTagIds(selectedTagIds.filter(id => id !== tag.id));
                                                setIsEditingTags(true);
                                            }}
                                            variant="subtle"
                                            size="sm"
                                            className="!p-0 !h-auto ml-1"
                                            title="Remove tag"
                                        >
                                            <CloseIcon className="w-3 h-3" />
                                        </Button>
                                    </div>
                                ))}
                                <Button
                                    type="button"
                                    onClick={() => setIsEditingTags(true)}
                                    variant="subtle"
                                    size="sm"
                                    className="!p-1 !h-auto"
                                    title="Add a new tag"
                                >
                                    <PlusIcon className="w-4 h-4" />
                                </Button>
                            </div>
                        ) : (
                            <div className="flex items-center gap-2">
                                <p className="text-pf-text-secondary">No tags assigned to this model.</p>
                                <Button
                                    type="button"
                                    onClick={() => setIsEditingTags(true)}
                                    variant="subtle"
                                    size="sm"
                                    className="!p-1 !h-auto"
                                    title="Add a new tag"
                                >
                                    <PlusIcon className="w-4 h-4" />
                                </Button>
                            </div>
                        )}
                    </div>
                )}
            </div>
        </PageTemplate>
    );
};

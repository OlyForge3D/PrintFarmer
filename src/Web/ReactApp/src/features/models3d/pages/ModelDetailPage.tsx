import React, { useState } from 'react';
import { useParams, useNavigate } from 'react-router';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { CloseIcon, ArrowLeftIcon, TagIcon, EditIcon, SaveIcon, PlusIcon, DownloadIcon } from '@/common/components/icons/MdiIcons';
import { PageTemplate } from '@/common/components/PageTemplate';
import TagInput from '@/components/TagInput';
import TagDisplay from '@/components/TagDisplay';
import { Button, Input, FormField, Textarea } from '@/common/components/ui';
import { apiClient } from '@/services/api';

interface ModelDetail {
    id: string;
    name: string; // Original filename uploaded by user (for display and editing)
    fileName: string;
    fileSize: number;
    fileType: string;
    uploadedAt: string;
    url: string;
    thumbnailPath?: string;
    description?: string;
    dimensionX?: number;
    dimensionY?: number;
    dimensionZ?: number;
    triangleCount?: number;
    isValid: boolean;
    validationErrors?: string;
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
    const [isEditingDescription, setIsEditingDescription] = useState(false);
    const [editedDescription, setEditedDescription] = useState('');
    const [isEditingTags, setIsEditingTags] = useState(false);

    // Fetch model details
    const { data: model, isLoading: modelLoading } = useQuery<ModelDetail>({
        queryKey: ['model-detail', modelId],
        queryFn: async () => {
            if (!modelId) throw new Error('Model ID is required');
            const result = await apiClient.getModel3DDetails(modelId);
            return result as unknown as ModelDetail;
        },
        enabled: !!modelId,
        staleTime: 5 * 60 * 1000,
        gcTime: 10 * 60 * 1000
    });

    // Fetch all available tags
    useQuery<TagOption[]>({
        queryKey: ['model-tags'],
        queryFn: async () => {
            const result = await apiClient.getTags();
            return result as unknown as TagOption[];
        },
        staleTime: 5 * 60 * 1000,
        gcTime: 10 * 60 * 1000
    });

    // Update model mutation (name and/or description)
    const updateModelMutation = useMutation({
        mutationFn: async (updates: { name?: string; description?: string }) => {
            if (!modelId) throw new Error('Model ID is required');
            await apiClient.updateModel3D(modelId, updates as Record<string, unknown>);
        },
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['model-detail', modelId] });
            setIsEditingName(false);
            setIsEditingDescription(false);
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
                        const createdTag = await apiClient.createNewTag({
                            name: newTag.name,
                            color: newTag.color,
                            description: newTag.description
                        });
                        // Add the new tag ID to the list
                        if ((createdTag as Record<string, unknown>).id && !finalTagIds.includes((createdTag as Record<string, unknown>).id as string)) {
                            finalTagIds = [...finalTagIds, (createdTag as Record<string, unknown>).id as string];
                        }
                    } catch (error: unknown) {
                        console.error('Failed to create tag:', error);
                        throw error;
                    }
                }
            }

            // Then update the model with all tag IDs (only real IDs, no temp IDs)
            // Determine current tags from model to calculate diff
            const diff = {
                toAdd: finalTagIds.filter((tagId: string) => !currentTags.map(t => t.id).includes(tagId)),
                toRemove: currentTags.map(t => t.id).filter((tagId: string) => !finalTagIds.includes(tagId))
            };

            // Assign new tags
            for (const tagId of diff.toAdd) {
                await apiClient.assignTagToModel(modelId, tagId);
            }

            // Remove tags
            for (const tagId of diff.toRemove) {
                await apiClient.removeTagFromModel(modelId, tagId);
            }

            await apiClient.updateModel3D(modelId, { tagIds: finalTagIds } as Record<string, unknown>);
        },
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['model-detail', modelId] });
            queryClient.invalidateQueries({ queryKey: ['model-tags'] });
            setIsEditingTags(false);
        }
    });

    // Initialize selected tags and editable fields when model loads
    React.useEffect(() => {
        if (model?.tags) {
            setSelectedTagIds(model.tags.map(t => t.id));
        }
        if (model?.name) {
            setEditedName(model.name);
        }
        if (model?.description) {
            setEditedDescription(model.description);
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
                    iconLeft={ <ArrowLeftIcon className="w-5 h-5" /> }
                >
                    Back to Models
                </Button>
            </div>

            {/* Model Preview and Info */}
            <div className="grid grid-cols-1 md:grid-cols-3 gap-6 mb-8">
                {/* Thumbnail */}
                <div className="md:col-span-1">
                    <div className="bg-pf-bg-1 rounded-lg border border-pf-border aspect-square flex items-center justify-center overflow-hidden">
                        {model.thumbnailPath ? (
                            <img
                                src={model.thumbnailPath}
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
                                                    updateModelMutation.mutate({ name: editedName.trim() });
                                                } else {
                                                    setIsEditingName(false);
                                                }
                                            }}
                                            disabled={updateModelMutation.isPending}
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

                        {/* Description - Editable */}
                        <div>
                            <FormField label="Description">
                                {isEditingDescription ? (
                                    <div className="flex gap-2 mt-2">
                                        <Textarea
                                            value={editedDescription}
                                            onChange={(e) => setEditedDescription(e.target.value)}
                                            placeholder="Add a description for this model..."
                                            className="flex-1 resize-none"
                                            rows={4}
                                        />
                                        <div className="flex flex-col gap-2">
                                            <Button
                                                variant="primary"
                                                size="sm"
                                                onClick={() => {
                                                    if (editedDescription !== model?.description) {
                                                        updateModelMutation.mutate({ description: editedDescription });
                                                    } else {
                                                        setIsEditingDescription(false);
                                                    }
                                                }}
                                                disabled={updateModelMutation.isPending}
                                            >
                                                <SaveIcon className="w-4 h-4" />
                                            </Button>
                                            <Button
                                                variant="secondary"
                                                size="sm"
                                                onClick={() => {
                                                    setEditedDescription(model?.description || '');
                                                    setIsEditingDescription(false);
                                                }}
                                            >
                                                <CloseIcon className="w-4 h-4" />
                                            </Button>
                                        </div>
                                    </div>
                                ) : (
                                    <div className="flex items-start justify-between gap-4">
                                        <p className="text-pf-text-primary whitespace-pre-wrap flex-1">
                                            {model.description || <span className="text-pf-text-tertiary italic">No description</span>}
                                        </p>
                                        <Button
                                            variant="subtle"
                                            size="sm"
                                            onClick={() => setIsEditingDescription(true)}
                                            className="p-1 flex-shrink-0"
                                        >
                                            <EditIcon className="w-4 h-4" />
                                        </Button>
                                    </div>
                                )}
                            </FormField>
                        </div>

                        {/* File Info - Hidden by default, shown on click or if dimensions available */}
                        <details className="group">
                            <summary className="cursor-pointer text-sm text-pf-text-secondary hover:text-pf-text-primary">
                                File Details
                            </summary>
                            <div className="mt-3 space-y-3 pt-3 border-t border-pf-border">
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

                                {model.triangleCount && (
                                    <div>
                                        <label className="text-sm text-pf-text-tertiary">Triangle Count</label>
                                        <p className="text-pf-text-primary font-medium">{model.triangleCount.toLocaleString()}</p>
                                    </div>
                                )}

                                {(model.dimensionX || model.dimensionY || model.dimensionZ) && (
                                    <div>
                                        <label className="text-sm text-pf-text-tertiary">Dimensions (mm)</label>
                                        <p className="text-pf-text-primary font-medium">
                                            X: {model.dimensionX?.toFixed(2) || '—'} × Y: {model.dimensionY?.toFixed(2) || '—'} × Z: {model.dimensionZ?.toFixed(2) || '—'}
                                        </p>
                                    </div>
                                )}

                                <div>
                                    <label className="text-sm text-pf-text-tertiary">Uploaded</label>
                                    <p className="text-pf-text-primary font-medium">
                                        {new Date(model.uploadedAt).toLocaleString()}
                                    </p>
                                </div>

                                {!model.isValid && model.validationErrors && (
                                    <div>
                                        <label className="text-sm text-pf-text-tertiary">Validation Issues</label>
                                        <p className="text-pf-alert text-sm">{model.validationErrors}</p>
                                    </div>
                                )}

                                <a
                                    href={model.url}
                                    download
                                    className="inline-flex items-center gap-2 px-4 py-2 bg-pf-accent text-white rounded hover:bg-pf-success-hover mt-3"
                                >
                                    <DownloadIcon className="w-4 h-4" />
                                    Download File
                                </a>
                            </div>
                        </details>
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
                        <TagInput
                            selectedTags={currentTags}
                            onChange={(tags) => {
                                setSelectedTagIds(tags.map(t => t.id));
                            }}
                            placeholder="Search and add tags..."
                            maxTags={20}
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
                        {currentTags.length > 0 ? (
                            <div className="flex flex-wrap gap-2 items-center">
                                {currentTags.map(tag => (
                                    <TagDisplay
                                        key={tag.id}
                                        tag={tag}
                                        showRemoveButton={false}
                                        onClick={() => setIsEditingTags(true)}
                                    />
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

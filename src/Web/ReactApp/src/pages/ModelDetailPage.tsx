import React, { useState } from 'react';
import { useParams, useNavigate } from 'react-router-dom';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { ArrowLeft, Edit2, Save, X, Tag, Plus } from 'lucide-react';
import { PageTemplate } from '@/components/PageTemplate';
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
    const [isEditing, setIsEditing] = useState(false);
    const [selectedTagIds, setSelectedTagIds] = useState<string[]>([]);

    // Fetch model details
    const { data: model, isLoading: modelLoading } = useQuery<ModelDetail>({
        queryKey: ['model-detail', modelId],
        queryFn: async () => {
            const response = await fetch(
                `${getApiBaseUrl()}/api/3d-models/${modelId}/details`,
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
            const response = await fetch(`${getApiBaseUrl()}/api/3d-models/tags`, {
                headers: getAuthHeaders()
            });
            if (!response.ok) throw new Error('Failed to fetch tags');
            return response.json();
        },
        staleTime: 5 * 60 * 1000,
        gcTime: 10 * 60 * 1000
    });

    // Update model tags mutation
    const updateTagsMutation = useMutation({
        mutationFn: async (tagIds: string[]) => {
            const response = await fetch(
                `${getApiBaseUrl()}/api/3d-models/${modelId}/tags`,
                {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        ...getAuthHeaders()
                    },
                    body: JSON.stringify({ tagIds })
                }
            );
            if (!response.ok) throw new Error('Failed to update tags');
        },
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['model-detail', modelId] });
            setIsEditing(false);
        }
    });

    // Initialize selected tags when model loads
    React.useEffect(() => {
        if (model?.tags) {
            setSelectedTagIds(model.tags.map(t => t.id));
        }
    }, [model]);

    if (modelLoading) {
        return (
            <PageTemplate
                title="Model Detail"
                subtitle="View and manage model details"
                icon={Tag}
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
                icon={Tag}
                maxWidth="max-w-4xl"
            >
                <div className="text-center py-8">
                    <p className="text-pf-text-secondary mb-4">The model you're looking for doesn't exist.</p>
                    <button
                        onClick={() => navigate('/models')}
                        className="px-4 py-2 bg-pf-accent text-white rounded hover:bg-pf-success-hover"
                    >
                        Back to Models
                    </button>
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
    const availableTagsForEditing = allTags.filter(
        t => !selectedTagIds.includes(t.id)
    );

    return (
        <PageTemplate
            title={model.name}
            subtitle="Model details and tag management"
            icon={Tag}
            maxWidth="max-w-4xl"
        >
            {/* Header */}
            <div className="flex items-center justify-between mb-6">
                <button
                    onClick={() => navigate('/models')}
                    className="flex items-center gap-2 text-pf-accent hover:text-pf-accent-hover"
                >
                    <ArrowLeft className="w-5 h-5" />
                    Back to Models
                </button>
                <button
                    onClick={() => {
                        if (isEditing) {
                            setSelectedTagIds(currentTags.map(t => t.id));
                            setIsEditing(false);
                        } else {
                            setIsEditing(true);
                        }
                    }}
                    className="flex items-center gap-2 px-4 py-2 bg-pf-bg-1 border border-pf-border rounded hover:bg-pf-bg-2"
                >
                    {isEditing ? (
                        <>
                            <X className="w-4 h-4" />
                            Cancel
                        </>
                    ) : (
                        <>
                            <Edit2 className="w-4 h-4" />
                            Edit Tags
                        </>
                    )}
                </button>
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
                                <Tag className="w-12 h-12" />
                            </div>
                        )}
                    </div>
                </div>

                {/* Model Info */}
                <div className="md:col-span-2">
                    <div className="bg-pf-bg-1 rounded-lg border border-pf-border p-6 space-y-4">
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
                    <Tag className="w-5 h-5" />
                    Tags
                </h3>

                {isEditing ? (
                    <div className="space-y-4">
                        {/* Current Tags */}
                        {selectedTagIds.length > 0 && (
                            <div>
                                <label className="text-sm font-medium text-pf-text-secondary mb-2 block">
                                    Current Tags
                                </label>
                                <div className="flex flex-wrap gap-2">
                                    {allTags
                                        .filter(t => selectedTagIds.includes(t.id))
                                        .map(tag => (
                                            <div
                                                key={tag.id}
                                                className="flex items-center gap-2 px-3 py-1 rounded-full text-white text-sm"
                                                style={{ backgroundColor: tag.color || '#6366f1' }}
                                            >
                                                {tag.name}
                                                <button
                                                    onClick={() =>
                                                        setSelectedTagIds(prev =>
                                                            prev.filter(id => id !== tag.id)
                                                        )
                                                    }
                                                    className="ml-1 hover:opacity-80"
                                                >
                                                    <X className="w-3 h-3" />
                                                </button>
                                            </div>
                                        ))}
                                </div>
                            </div>
                        )}

                        {/* Available Tags to Add */}
                        {availableTagsForEditing.length > 0 && (
                            <div>
                                <label className="text-sm font-medium text-pf-text-secondary mb-2 block">
                                    Available Tags
                                </label>
                                <div className="flex flex-wrap gap-2">
                                    {availableTagsForEditing.map(tag => (
                                        <button
                                            key={tag.id}
                                            onClick={() =>
                                                setSelectedTagIds(prev => [...prev, tag.id])
                                            }
                                            className="flex items-center gap-2 px-3 py-1 rounded-full text-white text-sm transition-opacity hover:opacity-80"
                                            style={{ backgroundColor: tag.color || '#6366f1', opacity: 0.6 }}
                                        >
                                            <Plus className="w-3 h-3" />
                                            {tag.name}
                                        </button>
                                    ))}
                                </div>
                            </div>
                        )}

                        {allTags.length === 0 && (
                            <p className="text-pf-text-secondary text-sm">
                                No tags available. Create some tags first.
                            </p>
                        )}

                        {/* Save Button */}
                        <div className="mt-6 flex gap-2">
                            <button
                                onClick={() =>
                                    updateTagsMutation.mutate(selectedTagIds)
                                }
                                disabled={updateTagsMutation.isPending}
                                className="flex items-center gap-2 px-4 py-2 bg-pf-accent text-white rounded hover:bg-pf-success-hover disabled:opacity-50"
                            >
                                <Save className="w-4 h-4" />
                                {updateTagsMutation.isPending ? 'Saving...' : 'Save Tags'}
                            </button>
                            <button
                                onClick={() => {
                                    setSelectedTagIds(currentTags.map(t => t.id));
                                    setIsEditing(false);
                                }}
                                className="px-4 py-2 bg-pf-bg-2 border border-pf-border rounded hover:bg-pf-bg-0"
                            >
                                Cancel
                            </button>
                        </div>
                    </div>
                ) : (
                    <div>
                        {currentTags.length > 0 ? (
                            <div className="flex flex-wrap gap-2">
                                {currentTags.map(tag => (
                                    <span
                                        key={tag.id}
                                        className="inline-block px-3 py-1 rounded-full text-white text-sm"
                                        style={{ backgroundColor: tag.color || '#6366f1' }}
                                        title={tag.description}
                                    >
                                        {tag.name}
                                    </span>
                                ))}
                            </div>
                        ) : (
                            <p className="text-pf-text-secondary">No tags assigned to this model.</p>
                        )}
                    </div>
                )}
            </div>
        </PageTemplate>
    );
};

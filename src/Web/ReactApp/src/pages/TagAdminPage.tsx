import React, { useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { Tag, Plus, Trash2, Edit2, X, Check, Loader, AlertCircle } from 'lucide-react';
import { PageTemplate } from '@/components/PageTemplate';
import { getApiBaseUrl, getAuthHeaders } from '@/utils/apiUrlHelpers';

interface TagOption {
    id: string;
    name: string;
    color?: string;
    description?: string;
    usageCount?: number; // Number of models with this tag
}

interface EditingTag {
    id: string;
    name: string;
    color?: string;
    description?: string;
}

export const TagAdminPage: React.FC = () => {
    const queryClient = useQueryClient();
    const [showNewTagForm, setShowNewTagForm] = useState(false);
    const [editingTagId, setEditingTagId] = useState<string | null>(null);
    const [newTagName, setNewTagName] = useState('');
    const [newTagColor, setNewTagColor] = useState('#6366f1');
    const [newTagDescription, setNewTagDescription] = useState('');
    const [editingTag, setEditingTag] = useState<EditingTag | null>(null);

    // Fetch all tags with usage count
    const { data: tags = [], isLoading } = useQuery<TagOption[]>({
        queryKey: ['admin-all-tags'],
        queryFn: async () => {
            // Fetch tags
            const tagsResponse = await fetch(`${getApiBaseUrl()}/3d-models/tags`, {
                headers: getAuthHeaders()
            });
            if (!tagsResponse.ok) throw new Error('Failed to fetch tags');
            const tagsData = await tagsResponse.json();

            // Fetch models to calculate usage count
            const modelsResponse = await fetch(`${getApiBaseUrl()}/3d-models`, {
                headers: getAuthHeaders()
            });
            if (!modelsResponse.ok) throw new Error('Failed to fetch models');
            const modelsData = await modelsResponse.json();

            // Calculate usage count for each tag
            const usageMap = new Map<string, number>();
            for (const model of modelsData) {
                if (model.tags && Array.isArray(model.tags)) {
                    for (const tag of model.tags) {
                        usageMap.set(tag.id, (usageMap.get(tag.id) || 0) + 1);
                    }
                }
            }

            return tagsData.map((tag: TagOption) => ({
                ...tag,
                usageCount: usageMap.get(tag.id) || 0
            }));
        },
        staleTime: 5 * 60 * 1000,
        gcTime: 10 * 60 * 1000
    });

    // Create tag mutation
    const createTagMutation = useMutation({
        mutationFn: async () => {
            if (!newTagName.trim()) throw new Error('Tag name is required');

            const response = await fetch(`${getApiBaseUrl()}/3d-models/tags`, {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json',
                    ...getAuthHeaders()
                },
                body: JSON.stringify({
                    name: newTagName.trim(),
                    color: newTagColor,
                    description: newTagDescription.trim()
                })
            });

            if (!response.ok) {
                const error = await response.json();
                throw new Error(error.message || 'Failed to create tag');
            }

            return response.json();
        },
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['admin-all-tags'] });
            queryClient.invalidateQueries({ queryKey: ['model-tags'] });
            setNewTagName('');
            setNewTagColor('#6366f1');
            setNewTagDescription('');
            setShowNewTagForm(false);
        }
    });

    // Delete tag mutation
    const deleteTagMutation = useMutation({
        mutationFn: async (tagId: string) => {
            const response = await fetch(
                `${getApiBaseUrl()}/3d-models/tags/${tagId}`,
                {
                    method: 'DELETE',
                    headers: getAuthHeaders()
                }
            );

            if (!response.ok) {
                const error = await response.json();
                throw new Error(error.message || 'Failed to delete tag');
            }
        },
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['admin-all-tags'] });
            queryClient.invalidateQueries({ queryKey: ['model-tags'] });
        }
    });

    const handleStartEdit = (tag: TagOption) => {
        setEditingTagId(tag.id);
        setEditingTag({
            id: tag.id,
            name: tag.name,
            color: tag.color,
            description: tag.description
        });
    };

    const handleCancelEdit = () => {
        setEditingTagId(null);
        setEditingTag(null);
    };

    const handleSaveEdit = async () => {
        if (!editingTag || !editingTag.name.trim()) {
            return;
        }

        // For now, we'll just invalidate and close since the API endpoint for update
        // would need to be added to the backend
        setEditingTagId(null);
        setEditingTag(null);
        // In a full implementation, you'd call an update mutation here
    };

    if (isLoading) {
        return (
            <PageTemplate
                title="Tag Management"
                subtitle="Manage all 3D model tags"
                icon={Tag}
                maxWidth="max-w-6xl"
            >
                <div className="flex items-center justify-center h-64">
                    <div className="pf-animate-spin rounded-full h-12 w-12 border-b-2 border-pf-accent"></div>
                </div>
            </PageTemplate>
        );
    }

    return (
        <PageTemplate
            title="Tag Management"
            subtitle="Manage all 3D model tags"
            icon={Tag}
            maxWidth="max-w-6xl"
        >
            <div className="space-y-6">
                {/* Create New Tag Form */}
                <div className="bg-pf-bg-1 rounded-lg border border-pf-border p-6">
                    <div className="flex items-center justify-between mb-4">
                        <h2 className="text-lg font-medium text-pf-text-primary flex items-center gap-2">
                            <Plus className="w-5 h-5" />
                            Create New Tag
                        </h2>
                        <button
                            onClick={() => {
                                setShowNewTagForm(!showNewTagForm);
                                if (showNewTagForm) {
                                    setNewTagName('');
                                    setNewTagColor('#6366f1');
                                    setNewTagDescription('');
                                }
                            }}
                            className="text-pf-text-tertiary hover:text-pf-text-primary"
                        >
                            <X className="w-5 h-5" />
                        </button>
                    </div>

                    {showNewTagForm && (
                        <div className="space-y-4">
                            <div>
                                <label className="block text-sm font-medium text-pf-text-secondary mb-1">
                                    Tag Name *
                                </label>
                                <input
                                    type="text"
                                    value={newTagName}
                                    onChange={(e) => setNewTagName(e.target.value)}
                                    placeholder="e.g., Miniature, Character, Utility..."
                                    disabled={createTagMutation.isPending}
                                    className="w-full px-3 py-2 bg-pf-bg-2 border border-pf-border rounded text-pf-text-primary placeholder-pf-text-tertiary disabled:opacity-50"
                                />
                            </div>

                            <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
                                <div>
                                    <label className="block text-sm font-medium text-pf-text-secondary mb-1">
                                        Color
                                    </label>
                                    <div className="flex gap-2 items-center">
                                        <input
                                            type="color"
                                            value={newTagColor}
                                            onChange={(e) => setNewTagColor(e.target.value)}
                                            disabled={createTagMutation.isPending}
                                            className="h-10 w-16 border border-pf-border rounded cursor-pointer disabled:opacity-50"
                                        />
                                        <span className="text-sm text-pf-text-tertiary">{newTagColor}</span>
                                    </div>
                                </div>

                                <div>
                                    <label className="block text-sm font-medium text-pf-text-secondary mb-1">
                                        Description (Optional)
                                    </label>
                                    <input
                                        type="text"
                                        value={newTagDescription}
                                        onChange={(e) => setNewTagDescription(e.target.value)}
                                        placeholder="Brief description of this tag..."
                                        disabled={createTagMutation.isPending}
                                        className="w-full px-3 py-2 bg-pf-bg-2 border border-pf-border rounded text-pf-text-primary placeholder-pf-text-tertiary disabled:opacity-50"
                                    />
                                </div>
                            </div>

                            {createTagMutation.isError && (
                                <div className="p-3 bg-pf-error bg-opacity-10 border border-pf-error text-pf-error rounded flex items-center gap-2">
                                    <AlertCircle className="w-4 h-4" />
                                    {createTagMutation.error instanceof Error
                                        ? createTagMutation.error.message
                                        : 'Failed to create tag'}
                                </div>
                            )}

                            <div className="flex gap-2">
                                <button
                                    onClick={() => createTagMutation.mutate()}
                                    disabled={createTagMutation.isPending || !newTagName.trim()}
                                    className="flex items-center gap-2 px-4 py-2 bg-pf-accent text-white rounded hover:bg-pf-success-hover disabled:opacity-50"
                                >
                                    {createTagMutation.isPending && (
                                        <Loader className="w-4 h-4 animate-spin" />
                                    )}
                                    Create Tag
                                </button>
                                <button
                                    onClick={() => {
                                        setShowNewTagForm(false);
                                        setNewTagName('');
                                        setNewTagColor('#6366f1');
                                        setNewTagDescription('');
                                    }}
                                    disabled={createTagMutation.isPending}
                                    className="px-4 py-2 bg-pf-bg-2 border border-pf-border rounded hover:bg-pf-bg-0 disabled:opacity-50"
                                >
                                    Cancel
                                </button>
                            </div>
                        </div>
                    )}

                    {!showNewTagForm && (
                        <button
                            onClick={() => setShowNewTagForm(true)}
                            className="w-full px-4 py-2 bg-pf-bg-2 border border-dashed border-pf-border rounded hover:bg-pf-bg-0 text-pf-text-secondary hover:text-pf-text-primary transition-colors"
                        >
                            + Add New Tag
                        </button>
                    )}
                </div>

                {/* Tags List */}
                <div className="bg-pf-bg-1 rounded-lg border border-pf-border overflow-hidden">
                    <table className="w-full">
                        <thead>
                            <tr className="border-b border-pf-border bg-pf-bg-2">
                                <th className="px-6 py-3 text-left text-sm font-medium text-pf-text-primary">
                                    Color
                                </th>
                                <th className="px-6 py-3 text-left text-sm font-medium text-pf-text-primary">
                                    Tag Name
                                </th>
                                <th className="px-6 py-3 text-left text-sm font-medium text-pf-text-primary">
                                    Description
                                </th>
                                <th className="px-6 py-3 text-left text-sm font-medium text-pf-text-primary">
                                    Usage
                                </th>
                                <th className="px-6 py-3 text-right text-sm font-medium text-pf-text-primary">
                                    Actions
                                </th>
                            </tr>
                        </thead>
                        <tbody>
                            {tags.length > 0 ? (
                                tags.map((tag) => (
                                    <tr
                                        key={tag.id}
                                        className="border-b border-pf-border hover:bg-pf-bg-2 transition-colors"
                                    >
                                        <td className="px-6 py-4">
                                            <div
                                                className="w-8 h-8 rounded border border-pf-border"
                                                style={{ backgroundColor: tag.color || '#6366f1' }}
                                                title={tag.color}
                                            />
                                        </td>
                                        <td className="px-6 py-4">
                                            {editingTagId === tag.id && editingTag ? (
                                                <input
                                                    type="text"
                                                    value={editingTag.name}
                                                    onChange={(e) =>
                                                        setEditingTag({
                                                            ...editingTag,
                                                            name: e.target.value
                                                        })
                                                    }
                                                    className="px-2 py-1 bg-pf-bg-2 border border-pf-border rounded text-pf-text-primary"
                                                />
                                            ) : (
                                                <span className="font-medium text-pf-text-primary">
                                                    {tag.name}
                                                </span>
                                            )}
                                        </td>
                                        <td className="px-6 py-4 text-pf-text-secondary text-sm">
                                            {editingTagId === tag.id && editingTag ? (
                                                <input
                                                    type="text"
                                                    value={editingTag.description || ''}
                                                    onChange={(e) =>
                                                        setEditingTag({
                                                            ...editingTag,
                                                            description: e.target.value
                                                        })
                                                    }
                                                    className="w-full px-2 py-1 bg-pf-bg-2 border border-pf-border rounded text-pf-text-primary"
                                                />
                                            ) : (
                                                tag.description || '-'
                                            )}
                                        </td>
                                        <td className="px-6 py-4">
                                            <span className="inline-block px-3 py-1 bg-pf-bg-2 rounded text-sm text-pf-text-primary">
                                                {tag.usageCount || 0} model{tag.usageCount !== 1 ? 's' : ''}
                                            </span>
                                        </td>
                                        <td className="px-6 py-4 text-right">
                                            {editingTagId === tag.id ? (
                                                <div className="flex justify-end gap-2">
                                                    <button
                                                        onClick={handleSaveEdit}
                                                        className="p-2 hover:bg-pf-success hover:bg-opacity-20 rounded text-pf-success"
                                                        title="Save changes"
                                                    >
                                                        <Check className="w-4 h-4" />
                                                    </button>
                                                    <button
                                                        onClick={handleCancelEdit}
                                                        className="p-2 hover:bg-pf-error hover:bg-opacity-20 rounded text-pf-error"
                                                        title="Cancel editing"
                                                    >
                                                        <X className="w-4 h-4" />
                                                    </button>
                                                </div>
                                            ) : (
                                                <div className="flex justify-end gap-2">
                                                    <button
                                                        onClick={() => handleStartEdit(tag)}
                                                        className="p-2 hover:bg-pf-bg-2 rounded text-pf-text-secondary hover:text-pf-text-primary"
                                                        title="Edit tag"
                                                        disabled={deleteTagMutation.isPending}
                                                    >
                                                        <Edit2 className="w-4 h-4" />
                                                    </button>
                                                    <button
                                                        onClick={() => deleteTagMutation.mutate(tag.id)}
                                                        className="p-2 hover:bg-pf-error hover:bg-opacity-20 rounded text-pf-error disabled:opacity-50"
                                                        title="Delete tag"
                                                        disabled={deleteTagMutation.isPending || (tag.usageCount || 0) > 0}
                                                    >
                                                        {deleteTagMutation.isPending ? (
                                                            <Loader className="w-4 h-4 animate-spin" />
                                                        ) : (
                                                            <Trash2 className="w-4 h-4" />
                                                        )}
                                                    </button>
                                                </div>
                                            )}
                                        </td>
                                    </tr>
                                ))
                            ) : (
                                <tr>
                                    <td colSpan={5} className="px-6 py-8 text-center text-pf-text-secondary">
                                        <Tag className="w-12 h-12 mx-auto mb-2 text-pf-text-tertiary" />
                                        No tags created yet. Create one to get started!
                                    </td>
                                </tr>
                            )}
                        </tbody>
                    </table>
                </div>

                {/* Statistics */}
                <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
                    <div className="bg-pf-bg-1 rounded-lg border border-pf-border p-4">
                        <div className="text-sm text-pf-text-tertiary">Total Tags</div>
                        <div className="text-3xl font-bold text-pf-accent mt-1">{tags.length}</div>
                    </div>
                    <div className="bg-pf-bg-1 rounded-lg border border-pf-border p-4">
                        <div className="text-sm text-pf-text-tertiary">Tagged Models</div>
                        <div className="text-3xl font-bold text-pf-accent mt-1">
                            {tags.reduce((sum, tag) => sum + (tag.usageCount || 0), 0)}
                        </div>
                    </div>
                    <div className="bg-pf-bg-1 rounded-lg border border-pf-border p-4">
                        <div className="text-sm text-pf-text-tertiary">Most Used Tag</div>
                        <div className="text-lg font-bold text-pf-text-primary mt-1">
                            {tags.length > 0
                                ? tags.reduce((max, tag) =>
                                    (tag.usageCount || 0) > (max.usageCount || 0) ? tag : max
                                )?.name || 'N/A'
                                : 'N/A'}
                        </div>
                    </div>
                </div>
            </div>
        </PageTemplate>
    );
};

export default TagAdminPage;

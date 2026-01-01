import React, { useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { DeleteIcon, CheckIcon, CloseIcon, TagIcon, EditIcon, LoadingIcon, PlusIcon } from '@/common/components/icons/MdiIcons';
import { PageTemplate } from '@/common/components/PageTemplate';
import { Button, Input, FormField, Alert } from '@/common/components/ui';
import { getApiBaseUrl, getAuthHeaders } from '@/common/utils/apiUrlHelpers';

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
                icon={TagIcon}
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
            icon={TagIcon}
        >
            <div className="space-y-6">
                {/* Create New Tag Form */}
                <div className="bg-pf-bg-1 rounded-lg border border-pf-border p-6">
                    <div className="flex items-center justify-between mb-4">
                        <h2 className="text-lg font-medium text-pf-text-primary flex items-center gap-2">
                            <PlusIcon className="w-5 h-5" />
                            Create New Tag
                        </h2>
                        <Button
                            type="button"
                            onClick={() => {
                                setShowNewTagForm(!showNewTagForm);
                                if (showNewTagForm) {
                                    setNewTagName('');
                                    setNewTagColor('#6366f1');
                                    setNewTagDescription('');
                                }
                            }}
                            variant="subtle"
                            size="sm"
                            className="!p-0 !h-auto"
                        >
                            <CloseIcon className="w-5 h-5" />
                        </Button>
                    </div>

                    {showNewTagForm && (
                        <div className="space-y-4">
                            <FormField
                                label="Tag Name *"
                                error={newTagName.trim() === '' ? 'Tag name is required' : undefined}
                                helper="e.g., Miniature, Character, Utility..."
                            >
                                <Input
                                    type="text"
                                    value={newTagName}
                                    onChange={(e) => setNewTagName(e.target.value)}
                                    placeholder="e.g., Miniature, Character, Utility..."
                                    disabled={createTagMutation.isPending}
                                />
                            </FormField>

                            <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
                                <FormField label="Color">
                                    <div className="flex gap-2 items-center">
                                        <input
                                            type="color"
                                            value={newTagColor}
                                            onChange={(e) => setNewTagColor(e.target.value)}
                                            disabled={createTagMutation.isPending}
                                            className="h-10 w-16 border border-pf-border rounded cursor-pointer disabled:opacity-50"
                                            aria-label="Select tag color"
                                        />
                                        <span className="text-sm text-pf-text-tertiary">{newTagColor}</span>
                                    </div>
                                </FormField>

                                <FormField label="Description (Optional)">
                                    <Input
                                        type="text"
                                        value={newTagDescription}
                                        onChange={(e) => setNewTagDescription(e.target.value)}
                                        placeholder="Brief description of this tag..."
                                        disabled={createTagMutation.isPending}
                                    />
                                </FormField>
                            </div>

                            {createTagMutation.isError && (
                                <Alert type="error" title="Error">
                                    {createTagMutation.error instanceof Error
                                        ? createTagMutation.error.message
                                        : 'Failed to create tag'}
                                </Alert>
                            )}

                            <div className="flex gap-2">
                                <Button
                                    variant="primary"
                                    onClick={() => createTagMutation.mutate()}
                                    disabled={createTagMutation.isPending || !newTagName.trim()}
                                >
                                    {createTagMutation.isPending && (
                                        <LoadingIcon className="w-4 h-4 mr-2" />
                                    )}
                                    Create Tag
                                </Button>
                                <Button
                                    variant="secondary"
                                    onClick={() => {
                                        setShowNewTagForm(false);
                                        setNewTagName('');
                                        setNewTagColor('#6366f1');
                                        setNewTagDescription('');
                                    }}
                                    disabled={createTagMutation.isPending}
                                >
                                    Cancel
                                </Button>
                            </div>
                        </div>
                    )}

                    {!showNewTagForm && (
                        <Button
                            variant="secondary"
                            onClick={() => setShowNewTagForm(true)}
                            className="w-full"
                        >
                            <PlusIcon className="w-4 h-4 mr-2" />
                            Add New Tag
                        </Button>
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
                                                style={{ backgroundColor: tag.color || 'var(--pf-accent)' }}
                                                title={tag.color}
                                            />
                                        </td>
                                        <td className="px-6 py-4">
                                            {editingTagId === tag.id && editingTag ? (
                                                <Input
                                                    type="text"
                                                    value={editingTag.name}
                                                    onChange={(e) =>
                                                        setEditingTag({
                                                            ...editingTag,
                                                            name: e.target.value
                                                        })
                                                    }
                                                />
                                            ) : (
                                                <span className="font-medium text-pf-text-primary">
                                                    {tag.name}
                                                </span>
                                            )}
                                        </td>
                                        <td className="px-6 py-4 text-pf-text-secondary text-sm">
                                            {editingTagId === tag.id && editingTag ? (
                                                <Input
                                                    type="text"
                                                    value={editingTag.description || ''}
                                                    onChange={(e) =>
                                                        setEditingTag({
                                                            ...editingTag,
                                                            description: e.target.value
                                                        })
                                                    }
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
                                                    <Button
                                                        type="button"
                                                        onClick={handleSaveEdit}
                                                        variant="success"
                                                        size="sm"
                                                        className="!p-2 !h-auto"
                                                        title="Save changes"
                                                    >
                                                        <CheckIcon className="w-4 h-4" />
                                                    </Button>
                                                    <Button
                                                        type="button"
                                                        onClick={handleCancelEdit}
                                                        variant="danger"
                                                        size="sm"
                                                        className="!p-2 !h-auto"
                                                        title="Cancel editing"
                                                    >
                                                        <CloseIcon className="w-4 h-4" />
                                                    </Button>
                                                </div>
                                            ) : (
                                                <div className="flex justify-end gap-2">
                                                    <Button
                                                        type="button"
                                                        onClick={() => handleStartEdit(tag)}
                                                        variant="subtle"
                                                        size="sm"
                                                        className="!p-2 !h-auto"
                                                        title="Edit tag"
                                                        disabled={deleteTagMutation.isPending}
                                                    >
                                                        <EditIcon className="w-4 h-4" />
                                                    </Button>
                                                    <Button
                                                        type="button"
                                                        onClick={() => deleteTagMutation.mutate(tag.id)}
                                                        variant="danger"
                                                        size="sm"
                                                        className="!p-2 !h-auto"
                                                        title="Delete tag"
                                                        disabled={deleteTagMutation.isPending || (tag.usageCount || 0) > 0}
                                                    >
                                                        {deleteTagMutation.isPending ? (
                                                            <LoadingIcon className="w-4 h-4" />
                                                        ) : (
                                                            <DeleteIcon className="w-4 h-4" />
                                                        )}
                                                    </Button>
                                                </div>
                                            )}
                                        </td>
                                    </tr>
                                ))
                            ) : (
                                <tr>
                                    <td colSpan={5} className="px-6 py-8 text-center text-pf-text-secondary">
                                        <TagIcon className="w-12 h-12 mx-auto mb-2 text-pf-text-tertiary" />
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

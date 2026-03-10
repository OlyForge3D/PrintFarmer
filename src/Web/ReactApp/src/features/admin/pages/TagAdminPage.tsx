import React, { useState, useEffect, useOptimistic, useTransition, useEffectEvent, useMemo, useRef } from 'react';
import { SelectableRow } from '@/common/components/Table/SelectableRow';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { DeleteIcon, CheckIcon, CloseIcon, TagIcon, EditIcon, LoadingIcon, PlusIcon, RefreshIcon } from '@/common/components/icons/MdiIcons';
import { Modal } from '@/common/components/modals/Modal';
import { PageTemplate } from '@/common/components/PageTemplate';
import { Button, Input, FormField, Alert, Tabs } from '@/common/components/ui';
import { apiClient } from '@/services/api';
import TagAnalyticsDashboard from '@/components/TagAnalyticsDashboard';
import type { TagOption, EditingTag } from '@/types/admin';

/**
 * Generate a visually distinct color using the golden angle.
 * The golden angle (~137.5°) ensures successive colors are well-distributed
 * across the color spectrum, avoiding similar adjacent colors.
 */
const generateTagColor = (index: number): string => {
    const goldenAngle = 137.508; // degrees
    const hue = (index * goldenAngle) % 360;
    // Use consistent saturation and lightness for vibrant, readable colors
    const saturation = 65; // Rich but not overwhelming
    const lightness = 55;  // Good contrast on both light and dark backgrounds
    return `hsl(${Math.round(hue)}, ${saturation}%, ${lightness}%)`;
};

/**
 * Convert HSL color string to hex format for API compatibility
 */
const hslToHex = (hslString: string): string => {
    const match = hslString.match(/hsl\((\d+),\s*(\d+)%,\s*(\d+)%\)/);
    if (!match) return '#6366f1';
    
    const h = parseInt(match[1]) / 360;
    const s = parseInt(match[2]) / 100;
    const l = parseInt(match[3]) / 100;
    
    const hue2rgb = (p: number, q: number, t: number) => {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1/6) return p + (q - p) * 6 * t;
        if (t < 1/2) return q;
        if (t < 2/3) return p + (q - p) * (2/3 - t) * 6;
        return p;
    };
    
    let r, g, b;
    if (s === 0) {
        r = g = b = l;
    } else {
        const q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        const p = 2 * l - q;
        r = hue2rgb(p, q, h + 1/3);
        g = hue2rgb(p, q, h);
        b = hue2rgb(p, q, h - 1/3);
    }
    
    const toHex = (x: number) => {
        const hex = Math.round(x * 255).toString(16);
        return hex.length === 1 ? '0' + hex : hex;
    };
    
    return `#${toHex(r)}${toHex(g)}${toHex(b)}`;
};

export const TagAdminPage: React.FC = () => {
    const queryClient = useQueryClient();
    const [activeTab, setActiveTab] = useState<'management' | 'analytics'>('management');
    const [showNewTagForm, setShowNewTagForm] = useState(false);
    const [editingTagId, setEditingTagId] = useState<string | null>(null);
    const [hoveredTagId, setHoveredTagId] = useState<string | null>(null);
    const [focusedIndex, setFocusedIndex] = useState<number>(-1);
    const [newTagName, setNewTagName] = useState('');
    const [newTagColor, setNewTagColor] = useState('#6366f1');
    const [newTagDescription, setNewTagDescription] = useState('');
    const [editingTag, setEditingTag] = useState<EditingTag | null>(null);
    const [isPending, startTransition] = useTransition();
    const editNameInputRef = useRef<HTMLInputElement>(null);
    const rowRefs = useRef<Map<number, HTMLTableRowElement>>(new Map());
    const isKeyboardNavigating = useRef(false);

    // Reset keyboard navigation mode when mouse moves
    const handleMouseMove = useEffectEvent(() => {
        isKeyboardNavigating.current = false;
    });

    useEffect(() => {
        window.addEventListener('mousemove', handleMouseMove);
        return () => window.removeEventListener('mousemove', handleMouseMove);
     
    }, []);

    // Scroll focused row into view when navigating with keyboard
    useEffect(() => {
        if (focusedIndex >= 0) {
            const row = rowRefs.current.get(focusedIndex);
            row?.scrollIntoView({ block: 'nearest', behavior: 'smooth' });
        }
    }, [focusedIndex]);

    // Fetch all tags with usage count
    const { data: tags = [], isLoading } = useQuery<TagOption[]>({
        queryKey: ['admin-all-tags'],
        queryFn: async () => {
            // Fetch tags
            const tagsData = await apiClient.getTags();

            // Fetch both Model3D and GcodeFile data to calculate usage count
            const [modelsData, gcodeFilesData] = await Promise.all([
                apiClient.get3DModels(),
                apiClient.getGcodeFilesQuery({})
            ]);

            // Calculate usage count for each tag (from both Model3D and GcodeFile)
            const usageMap = new Map<string, number>();
            
            // Count Model3D tags
            for (const model of modelsData) {
                if (model.tags && Array.isArray(model.tags)) {
                    for (const tag of model.tags) {
                        usageMap.set(tag.id, (usageMap.get(tag.id) || 0) + 1);
                    }
                }
            }
            
            // Count GcodeFile tags
            if (gcodeFilesData && gcodeFilesData.files && Array.isArray(gcodeFilesData.files)) {
                for (const file of gcodeFilesData.files) {
                    if (file.tags && Array.isArray(file.tags)) {
                        for (const tag of file.tags) {
                            usageMap.set(tag.id, (usageMap.get(tag.id) || 0) + 1);
                        }
                    }
                }
            }

            const typedTags = tagsData as unknown as TagOption[];
            return typedTags.map((tag: TagOption) => ({
                ...tag,
                usageCount: usageMap.get(tag.id) || 0
            }));
        },
        staleTime: 5 * 60 * 1000,
        gcTime: 10 * 60 * 1000
    });

    // Generate next color based on current tag count (golden angle distribution)
    const nextTagColor = useMemo(() => {
        return hslToHex(generateTagColor(tags.length));
    }, [tags.length]);

    // Optimistic UI state for deleted tags
    const [optimisticTags, addOptimisticDelete] = useOptimistic(
        tags,
        (state: TagOption[], deletedTagId: string) => 
            state.filter(tag => tag.id !== deletedTagId)
    );

    // Create tag mutation
    const createTagMutation = useMutation({
        mutationFn: async () => {
            if (!newTagName.trim()) throw new Error('Tag name is required');

            return apiClient.createNewTag({
                name: newTagName.trim(),
                color: newTagColor,
                description: newTagDescription.trim()
            });
        },
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['admin-all-tags'] });
            queryClient.invalidateQueries({ queryKey: ['model-tags'] });
            queryClient.invalidateQueries({ queryKey: ['tagAnalytics'] });
            setNewTagName('');
            setNewTagColor('#6366f1');
            setNewTagDescription('');
            setShowNewTagForm(false);
        }
    });

    // Delete tag mutation
    const deleteTagMutation = useMutation({
        mutationFn: async (tagId: string) => {
            return apiClient.deleteTagById(tagId);
        },
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['admin-all-tags'] });
            queryClient.invalidateQueries({ queryKey: ['model-tags'] });
            queryClient.invalidateQueries({ queryKey: ['tagAnalytics'] });
        }
    });

    // Handle delete with optimistic UI update
    const handleDeleteTag = (tagId: string) => {
        startTransition(async () => {
            // Show optimistic delete immediately
            addOptimisticDelete(tagId);
            
            try {
                // Execute mutation in background
                await deleteTagMutation.mutateAsync(tagId);
            } catch (error) {
                // On error, state rolls back automatically via useOptimistic
                const message = error instanceof Error ? error.message : 'Failed to delete tag';
                console.error('Delete tag error:', message);
            }
        });
    };

    const handleStartEdit = (tag: TagOption) => {
        setEditingTagId(tag.id);
        setEditingTag({
            id: tag.id,
            name: tag.name,
            color: tag.color,
            description: tag.description
        });
        // Focus the name input after React renders the input
        requestAnimationFrame(() => {
            editNameInputRef.current?.focus();
            editNameInputRef.current?.select();
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

    // Extract keyboard handler with useEffectEvent to access latest state without retriggers
    const handleKeyDown = useEffectEvent((e: KeyboardEvent) => {
        // 'Escape' to cancel editing (works even in inputs)
        if (e.key === 'Escape' && editingTagId) {
            e.preventDefault();
            handleCancelEdit();
            return;
        }
        
        // Skip other shortcuts if user is typing in an input
        if (['input', 'textarea'].includes((e.target as HTMLElement).tagName.toLowerCase())) {
            return;
        }
        
        // Arrow keys for row navigation (only on management tab with tags)
        if (activeTab === 'management' && tags.length > 0) {
            if (e.key === 'ArrowDown') {
                e.preventDefault();
                isKeyboardNavigating.current = true;
                setFocusedIndex(prev => {
                    // If not focused, start from hovered row (or first row)
                    if (prev === -1) {
                        const hoveredIndex = tags.findIndex(t => t.id === hoveredTagId);
                        if (hoveredIndex >= 0 && hoveredIndex < tags.length - 1) {
                            return hoveredIndex + 1; // Move down from hovered
                        }
                        return hoveredIndex >= 0 ? hoveredIndex : 0; // Stay on hovered or start at first
                    }
                    return prev < tags.length - 1 ? prev + 1 : prev;
                });
                return;
            }
            if (e.key === 'ArrowUp') {
                e.preventDefault();
                isKeyboardNavigating.current = true;
                setFocusedIndex(prev => {
                    // If not focused, start from hovered row (or last row)
                    if (prev === -1) {
                        const hoveredIndex = tags.findIndex(t => t.id === hoveredTagId);
                        if (hoveredIndex > 0) {
                            return hoveredIndex - 1; // Move up from hovered
                        }
                        return hoveredIndex >= 0 ? hoveredIndex : tags.length - 1; // Stay on hovered or start at last
                    }
                    return prev > 0 ? prev - 1 : prev;
                });
                return;
            }
        }
        
        // 'A' to add new tag
        if (e.key === 'a') {
            e.preventDefault();
            setShowNewTagForm(true);
            setNewTagName('');
            setNewTagColor(nextTagColor);
            setNewTagDescription('');
        }
        
        // 'E' to edit focused or hovered tag
        if (e.key === 'e') {
            e.preventDefault();
            // Prefer keyboard-focused row, fall back to hovered
            const targetTag = focusedIndex >= 0 && focusedIndex < tags.length 
                ? tags[focusedIndex] 
                : tags.find(t => t.id === hoveredTagId);
            if (targetTag) {
                handleStartEdit(targetTag);
            }
        }
    });

    // Keyboard shortcuts: 'a' to add, 'e' to edit hovered, 'Escape' to cancel
    useEffect(() => {
        window.addEventListener('keydown', handleKeyDown);
        return () => window.removeEventListener('keydown', handleKeyDown);
     
    }, []);

    if (isLoading) {
        return (
            <PageTemplate
                title="Tag Management"
                subtitle="Create, manage, and analyze 3D model tags"
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
            subtitle="Create, manage, and analyze 3D model tags"
            icon={TagIcon}
        >
            <Tabs defaultTab="management" activeTab={activeTab} onTabChange={(tabId) => setActiveTab(tabId as 'management' | 'analytics')}>
                <Tabs.List>
                    <Tabs.Tab id="management">Management</Tabs.Tab>
                    <Tabs.Tab id="analytics">Analytics</Tabs.Tab>
                </Tabs.List>
                <Tabs.Panels>
                    <Tabs.Panel id="management">
                        <div className="space-y-6">
                            {/* Add Tag Button */}
                            <div className="flex justify-between items-center">
                                <p className="text-sm text-pf-text-secondary">
                                    {tags.length} tag{tags.length !== 1 ? 's' : ''} in your library
                                </p>
                                <Button
                                    variant="primary"
                                    iconLeft={<PlusIcon className="w-4 h-4" />}
                                    onClick={() => {
                                        setNewTagName('');
                                        setNewTagColor(nextTagColor);
                                        setNewTagDescription('');
                                        setShowNewTagForm(true);
                                    }}
                                    title="Add new tag (A)"
                                >
                                    <kbd className="px-1 py-0.5 text-xs font-mono bg-pf-bg-0/20 rounded-sm">A</kbd>dd Tag
                                </Button>
                            </div>

                {/* Tags List */}
                <div className="bg-pf-bg-1 rounded-lg border border-pf-border overflow-x-auto">
                    <table className="w-full min-w-max">
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
                            {optimisticTags.length > 0 ? (
                                optimisticTags.map((tag, index) => (
                                    <SelectableRow
                                        key={tag.id}
                                        ref={(el: HTMLTableRowElement | null) => {
                                            if (el) rowRefs.current.set(index, el);
                                            else rowRefs.current.delete(index);
                                        }}
                                        className={`border-b border-pf-border ${focusedIndex === index ? 'ring-2 ring-inset ring-pf-accent bg-pf-bg-1' : ''}`}
                                        isSelected={false}
                                        onMouseEnter={() => {
                                            // Ignore hover during keyboard navigation (prevents scroll bounce)
                                            if (isKeyboardNavigating.current) return;
                                            setHoveredTagId(tag.id);
                                            setFocusedIndex(-1); // Clear keyboard focus on mouse interaction
                                        }}
                                        onMouseLeave={() => {
                                            if (isKeyboardNavigating.current) return;
                                            setHoveredTagId(null);
                                        }}
                                    >
                                        <td className="px-6 py-4">
                                            <div
                                                className="w-8 h-8 rounded-sm border border-pf-border"
                                                style={{ backgroundColor: tag.color || 'var(--pf-accent)' }}
                                                title={tag.color}
                                            />
                                        </td>
                                        <td className="px-6 py-4">
                                            {editingTagId === tag.id && editingTag ? (
                                                <Input
                                                    ref={editNameInputRef}
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
                                            <span className="inline-block px-3 py-1 bg-pf-bg-2 rounded-sm text-sm text-pf-text-primary">
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
                                                        title="Edit tag (E)"
                                                        disabled={isPending}
                                                    >
                                                        <EditIcon className="w-4 h-4" />
                                                    </Button>
                                                    <Button
                                                        type="button"
                                                        onClick={() => handleDeleteTag(tag.id)}
                                                        variant="danger"
                                                        size="sm"
                                                        className="!p-2 !h-auto"
                                                        title="Delete tag"
                                                        disabled={isPending}
                                                    >
                                                        {isPending ? (
                                                            <LoadingIcon className="w-4 h-4" />
                                                        ) : (
                                                            <DeleteIcon className="w-4 h-4" />
                                                        )}
                                                    </Button>
                                                </div>
                                            )}
                                        </td>
                                    </SelectableRow>
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
                    </Tabs.Panel>
            <Tabs.Panel id="analytics">
                <TagAnalyticsDashboard />
            </Tabs.Panel>
        </Tabs.Panels>
            </Tabs>

            {/* Add New Tag Modal */}
            <Modal
                isOpen={showNewTagForm}
                onClose={() => {
                    setShowNewTagForm(false);
                    setNewTagName('');
                    setNewTagColor(nextTagColor);
                    setNewTagDescription('');
                }}
                title="Create New Tag"
                width="max-w-md"
            >
                <div className="space-y-6">
                    {/* Tag Preview */}
                    <div className="flex flex-col items-center py-4 bg-pf-bg-2 rounded-lg">
                        <div
                            className="px-4 py-2 rounded-full text-white font-medium shadow-md transition-all duration-200"
                            style={{ backgroundColor: newTagColor }}
                        >
                            {newTagName || 'Tag Preview'}
                        </div>
                        <p className="text-xs text-pf-text-tertiary mt-2">Preview of your new tag</p>
                    </div>

                    {/* Tag Name */}
                    <FormField
                        label="Tag Name"
                        error={createTagMutation.isError ? undefined : (newTagName.trim() === '' ? undefined : undefined)}
                        helper="Choose a short, descriptive name"
                    >
                        <Input
                            type="text"
                            value={newTagName}
                            onChange={(e) => setNewTagName(e.target.value)}
                            onKeyDown={(e) => {
                                if (e.key === 'Enter' && newTagName.trim() && !createTagMutation.isPending) {
                                    e.preventDefault();
                                    createTagMutation.mutate();
                                }
                            }}
                            placeholder="e.g., Miniature, Character, Utility..."
                            disabled={createTagMutation.isPending}
                            autoFocus
                        />
                    </FormField>

                    {/* Color Selection */}
                    <FormField label="Color">
                        <div className="flex items-center gap-4">
                            <input
                                type="color"
                                value={newTagColor}
                                onChange={(e) => setNewTagColor(e.target.value)}
                                disabled={createTagMutation.isPending}
                                className="h-12 w-12 border-2 border-pf-border rounded-lg cursor-pointer disabled:opacity-50 shadow-xs"
                                aria-label="Select tag color"
                            />
                            <div className="flex-1">
                                <div className="flex gap-2">
                                    <Input
                                        type="text"
                                        value={newTagColor}
                                        onChange={(e) => {
                                            const val = e.target.value;
                                            if (/^#[0-9A-Fa-f]{0,6}$/.test(val)) {
                                                setNewTagColor(val);
                                            }
                                        }}
                                        placeholder="#6366f1"
                                        disabled={createTagMutation.isPending}
                                        className="font-mono flex-1"
                                    />
                                    <Button
                                        type="button"
                                        variant="secondary"
                                        onClick={() => setNewTagColor(hslToHex(generateTagColor(Math.floor(Math.random() * 100))))}
                                        disabled={createTagMutation.isPending}
                                        title="Generate new color"
                                        aria-label="Generate new color"
                                    >
                                        <RefreshIcon className="w-4 h-4" />
                                    </Button>
                                </div>
                                <p className="text-xs text-pf-text-tertiary mt-1">
                                    Click refresh for a new color
                                </p>
                            </div>
                        </div>
                    </FormField>

                    {/* Description */}
                    <FormField label="Description" helper="Optional - helps others understand this tag's purpose">
                        <Input
                            type="text"
                            value={newTagDescription}
                            onChange={(e) => setNewTagDescription(e.target.value)}
                            placeholder="Brief description of this tag..."
                            disabled={createTagMutation.isPending}
                        />
                    </FormField>

                    {/* Error Alert */}
                    {createTagMutation.isError && (
                        <Alert type="error" title="Error">
                            {createTagMutation.error instanceof Error
                                ? createTagMutation.error.message
                                : 'Failed to create tag'}
                        </Alert>
                    )}

                    {/* Actions */}
                    <div className="flex gap-3 pt-2">
                        <Button
                            variant="secondary"
                            onClick={() => {
                                setShowNewTagForm(false);
                                setNewTagName('');
                                setNewTagColor(nextTagColor);
                                setNewTagDescription('');
                            }}
                            disabled={createTagMutation.isPending}
                            className="flex-1"
                        >
                            Cancel
                        </Button>
                        <Button
                            variant="primary"
                            onClick={() => createTagMutation.mutate()}
                            disabled={createTagMutation.isPending || !newTagName.trim()}
                            className="flex-1"
                            loading={createTagMutation.isPending}
                        >
                            Create Tag
                        </Button>
                    </div>
                </div>
            </Modal>
        </PageTemplate>
    );
};

export default TagAdminPage;

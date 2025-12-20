import React, { useState, useRef, useEffect } from 'react';
import { X } from 'lucide-react';
import { Button } from '@/components/ui';

interface Tag {
    id: string;
    name: string;
    color?: string;
    description?: string;
}

interface TagEditorProps {
    allTags: Tag[];
    selectedTagIds: string[];
    onTagsChange: (tagIds: string[], newTags?: { name: string; color?: string; description?: string }[]) => void;
    placeholder?: string;
}

export const TagEditor: React.FC<TagEditorProps> = ({
    allTags,
    selectedTagIds,
    onTagsChange,
    placeholder = 'Add tags...'
}) => {
    const [searchQuery, setSearchQuery] = useState('');
    const [showSuggestions, setShowSuggestions] = useState(false);
    const [tempTagsDisplay, setTempTagsDisplay] = useState<Record<string, Tag>>({});
    const containerRef = useRef<HTMLDivElement>(null);
    const inputRef = useRef<HTMLInputElement>(null);
    const suggestionsRef = useRef<HTMLDivElement>(null);

    // Get selected tags for display - include both real tags and temporary tags
    const selectedTags = selectedTagIds.map(id => {
        // Check if this is a real tag in allTags
        const realTag = allTags.find(t => t.id === id);
        if (realTag) return realTag;

        // If it's a temporary tag, get it from our display state
        if (tempTagsDisplay[id]) return tempTagsDisplay[id];

        // Fallback (shouldn't happen)
        return { id, name: 'Tag', color: '#6366f1' };
    });

    // Filter tags that match search query and aren't already selected
    const filteredSuggestions = allTags.filter(tag =>
        !selectedTagIds.includes(tag.id) &&
        (tag.name.toLowerCase().includes(searchQuery.toLowerCase()) ||
            (tag.description?.toLowerCase().includes(searchQuery.toLowerCase()) ?? false))
    );

    // Handle tag selection from suggestions
    const selectTag = (tagId: string) => {
        if (!selectedTagIds.includes(tagId)) {
            onTagsChange([...selectedTagIds, tagId]);
        }
        setSearchQuery('');
        setShowSuggestions(false);
        inputRef.current?.focus();
    };

    // Handle remove tag
    const removeTag = (tagId: string) => {
        onTagsChange(selectedTagIds.filter(id => id !== tagId));
    };

    // Close suggestions when clicking outside
    useEffect(() => {
        const handleClickOutside = (event: MouseEvent) => {
            if (containerRef.current && !containerRef.current.contains(event.target as Node)) {
                setShowSuggestions(false);
            }
        };

        document.addEventListener('mousedown', handleClickOutside);
        return () => document.removeEventListener('mousedown', handleClickOutside);
    }, []);

    // Focus input when opened
    useEffect(() => {
        if (showSuggestions && inputRef.current) {
            inputRef.current.focus();
        }
    }, [showSuggestions]);

    return (
        <div ref={containerRef} className="relative w-full">
            {/* Selected tags + input field */}
            <div className="w-full min-h-12 bg-pf-bg-2 border border-pf-border rounded-lg p-2 flex flex-wrap gap-2 items-center">
                {/* Selected tags as removable pills */}
                {selectedTags.map(tag => (
                    <div
                        key={tag.id}
                        className="flex items-center gap-1 px-3 py-1 rounded-full text-white text-sm font-medium"
                        style={{ backgroundColor: tag.color || '#6366f1' }}
                    >
                        <span>{tag.name}</span>
                        <Button
                            type="button"
                            variant="subtle"
                            size="sm"
                            onClick={() => removeTag(tag.id)}
                            className="!p-0 !h-auto hover:opacity-80"
                            title="Remove tag"
                        >
                            <X className="w-3 h-3" />
                        </Button>
                    </div>
                ))}

                {/* Input field */}
                {/* eslint-disable-next-line local/pf-no-raw-html-controls */}
                <input
                    ref={inputRef}
                    type="text"
                    placeholder={selectedTags.length === 0 ? placeholder : 'Add more tags...'}
                    value={searchQuery}
                    onChange={(e) => {
                        setSearchQuery(e.target.value);
                        setShowSuggestions(true);
                    }}
                    onFocus={() => setShowSuggestions(true)}
                    onBlur={() => {
                        // Hide suggestions on blur, but leave input text in case user wants to edit
                        // Only clear if there's text and we're truly leaving the field
                        setTimeout(() => {
                            setShowSuggestions(false);
                        }, 100);
                    }}
                    onKeyDown={(e) => {
                        if (e.key === 'Enter' || e.key === 'Tab') {
                            if (e.key === 'Enter') {
                                e.preventDefault();
                            }
                            // Try to create tag if query is valid
                            const currentQuery = searchQuery.trim();

                            // Check if an exact match exists in allTags
                            const exactMatch = allTags.find(t => t.name.toLowerCase() === currentQuery.toLowerCase());

                            if (exactMatch && !selectedTagIds.includes(exactMatch.id)) {
                                // Exact tag exists - select it instead of creating new
                                selectTag(exactMatch.id);
                            } else if (currentQuery.length > 0 && !exactMatch) {
                                // Create new tag (doesn't exist yet)
                                const colors = ['#ef4444', '#f97316', '#eab308', '#22c55e', '#06b6d4', '#3b82f6', '#8b5cf6', '#ec4899'];
                                const randomColor = colors[Math.floor(Math.random() * colors.length)];
                                const newTag = { name: currentQuery, color: randomColor };
                                const tempId = `temp-${Date.now()}-${Math.random().toString(36).substr(2, 9)}`;

                                // Store the temp tag for display
                                setTempTagsDisplay(prev => ({
                                    ...prev,
                                    [tempId]: { id: tempId, name: currentQuery, color: randomColor }
                                }));

                                // Call onTagsChange with new tag
                                onTagsChange([...selectedTagIds, tempId], [newTag]);
                                // Immediately clear the input
                                setSearchQuery('');
                                setShowSuggestions(false);

                                // Re-focus for Enter key (not Tab, Tab should leave)
                                if (e.key === 'Enter') {
                                    setTimeout(() => inputRef.current?.focus(), 0);
                                }
                            } else if (filteredSuggestions.length > 0 && currentQuery.length > 0) {
                                // Select first matching existing tag
                                selectTag(filteredSuggestions[0].id);
                            }
                        } else if (e.key === 'Escape') {
                            setSearchQuery('');
                            setShowSuggestions(false);
                        }
                    }}
                    className="flex-1 min-w-32 px-2 py-1 bg-transparent outline-none text-pf-text-primary placeholder-pf-text-tertiary"
                />
            </div>

            {/* Suggestions popup - only show existing matching tags */}
            {showSuggestions && searchQuery.trim().length > 0 && filteredSuggestions.length > 0 && (
                <div
                    ref={suggestionsRef}
                    className="absolute top-full left-0 right-0 mt-1 bg-pf-bg-0 border border-pf-border rounded-lg shadow-lg z-10 max-h-64 overflow-y-auto"
                >
                    <div className="p-2 space-y-1">
                        {/* Existing matching tags */}
                        {filteredSuggestions.map(tag => (
                            <Button
                                type="button"
                                variant="subtle"
                                size="sm"
                                onClick={() => selectTag(tag.id)}
                                className="w-full flex items-center gap-3 p-2 rounded text-left !justify-start"
                            >
                                <div
                                    className="w-3 h-3 rounded-full flex-shrink-0"
                                    style={{ backgroundColor: tag.color || '#6366f1' }}
                                />
                                <div className="flex-1 min-w-0">
                                    <div className="text-pf-text-primary font-medium text-sm truncate">{tag.name}</div>
                                    {tag.description && (
                                        <div className="text-pf-text-tertiary text-xs truncate">{tag.description}</div>
                                    )}
                                </div>
                            </Button>
                        ))}
                    </div>
                </div>
            )}
        </div>
    );
};

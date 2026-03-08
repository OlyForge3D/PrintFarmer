import React, { useState, useRef, useEffect, useCallback } from 'react';
import { Button } from '@/common/components/ui/Button';
import { TagDto, TagSuggestionDto, tagService } from '../services/tagService';
import TagDisplay from './TagDisplay';

export interface TagInputProps {
  /** Selected tags */
  selectedTags: TagDto[];
  /** Callback when tags change */
  onChange: (tags: TagDto[]) => void;
  /** Placeholder text */
  placeholder?: string;
  /** Maximum number of tags allowed */
  maxTags?: number;
  /** Whether input is disabled */
  disabled?: boolean;
  /** CSS class for additional styling */
  className?: string;
  /** Custom validator for tag names */
  validator?: (tagName: string) => { valid: boolean; error?: string };
}

/**
 * TagInput Component
 * 
 * Input field with autocomplete suggestions, popular tags, and tag management.
 * Includes keyboard navigation and accessibility features.
 * 
 * Features:
 * - Input field with real-time autocomplete
 * - Popular tags dropdown
 * - Search debouncing
 * - Tag validation
 * - Duplicate prevention
 * - Keyboard navigation (arrow keys, Enter, Escape)
 * - WCAG 2.2 AA compliant
 */
export const TagInput: React.FC<TagInputProps> = ({
  selectedTags,
  onChange,
  placeholder = 'Add tags...',
  maxTags,
  disabled = false,
  className = '',
  validator,
}) => {
  const [inputValue, setInputValue] = useState('');
  const [suggestions, setSuggestions] = useState<TagSuggestionDto[]>([]);
  const [popularTags, setPopularTags] = useState<TagSuggestionDto[]>([]);
  const [showSuggestions, setShowSuggestions] = useState(false);
  const [selectedSuggestionIndex, setSelectedSuggestionIndex] = useState(-1);
  const [isLoading, setIsLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const inputRef = useRef<HTMLInputElement>(null);
  const suggestionsRef = useRef<HTMLDivElement>(null);

  // Load popular tags on mount
  useEffect(() => {
    const loadPopularTags = async () => {
      const tags = await tagService.getPopularTags(5);
      setPopularTags(tags);
    };

    loadPopularTags();
  }, []);

  // Handle search with debouncing
  const handleSearch = useCallback((query: string) => {
    if (!query.trim()) {
      setSuggestions([]);
      setPopularTags([]);
      const loadPopular = async () => {
        const tags = await tagService.getPopularTags(5);
        setPopularTags(tags);
      };
      loadPopular();
      return;
    }

    setIsLoading(true);
    tagService.searchTags(query, (results) => {
      setSuggestions(results);
      setIsLoading(false);
    });
  }, []);

  const handleInputChange = (e: React.ChangeEvent<HTMLInputElement>) => {
    const value = e.target.value;
    setInputValue(value);
    setSelectedSuggestionIndex(-1);
    
    if (value.trim()) {
      setShowSuggestions(true);
      handleSearch(value);
    } else {
      setShowSuggestions(true);
      setSuggestions([]);
    }
  };

  const addTag = async (suggestion: TagSuggestionDto | null = null) => {
    let tagToAdd: TagDto | null = null;

    if (suggestion) {
      // Add from suggestion
      tagToAdd = await tagService.getTagById(suggestion.id);
    } else if (inputValue.trim()) {
      // Create new tag from input
      const tagName = inputValue.trim();

      // Validate
      if (validator) {
        const validation = validator(tagName);
        if (!validation.valid) {
          setError(validation.error || 'Invalid tag');
          return;
        }
      }

      // Check for duplicates
      if (selectedTags.some(t => t.name.toLowerCase() === tagName.toLowerCase())) {
        setError('Tag already added');
        return;
      }

      // Check max tags
      if (maxTags && selectedTags.length >= maxTags) {
        setError(`Maximum ${maxTags} tags allowed`);
        return;
      }

      // Try to create
      tagToAdd = await tagService.createTag(tagName);
      if (!tagToAdd) {
        setError('Failed to create tag');
        return;
      }
    }

    if (tagToAdd) {
      // Check for duplicates
      if (selectedTags.some(t => t.id === tagToAdd!.id)) {
        setError('Tag already added');
        return;
      }

      // Check max tags
      if (maxTags && selectedTags.length >= maxTags) {
        setError(`Maximum ${maxTags} tags allowed`);
        return;
      }

      // Add tag
      onChange([...selectedTags, tagToAdd]);
      setInputValue('');
      setSuggestions([]);
      setShowSuggestions(false);
      setError(null);
      inputRef.current?.focus();
    }
  };

  const removeTag = (tagId: string) => {
    onChange(selectedTags.filter(t => t.id !== tagId));
    setError(null);
  };

  const handleKeyDown = (e: React.KeyboardEvent<HTMLInputElement>) => {
    if (disabled) return;

    const currentSuggestions = suggestions.length > 0 ? suggestions : popularTags;

    switch (e.key) {
      case 'Enter':
        e.preventDefault();
        if (selectedSuggestionIndex >= 0 && currentSuggestions[selectedSuggestionIndex]) {
          addTag(currentSuggestions[selectedSuggestionIndex]);
        } else {
          addTag();
        }
        setSelectedSuggestionIndex(-1);
        break;

      case 'ArrowDown':
        e.preventDefault();
        setSelectedSuggestionIndex(prev =>
          prev < currentSuggestions.length - 1 ? prev + 1 : prev
        );
        break;

      case 'ArrowUp':
        e.preventDefault();
        setSelectedSuggestionIndex(prev => (prev > 0 ? prev - 1 : -1));
        break;

      case 'Escape':
        e.preventDefault();
        setShowSuggestions(false);
        setSelectedSuggestionIndex(-1);
        break;

      case 'Backspace':
        if (inputValue === '' && selectedTags.length > 0) {
          removeTag(selectedTags[selectedTags.length - 1].id);
        }
        break;

      default:
        break;
    }
  };

  // Close suggestions when clicking outside
  useEffect(() => {
    const handleClickOutside = (e: MouseEvent) => {
      if (
        suggestionsRef.current &&
        !suggestionsRef.current.contains(e.target as Node) &&
        inputRef.current &&
        !inputRef.current.contains(e.target as Node)
      ) {
        setShowSuggestions(false);
      }
    };

    document.addEventListener('mousedown', handleClickOutside);
    return () => document.removeEventListener('mousedown', handleClickOutside);
  }, []);

  const currentSuggestions = suggestions.length > 0 ? suggestions : popularTags;
  const canAddMore = !maxTags || selectedTags.length < maxTags;

  return (
    <div className={`w-full ${className}`}>
      {/* Selected Tags */}
      {selectedTags.length > 0 && (
        <div className="flex flex-wrap gap-2 mb-3 p-2 bg-pf-bg-2 rounded-md">
          {selectedTags.map(tag => (
            <TagDisplay
              key={tag.id}
              tag={tag}
              showRemoveButton
              onRemove={removeTag}
              disabled={disabled}
            />
          ))}
        </div>
      )}

      {/* Input Container */}
      <div className="relative">
        <input
          ref={inputRef}
          type="text"
          value={inputValue}
          onChange={handleInputChange}
          onKeyDown={handleKeyDown}
          onFocus={() => {
            if (!disabled) {
              setShowSuggestions(true);
            }
          }}
          placeholder={placeholder}
          disabled={disabled || !canAddMore}
          aria-label="Tag input"
          aria-expanded={showSuggestions}
          aria-controls="tag-suggestions"
          aria-autocomplete="list"
          aria-haspopup="listbox"
          className={`w-full px-3 py-1.5 text-sm border border-pf-border rounded-md transition-colors duration-200 focus:outline-hidden focus:border-pf-accent text-pf-text-primary ${
            disabled ? 'bg-pf-bg-2 cursor-not-allowed' : 'bg-pf-bg-1'
          } ${error ? 'border-pf-error' : ''}`}
        />

        {isLoading && (
          <div className="absolute right-3 top-2.5">
            <div className="animate-spin h-5 w-5 text-pf-accent" />
          </div>
        )}

        {/* Suggestions Dropdown */}
        {showSuggestions && currentSuggestions.length > 0 && (
          <div
            ref={suggestionsRef}
            id="tag-suggestions"
            role="listbox"
            className="absolute top-full left-0 right-0 mt-2 bg-pf-bg-1 border border-pf-border rounded-md shadow-lg z-50 max-h-60 overflow-y-auto"
          >
            {currentSuggestions.map((suggestion, index) => (
              <Button
                key={suggestion.id}
                onClick={() => addTag(suggestion)}
                variant={index === selectedSuggestionIndex ? 'primary' : 'subtle'}
                className={`w-full px-4 py-2 text-left justify-between flex items-center ${
                  index === selectedSuggestionIndex
                    ? 'bg-pf-accent-bg text-pf-accent'
                    : 'hover:bg-pf-bg-2'
                }`}
                aria-selected={index === selectedSuggestionIndex}
                role="option"
              >
                <span>{suggestion.name}</span>
                <span className="text-xs text-pf-text-secondary">({suggestion.usageCount})</span>
              </Button>
            ))}
          </div>
        )}

        {/* Empty State */}
        {showSuggestions && currentSuggestions.length === 0 && inputValue.trim() && !isLoading && (
          <div className="absolute top-full left-0 right-0 mt-2 bg-pf-bg-1 border border-pf-border rounded-md shadow-lg p-4 text-center text-pf-text-secondary z-50">
            No tags found. Press Enter to create &quot;{inputValue.trim()}&quot;
          </div>
        )}
      </div>

      {/* Error Message */}
      {error && (
        <div className="mt-2 text-sm text-pf-error" role="alert">
          {error}
        </div>
      )}

      {/* Helper Text */}
      {maxTags && (
        <div className="mt-2 text-xs text-pf-text-secondary">
          {selectedTags.length} / {maxTags} tags
        </div>
      )}
    </div>
  );
};

export default TagInput;

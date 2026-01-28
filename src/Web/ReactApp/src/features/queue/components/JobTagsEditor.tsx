import React, { useState, useCallback, useRef, useEffect } from 'react';
import { Button } from '@/common/components/ui/Button';

export interface JobTagsEditorProps {
  tags: string[];
  isEditing: boolean;
  onTagsChange: (tags: string[]) => void;
}

const COMMON_TAGS = ['PLA', 'PETG', 'ABS', 'TPU', 'Nylon', 'Prototype', 'Production', 'Test', 'Urgent', 'Watch'];
const MAX_TAGS = 10;
const MAX_TAG_LENGTH = 30;

const JobTagsEditor: React.FC<JobTagsEditorProps> = ({
  tags,
  isEditing,
  onTagsChange,
}) => {
  const [localTags, setLocalTags] = useState<string[]>(tags);
  const [inputValue, setInputValue] = useState('');
  const [suggestions, setSuggestions] = useState<string[]>([]);
  const [showSuggestions, setShowSuggestions] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const inputRef = useRef<HTMLInputElement>(null);
  const suggestionsRef = useRef<HTMLUListElement>(null);

  // Sync local state with prop changes
  useEffect(() => {
    setLocalTags(tags);
  }, [tags]);

  const updateSuggestions = useCallback((value: string) => {
    if (!value.trim()) {
      setSuggestions(COMMON_TAGS.filter((tag) => !localTags.includes(tag)));
      return;
    }

    const filtered = COMMON_TAGS.filter(
      (tag) =>
        tag.toLowerCase().includes(value.toLowerCase()) &&
        !localTags.includes(tag)
    );
    setSuggestions(filtered);
  }, [localTags]);

  const handleInputChange = useCallback(
    (e: React.ChangeEvent<HTMLInputElement>) => {
      const value = e.target.value;
      setInputValue(value);
      setError(null);
      updateSuggestions(value);
      setShowSuggestions(true);
    },
    [updateSuggestions]
  );

  const addTag = useCallback(
    (tagToAdd: string) => {
      const trimmedTag = tagToAdd.trim();

      // Validation
      if (!trimmedTag) {
        setError('Tag cannot be empty');
        return;
      }

      if (trimmedTag.length > MAX_TAG_LENGTH) {
        setError(`Tag must be ${MAX_TAG_LENGTH} characters or less`);
        return;
      }

      if (localTags.includes(trimmedTag)) {
        setError('This tag already exists');
        return;
      }

      if (localTags.length >= MAX_TAGS) {
        setError(`Maximum ${MAX_TAGS} tags allowed`);
        return;
      }

      const newTags = [...localTags, trimmedTag];
      setLocalTags(newTags);
      onTagsChange(newTags);

      setInputValue('');
      setError(null);
      setShowSuggestions(false);
      updateSuggestions('');

      // Refocus input
      inputRef.current?.focus();
    },
    [localTags, onTagsChange, updateSuggestions]
  );

  const removeTag = useCallback(
    (tagToRemove: string) => {
      const newTags = localTags.filter((tag) => tag !== tagToRemove);
      setLocalTags(newTags);
      onTagsChange(newTags);
      setError(null);
    },
    [localTags, onTagsChange]
  );

  const handleKeyDown = useCallback(
    (e: React.KeyboardEvent<HTMLInputElement>) => {
      if (e.key === 'Enter') {
        e.preventDefault();
        addTag(inputValue);
      } else if (e.key === 'Escape') {
        setShowSuggestions(false);
      }
    },
    [inputValue, addTag]
  );

  const handleSuggestionClick = useCallback(
    (tag: string) => {
      addTag(tag);
    },
    [addTag]
  );

  if (isEditing) {
    return (
      <div className="space-y-2">
        <div className="space-y-2">
          <div className="flex flex-wrap gap-1.5">
            {localTags.map((tag) => (
              <div 
                key={tag} 
                className="inline-flex items-center gap-1 px-2 py-0.5 bg-pf-accent/20 text-pf-accent text-xs rounded-full" 
                role="status" 
                aria-label={`Tag: ${tag}`}
              >
                <span>{tag}</span>
                <Button
                  className="p-0 h-4 w-4 min-w-0 hover:text-pf-error"
                  onClick={() => removeTag(tag)}
                  aria-label={`Remove tag ${tag}`}
                  title={`Remove tag ${tag}`}
                  variant="ghost"
                  size="sm"
                >
                  ✕
                </Button>
              </div>
            ))}
          </div>

          <div className="relative">
            <input
              ref={inputRef}
              type="text"
              value={inputValue}
              onChange={handleInputChange}
              onKeyDown={handleKeyDown}
              onFocus={() => setShowSuggestions(true)}
              onBlur={() => setTimeout(() => setShowSuggestions(false), 200)}
              placeholder="Add tags (press Enter)"
              maxLength={MAX_TAG_LENGTH}
              className="w-full px-3 py-1.5 text-sm border border-pf-border rounded bg-pf-bg-0 text-pf-text-primary focus:outline-none focus:ring-2 focus:ring-pf-accent focus:border-transparent"
              aria-label="Add tags"
              aria-autocomplete="list"
              aria-controls="tag-suggestions"
              aria-expanded={showSuggestions && suggestions.length > 0}
              aria-invalid={!!error}
              aria-describedby={error ? 'tags-error' : 'tags-help'}
            />

            {showSuggestions && suggestions.length > 0 && (
              <ul
                id="tag-suggestions"
                className="absolute z-10 mt-1 w-full bg-pf-bg-1 border border-pf-border rounded-md shadow-lg max-h-40 overflow-auto"
                ref={suggestionsRef}
                role="listbox"
              >
                {suggestions.map((suggestion) => (
                  <li
                    key={suggestion}
                    className="px-3 py-1.5 text-sm text-pf-text-primary hover:bg-pf-bg-2 cursor-pointer"
                    role="option"
                    onClick={() => handleSuggestionClick(suggestion)}
                    onKeyDown={(e) => {
                      if (e.key === 'Enter') {
                        handleSuggestionClick(suggestion);
                      }
                    }}
                  >
                    {suggestion}
                  </li>
                ))}
              </ul>
            )}
          </div>
        </div>

        <div className="flex items-center justify-between text-xs">
          <span className="text-pf-text-muted">
            {localTags.length} / {MAX_TAGS} tags
          </span>

          {error && (
            <span id="tags-error" className="text-pf-error" role="alert">
              {error}
            </span>
          )}

          {!error && (
            <span id="tags-help" className="text-pf-text-muted italic">
              Suggested: {COMMON_TAGS.filter((t) => !localTags.includes(t)).slice(0, 3).join(', ')}
            </span>
          )}
        </div>
      </div>
    );
  }

  return (
    <div className="min-h-[40px]">
      {localTags.length > 0 ? (
        <div className="flex flex-wrap gap-1.5">
          {localTags.map((tag) => (
            <span 
              key={tag} 
              className="inline-flex px-2 py-0.5 bg-pf-accent/20 text-pf-accent text-xs rounded-full"
            >
              {tag}
            </span>
          ))}
        </div>
      ) : (
        <p className="text-sm text-pf-text-muted italic">No tags added</p>
      )}
    </div>
  );
};

export default JobTagsEditor;

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
      <div className="tags-editor editing">
        <div className="tags-input-wrapper">
          <div className="tags-list">
            {localTags.map((tag) => (
              <div key={tag} className="tag-chip" role="status" aria-label={`Tag: ${tag}`}>
                <span className="tag-text">{tag}</span>
                <Button
                  className="tag-remove-button"
                  onClick={() => removeTag(tag)}
                  aria-label={`Remove tag ${tag}`}
                  title={`Remove tag ${tag}`}
                  variant="subtle"
                  size="sm"
                >
                  ✕
                </Button>
              </div>
            ))}
          </div>

          <div className="tag-input-container">
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
              aria-label="Add tags"
              aria-autocomplete="list"
              aria-controls="tag-suggestions"
              aria-expanded={showSuggestions && suggestions.length > 0}
              aria-invalid={!!error}
              aria-describedby={error ? 'tags-error' : 'tags-help'}
            />
          </div>
        </div>

        {showSuggestions && suggestions.length > 0 && (
          <ul
            id="tag-suggestions"
            className="tag-suggestions"
            ref={suggestionsRef}
            role="listbox"
          >
            {suggestions.map((suggestion) => (
              <li
                key={suggestion}
                className="suggestion-item"
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

        <div className="tags-metadata">
          <div className="tag-count">
            {localTags.length} / {MAX_TAGS} tags
          </div>

          {error && (
            <div id="tags-error" className="error-message" role="alert">
              {error}
            </div>
          )}

          {!error && (
            <div id="tags-help" className="help-text">
              Suggested: {COMMON_TAGS.filter((t) => !localTags.includes(t)).slice(0, 3).join(', ')}
            </div>
          )}
        </div>
      </div>
    );
  }

  return (
    <div className="tags-editor view-only">
      {localTags.length > 0 ? (
        <div className="tags-list">
          {localTags.map((tag) => (
            <span key={tag} className="tag-chip view-only">
              {tag}
            </span>
          ))}
        </div>
      ) : (
        <p className="empty-message">No tags added</p>
      )}
    </div>
  );
};

export default JobTagsEditor;

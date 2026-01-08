import React, { useState, useCallback, useRef, useEffect } from 'react';

export interface JobNotesEditorProps {
  notes: string;
  isEditing: boolean;
  onNotesChange: (notes: string) => void;
}

const MAX_NOTES_LENGTH = 500;

const JobNotesEditor: React.FC<JobNotesEditorProps> = ({
  notes,
  isEditing,
  onNotesChange,
}) => {
  const [localNotes, setLocalNotes] = useState(notes);
  const [error, setError] = useState<string | null>(null);
  const autoSaveTimeoutRef = useRef<NodeJS.Timeout | null>(null);

  // Sync local state with prop changes
  useEffect(() => {
    setLocalNotes(notes);
  }, [notes]);

  const handleNotesChange = useCallback(
    (e: React.ChangeEvent<HTMLTextAreaElement>) => {
      const value = e.target.value;

      if (value.length > MAX_NOTES_LENGTH) {
        setError(`Notes must be ${MAX_NOTES_LENGTH} characters or less`);
        return;
      }

      setLocalNotes(value);
      setError(null);

      // Clear existing timeout
      if (autoSaveTimeoutRef.current) {
        clearTimeout(autoSaveTimeoutRef.current);
      }

      // Auto-save after 1 second of inactivity
      if (isEditing) {
        autoSaveTimeoutRef.current = setTimeout(() => {
          onNotesChange(value);
        }, 1000);
      }
    },
    [isEditing, onNotesChange]
  );

  const handleBlur = useCallback(() => {
    // Save immediately on blur
    if (autoSaveTimeoutRef.current) {
      clearTimeout(autoSaveTimeoutRef.current);
    }
    onNotesChange(localNotes);
  }, [localNotes, onNotesChange]);

  const charactersRemaining = MAX_NOTES_LENGTH - localNotes.length;
  const isNearLimit = charactersRemaining < 50;

  if (isEditing) {
    return (
      <div className="notes-editor editing">
        <textarea
          value={localNotes}
          onChange={handleNotesChange}
          onBlur={handleBlur}
          placeholder="Add notes about this job (e.g., special instructions, known issues, etc.)"
          maxLength={MAX_NOTES_LENGTH}
          rows={4}
          aria-label="Job notes"
          aria-invalid={!!error}
          aria-describedby={error ? 'notes-error' : 'notes-help'}
        />

        <div className="notes-metadata">
          <div className={`character-count ${isNearLimit ? 'warning' : ''}`}>
            {localNotes.length} / {MAX_NOTES_LENGTH}
            {isNearLimit && charactersRemaining >= 0 && (
              <span className="remaining-text">
                ({charactersRemaining} characters remaining)
              </span>
            )}
          </div>

          {error && (
            <div id="notes-error" className="error-message" role="alert">
              {error}
            </div>
          )}

          {!error && (
            <div id="notes-help" className="help-text">
              Notes are auto-saved as you type
            </div>
          )}
        </div>
      </div>
    );
  }

  return (
    <div className="notes-editor view-only">
      {localNotes ? (
        <div className="notes-content">
          <p>{localNotes}</p>
        </div>
      ) : (
        <p className="empty-message">No notes added</p>
      )}
    </div>
  );
};

export default JobNotesEditor;

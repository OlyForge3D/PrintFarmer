import React, { useState, useCallback, useRef, useEffect } from 'react';
import { Textarea } from '@/common/components/ui/Textarea';

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
      <div className="space-y-2">
        <Textarea
          value={localNotes}
          onChange={handleNotesChange}
          onBlur={handleBlur}
          placeholder="Add notes about this job (e.g., special instructions, known issues, etc.)"
          maxLength={MAX_NOTES_LENGTH}
          rows={4}
          aria-label="Job notes"
          aria-invalid={!!error}
          aria-describedby={error ? 'notes-error' : 'notes-help'}
          className="w-full"
        />

        <div className="flex items-center justify-between text-xs">
          <span className={`${isNearLimit ? 'text-pf-warning' : 'text-pf-text-muted'}`}>
            {localNotes.length} / {MAX_NOTES_LENGTH}
            {isNearLimit && charactersRemaining >= 0 && (
              <span className="ml-1">
                ({charactersRemaining} remaining)
              </span>
            )}
          </span>

          {error && (
            <span id="notes-error" className="text-pf-error" role="alert">
              {error}
            </span>
          )}

          {!error && (
            <span id="notes-help" className="text-pf-text-muted italic">
              Auto-saved as you type
            </span>
          )}
        </div>
      </div>
    );
  }

  return (
    <div className="min-h-[60px]">
      {localNotes ? (
        <p className="text-sm text-pf-text-primary whitespace-pre-wrap">{localNotes}</p>
      ) : (
        <p className="text-sm text-pf-text-muted italic">No notes added</p>
      )}
    </div>
  );
};

export default JobNotesEditor;

import React from 'react';
import { Button } from '@/common/components/ui/Button';
import { TagDto } from '../services/tagService';

export interface TagDisplayProps {
  /** The tag to display */
  tag: TagDto;
  /** Optional callback when remove button is clicked */
  onRemove?: (tagId: string) => void;
  /** Whether to show remove button */
  showRemoveButton?: boolean;
  /** Optional click handler for the tag itself */
  onClick?: (tag: TagDto) => void;
  /** CSS class for additional styling */
  className?: string;
  /** Whether the tag is disabled */
  disabled?: boolean;
}

/**
 * TagDisplay Component
 * 
 * Displays a single tag with optional remove button and click handler.
 * Includes accessibility features (ARIA labels, keyboard support).
 * 
 * Features:
 * - Visual tag with design tokens
 * - Optional inline remove button
 * - Hover effects and transitions
 * - Click handler for filtering
 * - Full keyboard accessibility
 * - WCAG 2.2 AA compliant
 */
export const TagDisplay: React.FC<TagDisplayProps> = ({
  tag,
  onRemove,
  showRemoveButton = false,
  onClick,
  className = '',
  disabled = false,
}) => {
  const handleRemoveClick = (e: React.MouseEvent) => {
    e.stopPropagation();
    onRemove?.(tag.id);
  };

  const handleKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === 'Enter' && !disabled) {
      if (e.ctrlKey || e.metaKey) {
        // Ctrl/Cmd+Enter to remove
        onRemove?.(tag.id);
      } else {
        // Enter to click
        onClick?.(tag);
      }
    } else if (e.key === 'Delete' && showRemoveButton && !disabled) {
      onRemove?.(tag.id);
    }
  };

  const handleRemoveKeyDown = (e: React.KeyboardEvent) => {
    e.stopPropagation();
    if (e.key === 'Enter' || e.key === ' ') {
      e.preventDefault();
      onRemove?.(tag.id);
    }
  };

  const tagStyles: React.CSSProperties = {
    backgroundColor: tag.color || '#6366f1',
    color: 'white',
  };

  return (
    <div
      className={`inline-flex items-center gap-2 px-3 py-1 rounded-full text-sm font-medium transition-all duration-200 ${
        disabled ? 'opacity-50 cursor-not-allowed' : 'hover:shadow-md cursor-default'
      } ${className}`}
      style={tagStyles}
      role="status"
      aria-label={`Tag: ${tag.name}${tag.description ? ` - ${tag.description}` : ''}`}
      title={tag.description}
    >
      <span
        onClick={() => !disabled && onClick?.(tag)}
        onKeyDown={handleKeyDown}
        role="button"
        tabIndex={disabled ? -1 : 0}
        className={`${!disabled && onClick ? 'cursor-pointer hover:underline' : ''}`}
      >
        {tag.name}
      </span>

      {showRemoveButton && (
        <Button
          onClick={handleRemoveClick}
          onKeyDown={handleRemoveKeyDown}
          className="ml-1 inline-flex items-center justify-center w-4 h-4 rounded-full hover:bg-white/30 transition-colors duration-150"
          aria-label={`Remove tag ${tag.name}`}
          title={`Remove ${tag.name}`}
          disabled={disabled}
          variant="subtle"
          size="sm"
        >
          <span className="text-xs font-bold leading-none">×</span>
        </Button>
      )}
    </div>
  );
};

export default TagDisplay;

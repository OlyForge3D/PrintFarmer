import React from 'react';
import { TagChip } from '@/common/components/ui/TagChip';
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
  const statusLabel = `Tag: ${tag.name}${tag.description ? ` - ${tag.description}` : ''}`;

  if (showRemoveButton) {
    return (
      <TagChip
        mode="removable"
        label={tag.name}
        color={tag.color || '#6366f1'}
        size="md"
        className={className}
        title={tag.description}
        disabled={disabled}
        ariaLabel={statusLabel}
        onClick={onClick ? () => onClick(tag) : undefined}
        onRemove={() => onRemove?.(tag.id)}
        removeLabel={`Remove tag ${tag.name}`}
      />
    );
  }

  if (onClick) {
    return (
      <TagChip
        mode="action"
        label={tag.name}
        color={tag.color || '#6366f1'}
        size="md"
        className={className}
        title={tag.description}
        disabled={disabled}
        ariaLabel={statusLabel}
        onClick={() => onClick(tag)}
      />
    );
  }

  return (
    <TagChip
      label={tag.name}
      color={tag.color || '#6366f1'}
      size="md"
      className={className}
      title={tag.description}
      disabled={disabled}
      ariaLabel={statusLabel}
    />
  );
};

export default TagDisplay;

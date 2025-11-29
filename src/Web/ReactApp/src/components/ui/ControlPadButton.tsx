import React from 'react';
import { Button, ButtonProps } from './Button';

export interface ControlPadButtonProps extends ButtonProps {
  /** Size of the button in the control pad */
  padSize?: 'small' | 'medium' | 'large';
}

/**
 * Specialized button component for control pads (XY/Z movement, homing)
 * Used in printer control interfaces for movement buttons
 * Features: Flexible sizing, proper padding/height for control pads, secondary variant default
 */
export function ControlPadButton({
  padSize = 'medium',
  variant = 'secondary',
  className = '',
  ...props
}: ControlPadButtonProps) {
  const sizeClasses = {
    small: 'w-8 h-8 p-0',
    medium: 'w-11 h-11 p-0 flex items-center justify-center',
    large: 'w-full h-full p-0 flex items-center justify-center',
  };

  return (
    <Button
      variant={variant}
      size="sm"
      className={`${sizeClasses[padSize]} !p-0 ${className}`}
      {...props}
    />
  );
}

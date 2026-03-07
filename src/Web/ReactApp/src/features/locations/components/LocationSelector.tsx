import React from 'react';
import { LocationTreePicker } from '@/features/locations/components/LocationTreePicker';

export interface LocationSelectorProps {
  value?: string;
  onChange: (locationId: string | null) => void;
  label?: string;
  required?: boolean;
  disabled?: boolean;
}

/**
 * LocationSelector wraps LocationTreePicker for backward compatibility.
 * All new code should use LocationTreePicker directly.
 */
export const LocationSelector: React.FC<LocationSelectorProps> = ({
  value,
  onChange,
  label = 'Location',
  required = false,
  disabled = false,
}) => {
  return (
    <LocationTreePicker
      value={value ?? null}
      onChange={onChange}
      label={label}
      required={required}
      disabled={disabled}
      placeholder={required ? 'Select a location' : 'No location (unassigned)'}
    />
  );
};

export default LocationSelector;

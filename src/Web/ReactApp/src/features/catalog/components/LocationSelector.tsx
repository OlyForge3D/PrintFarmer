import React, { useState, useEffect } from 'react';
import { Select } from '@/common/components/ui/Select';
import { Location, locationService } from '@/services/locationService';

export interface LocationSelectorProps {
  value?: string;
  onChange: (locationId: string | null) => void;
  label?: string;
  required?: boolean;
  disabled?: boolean;
}

export const LocationSelector: React.FC<LocationSelectorProps> = ({
  value,
  onChange,
  label = 'Location',
  required = false,
  disabled = false,
}) => {
  const [locations, setLocations] = useState<Location[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    loadLocations();
  }, []);

  const loadLocations = async () => {
    try {
      setLoading(true);
      setError(null);
      const data = await locationService.getAllLocations();
      setLocations(data);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to load locations');
    } finally {
      setLoading(false);
    }
  };

  return (
    <div>
      <label htmlFor="location" className="block text-sm font-medium text-pf-text-primary">
        {label}
        {required && <span style={{ color: 'var(--pf-error)' }} className="ml-1">*</span>}
      </label>
      <Select
        id="location"
        value={value || ''}
        onChange={(e) => onChange(e.target.value || null)}
        disabled={disabled || loading}
      >
        <option value="">
          {required ? 'Select a location' : 'No location (unassigned)'}
        </option>
        {locations.map((location) => (
          <option key={location.id} value={location.id}>
            {location.name} ({location.printerCount} printers)
          </option>
        ))}
      </Select>
      {error && <p className="mt-1 text-sm" style={{ color: 'var(--pf-error)' }}>{error}</p>}
    </div>
  );
};

export default LocationSelector;

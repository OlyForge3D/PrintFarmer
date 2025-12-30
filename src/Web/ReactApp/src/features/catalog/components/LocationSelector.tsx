import React, { useState, useEffect } from 'react';
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
      <label htmlFor="location" className="block text-sm font-medium text-gray-700">
        {label}
        {required && <span className="text-red-500 ml-1">*</span>}
      </label>
      <select
        id="location"
        value={value || ''}
        onChange={(e) => onChange(e.target.value || null)}
        disabled={disabled || loading}
        className="mt-1 block w-full border border-gray-300 rounded-md shadow-sm py-2 px-3 focus:outline-none focus:ring-blue-500 focus:border-blue-500 disabled:opacity-50"
      >
        <option value="">
          {required ? 'Select a location' : 'No location (unassigned)'}
        </option>
        {locations.map((location) => (
          <option key={location.id} value={location.id}>
            {location.name} ({location.printerCount} printers)
          </option>
        ))}
      </select>
      {error && <p className="mt-1 text-sm text-red-600">{error}</p>}
    </div>
  );
};

export default LocationSelector;

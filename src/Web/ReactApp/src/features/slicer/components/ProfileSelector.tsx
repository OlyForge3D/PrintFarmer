/* eslint-disable local/pf-no-raw-html-controls */
import React, { useMemo } from 'react';
import { HierarchicalProfilesResponse } from '@/services/slicerProfilesService';

interface ProfileSelectorProps {
  hierarchyData?: HierarchicalProfilesResponse;
  selectedProfileId: string;
  onChange: (profileId: string) => void;
  disabled?: boolean;
  className?: string;
}

/**
 * Profile selector component that displays profiles organized by manufacturer and model hierarchy
 */
export const ProfileSelector: React.FC<ProfileSelectorProps> = ({
  hierarchyData,
  selectedProfileId,
  onChange,
  disabled = false,
  className
}) => {
  // Flatten all process profiles with hierarchy context for display
  const profileOptions = useMemo(() => {
    if (!hierarchyData?.byHierarchy) return [];

    const options: Array<{
      id: string;
      name: string;
      label: string;
      manufacturer: string;
      model: string;
    }> = [];

    // Walk through manufacturer → model → profiles hierarchy
    for (const [manufacturer, mfgData] of Object.entries(hierarchyData.byHierarchy)) {
      for (const modelData of Object.values(mfgData.models)) {
        for (const profile of modelData.processProfiles) {
          options.push({
            id: profile.id,
            name: profile.name,
            label: `${manufacturer} › ${modelData.name} › ${profile.name}`,
            manufacturer,
            model: modelData.name,
          });
        }
      }
    }

    return options;
  }, [hierarchyData]);

  // Group options by manufacturer and model for optgroup rendering
  const groupedOptions = useMemo(() => {
    const groups: Record<string, Record<string, typeof profileOptions>> = {};

    for (const option of profileOptions) {
      if (!groups[option.manufacturer]) {
        groups[option.manufacturer] = {};
      }
      if (!groups[option.manufacturer][option.model]) {
        groups[option.manufacturer][option.model] = [];
      }
      groups[option.manufacturer][option.model].push(option);
    }

    return groups;
  }, [profileOptions]);

  return (
    <select
      value={selectedProfileId}
      onChange={e => onChange(e.target.value)}
      disabled={disabled}
      className={`w-full ${className ?? ''}`}
    >
      <option value="">-- Select Process Profile --</option>
      
      {Object.entries(groupedOptions).map(([manufacturer, models]) => (
        <optgroup key={manufacturer} label={manufacturer}>
          {Object.entries(models).map(([modelName, profiles]) => (
            <optgroup key={`${manufacturer}-${modelName}`} label={`  ${modelName}`}>
              {profiles.map(profile => (
                <option key={profile.id} value={profile.id}>
                  {profile.name}
                </option>
              ))}
            </optgroup>
          ))}
        </optgroup>
      ))}
    </select>
  );
};

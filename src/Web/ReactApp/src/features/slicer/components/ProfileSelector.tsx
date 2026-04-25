/* eslint-disable local/pf-no-raw-html-controls */
import React, { useMemo } from 'react';
import type { HierarchicalProfilesResponse, ProcessProfileListItem } from '@/services/slicerProfilesService';

interface ProfileSelectorProps {
  hierarchyData?: HierarchicalProfilesResponse;
  selectedProfileId: string;
  onChange: (profileId: string) => void;
  disabled?: boolean;
  className?: string;
}

interface ProfileOption {
  id: string;
  name: string;
  isSystem: boolean;
}

/**
 * Profile selector component that displays profiles grouped by User/System presets.
 * User presets appear first, system presets second. Within each group, order is preserved.
 */
export const ProfileSelector: React.FC<ProfileSelectorProps> = ({
  hierarchyData,
  selectedProfileId,
  onChange,
  disabled = false,
  className
}) => {
  // Flatten all process profiles from the hierarchy
  const allProfiles = useMemo(() => {
    if (!hierarchyData?.byHierarchy) return [];

    const profiles: ProfileOption[] = [];

    for (const mfgData of Object.values(hierarchyData.byHierarchy)) {
      for (const modelData of Object.values(mfgData.models)) {
        for (const profile of modelData.processProfiles) {
          profiles.push({
            id: profile.id,
            name: profile.name,
            isSystem: (profile as ProcessProfileListItem).isSystem ?? true,
          });
        }
      }
    }

    return profiles;
  }, [hierarchyData]);

  // Split into User and System groups
  const { user, system } = useMemo(() => {
    const u: ProfileOption[] = [];
    const s: ProfileOption[] = [];
    for (const p of allProfiles) {
      (p.isSystem ? s : u).push(p);
    }
    return { user: u, system: s };
  }, [allProfiles]);

  return (
    <select
      value={selectedProfileId}
      onChange={e => onChange(e.target.value)}
      disabled={disabled}
      className={`w-full ${className ?? ''}`}
    >
      <option value="">-- Select Process Profile --</option>

      {user.length > 0 && (
        <optgroup label="User Presets">
          {user.map(p => (
            <option key={p.id} value={p.id}>{p.name}</option>
          ))}
        </optgroup>
      )}
      {system.length > 0 && (
        <optgroup label="System Presets">
          {system.map(p => (
            <option key={p.id} value={p.id}>{p.name}</option>
          ))}
        </optgroup>
      )}
    </select>
  );
};

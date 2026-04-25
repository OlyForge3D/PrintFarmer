import React, { useMemo } from 'react';
import { Select } from '@/common/components/ui';
import { ProfileSelector } from '@/features/slicer/components/ProfileSelector';
import type { ProcessProfileListItem, HierarchicalProfilesResponse } from './types';

interface ProcessProfileSelectorProps {
  /** Available process profiles filtered by machine (may be empty) */
  availableProcessProfiles: ProcessProfileListItem[];
  /** Full hierarchy data for fallback ProfileSelector */
  hierarchyProfiles: HierarchicalProfilesResponse | undefined;
  /** Fallback flat list of process profiles */
  printerProcessProfiles: ProcessProfileListItem[];
  /** Selected process preset ID */
  selectedProcessPresetId: string;
  /** Callback when process profile changes */
  onProcessProfileChange: (profileId: string) => void;
  /** Optional CSS class name */
  className?: string;
}

/** Split profiles into User (first) and System (second) groups, preserving order within each. */
function groupByUserSystem(profiles: ProcessProfileListItem[]) {
  const user: ProcessProfileListItem[] = [];
  const system: ProcessProfileListItem[] = [];
  for (const p of profiles) {
    (p.isSystem ? system : user).push(p);
  }
  return { user, system };
}

function renderProfileOption(profile: ProcessProfileListItem) {
  return (
    <option key={profile.id} value={profile.id}>
      {profile.name} - {profile.quality} ({profile.layerHeight}mm)
    </option>
  );
}

/**
 * Process profile selection component.
 * Shows filtered profiles if available, otherwise falls back to hierarchical or flat list.
 * Profiles are grouped by User presets (first) then System presets.
 */
export const ProcessProfileSelector: React.FC<ProcessProfileSelectorProps> = ({
  availableProcessProfiles,
  hierarchyProfiles,
  printerProcessProfiles,
  selectedProcessPresetId,
  onProcessProfileChange,
  className
}) => {
  const hasFilteredProfiles = availableProcessProfiles.length > 0;

  const filteredGroups = useMemo(
    () => groupByUserSystem(availableProcessProfiles),
    [availableProcessProfiles]
  );

  const fallbackGroups = useMemo(
    () => groupByUserSystem(printerProcessProfiles),
    [printerProcessProfiles]
  );

  return (
    <div className={`bg-pf-panel border border-pf-border rounded-lg p-4 ${className ?? ''}`}>
      <label className="block text-sm font-semibold text-pf-text-primary mb-2">Process Profile</label>
      {hasFilteredProfiles ? (
        <Select
          value={selectedProcessPresetId}
          onChange={e => onProcessProfileChange(e.target.value)}
          className="w-full"
        >
          <option value="">-- Select Process Profile --</option>
          {filteredGroups.user.length > 0 && (
            <optgroup label="User Presets">
              {filteredGroups.user.map(renderProfileOption)}
            </optgroup>
          )}
          {filteredGroups.system.length > 0 && (
            <optgroup label="System Presets">
              {filteredGroups.system.map(renderProfileOption)}
            </optgroup>
          )}
        </Select>
      ) : hierarchyProfiles ? (
        <ProfileSelector
          hierarchyData={hierarchyProfiles}
          selectedProfileId={selectedProcessPresetId}
          onChange={onProcessProfileChange}
        />
      ) : (
        <Select
          value={selectedProcessPresetId}
          onChange={e => onProcessProfileChange(e.target.value)}
          className="w-full"
        >
          <option value="">-- Select Process Profile --</option>
          {fallbackGroups.user.length > 0 && (
            <optgroup label="User Presets">
              {fallbackGroups.user.map(p => (
                <option key={p.id} value={p.id}>{p.name}</option>
              ))}
            </optgroup>
          )}
          {fallbackGroups.system.length > 0 && (
            <optgroup label="System Presets">
              {fallbackGroups.system.map(p => (
                <option key={p.id} value={p.id}>{p.name}</option>
              ))}
            </optgroup>
          )}
        </Select>
      )}
    </div>
  );
};

export default ProcessProfileSelector;

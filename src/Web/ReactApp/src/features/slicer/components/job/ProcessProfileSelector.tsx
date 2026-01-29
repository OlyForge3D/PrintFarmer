import React from 'react';
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

/**
 * Process profile selection component.
 * Shows filtered profiles if available, otherwise falls back to hierarchical or flat list.
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

  return (
    <div className={`bg-pf-panel border border-pf-border rounded-lg p-4 ${className ?? ''}`}>
      <label className="block text-sm font-semibold text-pf-text mb-2">Process Profile</label>
      {hasFilteredProfiles ? (
        <Select
          value={selectedProcessPresetId}
          onChange={e => onProcessProfileChange(e.target.value)}
          className="w-full"
        >
          <option value="">-- Select Process Profile --</option>
          {availableProcessProfiles.map(profile => (
            <option key={profile.id} value={profile.id}>
              {profile.name} - {profile.quality} ({profile.layerHeight}mm)
            </option>
          ))}
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
          {printerProcessProfiles.map(p => (
            <option key={p.id} value={p.id}>{p.name}</option>
          ))}
        </Select>
      )}
    </div>
  );
};

export default ProcessProfileSelector;

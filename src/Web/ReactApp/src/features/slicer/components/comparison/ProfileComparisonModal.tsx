import React, { useState, useMemo } from 'react';
import { Modal } from '@/common/components/modals/Modal';
import { Button, Select, FormField } from '@/common/components/ui';
import { RefreshIcon } from '@/common/components/icons/MdiIcons';
import { ProfileDiffView } from './ProfileDiffView';
import { useProfileSchema } from '../settings/schema/useProfileSchema';

export interface ProfileComparisonModalProps {
  isOpen: boolean;
  onClose: () => void;
  profileType: 'process' | 'machine' | 'filament';
  profiles: Array<{ id: string; name: string; [key: string]: unknown }>;
  initialLeftId?: string;
  initialRightId?: string;
}

export function ProfileComparisonModal({
  isOpen,
  onClose,
  profileType,
  profiles,
  initialLeftId,
  initialRightId,
}: ProfileComparisonModalProps) {
  // Key-based reset: remount inner content when modal opens with new initial IDs
  const resetKey = `${isOpen}-${initialLeftId}-${initialRightId}`;

  return (
    <Modal
      isOpen={isOpen}
      onClose={onClose}
      title={`Compare ${profileType.charAt(0).toUpperCase() + profileType.slice(1)} Profiles`}
      size="full"
      className="h-[90vh]"
    >
      <ProfileComparisonContent
        key={resetKey}
        profileType={profileType}
        profiles={profiles}
        initialLeftId={initialLeftId || ''}
        initialRightId={initialRightId || ''}
      />
    </Modal>
  );
}

interface ProfileComparisonContentProps {
  profileType: 'process' | 'machine' | 'filament';
  profiles: Array<{ id: string; name: string; [key: string]: unknown }>;
  initialLeftId: string;
  initialRightId: string;
}

function ProfileComparisonContent({
  profileType,
  profiles,
  initialLeftId,
  initialRightId,
}: ProfileComparisonContentProps) {
  const [leftId, setLeftId] = useState<string>(initialLeftId);
  const [rightId, setRightId] = useState<string>(initialRightId);

  const { data: schema } = useProfileSchema(profileType);

  const leftProfile = useMemo(() => {
    return profiles.find((p) => p.id === leftId);
  }, [profiles, leftId]);

  const rightProfile = useMemo(() => {
    return profiles.find((p) => p.id === rightId);
  }, [profiles, rightId]);

  const canCompare = leftId && rightId && leftId !== rightId;

  const handleSwap = () => {
    const temp = leftId;
    setLeftId(rightId);
    setRightId(temp);
  };

  const profileOptions = profiles.map((p) => ({
    value: p.id,
    label: p.name,
  }));

  return (
    <div className="flex h-full flex-col gap-4">
      {/* Selection controls */}
      <div className="flex items-end gap-3 border-b border-pf-border pb-4">
        <FormField label="First Profile" htmlFor="left-profile" required className="flex-1">
          <Select
            id="left-profile"
            value={leftId}
            onChange={(e) => setLeftId(e.target.value)}
            containerClassName="w-full"
          >
            <option value="">Select a profile...</option>
            {profileOptions
              .filter((opt) => opt.value !== rightId)
              .map((opt) => (
                <option key={opt.value} value={opt.value}>
                  {opt.label}
                </option>
              ))}
          </Select>
        </FormField>

        <Button
          variant="subtle"
          size="md"
          onClick={handleSwap}
          disabled={!leftId || !rightId}
          iconLeft={<RefreshIcon />}
          className="mb-1"
          title="Swap profiles"
        >
          Swap
        </Button>

        <FormField label="Second Profile" htmlFor="right-profile" required className="flex-1">
          <Select
            id="right-profile"
            value={rightId}
            onChange={(e) => setRightId(e.target.value)}
            containerClassName="w-full"
          >
            <option value="">Select a profile...</option>
            {profileOptions
              .filter((opt) => opt.value !== leftId)
              .map((opt) => (
                <option key={opt.value} value={opt.value}>
                  {opt.label}
                </option>
              ))}
          </Select>
        </FormField>
      </div>

      {/* Comparison view */}
      <div className="flex-1 overflow-auto">
        {!canCompare && (
          <div className="flex h-full items-center justify-center text-pf-text-secondary">
            <div className="text-center">
              <p className="text-lg font-medium">Select two different profiles to compare</p>
              <p className="mt-2 text-sm">
                Choose profiles from the dropdowns above to see their differences
              </p>
            </div>
          </div>
        )}

        {canCompare && leftProfile && rightProfile && (
          <ProfileDiffView
            profileType={profileType}
            leftProfile={leftProfile}
            rightProfile={rightProfile}
            leftLabel={leftProfile.name}
            rightLabel={rightProfile.name}
            schema={schema}
            showOnlyDifferences={false}
          />
        )}
      </div>
    </div>
  );
}

/**
 * ProcessProfileEditorModal - Modal wrapper for editing process (print) profiles.
 *
 * Follows the same pattern as ProfileEditorModal (machine/filament) but
 * specialised for OrcaProcessProfile:
 *  - Accepts a process profile (name + settings dict)
 *  - Renders MetadataProfileEditor with profileType="process"
 *  - Tracks changes with save/cancel + "Save as new custom profile" workflow
 */

import React, { useState, useCallback } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { Modal } from '@/common/components/modals/Modal';
import { Button, Input, Alert } from '@/common/components/ui';
import { MetadataProfileEditor } from '@/features/slicer/components/settings/MetadataProfileRenderer';
import { slicerProfilesService } from '@/services/slicerProfilesService';
import type { OrcaProcessProfile } from '@/services/slicerProfilesService';

interface ProcessProfileEditorModalProps {
  isOpen: boolean;
  onClose: () => void;
  /** Original process profile to edit */
  originalProfile: OrcaProcessProfile | null;
  /** Current live settings from the inline panel — used as initial state so inline tweaks aren't lost */
  currentSettings?: Record<string, unknown>;
  /** Fires after a custom profile is saved successfully */
  onSaveSuccess?: (profileId: string, profileName: string) => void;
  /** Fires when the user applies setting edits without saving a new profile */
  onApply?: (settings: Record<string, unknown>) => void;
}

function extractSettings(profile: OrcaProcessProfile | null): Record<string, unknown> {
  if (!profile) return {};
  return {
    layer_height: profile.layerHeight,
    sparse_infill_density: profile.infillPercentage,
    outer_wall_speed: profile.printSpeed,
    enable_support: profile.supports,
    ...(profile.settings ?? {}),
  };
}

export function ProcessProfileEditorModal({
  isOpen,
  onClose,
  originalProfile,
  currentSettings: currentSettingsProp,
  onSaveSuccess,
  onApply,
}: ProcessProfileEditorModalProps) {
  const queryClient = useQueryClient();

  const [profileName, setProfileName] = useState('');
  const [showSaveForm, setShowSaveForm] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);

  const [settings, setSettings] = useState<Record<string, unknown>>({});
  const [originalSettings, setOriginalSettings] = useState<Record<string, unknown>>({});
  const [hasChanges, setHasChanges] = useState(false);

  // Re-initialise state each time the modal opens
  React.useEffect(() => {
    if (isOpen) {
      const baseName = originalProfile?.name || 'Custom Process';
      setProfileName(`${baseName} (Custom)`);
      setShowSaveForm(false);
      setSaveError(null);
      setHasChanges(false);
      const baseSettings = extractSettings(originalProfile);
      // Prefer current live settings (from inline panel) so user tweaks aren't lost
      const initial = currentSettingsProp
        ? { ...baseSettings, ...currentSettingsProp }
        : baseSettings;
      setSettings(initial);
      setOriginalSettings(baseSettings);
    }
  }, [isOpen, originalProfile, currentSettingsProp]);

  const handleUpdate = useCallback((key: string, value: unknown) => {
    setSettings((prev) => ({ ...prev, [key]: value }));
    setHasChanges(true);
  }, []);

  // Save as new custom profile
  const saveMutation = useMutation({
    mutationFn: async () => {
      return slicerProfilesService.uploadProfile({
        name: profileName.trim(),
        profileType: 'process',
        rawJson: JSON.stringify(settings),
      });
    },
    onSuccess: (response) => {
      queryClient.invalidateQueries({ queryKey: ['customProfiles'] });
      queryClient.invalidateQueries({ queryKey: ['processProfilesForMachines'] });
      onSaveSuccess?.(response.id, response.name);
      onClose();
    },
    onError: (error: Error) => {
      setSaveError(error.message || 'Failed to save profile');
    },
  });

  const handleSave = () => {
    if (!profileName.trim()) {
      setSaveError('Profile name is required');
      return;
    }
    setSaveError(null);
    saveMutation.mutate();
  };

  const handleApply = () => {
    onApply?.(settings);
    onClose();
  };

  const modalTitle = `Edit Process Profile${originalProfile?.name ? `: ${originalProfile.name}` : ''}`;

  return (
    <Modal
      isOpen={isOpen}
      onClose={onClose}
      title={modalTitle}
      width="max-w-5xl"
      maxHeight="max-h-[95vh]"
      footer={
        <div className="flex items-center justify-between w-full">
          <div className="text-sm text-pf-text-muted">
            {hasChanges && <span className="text-pf-warning">Settings modified</span>}
          </div>
          <div className="flex gap-2">
            <Button variant="secondary" onClick={onClose}>
              Cancel
            </Button>
            {!showSaveForm ? (
              <>
                {onApply && (
                  <Button variant="primary" onClick={handleApply} disabled={!hasChanges}>
                    Apply
                  </Button>
                )}
                <Button
                  variant="primary"
                  onClick={() => setShowSaveForm(true)}
                  disabled={!hasChanges}
                >
                  Save as Custom Profile
                </Button>
              </>
            ) : (
              <>
                <Button variant="secondary" onClick={() => { setShowSaveForm(false); setSaveError(null); }}>
                  Back
                </Button>
                <Button variant="primary" onClick={handleSave} loading={saveMutation.isPending}>
                  Save
                </Button>
              </>
            )}
          </div>
        </div>
      }
    >
      {/* Save-as form */}
      {showSaveForm && (
        <div className="mb-4 p-4 bg-pf-panel-secondary rounded-lg border border-pf-border">
          <label className="block text-sm font-medium text-pf-text-primary mb-2">
            Custom Profile Name
          </label>
          <Input
            type="text"
            value={profileName}
            onChange={(e) => setProfileName(e.target.value)}
            placeholder="Enter profile name..."
            className="mb-2"
          />
          {saveError && (
            <Alert type="error" className="mt-2">{saveError}</Alert>
          )}
          <p className="text-xs text-pf-text-muted mt-2">
            Your custom process profile will be saved and appear in the &quot;My Profiles&quot; section.
          </p>
        </div>
      )}

      {/* Profile editor — metadata-driven */}
      <div className="flex flex-col" style={{ height: 'calc(95vh - 200px)' }}>
        <MetadataProfileEditor
          profileType="process"
          settings={settings}
          originalSettings={originalSettings}
          onUpdate={handleUpdate}
        />
      </div>
    </Modal>
  );
}

export default ProcessProfileEditorModal;

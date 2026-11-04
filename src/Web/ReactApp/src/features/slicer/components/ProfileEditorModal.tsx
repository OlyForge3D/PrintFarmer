/**
 * ProfileEditorModal - Modal wrapper for editing slicer profiles (Machine, Filament)
 * 
 * This modal provides:
 * - OrcaSlicer-style profile editing interface
 * - Edit existing profile settings with visual change tracking
 * - Update user profiles or save a new custom copy
 * - Reset individual settings to original values
 * 
 * Supports two profile types:
 * - Machine: Printer hardware settings (bed size, nozzle, gcode flavor)
 * - Filament: Material settings (temperature, cooling, retraction)
 * 
 * Note: Process profiles are edited inline in NewSliceJobPage, not via this modal.
 */

import React, { useState, useMemo } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { Modal } from '@/common/components/modals/Modal';
import { Button, Input, Alert } from '@/common/components/ui';
import { MetadataProfileEditor } from '@/features/slicer/components/settings/MetadataProfileRenderer';
import {
  hydrateFilamentProfileSettings,
  serializeFilamentProfileSettings,
} from '@/features/slicer/components/filamentProfileEditorAdapter';
import {
  MACHINE_PROFILE_ADDITIONAL_SECTIONS,
  MACHINE_PROFILE_ADDITIONAL_SETTINGS,
  MACHINE_PROFILE_COMMON_SIMPLE_KEYS,
  areProfileSettingsEqual,
  hydrateMachineProfileSettings,
  serializeMachineProfileSettings,
} from '@/features/slicer/components/machineProfileEditorAdapter';
import { slicerProfilesService } from '@/services/slicerProfilesService';
import type { CustomProfile, OrcaMachineProfile, OrcaFilamentProfile } from '@/services/slicerProfilesService';
import type { FilamentBedContext } from '@/features/slicer/utils/bedSurfaceTemperature';

export type ProfileType = 'machine' | 'filament';

interface ProfileEditorModalProps {
  /** Whether the modal is open */
  isOpen: boolean;
  /** Callback when modal closes */
  onClose: () => void;
  /** Type of profile being edited */
  profileType: ProfileType;
  /** Original profile data (for name and as starting point) */
  originalProfile: OrcaMachineProfile | OrcaFilamentProfile | null;
  /** User-owned source from the custom profile list; absent for library profiles. */
  customProfile?: CustomProfile;
  /** Callback when custom profile is saved successfully */
  onSaveSuccess?: (profileId: string, profileName: string) => void;
  filamentBedContext?: FilamentBedContext;
}

/**
 * Get default profile name for saving
 */
function getDefaultProfileName(profileType: ProfileType, originalName: string | undefined): string {
  const baseName = originalName || `Custom ${profileType.charAt(0).toUpperCase() + profileType.slice(1)}`;
  return `${baseName} (Custom)`;
}

/**
 * Extract editor values while retaining native wire settings separately for saving.
 */
function isMachineProfile(
  profile: OrcaMachineProfile | OrcaFilamentProfile,
): profile is OrcaMachineProfile {
  return !('material' in profile);
}

function extractSettings(
  profileType: ProfileType,
  profile: OrcaMachineProfile | OrcaFilamentProfile | null,
): Record<string, unknown> {
  if (!profile) return {};
  if (profileType === 'machine' && isMachineProfile(profile)) {
    return hydrateMachineProfileSettings(profile);
  }
  return hydrateFilamentProfileSettings(profile.settings ?? {});
}

function extractRawSettings(
  profile: OrcaMachineProfile | OrcaFilamentProfile | null,
): Record<string, unknown> {
  return { ...(profile?.settings ?? {}) };
}

export function ProfileEditorModal({
  isOpen,
  onClose,
  profileType,
  originalProfile,
  customProfile,
  onSaveSuccess,
  filamentBedContext,
}: ProfileEditorModalProps) {
  const queryClient = useQueryClient();
  const profileNameId = React.useId();
  
  // Profile name for saving
  const [profileName, setProfileName] = useState('');
  const [showSaveForm, setShowSaveForm] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const sourceName = customProfile?.name ?? originalProfile?.name;
  const canUpdateSource = Boolean(customProfile && !customProfile.isSystem);
  
  // Unified settings state — works for all profile types
  const [settings, setSettings] = useState<Record<string, unknown>>({});
  const [originalSettings, setOriginalSettings] = useState<Record<string, unknown>>({});
  const [rawSettings, setRawSettings] = useState<Record<string, unknown>>({});
  
  // Initialize profile name and reset state when modal opens
  React.useEffect(() => {
    if (isOpen) {
      setProfileName(getDefaultProfileName(profileType, sourceName));
      setShowSaveForm(false);
      setSaveError(null);
      setLoadError(null);
      let raw = extractRawSettings(originalProfile);
      if (customProfile) {
        try {
          const parsed: unknown = JSON.parse(customProfile.rawJson ?? '');
          if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) {
            throw new Error('Profile settings must be a JSON object');
          }
          raw = { ...parsed };
        } catch (error) {
          setSettings({});
          setOriginalSettings({});
          setRawSettings({});
          setLoadError(`Cannot load custom profile: ${error instanceof Error ? error.message : 'Invalid JSON'}`);
          return;
        }
      }
      const extracted = customProfile
        ? extractSettings(profileType, profileType === 'machine'
          ? { name: customProfile.name, manufacturer: '', settings: raw }
          : { name: customProfile.name, material: '', nozzleTemperature: 0, bedTemperature: 0, printSpeed: 0, settings: raw })
        : extractSettings(profileType, originalProfile);
      setSettings(extracted);
      setOriginalSettings(extracted);
      setRawSettings(raw);
    }
  }, [isOpen, profileType, originalProfile, customProfile, sourceName]);
  
  // Handle individual setting updates
  const handleUpdate = React.useCallback((key: string, value: unknown) => {
    setSettings((previous) => {
      const next = { ...previous };
      if (value === undefined) {
        delete next[key];
      } else {
        next[key] = value;
      }
      return next;
    });
  }, []);

  const hasChanges = useMemo(
    () => !areProfileSettingsEqual(settings, originalSettings),
    [originalSettings, settings],
  );
  
  const saveMutation = useMutation({
    mutationFn: async (saveAs: boolean) => {
      const serializedSettings = profileType === 'machine'
        ? serializeMachineProfileSettings(settings, originalSettings, rawSettings)
        : serializeFilamentProfileSettings(settings, originalSettings, rawSettings);
      if (customProfile && canUpdateSource && !saveAs) {
        return slicerProfilesService.updateCustomProfile(customProfile.id, {
          rawJson: JSON.stringify(serializedSettings),
        });
      }
      const response = await slicerProfilesService.uploadProfile({
        name: profileName.trim(),
        profileType,
        rawJson: JSON.stringify(serializedSettings),
        ...(customProfile?.printerModelId ? { printerModelId: customProfile.printerModelId } : {}),
        ...(customProfile?.compatiblePrinters ? { compatiblePrinters: customProfile.compatiblePrinters } : {}),
      });
      return response;
    },
    onSuccess: (response) => {
      // Invalidate custom profiles cache
      queryClient.invalidateQueries({ queryKey: ['customProfiles'] });
      
      // Notify parent
      if (onSaveSuccess) {
        onSaveSuccess(response.id, response.name);
      }
      
      // Close modal
      onClose();
    },
    onError: (error: Error) => {
      setSaveError(error.message || 'Failed to save profile');
    },
  });
  
  const handleSave = () => {
    if (loadError || saveMutation.isPending) return;
    if (!showSaveForm && !canUpdateSource) {
      setShowSaveForm(true);
      return;
    }
    if (showSaveForm && !profileName.trim()) {
      setSaveError('Profile name is required');
      return;
    }
    setSaveError(null);
    saveMutation.mutate(showSaveForm);
  };
  
  const handleSaveAsClick = () => {
    setSaveError(null);
    setShowSaveForm(true);
  };
  
  const handleCancelSave = () => {
    setShowSaveForm(false);
    setSaveError(null);
  };
  
  // Determine modal title based on profile type
  const modalTitle = useMemo(() => {
    switch (profileType) {
      case 'machine':
        return `Edit Machine Profile${sourceName ? `: ${sourceName}` : ''}`;
      case 'filament':
        return `Edit Filament Profile${sourceName ? `: ${sourceName}` : ''}`;
      default:
        return 'Edit Profile';
    }
  }, [profileType, sourceName]);
  
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
            {hasChanges && (
              <span className="text-pf-warning">Settings modified</span>
            )}
          </div>
          <div className="flex gap-2">
            <Button variant="secondary" onClick={onClose}>
              Cancel
            </Button>
            {!showSaveForm ? (
              <>
                {canUpdateSource && (
                  <Button variant="secondary" onClick={handleSaveAsClick} disabled={saveMutation.isPending}>
                    Save as
                  </Button>
                )}
                <Button variant="primary" onClick={handleSave} loading={saveMutation.isPending}>
                  Save
                </Button>
              </>
            ) : (
              <>
                <Button variant="secondary" onClick={handleCancelSave}>
                  Back
                </Button>
                <Button 
                  variant="primary" 
                  onClick={handleSave}
                  loading={saveMutation.isPending}
                >
                  Save
                </Button>
              </>
            )}
          </div>
        </div>
      }
    >
      {loadError && <Alert type="error">{loadError}</Alert>}
      {!showSaveForm && saveError && <Alert type="error">{saveError}</Alert>}
      {/* Save-as form */}
      {showSaveForm && (
        <div className="mb-4 p-4 bg-pf-bg-1 rounded-lg border border-pf-border">
          <label
            htmlFor={profileNameId}
            className="block text-sm font-medium text-pf-text-primary mb-2"
          >
            Custom Profile Name
          </label>
          <Input
            id={profileNameId}
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
            Your custom profile will be saved and appear in the &quot;My Profiles&quot; section.
          </p>
        </div>
      )}
      
      {/* Profile Editor Content — metadata-driven, all profile types */}
      <div className="flex flex-col h-[calc(95vh-200px)]">
        <MetadataProfileEditor
          profileType={profileType}
          settings={settings}
          originalSettings={originalSettings}
          onUpdate={handleUpdate}
          filamentBedContext={filamentBedContext}
          additionalSettings={profileType === 'machine'
            ? MACHINE_PROFILE_ADDITIONAL_SETTINGS
            : undefined}
          additionalSections={profileType === 'machine'
            ? MACHINE_PROFILE_ADDITIONAL_SECTIONS
            : undefined}
          simpleModeSettingKeys={profileType === 'machine'
            ? MACHINE_PROFILE_COMMON_SIMPLE_KEYS
            : undefined}
        />
      </div>
    </Modal>
  );
}

export default ProfileEditorModal;

/**
 * ProfileEditorModal - Modal wrapper for editing slicer profiles (Machine, Filament)
 * 
 * This modal provides:
 * - OrcaSlicer-style profile editing interface
 * - Edit existing profile settings with visual change tracking
 * - Save modifications as new custom profile
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
import { FilamentProfileEditor } from '@/features/slicer/components/settings/FilamentProfileEditor';
import type { FilamentSettingsViewMode } from '@/features/slicer/components/settings/filamentSettingsTypes';
import { 
  DEFAULT_ORCA_FILAMENT_SETTINGS, 
  type OrcaFilamentSettings,
} from '@/features/slicer/components/settings/filamentSettingsTypes';
import { MachineProfileEditor } from '@/features/slicer/components/settings/MachineProfileEditor';
import { 
  DEFAULT_ORCA_MACHINE_SETTINGS,
  type OrcaMachineSettings,
} from '@/features/slicer/components/settings/machineSettingsTypes';
import { slicerProfilesService } from '@/services/slicerProfilesService';
import type { OrcaMachineProfile, OrcaFilamentProfile } from '@/services/slicerProfilesService';
import type { MachineSettingsViewMode } from '@/features/slicer/components/settings/machineSettingsTypes';

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
  /** Callback when custom profile is saved successfully */
  onSaveSuccess?: (profileId: string, profileName: string) => void;
  /** Initial view mode for the editor panel */
  initialViewMode?: MachineSettingsViewMode;
}

/**
 * Get default profile name for saving
 */
function getDefaultProfileName(profileType: ProfileType, originalName: string | undefined): string {
  const baseName = originalName || `Custom ${profileType.charAt(0).toUpperCase() + profileType.slice(1)}`;
  return `${baseName} (Custom)`;
}

function convertOrcaMachineProfileToSettings(profile: OrcaMachineProfile | null): Partial<OrcaMachineSettings> {
  if (!profile) return DEFAULT_ORCA_MACHINE_SETTINGS;

  const profileSettings = (profile.settings ?? {}) as Record<string, unknown>;

  return {
    ...DEFAULT_ORCA_MACHINE_SETTINGS,
    printer_model: profile.printerModel ?? '',
    nozzle_diameter: profile.nozzleDiameter ?? DEFAULT_ORCA_MACHINE_SETTINGS.nozzle_diameter,
    ...profileSettings,
  };
}

function convertOrcaFilamentProfileToSettings(profile: OrcaFilamentProfile | null): Partial<OrcaFilamentSettings> {
  if (!profile) return DEFAULT_ORCA_FILAMENT_SETTINGS;

  const rawSettings = (profile.settings ?? {}) as Record<string, unknown>;

  // Normalize array values to their first element (OrcaSlicer stores some strings as ["value"])
  const profileSettings: Record<string, unknown> = {};
  for (const [key, value] of Object.entries(rawSettings)) {
    profileSettings[key] = Array.isArray(value) && value.length > 0 ? value[0] : value;
  }

  return {
    ...DEFAULT_ORCA_FILAMENT_SETTINGS,
    ...profileSettings,
    // Typed overrides AFTER spread so they take precedence over raw array values
    filament_type: profile.material || DEFAULT_ORCA_FILAMENT_SETTINGS.filament_type,
    filament_vendor: profile.manufacturer,
    nozzle_temperature: profile.nozzleTemperature ?? DEFAULT_ORCA_FILAMENT_SETTINGS.nozzle_temperature,
    hot_plate_temp: profile.bedTemperature ?? DEFAULT_ORCA_FILAMENT_SETTINGS.hot_plate_temp,
  };
}

export function ProfileEditorModal({
  isOpen,
  onClose,
  profileType,
  originalProfile,
  onSaveSuccess,
  initialViewMode = 'simple',
}: ProfileEditorModalProps) {
  const queryClient = useQueryClient();
  
  // Profile name for saving
  const [profileName, setProfileName] = useState('');
  const [showSaveForm, setShowSaveForm] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);
  
  // Settings state for each profile type
  const [machineSettings, setMachineSettings] = useState<Partial<OrcaMachineSettings>>(DEFAULT_ORCA_MACHINE_SETTINGS);
  const [filamentSettings, setFilamentSettings] = useState<Partial<OrcaFilamentSettings>>(DEFAULT_ORCA_FILAMENT_SETTINGS);
  
  // Track if settings have been modified
  const [hasChanges, setHasChanges] = useState(false);
  
  // View mode for filament editor (Simple/Advanced)
  const [filamentViewMode, setFilamentViewMode] = useState<FilamentSettingsViewMode>('simple');
  
  // Initialize profile name and reset state when modal opens
  React.useEffect(() => {
    if (isOpen) {
      setProfileName(getDefaultProfileName(profileType, originalProfile?.name));
      setShowSaveForm(false);
      setSaveError(null);
      setHasChanges(false);

      // Prefill editor with selected profile values when available
      if (profileType === 'machine') {
        setMachineSettings(convertOrcaMachineProfileToSettings(originalProfile as OrcaMachineProfile | null));
        setFilamentSettings(DEFAULT_ORCA_FILAMENT_SETTINGS);
      } else {
        setMachineSettings(DEFAULT_ORCA_MACHINE_SETTINGS);
        setFilamentSettings(convertOrcaFilamentProfileToSettings(originalProfile as OrcaFilamentProfile | null));
      }
    }
  }, [isOpen, profileType, originalProfile]);
  
  // Get current settings based on profile type
  const getCurrentSettings = React.useCallback(() => {
    switch (profileType) {
      case 'machine':
        return machineSettings;
      case 'filament':
        return filamentSettings;
      default:
        return {};
    }
  }, [profileType, machineSettings, filamentSettings]);
  
  // Save mutation using uploadProfile
  const saveMutation = useMutation({
    mutationFn: async () => {
      const settings = getCurrentSettings();
      const response = await slicerProfilesService.uploadProfile({
        name: profileName.trim(),
        profileType,
        rawJson: JSON.stringify(settings),
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
    if (!profileName.trim()) {
      setSaveError('Profile name is required');
      return;
    }
    setSaveError(null);
    saveMutation.mutate();
  };
  
  const handleSaveAsClick = () => {
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
        return `Edit Machine Profile${originalProfile?.name ? `: ${originalProfile.name}` : ''}`;
      case 'filament':
        return `Edit Filament Profile${originalProfile?.name ? `: ${originalProfile.name}` : ''}`;
      default:
        return 'Edit Profile';
    }
  }, [profileType, originalProfile]);
  
  return (
    <Modal
      isOpen={isOpen}
      onClose={onClose}
      title={modalTitle}
      width="max-w-4xl"
      maxHeight="max-h-[85vh]"
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
              <Button 
                variant="primary" 
                onClick={handleSaveAsClick}
                disabled={!hasChanges}
              >
                Save as Custom Profile
              </Button>
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
      {/* Save Form (shown when Save as Custom clicked) */}
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
            Your custom profile will be saved and appear in the &quot;My Profiles&quot; section.
          </p>
        </div>
      )}
      
      {/* Profile Editor Content */}
      <div className="min-h-100">
        {profileType === 'machine' && (
          <MachineProfileEditor
            settings={machineSettings}
            onChange={(settings) => {
              setMachineSettings(settings);
              setHasChanges(true);
            }}
            initialViewMode={initialViewMode}
          />
        )}
        
        {profileType === 'filament' && (
          <FilamentProfileEditor
            settings={filamentSettings}
            onChange={(settings) => {
              setFilamentSettings(settings);
              setHasChanges(true);
            }}
            viewMode={filamentViewMode}
            onViewModeChange={setFilamentViewMode}
          />
        )}
      </div>
    </Modal>
  );
}

export default ProfileEditorModal;

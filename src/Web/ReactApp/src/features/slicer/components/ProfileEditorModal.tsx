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
  type OrcaFilamentSettings,
} from '@/features/slicer/components/settings/filamentSettingsTypes';
import { MachineProfileEditor } from '@/features/slicer/components/settings/MachineProfileEditor';
import { 
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

/** Coerce raw settings values: unwrap arrays, parse numeric strings, convert "0"/"1" booleans */
/** Keys whose array values should be joined into a comma-separated string (not first-element) */
const ARRAY_JOIN_KEYS = new Set([
  'bed_exclude_area', 'thumbnails', 'retraction_distances_when_cut',
  'extruder_offset', 'extruder_printable_area', 'retract_lift_enforce',
  'z_hop_types', 'compatible_printers', 'compatible_prints',
]);

function coerceSettingsValues(raw: Record<string, unknown>, booleanKeys: Set<string>): Record<string, unknown> {
  const result: Record<string, unknown> = {};
  for (const [key, rawValue] of Object.entries(raw)) {
    let value: unknown;
    if (Array.isArray(rawValue)) {
      if (ARRAY_JOIN_KEYS.has(key)) {
        // Join as comma-separated string for display
        value = rawValue.join(', ');
      } else if (rawValue.length > 0) {
        value = rawValue[0];
      } else {
        // Empty array — skip this key so default value is used
        continue;
      }
    } else if (rawValue === null || rawValue === undefined) {
      continue;
    } else {
      value = rawValue;
    }
    if (typeof value === 'string') {
      if (booleanKeys.has(key)) {
        value = value === '1' || value === 'true';
      } else if (value !== '' && !isNaN(Number(value))) {
        value = Number(value);
      }
    }
    // Skip NaN values so defaults are used
    if (typeof value === 'number' && isNaN(value)) continue;
    result[key] = value;
  }
  return result;
}

function convertOrcaMachineProfileToSettings(profile: OrcaMachineProfile | null): Partial<OrcaMachineSettings> {
  if (!profile) return {};

  const machineBooleanKeys = new Set([
    'support_multi_bed_types', 'pellet_modded_printer', 'support_chamber_temp_control',
    'support_air_filtration', 'scan_first_layer', 'disable_m73', 'use_relative_e_distances',
    'use_firmware_retraction', 'fan_speedup_overhangs', 'auxiliary_fan',
    'single_extruder_multi_material', 'manual_filament_change', 'purge_in_prime_tower',
    'enable_filament_ramming', 'high_current_on_filament_swap', 'retract_when_changing_layer',
    'wipe', 'travel_slope', 'emit_machine_limits_to_gcode', 'resonance_avoidance',
    'wipe_before_external_loop',
  ]);

  const profileSettings = coerceSettingsValues(
    (profile.settings ?? {}) as Record<string, unknown>,
    machineBooleanKeys,
  );

  return {
    ...profileSettings,
    printer_model: profile.printerModel ?? '',
    nozzle_diameter: profile.nozzleDiameter,
  };
}

function convertOrcaFilamentProfileToSettings(profile: OrcaFilamentProfile | null): Partial<OrcaFilamentSettings> {
  if (!profile) return {};

  const filamentBooleanKeys = new Set([
    'enable_pressure_advance', 'adaptive_pressure_advance', 'adaptive_pressure_advance_overhangs',
    'filament_soluble', 'filament_is_support', 'activate_chamber_temp_control',
    'filament_adaptive_volumetric_speed', 'reduce_fan_stop_start_freq', 'enable_overhang_bridge_fan',
    'slow_down_for_layer_cooling', 'dont_slow_down_outer_wall', 'activate_air_filtration',
    'filament_wipe', 'filament_retract_when_changing_layer', 'filament_long_retractions_when_cut',
    'enable_volumetric_extrusion', 'set_other_flow_ratios', 'long_retractions_when_ec',
    'filament_multitool_ramming',
  ]);

  const profileSettings = coerceSettingsValues(
    (profile.settings ?? {}) as Record<string, unknown>,
    filamentBooleanKeys,
  );

  return {
    ...profileSettings,
    filament_type: profile.material || '',
    filament_vendor: profile.manufacturer,
    nozzle_temperature: profile.nozzleTemperature,
    hot_plate_temp: profile.bedTemperature,
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
  const [machineSettings, setMachineSettings] = useState<Partial<OrcaMachineSettings>>({});
  const [filamentSettings, setFilamentSettings] = useState<Partial<OrcaFilamentSettings>>({});
  
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
        setFilamentSettings({});
      } else {
        setMachineSettings({});
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
      <div>
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

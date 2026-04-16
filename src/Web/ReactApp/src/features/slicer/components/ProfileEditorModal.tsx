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
import { MetadataProfileEditor } from '@/features/slicer/components/settings/MetadataProfileRenderer';
import { slicerProfilesService } from '@/services/slicerProfilesService';
import type { OrcaMachineProfile, OrcaFilamentProfile } from '@/services/slicerProfilesService';

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
  initialViewMode?: 'simple' | 'advanced';
}

/**
 * Get default profile name for saving
 */
function getDefaultProfileName(profileType: ProfileType, originalName: string | undefined): string {
  const baseName = originalName || `Custom ${profileType.charAt(0).toUpperCase() + profileType.slice(1)}`;
  return `${baseName} (Custom)`;
}

/**
 * Extract settings from a profile DTO.
 * The backend now returns properly-typed values (arrays as List<string>,
 * scalars as strings) so no frontend coercion is needed.
 */
function extractSettings(profile: OrcaMachineProfile | OrcaFilamentProfile | null): Record<string, unknown> {
  if (!profile) return {};
  return { ...(profile.settings ?? {}) };
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
  
  // Unified settings state — works for all profile types
  const [settings, setSettings] = useState<Record<string, unknown>>({});
  
  // Track if settings have been modified
  const [hasChanges, setHasChanges] = useState(false);
  
  // Initialize profile name and reset state when modal opens
  React.useEffect(() => {
    if (isOpen) {
      setProfileName(getDefaultProfileName(profileType, originalProfile?.name));
      setShowSaveForm(false);
      setSaveError(null);
      setHasChanges(false);
      setSettings(extractSettings(originalProfile));
    }
  }, [isOpen, profileType, originalProfile]);
  
  // Handle individual setting updates
  const handleUpdate = React.useCallback((key: string, value: unknown) => {
    setSettings(prev => ({ ...prev, [key]: value }));
    setHasChanges(true);
  }, []);
  
  // Save mutation using uploadProfile
  const saveMutation = useMutation({
    mutationFn: async () => {
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
      
      {/* Profile Editor Content — metadata-driven, all profile types */}
      <div>
        <MetadataProfileEditor
          profileType={profileType}
          settings={settings}
          onUpdate={handleUpdate}
          initialViewMode={initialViewMode}
        />
      </div>
    </Modal>
  );
}

export default ProfileEditorModal;

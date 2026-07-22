import React, { useState, useMemo } from 'react';
import { useMutation, useQuery } from '@tanstack/react-query';
import { apiClient } from '@/services/api';
import { toast } from 'sonner';
import { Button, Alert, FormField } from '@/common/components/ui';
import Dropdown from '@/common/components/ui/Select';
import { LoadingIcon, CheckIcon } from '@/common/components/icons/MdiIcons';
import { Modal } from '@/common/components/modals/Modal';

interface MachineProfile {
  id: string;
  name: string;
  manufacturer: string;
}

interface CloneProfilesModalProps {
  isOpen: boolean;
  onClose: () => void;
  printerId: string;
  printerName: string;
  onSuccess?: () => void;
}

export const CloneProfilesModal: React.FC<CloneProfilesModalProps> = ({
  isOpen,
  onClose,
  printerId,
  printerName,
  onSuccess
}) => {
  const [selectedMachineId, setSelectedMachineId] = useState<string>('');

  // Fetch available machine profiles to clone from
  const { data: allProfiles, isLoading: profilesLoading } = useQuery({
    queryKey: ['slicerProfilesExtended'],
    queryFn: async () => {
      const response = await apiClient.get<{
        machineProfiles?: Array<{ id: string; name: string; manufacturer?: string }>;
      }>('/slicer/profiles');
      return response.data;
    },
    enabled: isOpen,
    staleTime: 30_000
  });

  // Extract unique machine profiles sorted by name
  const machineProfiles = useMemo<MachineProfile[]>(() => {
    if (!allProfiles?.machineProfiles) return [];
    
    const profiles = allProfiles.machineProfiles;
    
    return profiles
      .map(p => ({
        id: p.id,
        name: p.name,
        manufacturer: p.manufacturer || 'Unknown'
      }))
      .sort((a, b) => a.name.localeCompare(b.name));
  }, [allProfiles]);

  const cloneMutation = useMutation({
    mutationFn: async (sourceMachineId: string) => {
      const response = await apiClient.post<{
        totalProfilesCloned: number;
        sourceMachineName: string;
      }>('/slicer/profiles/clone-from-template', {
        sourceMachineProfileId: sourceMachineId,
        targetPrinterId: printerId
      });
      return response.data;
    },
    onSuccess: (data) => {
      toast.success(
        `Cloned ${data.totalProfilesCloned} profiles from ${data.sourceMachineName} to ${printerName}`
      );
      setSelectedMachineId('');
      onClose();
      onSuccess?.();
    },
    onError: (error: Error) => {
      toast.error(`Failed to clone profiles: ${error.message}`);
    }
  });

  const handleClone = () => {
    if (!selectedMachineId) {
      toast.error('Please select a machine profile to clone from');
      return;
    }
    cloneMutation.mutate(selectedMachineId);
  };

  const modalFooter = (
    <div className="flex gap-2">
      <Button
        onClick={onClose}
        variant="secondary"
        disabled={cloneMutation.isPending}
      >
        Cancel
      </Button>
      <Button
        onClick={handleClone}
        variant="primary"
        disabled={!selectedMachineId || profilesLoading || cloneMutation.isPending}
        iconLeft={cloneMutation.isPending ? <LoadingIcon className="animate-spin w-4 h-4" /> : <CheckIcon className="w-4 h-4" />}
      >
        {cloneMutation.isPending ? 'Cloning...' : 'Clone Profiles'}
      </Button>
    </div>
  );

  return (
    <Modal
      isOpen={isOpen}
      onClose={onClose}
      title="Clone Profiles"
      width="max-w-md"
      footer={modalFooter}
    >
              <Alert type="info" title="Clone Profiles">
                Clone process profiles from a similar machine to get started quickly with {printerName}.
              </Alert>

              {profilesLoading ? (
                <div className="flex items-center justify-center py-8">
                  <LoadingIcon className="animate-spin w-6 h-6" />
                </div>
              ) : machineProfiles.length === 0 ? (
                <Alert type="warning" title="No Machine Profiles Available">
                  There are no machine profiles available to clone from. Try importing profiles first.
                </Alert>
              ) : (
                <>
                  <FormField label="Source Machine Profile" htmlFor="source-machine-profile">
                    <Dropdown
                      id="source-machine-profile"
                      label="Source Machine Profile"
                      aria-label="Source Machine Profile"
                      title="Source Machine Profile"
                      value={selectedMachineId}
                      onChange={(e) => setSelectedMachineId(e.target.value)}
                      disabled={cloneMutation.isPending}
                    >
                      <option value="">-- Select a machine profile --</option>
                      {machineProfiles.map((machine) => (
                        <option key={machine.id} value={machine.id}>
                          {machine.name}
                        </option>
                      ))}
                    </Dropdown>
                  </FormField>

                  {cloneMutation.error && (
                    <Alert type="error" title="Clone Failed">
                      {cloneMutation.error.message}
                    </Alert>
                  )}
                </>
              )}
    </Modal>
  );
};

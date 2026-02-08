import { useMemo, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Modal } from '@/common/components/modals/Modal';
import { Button } from '@/common/components/ui/Button';
import type { Printer } from '@/types/api';
import type { CreateMaintenanceLogRequest, CreateMaintenanceScheduleRequest } from '@/types/maintenance';
import { maintenanceService } from '@/services/maintenanceService';
import { LogMaintenanceModal } from './LogMaintenanceModal';
import { CreateScheduleModal } from './CreateScheduleModal';

interface PrinterMaintenanceActionsModalProps {
  isOpen: boolean;
  printer: Printer;
  onClose: () => void;
}

type ActiveModal = 'actions' | 'log' | 'schedule';

export function PrinterMaintenanceActionsModal({ isOpen, printer, onClose }: PrinterMaintenanceActionsModalProps) {
  const queryClient = useQueryClient();
  const [activeModal, setActiveModal] = useState<ActiveModal>('actions');

  const printerId = printer.id;
  const printerName = printer.name || 'Printer';

  const shouldFetchSchedules = isOpen && (activeModal === 'actions' || activeModal === 'log');
  const { data: schedules = [] } = useQuery({
    queryKey: ['printerSchedules', printerId],
    queryFn: () => maintenanceService.getPrinterSchedules(printerId),
    enabled: shouldFetchSchedules,
  });

  const title = useMemo(() => {
    if (activeModal === 'log') return 'Log Maintenance';
    if (activeModal === 'schedule') return 'Schedule Maintenance';
    return 'Maintenance';
  }, [activeModal]);

  const closeAll = () => {
    setActiveModal('actions');
    onClose();
  };

  const handleLogSubmit = async (data: CreateMaintenanceLogRequest) => {
    await maintenanceService.createMaintenanceLog(data);
    await queryClient.invalidateQueries({ queryKey: ['printerMaintenanceLogs', printerId] });
    await queryClient.invalidateQueries({ queryKey: ['printerStatistics', printerId] });
    await queryClient.invalidateQueries({ queryKey: ['printerAlerts', printerId] });
    closeAll();
  };

  const handleScheduleSubmit = async (data: CreateMaintenanceScheduleRequest) => {
    await maintenanceService.createSchedule(data);
    await queryClient.invalidateQueries({ queryKey: ['printerSchedules', printerId] });
    closeAll();
  };

  return (
    <>
      <Modal
        isOpen={isOpen && activeModal === 'actions'}
        onClose={closeAll}
        title={title}
        size="sm"
        footer={
          <Button type="button" variant="subtle" onClick={closeAll}>
            Cancel
          </Button>
        }
      >
        <div className="space-y-4">
          <div>
            <p className="text-sm text-pf-text-secondary">{printerName}</p>
          </div>

          <div className="grid grid-cols-1 gap-2">
            <Button type="button" variant="primary" onClick={() => setActiveModal('log')}>
              Add maintenance log
            </Button>
            <Button type="button" variant="secondary" onClick={() => setActiveModal('schedule')}>
              Schedule maintenance task
            </Button>
          </div>
        </div>
      </Modal>

      <LogMaintenanceModal
        isOpen={isOpen && activeModal === 'log'}
        printerId={printerId}
        printerName={printerName}
        schedules={schedules}
        onSubmit={handleLogSubmit}
        onClose={closeAll}
      />

      <CreateScheduleModal
        isOpen={isOpen && activeModal === 'schedule'}
        printerId={printerId}
        printerName={printerName}
        onSubmit={handleScheduleSubmit}
        onClose={closeAll}
      />
    </>
  );
}

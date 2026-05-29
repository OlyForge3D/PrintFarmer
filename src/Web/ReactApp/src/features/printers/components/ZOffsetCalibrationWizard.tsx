import { useState, useCallback } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import clsx from 'clsx';
import { Modal } from '@/common/components/modals/Modal';
import { Button, Card, Alert, ProgressBar } from '@/common/components/ui';
import { apiClient } from '@/services/api';
import { queryKeys } from '@/common/hooks/useApi';
import type { CommandResult, Printer, PrinterBackendString } from '@/types/api';

const WIZARD_STEPS = [
  'Introduction',
  'Home Axes',
  'Move to Center',
  'Adjust Z-Offset',
  'Save',
  'Done',
] as const;

const Z_INCREMENTS = [0.01, 0.05, 0.1] as const;

interface ZOffsetCalibrationWizardProps {
  isOpen: boolean;
  onClose: () => void;
  printer: Printer;
  /** Bed X dimension in mm (used to calculate center position). Defaults to 220. */
  bedSizeX?: number;
  /** Bed Y dimension in mm. Defaults to 220. */
  bedSizeY?: number;
}

export function ZOffsetCalibrationWizard({ isOpen, onClose, printer, bedSizeX = 220, bedSizeY = 220 }: ZOffsetCalibrationWizardProps) {
  const queryClient = useQueryClient();
  const [stepIndex, setStepIndex] = useState(0);
  const [zOffset, setZOffset] = useState(printer.zOffsetMm ?? 0);
  const [selectedIncrement, setSelectedIncrement] = useState<number>(0.05);
  const [isCommandRunning, setIsCommandRunning] = useState(false);

  const currentStep = WIZARD_STEPS[stepIndex];
  const progressPercent = ((stepIndex + 1) / WIZARD_STEPS.length) * 100;

  const sendGcodeMutation = useMutation({
    mutationFn: (command: string) => apiClient.sendGcode(printer.id, command),
    onError: (error: Error) => {
      toast.error(`Command failed: ${error.message}`);
    },
  });

  const saveZOffsetMutation = useMutation({
    mutationFn: (offsetMm: number) =>
      apiClient.saveZOffset(printer.id, { offsetMm, saveToFirmware: true }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: queryKeys.printers });
      toast.success('Z-offset saved successfully');
      setStepIndex(5);
    },
    onError: (error: Error) => {
      toast.error(`Failed to save Z-offset: ${error.message}`);
    },
  });

  const sendGcodeAndWait = useCallback(
    async (command: string): Promise<CommandResult> => {
      setIsCommandRunning(true);
      try {
        const result = await sendGcodeMutation.mutateAsync(command);
        return result;
      } finally {
        setIsCommandRunning(false);
      }
    },
    [sendGcodeMutation]
  );

  const handleHomeAxes = useCallback(async () => {
    const result = await sendGcodeAndWait('G28');
    if (result.success) {
      toast.success('Axes homed successfully');
      setStepIndex(2);
    }
  }, [sendGcodeAndWait]);

  const handleMoveToCenter = useCallback(async () => {
    const centerX = bedSizeX / 2;
    const centerY = bedSizeY / 2;
    const result = await sendGcodeAndWait(
      `G1 X${centerX.toFixed(0)} Y${centerY.toFixed(0)} Z10 F3000`
    );
    if (result.success) {
      toast.success('Moved to bed center');
      setStepIndex(3);
    }
  }, [sendGcodeAndWait, bedSizeX, bedSizeY]);

  const handleZAdjust = useCallback(
    async (direction: 'up' | 'down') => {
      const delta = direction === 'down' ? -selectedIncrement : selectedIncrement;
      const newOffset = parseFloat((zOffset + delta).toFixed(3));
      setZOffset(newOffset);
      await sendGcodeAndWait(`G1 Z${Math.max(0, 10 + newOffset).toFixed(3)} F300`);
    },
    [sendGcodeAndWait, zOffset, selectedIncrement]
  );

  const handleSave = useCallback(() => {
    saveZOffsetMutation.mutate(zOffset);
  }, [saveZOffsetMutation, zOffset]);

  const handleClose = useCallback(() => {
    setStepIndex(0);
    setZOffset(printer.zOffsetMm ?? 0);
    setSelectedIncrement(0.05);
    setIsCommandRunning(false);
    onClose();
  }, [onClose, printer.zOffsetMm]);

  const renderStepContent = () => {
    switch (currentStep) {
      case 'Introduction':
        return <IntroductionStep />;
      case 'Home Axes':
        return <HomeAxesStep onHome={handleHomeAxes} isRunning={isCommandRunning} />;
      case 'Move to Center':
        return (
          <MoveToCenterStep
            onMove={handleMoveToCenter}
            isRunning={isCommandRunning}
            centerX={bedSizeX / 2}
            centerY={bedSizeY / 2}
          />
        );
      case 'Adjust Z-Offset':
        return (
          <AdjustZOffsetStep
            zOffset={zOffset}
            selectedIncrement={selectedIncrement}
            onIncrementChange={setSelectedIncrement}
            onAdjust={handleZAdjust}
            onContinue={() => setStepIndex(4)}
            isRunning={isCommandRunning}
          />
        );
      case 'Save':
        return (
          <SaveStep
            zOffset={zOffset}
            backend={printer.backend as unknown as PrinterBackendString}
            onSave={handleSave}
            isSaving={saveZOffsetMutation.isPending}
          />
        );
      case 'Done':
        return <DoneStep zOffset={zOffset} />;
      default:
        return null;
    }
  };

  const canGoNext =
    currentStep === 'Introduction' || currentStep === 'Done';

  const footer = (
    <div className="flex items-center justify-between w-full">
      <Button
        variant="ghost"
        onClick={stepIndex > 0 && currentStep !== 'Done' ? () => setStepIndex(stepIndex - 1) : handleClose}
      >
        {stepIndex === 0 || currentStep === 'Done' ? 'Cancel' : 'Back'}
      </Button>
      {canGoNext && currentStep !== 'Done' && (
        <Button variant="primary" onClick={() => setStepIndex(stepIndex + 1)}>
          Next
        </Button>
      )}
      {currentStep === 'Done' && (
        <Button variant="primary" onClick={handleClose}>
          Finish
        </Button>
      )}
    </div>
  );

  return (
    <Modal isOpen={isOpen} onClose={handleClose} title="Z-Offset Calibration" size="lg" footer={footer}>
      <div className="space-y-4">
        <ProgressBar value={progressPercent} className="mb-2" />
        <div className="text-sm text-pf-text-secondary mb-4">
          Step {stepIndex + 1} of {WIZARD_STEPS.length}: {currentStep}
        </div>
        {renderStepContent()}
      </div>
    </Modal>
  );
}

function IntroductionStep() {
  return (
    <div className="space-y-4">
      <p className="text-pf-text-primary">
        This wizard will guide you through calibrating your printer&apos;s Z-offset — the distance
        between the nozzle tip and the print bed when the Z-axis is at its home position.
      </p>
      <Alert variant="info" title="What is Z-offset?">
        A correct Z-offset ensures your first layer adheres properly to the bed. Too high and the
        filament won&apos;t stick; too low and the nozzle may scratch the bed.
      </Alert>
      <FirstLayerVisualGuide />
    </div>
  );
}

function HomeAxesStep({
  onHome,
  isRunning,
}: {
  onHome: () => void;
  isRunning: boolean;
}) {
  return (
    <div className="space-y-4">
      <p className="text-pf-text-primary">
        First, we need to home all axes so the printer knows its exact position.
      </p>
      <Alert variant="warning" title="Safety Warning">
        Make sure the print bed is clear and nothing obstructs the nozzle path.
      </Alert>
      <div className="flex justify-center">
        <Button variant="primary" onClick={onHome} loading={isRunning} disabled={isRunning}>
          {isRunning ? 'Homing...' : 'Home All Axes (G28)'}
        </Button>
      </div>
    </div>
  );
}

function MoveToCenterStep({
  onMove,
  isRunning,
  centerX,
  centerY,
}: {
  onMove: () => void;
  isRunning: boolean;
  centerX: number;
  centerY: number;
}) {
  return (
    <div className="space-y-4">
      <p className="text-pf-text-primary">
        Now we&apos;ll move the nozzle to the center of the bed at a safe height (Z=10mm).
      </p>
      <div className="text-sm text-pf-text-secondary">
        Target position: X{centerX.toFixed(0)} Y{centerY.toFixed(0)} Z10
      </div>
      <div className="flex justify-center">
        <Button variant="primary" onClick={onMove} loading={isRunning} disabled={isRunning}>
          {isRunning ? 'Moving...' : 'Move to Center'}
        </Button>
      </div>
    </div>
  );
}

function AdjustZOffsetStep({
  zOffset,
  selectedIncrement,
  onIncrementChange,
  onAdjust,
  onContinue,
  isRunning,
}: {
  zOffset: number;
  selectedIncrement: number;
  onIncrementChange: (inc: number) => void;
  onAdjust: (direction: 'up' | 'down') => void;
  onContinue: () => void;
  isRunning: boolean;
}) {
  return (
    <div className="space-y-4">
      <p className="text-pf-text-primary">
        Use the buttons below to lower or raise the nozzle. Place a piece of paper between the
        nozzle and bed — you should feel slight friction when the offset is correct.
      </p>

      <Card>
        <Card.Body className="text-center space-y-4">
          <div className="text-3xl font-mono font-bold text-pf-text-primary">
            Z-Offset: {zOffset >= 0 ? '+' : ''}{zOffset.toFixed(3)} mm
          </div>

          <div className="flex justify-center gap-2">
            {Z_INCREMENTS.map((inc) => (
              <Button
                key={inc}
                variant={selectedIncrement === inc ? 'primary' : 'secondary'}
                size="sm"
                onClick={() => onIncrementChange(inc)}
              >
                {inc} mm
              </Button>
            ))}
          </div>

          <div className="flex justify-center gap-4">
            <Button
              variant="secondary"
              onClick={() => onAdjust('down')}
              disabled={isRunning}
              loading={isRunning}
            >
              ▼ Nozzle Down (-{selectedIncrement})
            </Button>
            <Button
              variant="secondary"
              onClick={() => onAdjust('up')}
              disabled={isRunning}
              loading={isRunning}
            >
              ▲ Nozzle Up (+{selectedIncrement})
            </Button>
          </div>
        </Card.Body>
      </Card>

      <FirstLayerVisualGuide />

      <div className="flex justify-center">
        <Button variant="primary" onClick={onContinue}>
          Looks Good — Continue
        </Button>
      </div>
    </div>
  );
}

function SaveStep({
  zOffset,
  backend,
  onSave,
  isSaving,
}: {
  zOffset: number;
  backend?: PrinterBackendString;
  onSave: () => void;
  isSaving: boolean;
}) {
  const backendName = backend === 'Moonraker' ? 'Klipper/Moonraker' : 'Marlin/PrusaLink';
  const firmwareCmd =
    backend === 'Moonraker'
      ? `SET_GCODE_OFFSET Z=${zOffset.toFixed(3)} + SAVE_CONFIG`
      : `M851 Z${zOffset.toFixed(3)} + M500`;

  return (
    <div className="space-y-4">
      <p className="text-pf-text-primary">
        Ready to save your calibrated Z-offset.
      </p>
      <Card>
        <Card.Body className="space-y-2">
          <div className="flex justify-between">
            <span className="text-pf-text-secondary">Z-Offset:</span>
            <span className="font-mono font-bold text-pf-text-primary">
              {zOffset.toFixed(3)} mm
            </span>
          </div>
          <div className="flex justify-between">
            <span className="text-pf-text-secondary">Backend:</span>
            <span className="text-pf-text-primary">{backendName}</span>
          </div>
          <div className="flex justify-between">
            <span className="text-pf-text-secondary">Firmware command:</span>
            <span className="font-mono text-xs text-pf-text-secondary">{firmwareCmd}</span>
          </div>
        </Card.Body>
      </Card>
      <Alert variant="info" title="What happens next">
        The Z-offset will be saved to both PrintFarmer and your printer&apos;s firmware so it
        persists across reboots.
      </Alert>
      <div className="flex justify-center">
        <Button variant="primary" onClick={onSave} loading={isSaving} disabled={isSaving}>
          {isSaving ? 'Saving...' : 'Save Z-Offset'}
        </Button>
      </div>
    </div>
  );
}

function DoneStep({ zOffset }: { zOffset: number }) {
  return (
    <div className="space-y-4 text-center">
      <div className="text-5xl mb-4">✅</div>
      <h3 className="text-xl font-bold text-pf-text-primary">Calibration Complete!</h3>
      <p className="text-pf-text-secondary">
        Your Z-offset of{' '}
        <span className="font-mono font-bold">{zOffset.toFixed(3)} mm</span> has been saved.
      </p>
      <Alert variant="success" title="Tip">
        Run a small test print to verify the first layer. If it still needs adjustment, run this
        wizard again.
      </Alert>
    </div>
  );
}

/** CSS-based visual guide showing good vs bad first layers. */
function FirstLayerVisualGuide() {
  const examples = [
    {
      label: 'Too Far',
      description: 'Filament doesn\'t stick, rounded beads',
      color: 'bg-red-500',
      height: 'h-1',
      gap: 'gap-2',
    },
    {
      label: 'Just Right',
      description: 'Slightly squished, good adhesion',
      color: 'bg-green-500',
      height: 'h-1.5',
      gap: 'gap-0.5',
    },
    {
      label: 'Too Close',
      description: 'Over-squished, transparent, may scratch bed',
      color: 'bg-amber-500',
      height: 'h-0.5',
      gap: 'gap-0',
    },
  ] as const;

  return (
    <Card>
      <Card.Header>
        <span className="text-sm font-medium text-pf-text-primary">First Layer Visual Guide</span>
      </Card.Header>
      <Card.Body>
        <div className="grid grid-cols-3 gap-4">
          {examples.map((ex) => (
            <div key={ex.label} className="text-center space-y-2">
              <div className="text-sm font-medium text-pf-text-primary">{ex.label}</div>
              <div
                className={clsx('flex flex-col justify-center mx-auto w-20', ex.gap)}
                role="img"
                aria-label={`${ex.label}: ${ex.description}`}
              >
                {[0, 1, 2].map((i) => (
                  <div key={i} className={clsx('rounded-full', ex.color, ex.height)} />
                ))}
              </div>
              <div className="text-xs text-pf-text-secondary">{ex.description}</div>
            </div>
          ))}
        </div>
      </Card.Body>
    </Card>
  );
}

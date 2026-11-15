import { PrinterCoordinateRow } from '@/features/printers/components/PrinterCoordinateRow';
import { PrinterMotionHelp } from '@/features/printers/components/PrinterControlsMode';
import { MotionControlButton } from '@/features/printers/components/MotionControlButton';
import { ControlPadButton, MoveDistanceSlider } from '@/common/components/ui';
import type { MoveRequest } from '@/types/api';
import {
  HomeIcon,
  DisableMotorsIcon,
  ArrowUpIcon,
  ArrowDownIcon,
  ArrowLeftIcon,
  ArrowRightIcon,
} from '@/common/components/icons/MdiIcons';
import { getHomeButtonStyle } from '@/features/printers/utils/homeButtonStyle';
import { knownHomedAxes } from '@/features/printers/utils/homedAxes';
import {
  EXTRUDE_DISTANCE_OPTIONS,
  EXTRUDE_SPEED_OPTIONS,
} from '@/features/printers/constants/temperaturePresets';

interface MovementControlSectionProps {
  layout?: 'card' | 'sidebar';
  // Position state
  moveX: number | string;
  moveY: number | string;
  moveZ: number | string;
  step: number;
  extrudeStep: number;
  extrudeSpeed: number;
  
  // Printer state
  printerX?: number | null;
  printerY?: number | null;
  printerZ?: number | null;
  homedAxes?: string;
  hotendTemp?: number;
  
  // Flags
  movementActionPending: boolean;
  movementUnavailable?: boolean;
  canMove: boolean;
  canHomeAll?: boolean;
  canHomeXY?: boolean;
  canHomeZ?: boolean;
  canDisableMotors: boolean;
  canSetStep: boolean;
  canManualMove: boolean;
  canExtrude: boolean;
  
  // Callbacks
  onMoveXChange: (value: number | string) => void;
  onMoveYChange: (value: number | string) => void;
  onMoveZChange: (value: number | string) => void;
  onStepChange: (step: number) => void;
  onExtrudeStepChange: (step: number) => void;
  onExtrudeSpeedChange: (speed: number) => void;
  onMove: (axis: 'X' | 'Y' | 'Z', distance: number) => void | Promise<void>;
  onMoveTo?: (position: MoveRequest) => void | Promise<void>;
  onHome: (axes?: string) => void | Promise<void>;
  onDisableMotors: () => void;
  onExtrude: (direction: 'extrude' | 'retract') => void;

  /** Optional content rendered to the right of the extrude controls */
  rightContent?: React.ReactNode;
}

export function MovementControlSection({
  layout = 'card',
  moveX,
  moveY,
  moveZ,
  step,
  extrudeStep,
  extrudeSpeed,
  printerX,
  printerY,
  printerZ,
  homedAxes,
  movementActionPending,
  movementUnavailable,
  canMove,
  canHomeAll = canMove,
  canHomeXY = canMove,
  canHomeZ = canMove,
  canDisableMotors,
  canSetStep,
  canManualMove,
  canExtrude,
  onMoveXChange,
  onMoveYChange,
  onMoveZChange,
  onStepChange,
  onExtrudeStepChange,
  onExtrudeSpeedChange,
  onMove,
  onMoveTo,
  onHome,
  onDisableMotors,
  onExtrude,
  rightContent,
}: MovementControlSectionProps) {
  const homedAxesRaw = knownHomedAxes(homedAxes);
  const isHomedStateKnown = homedAxesRaw !== undefined;
  const homedAxesLower = homedAxesRaw ?? '';
  const isXHomed = isHomedStateKnown && homedAxesLower.includes('x');
  const isYHomed = isHomedStateKnown && homedAxesLower.includes('y');
  const isZHomed = isHomedStateKnown && homedAxesLower.includes('z');
  const isXYHomed = isXHomed && isYHomed;
  const isAllHomed = isXYHomed && isZHomed;

  return (
    <div className={layout === 'sidebar' ? undefined : 'mb-2'}>
      <div className={layout === 'sidebar' ? 'flex flex-wrap gap-4 items-start' : 'flex flex-wrap gap-6 items-start'}>
        {/* Left Column: Move */}
        <div className={layout === 'sidebar' ? 'flex flex-col gap-1 items-start' : 'flex flex-col gap-2 items-start'}>
          <div className={layout === 'sidebar' ? 'text-[10px] uppercase text-pf-text-secondary font-bold tracking-wide' : 'text-xs uppercase text-pf-text-secondary font-bold tracking-wide -ml-1'}>
            Move
          </div>
          <div className="flex gap-2 items-start">
            {/* XY Pad */}
            <div className="grid grid-cols-3 grid-rows-3 gap-1 w-fit">
              {/* Top row */}
              <MotionControlButton
                disabled={movementActionPending || !canHomeAll}
                onClick={() => onHome()}
                title="Home all axes"
                padSize="small"
                className={getHomeButtonStyle(isHomedStateKnown, isAllHomed).className}
                style={getHomeButtonStyle(isHomedStateKnown, isAllHomed).style}
              >
                <HomeIcon className="h-4 w-4" />
              </MotionControlButton>
              <MotionControlButton
                disabled={movementActionPending || !canMove || !isYHomed}
                onClick={() => onMove('Y', step)}
                aria-label="Jog Y positive"
                padSize="small"
              >
                {layout === 'sidebar' ? <ArrowUpIcon className="h-4 w-4" /> : '▲'}
              </MotionControlButton>
              <ControlPadButton
                disabled={!canDisableMotors}
                onClick={onDisableMotors}
                title="Disable Motors (M84)"
                padSize="small"
              >
                <DisableMotorsIcon className="h-4 w-4" />
              </ControlPadButton>

              {/* Middle row */}
              <MotionControlButton
                disabled={movementActionPending || !canMove || !isXHomed}
                onClick={() => onMove('X', -step)}
                aria-label="Jog X negative"
                padSize="small"
              >
                {layout === 'sidebar' ? <ArrowLeftIcon className="h-4 w-4" /> : '◀'}
              </MotionControlButton>
              <MotionControlButton
                disabled={movementActionPending || !canHomeXY}
                onClick={() => onHome('xy')}
                title="Home X/Y"
                padSize="small"
                className={getHomeButtonStyle(isHomedStateKnown, isXYHomed).className}
                style={getHomeButtonStyle(isHomedStateKnown, isXYHomed).style}
              >
                <HomeIcon className="h-4 w-4" />
              </MotionControlButton>
              <MotionControlButton
                disabled={movementActionPending || !canMove || !isXHomed}
                onClick={() => onMove('X', step)}
                aria-label="Jog X positive"
                padSize="small"
              >
                {layout === 'sidebar' ? <ArrowRightIcon className="h-4 w-4" /> : '▶'}
              </MotionControlButton>

              {/* Bottom row */}
              <div></div>
              <MotionControlButton
                disabled={movementActionPending || !canMove || !isYHomed}
                onClick={() => onMove('Y', -step)}
                aria-label="Jog Y negative"
                padSize="small"
              >
                {layout === 'sidebar' ? <ArrowDownIcon className="h-4 w-4" /> : '▼'}
              </MotionControlButton>
              <div></div>
            </div>

            {/* Z Pad */}
            <div className="flex flex-col gap-1 w-fit">
              <MotionControlButton
                disabled={movementActionPending || !canMove || !isZHomed}
                onClick={() => onMove('Z', step)}
                aria-label="Jog Z positive"
                padSize="small"
              >
                Z+
              </MotionControlButton>
              <MotionControlButton
                disabled={movementActionPending || !canHomeZ}
                onClick={() => onHome('z')}
                title="Home Z"
                padSize="small"
                className={getHomeButtonStyle(isHomedStateKnown, isZHomed).className}
                style={getHomeButtonStyle(isHomedStateKnown, isZHomed).style}
              >
                <HomeIcon className="h-4 w-4" />
              </MotionControlButton>
              <MotionControlButton
                disabled={movementActionPending || !canMove || !isZHomed}
                onClick={() => onMove('Z', -step)}
                aria-label="Jog Z negative"
                padSize="small"
              >
                Z-
              </MotionControlButton>
            </div>

            {/* Extrude Pad */}
            <div className="flex gap-1.5 items-center">
              {/* Length vertical slider */}
              <div className="flex flex-col items-center gap-0.5">
                <span className="text-[8px] text-pf-text-tertiary uppercase leading-none">len</span>
                <input
                  type="range"
                  min={0}
                  max={EXTRUDE_DISTANCE_OPTIONS.length - 1}
                  step={1}
                  value={
                    (EXTRUDE_DISTANCE_OPTIONS as readonly number[]).indexOf(extrudeStep) >= 0
                      ? (EXTRUDE_DISTANCE_OPTIONS as readonly number[]).indexOf(extrudeStep)
                      : 0
                  }
                  onChange={(e) => onExtrudeStepChange(EXTRUDE_DISTANCE_OPTIONS[Number(e.target.value)])}
                  disabled={!canExtrude}
                  className="h-20 w-4 accent-pf-accent cursor-pointer disabled:opacity-40 disabled:cursor-not-allowed"
                  style={{ writingMode: 'vertical-lr', direction: 'rtl' }}
                  aria-label="Extrude distance"
                />
                <span className="text-[9px] font-bold text-pf-text-primary tabular-nums leading-none">
                  {extrudeStep}mm
                </span>
              </div>

              {/* E+ / E- buttons */}
              <div className="flex flex-col gap-1 w-fit">
                <ControlPadButton
                  disabled={movementActionPending || !canExtrude}
                  onClick={() => onExtrude('extrude')}
                  title="Extrude filament"
                  aria-label={`Extrude ${extrudeStep}mm at ${extrudeSpeed}mm/s`}
                  padSize="small"
                >
                  E+
                </ControlPadButton>
                <div className="h-8 w-8" />
                <ControlPadButton
                  disabled={movementActionPending || !canExtrude}
                  onClick={() => onExtrude('retract')}
                  title="Retract filament"
                  aria-label={`Retract ${extrudeStep}mm at ${extrudeSpeed}mm/s`}
                  padSize="small"
                >
                  E-
                </ControlPadButton>
              </div>

              {/* Speed vertical slider */}
              <div className="flex flex-col items-center gap-0.5">
                <span className="text-[8px] text-pf-text-tertiary uppercase leading-none">spd</span>
                <input
                  type="range"
                  min={0}
                  max={EXTRUDE_SPEED_OPTIONS.length - 1}
                  step={1}
                  value={
                    (EXTRUDE_SPEED_OPTIONS as readonly number[]).indexOf(extrudeSpeed) >= 0
                      ? (EXTRUDE_SPEED_OPTIONS as readonly number[]).indexOf(extrudeSpeed)
                      : 0
                  }
                  onChange={(e) => onExtrudeSpeedChange(EXTRUDE_SPEED_OPTIONS[Number(e.target.value)])}
                  disabled={!canExtrude}
                  className="h-20 w-4 accent-pf-accent cursor-pointer disabled:opacity-40 disabled:cursor-not-allowed"
                  style={{ writingMode: 'vertical-lr', direction: 'rtl' }}
                  aria-label="Extrude speed"
                />
                <span className="text-[9px] font-bold text-pf-text-primary tabular-nums leading-none">
                  {extrudeSpeed}mm/s
                </span>
              </div>
            </div>
          </div>
          {/* Move distance slider */}
          {layout === 'card' && <MoveDistanceSlider value={step} onChange={onStepChange} disabled={!canSetStep} />}
        </div>

        {/* Right content (Control/Filament) — same level as Move column */}
        {rightContent}
      </div>

      {layout === 'sidebar' && (
        <div className="my-4">
          <div className="text-[10px] uppercase text-pf-text-secondary font-bold tracking-wide mb-1">Step Size</div>
          <MoveDistanceSlider value={step} onChange={onStepChange} disabled={!canSetStep} />
        </div>
      )}
      <PrinterCoordinateRow
        values={{ X: moveX, Y: moveY, Z: moveZ }}
        positions={{ X: printerX, Y: printerY, Z: printerZ }}
        homedAxes={homedAxes}
        onChange={(axis, value) => ({ X: onMoveXChange, Y: onMoveYChange, Z: onMoveZChange })[axis](value)}
        disabled={movementActionPending || !canManualMove}
        onMove={onMove}
        onMoveTo={onMoveTo}
      />
      <PrinterMotionHelp movementUnavailable={movementUnavailable} />
    </div>
  );
}

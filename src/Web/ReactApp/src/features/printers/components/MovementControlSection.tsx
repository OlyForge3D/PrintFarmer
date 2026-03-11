import { ControlPadButton, MovementInput, MoveDistanceSlider } from '@/common/components/ui';
import {
  HomeIcon,
  DisableMotorsIcon,
} from '@/common/components/icons/MdiIcons';
import { getHomeButtonStyle } from '@/features/printers/utils/homeButtonStyle';
import {
  EXTRUDE_DISTANCE_OPTIONS,
  EXTRUDE_SPEED_OPTIONS,
} from '@/features/printers/constants/temperaturePresets';

interface MovementControlSectionProps {
  // Position state
  moveX: number | string;
  moveY: number | string;
  moveZ: number | string;
  step: number;
  extrudeStep: number;
  extrudeSpeed: number;
  
  // Printer state
  printerX?: number;
  printerY?: number;
  printerZ?: number;
  homedAxes?: string;
  hotendTemp?: number;
  extrudeMinTemp: number;
  
  // Flags
  movementActionPending: boolean;
  canMove: boolean;
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
  onMove: (axis: 'X' | 'Y' | 'Z', distance: number) => void;
  onHome: (axes?: string) => void;
  onDisableMotors: () => void;
  onExtrude: (direction: 'extrude' | 'retract') => void;

  /** Optional content rendered to the right of the extrude controls */
  rightContent?: React.ReactNode;
}

export function MovementControlSection({
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
  extrudeMinTemp,
  movementActionPending,
  canMove,
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
  onHome,
  onDisableMotors,
  onExtrude,
  rightContent,
}: MovementControlSectionProps) {
  const homedAxesRaw = homedAxes;
  const isHomedStateKnown = typeof homedAxesRaw === 'string';
  const homedAxesLower = (homedAxesRaw ?? '').toLowerCase();
  const isXHomed = isHomedStateKnown && homedAxesLower.includes('x');
  const isYHomed = isHomedStateKnown && homedAxesLower.includes('y');
  const isZHomed = isHomedStateKnown && homedAxesLower.includes('z');
  const isXYHomed = isXHomed && isYHomed;
  const isAllHomed = isXYHomed && isZHomed;

  return (
    <div className="mb-2">
      <div className="flex gap-6 items-start">
        {/* Left Column: Move */}
        <div className="flex flex-col gap-2 items-start">
          <div className="text-xs uppercase text-pf-text-secondary font-bold tracking-wide -ml-1">
            Move
          </div>
          <div className="flex gap-2 items-start">
            {/* XY Pad */}
            <div className="grid grid-cols-3 grid-rows-3 gap-1 w-fit">
              {/* Top row */}
              <ControlPadButton
                disabled={movementActionPending || !canMove}
                onClick={() => onHome()}
                title="Home all axes"
                padSize="small"
                className={getHomeButtonStyle(isHomedStateKnown, isAllHomed).className}
                style={getHomeButtonStyle(isHomedStateKnown, isAllHomed).style}
              >
                <HomeIcon className="h-4 w-4" />
              </ControlPadButton>
              <ControlPadButton
                disabled={movementActionPending || !canMove}
                onClick={() => onMove('Y', step)}
                padSize="small"
              >
                ▲
              </ControlPadButton>
              <ControlPadButton
                disabled={!canDisableMotors}
                onClick={onDisableMotors}
                title="Disable Motors (M84)"
                padSize="small"
              >
                <DisableMotorsIcon className="h-4 w-4" />
              </ControlPadButton>

              {/* Middle row */}
              <ControlPadButton
                disabled={movementActionPending || !canMove}
                onClick={() => onMove('X', -step)}
                padSize="small"
              >
                ◀
              </ControlPadButton>
              <ControlPadButton
                disabled={movementActionPending || !canMove}
                onClick={() => onHome('xy')}
                title="Home X/Y"
                padSize="small"
                className={getHomeButtonStyle(isHomedStateKnown, isXYHomed).className}
                style={getHomeButtonStyle(isHomedStateKnown, isXYHomed).style}
              >
                <HomeIcon className="h-4 w-4" />
              </ControlPadButton>
              <ControlPadButton
                disabled={movementActionPending || !canMove}
                onClick={() => onMove('X', step)}
                padSize="small"
              >
                ▶
              </ControlPadButton>

              {/* Bottom row */}
              <div></div>
              <ControlPadButton
                disabled={movementActionPending || !canMove}
                onClick={() => onMove('Y', -step)}
                padSize="small"
              >
                ▼
              </ControlPadButton>
              <div></div>
            </div>

            {/* Z Pad */}
            <div className="flex flex-col gap-1 w-fit">
              <ControlPadButton
                disabled={movementActionPending || !canMove}
                onClick={() => onMove('Z', step)}
                padSize="small"
              >
                Z+
              </ControlPadButton>
              <ControlPadButton
                disabled={movementActionPending || !canMove}
                onClick={() => onHome('z')}
                title="Home Z"
                padSize="small"
                className={getHomeButtonStyle(isHomedStateKnown, isZHomed).className}
                style={getHomeButtonStyle(isHomedStateKnown, isZHomed).style}
              >
                <HomeIcon className="h-4 w-4" />
              </ControlPadButton>
              <ControlPadButton
                disabled={movementActionPending || !canMove}
                onClick={() => onMove('Z', -step)}
                padSize="small"
              >
                Z-
              </ControlPadButton>
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
                  title={`Extrude filament (min ${extrudeMinTemp}°C)`}
                  aria-label={`Extrude ${extrudeStep}mm at ${extrudeSpeed}mm/s`}
                  padSize="small"
                >
                  E+
                </ControlPadButton>
                <div className="h-8 w-8" />
                <ControlPadButton
                  disabled={movementActionPending || !canExtrude}
                  onClick={() => onExtrude('retract')}
                  title={`Retract filament (min ${extrudeMinTemp}°C)`}
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
          <MoveDistanceSlider value={step} onChange={onStepChange} disabled={!canSetStep} />
        </div>

        {/* Right content (Control/Filament) — same level as Move column */}
        {rightContent}
      </div>

      {/* Manual position inputs */}
      <div className="grid grid-cols-4 gap-2 mt-3 w-72 pt-2">
        <MovementInput
          axis="X"
          currentPosition={printerX}
          disabled={movementActionPending || !canManualMove}
          value={moveX}
          onChange={(e) => onMoveXChange(e.target.value === '' ? '' : Number(e.target.value))}
          className="w-full!"
        />
        <MovementInput
          axis="Y"
          currentPosition={printerY}
          disabled={movementActionPending || !canManualMove}
          value={moveY}
          onChange={(e) => onMoveYChange(e.target.value === '' ? '' : Number(e.target.value))}
          className="w-full!"
        />
        <MovementInput
          axis="Z"
          currentPosition={printerZ}
          disabled={movementActionPending || !canManualMove}
          value={moveZ}
          onChange={(e) => onMoveZChange(e.target.value === '' ? '' : Number(e.target.value))}
          className="w-full!"
        />
        <div className="pt-2">
          <ControlPadButton
            disabled={
              movementActionPending ||
              !canManualMove ||
              (moveX === '' && moveY === '' && moveZ === '')
            }
            onClick={async () => {
              if (moveX !== '') await onMove('X', Number(moveX));
              if (moveY !== '') await onMove('Y', Number(moveY));
              if (moveZ !== '') await onMove('Z', Number(moveZ));
            }}
            title="Go to position"
            padSize="small"
          >
            GO
          </ControlPadButton>
        </div>
      </div>
    </div>
  );
}

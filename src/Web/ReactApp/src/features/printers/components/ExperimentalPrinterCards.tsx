import { useState, useMemo } from 'react';
import { getBackendIcon } from '@/common/utils/printerBackendIcon';
import type { Printer } from '@/types/api';
import { Button } from '@/common/components/ui';
import { 
  NozzleIcon, 
  BedIcon, 
  EditIcon, 
  PlayIcon, 
  PauseIcon, 
  EmergencyStopIcon, 
  HistoryIcon,
  ExternalLinkIcon,
  CameraIcon,
  SnowflakeIcon,
  ChevronDownIcon,
  ChevronUpIcon,
} from '@/common/components/icons/MdiIcons';
import { usePrinters } from '@/common/hooks/useApi';
import { usePrinterDisplay } from '@/common/hooks/usePrinterDisplay';

interface CardProps {
  printer: Printer;
  onEdit?: (printer: Printer) => void;
}

// Shared utilities
function formatTemperature(temp: number | undefined): string {
  if (temp === undefined || temp === null) return '---';
  return `${temp.toFixed(1)}°C`;
}

function toCamelCase(str: string): string {
  return str.charAt(0).toUpperCase() + str.slice(1).toLowerCase();
}

// ============================================================
// 1. GLASSMORPHISM CARD - Frosted glass effect
// ============================================================
export function GlassmorphismCard({ printer: initialPrinter, onEdit }: CardProps) {
  const { data: allPrinters = [] } = usePrinters();
  const apiPrinter = useMemo(
    () => allPrinters.find(p => p.id === initialPrinter.id) ?? initialPrinter,
    [allPrinters, initialPrinter]
  );
  const printer = usePrinterDisplay(apiPrinter);
  
  const state = printer.state ?? 'Unknown';
  const isOnline = printer.isOnline ?? false;
  const isPrinting = isOnline && (state === 'Printing' || state === 'Busy');
  
  const glowColor = !isOnline 
    ? 'shadow-slate-500/20' 
    : isPrinting 
      ? 'shadow-green-500/30' 
      : 'shadow-blue-500/20';

  return (
    <div className={`relative min-w-[23rem] rounded-2xl p-4 backdrop-blur-xl bg-white/5 border border-white/10 shadow-xl ${glowColor} hover:shadow-2xl transition-all duration-300`}>
      {/* Gradient overlay */}
      <div className="absolute inset-0 rounded-2xl bg-gradient-to-br from-white/10 via-transparent to-black/20 pointer-events-none" />
      
      <div className="relative z-10">
        {/* Header */}
        <div className="flex justify-between items-start mb-4">
          <div>
            <h3 className="font-bold text-xl text-white font-bebas uppercase tracking-wide">
              {printer.name}
            </h3>
            <p className="text-white/60 text-sm">
              {printer.manufacturerName} {printer.modelName}
            </p>
          </div>
          <div className={`px-3 py-1 rounded-full text-xs font-semibold backdrop-blur-md ${
            !isOnline ? 'bg-slate-500/50 text-white' :
            isPrinting ? 'bg-green-500/50 text-white' :
            'bg-blue-500/50 text-white'
          }`}>
            {getBackendIcon(printer.backend)}
            <span className="ml-1">{isOnline ? toCamelCase(state) : 'Offline'}</span>
          </div>
        </div>

        {/* Temperature display with glass pills */}
        <div className="flex gap-3 mb-4">
          <div className="flex-1 bg-white/5 backdrop-blur-sm rounded-xl p-3 border border-white/10">
            <div className="flex items-center gap-2">
              <NozzleIcon className="w-5 h-5 text-red-400" isOn={(printer.hotendTarget ?? 0) > 0} />
              <div>
                <div className="text-white/50 text-xs">Hotend</div>
                <div className="text-white font-semibold">{formatTemperature(printer.hotendTemp)}</div>
              </div>
            </div>
          </div>
          <div className="flex-1 bg-white/5 backdrop-blur-sm rounded-xl p-3 border border-white/10">
            <div className="flex items-center gap-2">
              <BedIcon className="w-5 h-5 text-blue-400" isOn={(printer.bedTarget ?? 0) > 0} />
              <div>
                <div className="text-white/50 text-xs">Bed</div>
                <div className="text-white font-semibold">{formatTemperature(printer.bedTemp)}</div>
              </div>
            </div>
          </div>
        </div>

        {/* Progress */}
        {isPrinting && printer.progress !== undefined && (
          <div className="mb-4">
            <div className="flex justify-between text-xs text-white/60 mb-1">
              <span className="truncate">{printer.jobName || 'Printing...'}</span>
              <span className="font-bold text-white">{Math.round(printer.progress)}%</span>
            </div>
            <div className="h-2 bg-white/10 rounded-full overflow-hidden">
              <div 
                className="h-full bg-gradient-to-r from-green-400 to-emerald-500 rounded-full transition-all duration-300"
                style={{ width: `${printer.progress}%` }}
              />
            </div>
          </div>
        )}

        {/* Action buttons */}
        <div className="flex gap-2">
          <Button variant="secondary" size="sm" className="flex-1 !bg-white/10 !border-white/20 hover:!bg-white/20" onClick={() => onEdit?.(printer)}>
            <EditIcon className="w-4 h-4 mr-1" /> Edit
          </Button>
          <Button variant="secondary" size="sm" className="!bg-white/10 !border-white/20 hover:!bg-white/20">
            <ExternalLinkIcon className="w-4 h-4" />
          </Button>
          <Button variant="secondary" size="sm" className="!bg-white/10 !border-white/20 hover:!bg-white/20">
            <HistoryIcon className="w-4 h-4" />
          </Button>
        </div>
      </div>
    </div>
  );
}

// ============================================================
// 2. SEGMENTED CARD - Collapsible sections
// ============================================================
// Note: onEdit prop intentionally unused - segmented card only shows info
export function SegmentedCard({ printer: initialPrinter }: Omit<CardProps, 'onEdit'>) {
  const { data: allPrinters = [] } = usePrinters();
  const apiPrinter = useMemo(
    () => allPrinters.find(p => p.id === initialPrinter.id) ?? initialPrinter,
    [allPrinters, initialPrinter]
  );
  const printer = usePrinterDisplay(apiPrinter);
  
  const [expandedSection, setExpandedSection] = useState<'temps' | 'move' | 'control' | null>(null);
  
  const state = printer.state ?? 'Unknown';
  const isOnline = printer.isOnline ?? false;
  const isPrinting = isOnline && (state === 'Printing' || state === 'Busy');

  const toggleSection = (section: 'temps' | 'move' | 'control') => {
    setExpandedSection(expandedSection === section ? null : section);
  };

  return (
    <div className="min-w-[23rem] rounded-xl bg-pf-bg-1 border border-pf-border overflow-hidden">
      {/* Header - Always visible */}
      <div className="p-4 bg-gradient-to-r from-pf-bg-2 to-pf-bg-1 border-b border-pf-border">
        <div className="flex justify-between items-center">
          <div>
            <h3 className="font-bold text-lg text-pf-text-primary font-bebas uppercase">{printer.name}</h3>
            <p className="text-pf-text-secondary text-xs">{printer.manufacturerName} {printer.modelName}</p>
          </div>
          <div className={`px-3 py-1 rounded-full text-xs font-bold ${
            !isOnline ? 'bg-slate-600 text-white' :
            isPrinting ? 'bg-green-600 text-white' :
            'bg-blue-600 text-white'
          }`}>
            {isOnline ? toCamelCase(state) : 'Offline'}
          </div>
        </div>
        
        {isPrinting && printer.progress !== undefined && (
          <div className="mt-3">
            <div className="flex justify-between text-xs text-pf-text-secondary mb-1">
              <span>{printer.jobName || 'Printing...'}</span>
              <span className="font-bold">{Math.round(printer.progress)}%</span>
            </div>
            <div className="h-1.5 bg-pf-bg-0 rounded-full overflow-hidden">
              <div className="h-full bg-green-500 rounded-full" style={{ width: `${printer.progress}%` }} />
            </div>
          </div>
        )}
      </div>

      {/* Collapsible Sections */}
      {['temps', 'move', 'control'].map((section) => (
        <div key={section} className="border-b border-pf-border last:border-b-0">
          {/* eslint-disable-next-line local/pf-no-raw-html-controls -- This is a disclosure/toggle button, not an action button */}
          <button
            onClick={() => toggleSection(section as 'temps' | 'move' | 'control')}
            className="w-full px-4 py-3 flex justify-between items-center hover:bg-pf-bg-2 transition-colors"
          >
            <span className="text-sm font-semibold text-pf-text-primary uppercase tracking-wide">
              {section === 'temps' ? '🌡️ Temperature' : section === 'move' ? '🎮 Movement' : '⚡ Control'}
            </span>
            {expandedSection === section ? <ChevronUpIcon className="w-4 h-4" /> : <ChevronDownIcon className="w-4 h-4" />}
          </button>
          
          {expandedSection === section && (
            <div className="px-4 pb-4 bg-pf-bg-0/50">
              {section === 'temps' && (
                <div className="grid grid-cols-2 gap-3">
                  <div className="bg-pf-bg-1 rounded-lg p-3">
                    <div className="flex items-center gap-2 mb-2">
                      <NozzleIcon className="w-4 h-4 text-red-500" isOn={(printer.hotendTarget ?? 0) > 0} />
                      <span className="text-xs text-pf-text-secondary">Hotend</span>
                    </div>
                    <div className="text-lg font-bold text-pf-text-primary">{formatTemperature(printer.hotendTemp)}</div>
                  </div>
                  <div className="bg-pf-bg-1 rounded-lg p-3">
                    <div className="flex items-center gap-2 mb-2">
                      <BedIcon className="w-4 h-4 text-blue-500" isOn={(printer.bedTarget ?? 0) > 0} />
                      <span className="text-xs text-pf-text-secondary">Bed</span>
                    </div>
                    <div className="text-lg font-bold text-pf-text-primary">{formatTemperature(printer.bedTemp)}</div>
                  </div>
                </div>
              )}
              {section === 'move' && (
                <div className="text-center text-pf-text-secondary text-sm py-4">
                  Movement controls here...
                </div>
              )}
              {section === 'control' && (
                <div className="flex gap-2 justify-center py-2">
                  <Button variant="secondary" size="sm" disabled={!isPrinting}><PauseIcon className="w-4 h-4" /></Button>
                  <Button variant="success" size="sm" disabled={!isOnline}><PlayIcon className="w-4 h-4" /></Button>
                  <Button variant="danger" size="sm" disabled={!isOnline}><EmergencyStopIcon className="w-4 h-4" /></Button>
                </div>
              )}
            </div>
          )}
        </div>
      ))}
    </div>
  );
}

// ============================================================
// 3. STATUS GLOW CARD - Color accent based on state
// ============================================================
export function StatusGlowCard({ printer: initialPrinter, onEdit }: CardProps) {
  const { data: allPrinters = [] } = usePrinters();
  const apiPrinter = useMemo(
    () => allPrinters.find(p => p.id === initialPrinter.id) ?? initialPrinter,
    [allPrinters, initialPrinter]
  );
  const printer = usePrinterDisplay(apiPrinter);
  
  const state = printer.state ?? 'Unknown';
  const isOnline = printer.isOnline ?? false;
  const isPrinting = isOnline && (state === 'Printing' || state === 'Busy');
  const isPaused = state === 'Paused';
  const isError = state === 'Error' || state === 'Shutdown';

  const borderColor = !isOnline ? 'border-slate-500' :
    isPrinting ? 'border-green-500' :
    isPaused ? 'border-yellow-500' :
    isError ? 'border-red-500' :
    'border-blue-500';
    
  const glowClass = !isOnline ? '' :
    isPrinting ? 'shadow-[0_0_20px_rgba(34,197,94,0.3)]' :
    isPaused ? 'shadow-[0_0_20px_rgba(234,179,8,0.3)]' :
    isError ? 'shadow-[0_0_20px_rgba(239,68,68,0.3)]' :
    'shadow-[0_0_15px_rgba(59,130,246,0.2)]';

  const accentBg = !isOnline ? 'from-slate-600' :
    isPrinting ? 'from-green-600' :
    isPaused ? 'from-yellow-600' :
    isError ? 'from-red-600' :
    'from-blue-600';

  return (
    <div className={`min-w-[23rem] rounded-xl bg-pf-bg-1 border-2 ${borderColor} ${glowClass} transition-all duration-500 overflow-hidden`}>
      {/* Colored accent strip */}
      <div className={`h-1 bg-gradient-to-r ${accentBg} to-transparent`} />
      
      <div className="p-4">
        {/* Header */}
        <div className="flex justify-between items-start mb-4">
          <div className="flex items-center gap-3">
            {/* Animated pulse indicator */}
            <div className="relative">
              <div className={`w-3 h-3 rounded-full ${
                !isOnline ? 'bg-slate-500' :
                isPrinting ? 'bg-green-500' :
                isPaused ? 'bg-yellow-500' :
                isError ? 'bg-red-500' :
                'bg-blue-500'
              }`} />
              {isOnline && (
                <div className={`absolute inset-0 w-3 h-3 rounded-full animate-ping ${
                  isPrinting ? 'bg-green-500' :
                  isPaused ? 'bg-yellow-500' :
                  isError ? 'bg-red-500' :
                  'bg-blue-500'
                } opacity-75`} />
              )}
            </div>
            <div>
              <h3 className="font-bold text-lg text-pf-text-primary font-bebas uppercase">{printer.name}</h3>
              <p className="text-pf-text-secondary text-xs">{printer.manufacturerName} {printer.modelName}</p>
            </div>
          </div>
          <span className="text-xs font-semibold text-pf-text-secondary uppercase">
            {isOnline ? state : 'Offline'}
          </span>
        </div>

        {/* Temperatures in colored cards */}
        <div className="grid grid-cols-2 gap-3 mb-4">
          <div className="bg-gradient-to-br from-red-500/10 to-transparent rounded-lg p-3 border border-red-500/20">
            <NozzleIcon className="w-5 h-5 text-red-400 mb-1" isOn={(printer.hotendTarget ?? 0) > 0} />
            <div className="text-2xl font-bold text-pf-text-primary">{printer.hotendTemp?.toFixed(0) ?? '---'}°</div>
            {printer.hotendTarget && printer.hotendTarget > 0 && (
              <div className="text-xs text-red-400">→ {printer.hotendTarget}°</div>
            )}
          </div>
          <div className="bg-gradient-to-br from-blue-500/10 to-transparent rounded-lg p-3 border border-blue-500/20">
            <BedIcon className="w-5 h-5 text-blue-400 mb-1" isOn={(printer.bedTarget ?? 0) > 0} />
            <div className="text-2xl font-bold text-pf-text-primary">{printer.bedTemp?.toFixed(0) ?? '---'}°</div>
            {printer.bedTarget && printer.bedTarget > 0 && (
              <div className="text-xs text-blue-400">→ {printer.bedTarget}°</div>
            )}
          </div>
        </div>

        {/* Progress with animated gradient */}
        {isPrinting && printer.progress !== undefined && (
          <div className="mb-4">
            <div className="flex justify-between text-xs mb-1">
              <span className="text-pf-text-secondary truncate">{printer.jobName}</span>
              <span className="font-bold text-green-400">{Math.round(printer.progress)}%</span>
            </div>
            <div className="h-2 bg-pf-bg-0 rounded-full overflow-hidden">
              <div 
                className="h-full bg-gradient-to-r from-green-500 via-emerald-400 to-green-500 bg-[length:200%_100%] animate-[shimmer_2s_infinite] rounded-full"
                style={{ width: `${printer.progress}%` }}
              />
            </div>
          </div>
        )}

        {/* Quick actions */}
        <div className="flex gap-2">
          <Button variant="secondary" size="sm" className="flex-1" onClick={() => onEdit?.(printer)}>
            <EditIcon className="w-4 h-4" />
          </Button>
          <Button variant="secondary" size="sm"><ExternalLinkIcon className="w-4 h-4" /></Button>
          <Button variant="secondary" size="sm"><CameraIcon className="w-4 h-4" /></Button>
        </div>
      </div>
    </div>
  );
}

// ============================================================
// 4. COMPACT DASHBOARD CARD - Information dense with gauges
// ============================================================
export function CompactDashboardCard({ printer: initialPrinter, onEdit }: CardProps) {
  const { data: allPrinters = [] } = usePrinters();
  const apiPrinter = useMemo(
    () => allPrinters.find(p => p.id === initialPrinter.id) ?? initialPrinter,
    [allPrinters, initialPrinter]
  );
  const printer = usePrinterDisplay(apiPrinter);
  
  const state = printer.state ?? 'Unknown';
  const isOnline = printer.isOnline ?? false;
  const isPrinting = isOnline && (state === 'Printing' || state === 'Busy');

  // Mini circular gauge component
  const MiniGauge = ({ value, max, color, label }: { value: number; max: number; color: string; label: string }) => {
    const percentage = Math.min(100, (value / max) * 100);
    const circumference = 2 * Math.PI * 20;
    const strokeDashoffset = circumference - (percentage / 100) * circumference;
    
    return (
      <div className="flex flex-col items-center">
        <svg width="50" height="50" className="transform -rotate-90">
          <circle cx="25" cy="25" r="20" fill="none" stroke="currentColor" className="text-pf-bg-0" strokeWidth="4" />
          <circle 
            cx="25" cy="25" r="20" fill="none" stroke={color} strokeWidth="4"
            strokeDasharray={circumference}
            strokeDashoffset={strokeDashoffset}
            strokeLinecap="round"
            className="transition-all duration-500"
          />
        </svg>
        <span className="text-xs text-pf-text-secondary mt-1">{label}</span>
        <span className="text-sm font-bold text-pf-text-primary">{value.toFixed(0)}°</span>
      </div>
    );
  };

  return (
    <div className="min-w-[20rem] rounded-xl bg-pf-bg-1 border border-pf-border p-3">
      {/* Compact header */}
      <div className="flex justify-between items-center mb-3">
        <div className="flex items-center gap-2">
          {getBackendIcon(printer.backend)}
          <div>
            <h3 className="font-bold text-sm text-pf-text-primary font-bebas uppercase">{printer.name}</h3>
            <p className="text-pf-text-secondary text-[10px]">{printer.manufacturerName}</p>
          </div>
        </div>
        <div className={`w-2 h-2 rounded-full ${
          !isOnline ? 'bg-slate-500' : isPrinting ? 'bg-green-500' : 'bg-blue-500'
        }`} />
      </div>

      {/* Gauge row */}
      <div className="flex justify-around mb-3">
        <MiniGauge value={printer.hotendTemp ?? 0} max={300} color="#ef4444" label="Hotend" />
        <MiniGauge value={printer.bedTemp ?? 0} max={120} color="#3b82f6" label="Bed" />
        {isPrinting && (
          <MiniGauge value={printer.progress ?? 0} max={100} color="#22c55e" label="Progress" />
        )}
      </div>

      {/* Position row */}
      <div className="flex justify-between text-xs bg-pf-bg-0 rounded-lg p-2 mb-3">
        <span className="text-pf-text-secondary">X: <span className="text-pf-text-primary font-mono">{printer.x?.toFixed(1) ?? '---'}</span></span>
        <span className="text-pf-text-secondary">Y: <span className="text-pf-text-primary font-mono">{printer.y?.toFixed(1) ?? '---'}</span></span>
        <span className="text-pf-text-secondary">Z: <span className="text-pf-text-primary font-mono">{printer.z?.toFixed(1) ?? '---'}</span></span>
      </div>

      {/* Job info if printing */}
      {isPrinting && printer.jobName && (
        <div className="text-xs text-pf-text-secondary truncate mb-2">
          📄 {printer.jobName}
        </div>
      )}

      {/* Compact action bar */}
      <div className="flex gap-1">
        <Button variant="secondary" size="sm" className="flex-1 !text-xs !py-1" onClick={() => onEdit?.(printer)}>Edit</Button>
        <Button variant="secondary" size="sm" className="!py-1"><ExternalLinkIcon className="w-3 h-3" /></Button>
      </div>
    </div>
  );
}

// ============================================================
// 5. FLIP CARD - Front shows status, back shows controls
// ============================================================
export function FlipCard({ printer: initialPrinter, onEdit }: CardProps) {
  const { data: allPrinters = [] } = usePrinters();
  const apiPrinter = useMemo(
    () => allPrinters.find(p => p.id === initialPrinter.id) ?? initialPrinter,
    [allPrinters, initialPrinter]
  );
  const printer = usePrinterDisplay(apiPrinter);
  const [isFlipped, setIsFlipped] = useState(false);
  
  const state = printer.state ?? 'Unknown';
  const isOnline = printer.isOnline ?? false;
  const isPrinting = isOnline && (state === 'Printing' || state === 'Busy');

  return (
    <div className="min-w-[23rem] h-[280px] perspective-1000">
      <div 
        className={`relative w-full h-full transition-transform duration-500 transform-style-preserve-3d cursor-pointer ${isFlipped ? 'rotate-y-180' : ''}`}
        onClick={() => setIsFlipped(!isFlipped)}
        style={{ transformStyle: 'preserve-3d' }}
      >
        {/* Front face */}
        <div 
          className="absolute inset-0 rounded-xl bg-pf-bg-1 border border-pf-border p-4 backface-hidden"
          style={{ backfaceVisibility: 'hidden' }}
        >
          <div className="h-full flex flex-col">
            <div className="flex justify-between items-start mb-4">
              <div>
                <h3 className="font-bold text-xl text-pf-text-primary font-bebas uppercase">{printer.name}</h3>
                <p className="text-pf-text-secondary text-sm">{printer.manufacturerName} {printer.modelName}</p>
              </div>
              <div className={`px-3 py-1 rounded-full text-xs font-bold ${
                !isOnline ? 'bg-slate-600 text-white' :
                isPrinting ? 'bg-green-600 text-white' :
                'bg-blue-600 text-white'
              }`}>
                {isOnline ? toCamelCase(state) : 'Offline'}
              </div>
            </div>

            <div className="flex-1 flex items-center justify-center">
              <div className="text-center">
                <div className="flex justify-center gap-8 mb-4">
                  <div>
                    <NozzleIcon className="w-8 h-8 text-red-500 mx-auto mb-1" isOn={(printer.hotendTarget ?? 0) > 0} />
                    <div className="text-2xl font-bold text-pf-text-primary">{printer.hotendTemp?.toFixed(0) ?? '---'}°</div>
                  </div>
                  <div>
                    <BedIcon className="w-8 h-8 text-blue-500 mx-auto mb-1" isOn={(printer.bedTarget ?? 0) > 0} />
                    <div className="text-2xl font-bold text-pf-text-primary">{printer.bedTemp?.toFixed(0) ?? '---'}°</div>
                  </div>
                </div>
                {isPrinting && (
                  <div className="text-4xl font-bold text-green-500">{Math.round(printer.progress ?? 0)}%</div>
                )}
              </div>
            </div>

            <div className="text-center text-xs text-pf-text-secondary">
              Click to flip for controls →
            </div>
          </div>
        </div>

        {/* Back face */}
        <div 
          className="absolute inset-0 rounded-xl bg-pf-bg-1 border border-pf-border p-4 backface-hidden rotate-y-180"
          style={{ backfaceVisibility: 'hidden', transform: 'rotateY(180deg)' }}
        >
          <div className="h-full flex flex-col">
            <div className="flex justify-between items-center mb-4">
              <h3 className="font-bold text-lg text-pf-text-primary">Controls</h3>
              <span className="text-xs text-pf-text-secondary">← Click to flip back</span>
            </div>

            <div className="flex-1 flex flex-col gap-3">
              <div className="text-xs text-pf-text-secondary uppercase font-bold">Temperature</div>
              <div className="grid grid-cols-2 gap-2">
                <Button variant="secondary" size="sm">🔥 Preheat PLA</Button>
                <Button variant="secondary" size="sm">🔥 Preheat ABS</Button>
              </div>
              <Button variant="secondary" size="sm" className="w-full">❄️ Cooldown</Button>

              <div className="text-xs text-pf-text-secondary uppercase font-bold mt-2">Control</div>
              <div className="grid grid-cols-3 gap-2">
                <Button variant="secondary" size="sm" disabled={!isPrinting}><PauseIcon className="w-4 h-4" /></Button>
                <Button variant="success" size="sm" disabled={!isOnline}><PlayIcon className="w-4 h-4" /></Button>
                <Button variant="danger" size="sm" disabled={!isOnline}><EmergencyStopIcon className="w-4 h-4" /></Button>
              </div>
            </div>

            <Button variant="secondary" size="sm" className="mt-2" onClick={(e) => { e.stopPropagation(); onEdit?.(printer); }}>
              <EditIcon className="w-4 h-4 mr-1" /> Edit Printer
            </Button>
          </div>
        </div>
      </div>

      <style>{`
        .perspective-1000 { perspective: 1000px; }
        .transform-style-preserve-3d { transform-style: preserve-3d; }
        .backface-hidden { backface-visibility: hidden; }
        .rotate-y-180 { transform: rotateY(180deg); }
      `}</style>
    </div>
  );
}

// ============================================================
// 6. MINIMAL + DRAWER CARD - Minimal by default, expands on click
// ============================================================
export function DrawerCard({ printer: initialPrinter, onEdit }: CardProps) {
  const { data: allPrinters = [] } = usePrinters();
  const apiPrinter = useMemo(
    () => allPrinters.find(p => p.id === initialPrinter.id) ?? initialPrinter,
    [allPrinters, initialPrinter]
  );
  const printer = usePrinterDisplay(apiPrinter);
  const [isExpanded, setIsExpanded] = useState(false);
  
  const state = printer.state ?? 'Unknown';
  const isOnline = printer.isOnline ?? false;
  const isPrinting = isOnline && (state === 'Printing' || state === 'Busy');

  return (
    <div className={`min-w-[23rem] rounded-xl bg-pf-bg-1 border border-pf-border overflow-hidden transition-all duration-300 ${isExpanded ? 'shadow-xl' : ''}`}>
      {/* Always visible header - clickable to expand */}
      {/* eslint-disable-next-line local/pf-no-raw-html-controls -- This is a disclosure/toggle button, not an action button */}
      <button 
        onClick={() => setIsExpanded(!isExpanded)}
        className="w-full p-4 flex justify-between items-center hover:bg-pf-bg-2 transition-colors text-left"
      >
        <div className="flex items-center gap-3">
          {getBackendIcon(printer.backend)}
          <div>
            <h3 className="font-bold text-lg text-pf-text-primary font-bebas uppercase">{printer.name}</h3>
            <div className="flex items-center gap-2 text-xs text-pf-text-secondary">
              <span className={`w-2 h-2 rounded-full ${
                !isOnline ? 'bg-slate-500' : isPrinting ? 'bg-green-500' : 'bg-blue-500'
              }`} />
              <span>{isOnline ? state : 'Offline'}</span>
              {isPrinting && printer.progress !== undefined && (
                <span className="font-bold text-green-500">• {Math.round(printer.progress)}%</span>
              )}
            </div>
          </div>
        </div>
        <div className="flex items-center gap-3">
          <div className="text-right text-xs">
            <div className="text-pf-text-secondary">🔥 {printer.hotendTemp?.toFixed(0) ?? '---'}°</div>
            <div className="text-pf-text-secondary">🛏️ {printer.bedTemp?.toFixed(0) ?? '---'}°</div>
          </div>
          {isExpanded ? <ChevronUpIcon className="w-5 h-5" /> : <ChevronDownIcon className="w-5 h-5" />}
        </div>
      </button>

      {/* Expandable drawer content */}
      <div className={`overflow-hidden transition-all duration-300 ${isExpanded ? 'max-h-96' : 'max-h-0'}`}>
        <div className="p-4 pt-0 border-t border-pf-border bg-pf-bg-0/50">
          {/* Temperature controls */}
          <div className="mb-4">
            <div className="text-xs text-pf-text-secondary uppercase font-bold mb-2">Temperature Presets</div>
            <div className="flex gap-2 flex-wrap">
              <Button variant="secondary" size="sm">PLA (200/60)</Button>
              <Button variant="secondary" size="sm">PETG (240/80)</Button>
              <Button variant="secondary" size="sm">ABS (250/100)</Button>
              <Button variant="secondary" size="sm"><SnowflakeIcon className="w-4 h-4" /></Button>
            </div>
          </div>

          {/* Control buttons */}
          <div className="mb-4">
            <div className="text-xs text-pf-text-secondary uppercase font-bold mb-2">Print Control</div>
            <div className="flex gap-2">
              <Button variant="secondary" size="sm" disabled={!isPrinting} className="flex-1">
                <PauseIcon className="w-4 h-4 mr-1" /> Pause
              </Button>
              <Button variant="success" size="sm" disabled={!isOnline} className="flex-1">
                <PlayIcon className="w-4 h-4 mr-1" /> Resume
              </Button>
              <Button variant="danger" size="sm" disabled={!isOnline}>
                <EmergencyStopIcon className="w-4 h-4" />
              </Button>
            </div>
          </div>

          {/* Action links */}
          <div className="flex gap-2 pt-2 border-t border-pf-border">
            <Button variant="secondary" size="sm" className="flex-1" onClick={() => onEdit?.(printer)}>
              <EditIcon className="w-4 h-4 mr-1" /> Edit
            </Button>
            <Button variant="secondary" size="sm">
              <ExternalLinkIcon className="w-4 h-4 mr-1" /> Open UI
            </Button>
            <Button variant="secondary" size="sm">
              <HistoryIcon className="w-4 h-4 mr-1" /> History
            </Button>
          </div>
        </div>
      </div>
    </div>
  );
}

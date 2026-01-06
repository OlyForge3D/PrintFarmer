import React, { useState, useCallback } from 'react';
// ...existing code...
import moonrakerIcon from '@/assets/moonraker.svg';
import octoprintIcon from '@/assets/octoprint.svg';
import type { Printer } from '@/types/api';
import { PrinterBackend } from '@/types/api';
import { useAuth } from '@/features/auth/hooks/useAuth';
import { getApiBaseUrl, getAuthHeaders } from '@/common/utils/apiUrlHelpers';
import { 
  ChevronDown, ChevronUp, Cog, Square as StopIcon, Home, Upload, RefreshCw,
  Camera, CameraOff, ExternalLink, History, Thermometer, RotateCcw, FileText
} from 'lucide-react';
import { PlayIcon, PauseIcon, SnowflakeIcon, ArrowUpIcon, ArrowLeftIcon, ArrowRightIcon, ArrowDownIcon, ArrowsAllDirectionsIcon } from '@/common/components/icons/MdiIcons';
import { Button, Input, FileUpload } from '@/common/components/ui';
import { usePrinterDisplay } from '@/common/hooks/usePrinterDisplay';

interface EnhancedPrinterCardProps { printer: Printer; }
interface TempPresets { [k: string]: { hotend: number; bed: number }; }
const DEFAULT_PRESETS: TempPresets = { abs: { hotend: 250, bed: 90 }, asa: { hotend: 260, bed: 95 }, pla: { hotend: 210, bed: 60 }, pc: { hotend: 280, bed: 110 }, pctg: { hotend: 230, bed: 75 }, petg: { hotend: 240, bed: 70 } };

export function EnhancedPrinterCard({ printer: printerProp }: EnhancedPrinterCardProps) {
  const { hasPermission } = useAuth();
  // Merge with realtime SignalR updates
  const printer = usePrinterDisplay(printerProp);
  const [isExpanded, setIsExpanded] = useState(false);
  const [isCameraVisible, setIsCameraVisible] = useState(false);
  const [moveStep, setMoveStep] = useState(10);
  const [tempInputs, setTempInputs] = useState({ hotend: 200, bed: 60 });
  const [moveInputs, setMoveInputs] = useState({ x: 0, y: 0, z: 25 });
  const [selectedFile, setSelectedFile] = useState<File | null>(null);
  const [isUploading, setIsUploading] = useState(false);

  // Use printer data directly (already contains merged realtime status from API)
  const currentStatus = {
    isOnline: printer.isOnline,
    state: printer.state,
    progress: printer.progress,
    jobName: printer.jobName,
    hotendTemp: printer.hotendTemp,
    bedTemp: printer.bedTemp,
    hotendTarget: printer.hotendTarget,
    bedTarget: printer.bedTarget,
    x: printer.x,
    y: printer.y,
    z: printer.z,
    cameraStreamUrl: printer.cameraStreamUrl,
    cameraSnapshotUrl: printer.cameraSnapshotUrl,
  };

  const progressNow = typeof currentStatus.progress === 'number' ? Math.round(currentStatus.progress) : 0;
  const widthClass = (pct: number) => `pf-w-${Math.min(100, Math.max(0, Math.round(pct / 5) * 5))}`;
  const getStatusColor = (online: boolean, state?: string) => !online ? 'bg-gray-100 text-gray-800 border-gray-300' : ({ printing: 'bg-green-100 text-green-800 border-green-300', paused: 'bg-yellow-100 text-yellow-800 border-yellow-300', error: 'bg-red-100 text-red-800 border-red-300', ready: 'bg-blue-100 text-blue-800 border-blue-300', idle: 'bg-blue-100 text-blue-800 border-blue-300', operational: 'bg-blue-100 text-blue-800 border-blue-300' } as Record<string, string>)[(state||'').toLowerCase()] || 'bg-gray-100 text-gray-800 border-gray-300';
  const getBackendIcon = (b: PrinterBackend) => {
    if (b === PrinterBackend.Moonraker) return <img src={moonrakerIcon} alt="Moonraker" title="Moonraker" className="inline h-5 w-5 align-middle" />;
    if (b === PrinterBackend.PrusaLink) return <span title="PrusaLink" aria-label="PrusaLink" role="img">🔗</span>;
    if (b === PrinterBackend.SDCP) return <span title="SDCP" aria-label="SDCP" role="img">📡</span>;
    if (b === PrinterBackend.OctoPrint) return <img src={octoprintIcon} alt="OctoPrint" title="OctoPrint" className="inline h-5 w-5 align-middle" />;
    return <span title="Other" aria-label="Other" role="img">🖨️</span>;
  };
  const formatTemperature = (t?: number, target?: number) => target !== undefined ? `${(t || 0).toFixed(1)}° → ${(target || 0).toFixed(1)}°` : `${(t || 0).toFixed(1)}°`;
  const formatPosition = (v?: number) => v !== undefined ? v.toFixed(1) : '--';
  const isPrinting = currentStatus.isOnline && currentStatus.state === 'printing';
  const isPaused = currentStatus.isOnline && currentStatus.state === 'paused';
  const isShutdown = currentStatus.state === 'shutdown';
  const apiCall = (path: string, body?: unknown) => {
    const init: RequestInit = { method: 'POST' };
    if (body && typeof body === 'object') {
      init.headers = { 'Content-Type': 'application/json', ...getAuthHeaders() };
      init.body = JSON.stringify(body);
    } else {
      init.headers = getAuthHeaders();
    }
    return fetch(`${getApiBaseUrl()}${path}`, init).catch(e => console.error(e));
  };
  const handlePause = useCallback(() => apiCall(`/api/printers/${printer.id}/pause`), [printer.id]);
  const handleResume = useCallback(() => apiCall(`/api/printers/${printer.id}/resume`), [printer.id]);
  const handleEmergencyStop = useCallback(() => apiCall(`/api/printers/${printer.id}/emergency-stop`), [printer.id]);
  const handleFirmwareRestart = useCallback(() => apiCall(`/api/printers/${printer.id}/firmware-restart`), [printer.id]);
  const handleHomeXY = useCallback(() => apiCall(`/api/printers/${printer.id}/homexy`), [printer.id]);
  const handleHomeZ = useCallback(() => apiCall(`/api/printers/${printer.id}/homez`), [printer.id]);
  const handleSetTemperatures = useCallback(() => apiCall(`/api/printers/${printer.id}/temps`, { hotend: tempInputs.hotend, bed: tempInputs.bed }), [printer.id, tempInputs]);
  const handleApplyPreset = useCallback((m: keyof TempPresets) => { const p = DEFAULT_PRESETS[m]; setTempInputs(p); apiCall(`/api/printers/${printer.id}/temps`, p); }, [printer.id]);
  const handleMove = useCallback((x?: number | null, y?: number | null, z?: number | null) => apiCall(`/api/printers/${printer.id}/move`, { x: x || undefined, y: y || undefined, z: z || undefined }), [printer.id]);
  const handleMoveTo = useCallback(() => apiCall(`/api/printers/${printer.id}/move-to`, moveInputs), [printer.id, moveInputs]);
  const handleFileUpload = useCallback(async () => { if (!selectedFile) return; const formData = new FormData(); formData.append('file', selectedFile); setIsUploading(true); try { const r = await fetch(`${getApiBaseUrl()}/printers/${printer.id}/files/upload`, { method: 'POST', body: formData, headers: getAuthHeaders() }); if (r.ok) setSelectedFile(null); } finally { setIsUploading(false); } }, [printer.id, selectedFile]);

  if (!isExpanded) {
    return (
      <div className="bg-white border border-gray-200 rounded-lg p-4 hover:shadow-md transition-shadow overflow-hidden flex flex-col min-h-0">
        <div className="flex items-center justify-between">
          <div className="flex items-center space-x-3 min-w-0 flex-1">
            <span className="text-xl">{getBackendIcon(printer.backend)}</span>
            <div className="min-w-0 flex-1">
              <h3 className="text-lg font-medium text-gray-900 truncate">{printer.name}</h3>
              {(printer.manufacturerName || printer.modelName) && <p className="text-sm text-gray-500 truncate">{[printer.manufacturerName, printer.modelName].filter(Boolean).join(' ')}</p>}
              <div className="flex items-center space-x-2 text-sm text-gray-500">
                <span>{printer.serverUrl}</span>
                <a href={`${printer.serverUrl}${printer.frontendPort && printer.frontendPort !== 80 && printer.frontendPort !== 443 ? ':' + printer.frontendPort : ''}`} target="_blank" rel="noopener noreferrer" className="text-blue-500 hover:text-blue-700" aria-label="Open printer server URL in new tab" title="Open printer server URL in new tab"><ExternalLink className="h-3 w-3" aria-hidden="true" /></a>
              </div>
              <div className="flex items-center space-x-2 text-sm text-gray-500 mt-1">
                <span className={`inline-flex items-center px-2 py-0.5 rounded-full text-xs font-medium border ${getStatusColor(currentStatus.isOnline, currentStatus.state)}`}>{currentStatus.isOnline ? (currentStatus.state || 'Online') : 'Offline'}</span>
              </div>
            </div>
          </div>
          <div className="flex items-center space-x-2">
            {hasPermission('printers', 'execute') && currentStatus.isOnline && <>
              <Button
                type="button"
                variant="subtle"
                size="sm"
                onClick={handlePause}
                disabled={!isPrinting}
                title="Pause"
                aria-label="Pause"
                className="!p-2 !h-auto"
                iconCenter={<PauseIcon className="h-4 w-4" />}
              >
        </Button>
              <Button
                type="button"
                variant="subtle"
                size="sm"
                onClick={handleResume}
                disabled={!isPaused}
                title="Resume"
                aria-label="Resume"
                className="!p-2 !h-auto"
                iconCenter={<PlayIcon className="h-4 w-4" />}
              >
        </Button>
              <Button
                type="button"
                variant={isShutdown ? 'secondary' : 'danger'}
                size="sm"
                onClick={isShutdown ? handleFirmwareRestart : handleEmergencyStop}
                title={isShutdown ? 'Firmware Restart' : 'Emergency Stop'}
                className="!p-2 !h-auto"
                iconCenter={isShutdown ? <RotateCcw className="h-4 w-4" /> : <StopIcon className="h-4 w-4" />}
              >
        </Button>
            </>
            }
            <Button
              type="button"
              variant="subtle"
              size="sm"
              onClick={() => setIsExpanded(true)}
              title="Expand"
              aria-label="Expand"
              className="!p-2 !h-auto"
              iconCenter={<ChevronDown className="h-4 w-4" />}
            >
        </Button>
          </div>
        </div>
        {progressNow > 0 && (
          <div className="mt-3">
            <div className="flex justify-between text-sm text-gray-600 mb-1">
              <span className="truncate">{currentStatus.jobName || 'Printing...'}</span>
              <span>{progressNow}%</span>
            </div>
            <div className="w-full" aria-label="Print progress">
              <div className="relative h-2 bg-gray-200 rounded-full overflow-hidden">
                <div className={`absolute left-0 top-0 h-2 bg-green-600 rounded-full transition-all duration-300 ${widthClass(progressNow)}`} />
              </div>
            </div>
          </div>
        )}
      </div>
    );
  }

  return (
    <div className="bg-white border border-gray-200 rounded-lg shadow-lg">
      <div className="p-4 border-b border-gray-200">
        <div className="flex items-center justify-between">
          <div className="flex items-center space-x-3 min-w-0 flex-1">
            <span className="text-xl">{getBackendIcon(printer.backend)}</span>
            <div className="min-w-0 flex-1">
              <h3 className="text-lg font-medium text-gray-900">{printer.name}</h3>
              {(printer.manufacturerName || printer.modelName) && <p className="text-sm text-gray-500">{[printer.manufacturerName, printer.modelName].filter(Boolean).join(' ')}</p>}
              <div className="flex items-center space-x-2 text-sm text-gray-500">
                <span>{printer.serverUrl}</span>
                <a href={`${printer.serverUrl}${printer.frontendPort && printer.frontendPort !== 80 && printer.frontendPort !== 443 ? ':' + printer.frontendPort : ''}`} target="_blank" rel="noopener noreferrer" className="text-blue-500 hover:text-blue-700" aria-label="Open printer server URL in new tab" title="Open printer server URL in new tab"><ExternalLink className="h-3 w-3" aria-hidden="true" /></a>
                {(currentStatus.cameraSnapshotUrl || currentStatus.cameraStreamUrl) && (
                  <Button
                    type="button"
                    variant="subtle"
                    size="sm"
                    onClick={() => setIsCameraVisible(!isCameraVisible)}
                    title={isCameraVisible ? 'Hide camera' : 'Show camera'}
                    className="!p-0 !h-auto text-blue-500 hover:text-blue-700"
                    iconCenter={isCameraVisible ? <CameraOff className="h-3 w-3" /> : <Camera className="h-3 w-3" />}
                  >
        </Button>
                )}
              </div>
            </div>
          </div>
          <div className="flex items-center space-x-2">
            <Button
              type="button"
              variant="subtle"
              size="sm"
              onClick={() => setIsExpanded(false)}
              title="Collapse"
              aria-label="Collapse"
              className="!p-2 !h-auto"
              iconCenter={<ChevronUp className="h-4 w-4" />}
            >
        </Button>
            <Button
              type="button"
              variant="subtle"
              size="sm"
              title="History"
              aria-label="History"
              className="!p-2 !h-auto"
              iconCenter={<History className="h-4 w-4" />}
            >
        </Button>
            <Button
              type="button"
              variant="subtle"
              size="sm"
              title="Settings"
              aria-label="Settings"
              className="!p-2 !h-auto"
              iconCenter={<Cog className="h-4 w-4" />}
            >
        </Button>
          </div>
        </div>
        {isCameraVisible && (currentStatus.cameraSnapshotUrl || currentStatus.cameraStreamUrl) && (
          <div className="mt-3">
            <img
              src={`${currentStatus.cameraSnapshotUrl || currentStatus.cameraStreamUrl}?t=${Date.now()}`}
              alt="Camera snapshot"
              className="w-full h-32 object-cover rounded border"
              onError={(e) => (e.target as HTMLImageElement).style.display = 'none'}
            />
            {/* For OctoPrint, optionally show a note if only stream is available */}
            {printer.backend === PrinterBackend.OctoPrint && !currentStatus.cameraSnapshotUrl && currentStatus.cameraStreamUrl && (
              <div className="text-xs text-gray-400 mt-1">Live stream only (no snapshot)</div>
            )}
          </div>
        )}
      </div>
      <div className="p-4 border-b border-gray-200">
        <h4 className="text-sm font-medium text-gray-700 mb-3 flex items-center"><Thermometer className="h-4 w-4 mr-2" />Temperatures</h4>
        <div className="grid grid-cols-3 gap-4 mb-3">
          <div><label className="block text-xs text-gray-500 mb-1" htmlFor={`hotend-${printer.id}`}>Hotend</label><div className="relative"><Input id={`hotend-${printer.id}`} type="number" value={tempInputs.hotend} onChange={(e) => setTempInputs(p => ({ ...p, hotend: Number(e.target.value) }))} disabled={isPrinting} aria-label="Hotend target temperature" /><span className="absolute right-2 top-1/2 -translate-y-1/2 text-xs text-gray-400">°C</span></div><div className="text-xs text-gray-500 mt-1">[{formatTemperature(currentStatus.hotendTemp, currentStatus.hotendTarget)}]</div></div>
          <div><label className="block text-xs text-gray-500 mb-1" htmlFor={`bed-${printer.id}`}>Bed</label><div className="relative"><Input id={`bed-${printer.id}`} type="number" value={tempInputs.bed} onChange={(e) => setTempInputs(p => ({ ...p, bed: Number(e.target.value) }))} disabled={isPrinting} aria-label="Bed target temperature" /><span className="absolute right-2 top-1/2 -translate-y-1/2 text-xs text-gray-400">°C</span></div><div className="text-xs text-gray-500 mt-1">[{formatTemperature(currentStatus.bedTemp, currentStatus.bedTarget)}]</div></div>
          <div className="flex items-end"><Button
                type="button"
                variant="primary"
                size="sm"
                onClick={handleSetTemperatures}
                disabled={isPrinting}
                className="w-full"
              >
                SET
              </Button></div>
        </div>
  <div className="flex flex-wrap gap-2">{Object.keys(DEFAULT_PRESETS).map(m => <Button
              type="button"
              key={m}
              variant="secondary"
              size="sm"
              onClick={() => handleApplyPreset(m as keyof TempPresets)}
              disabled={isPrinting}
              title={`${DEFAULT_PRESETS[m].hotend}°/${DEFAULT_PRESETS[m].bed}°`}
            >
              {m.toUpperCase()}
            </Button>)}<Button
            type="button"
            variant="primary"
            size="sm"
            onClick={() => handleApplyPreset('pla')}
            disabled={isPrinting}
            title="Cooldown (0°/0°)"
            aria-label="Cooldown (0°/0°)"
            iconCenter={<SnowflakeIcon className="h-4 w-4" />}
          >
        </Button></div>
      </div>
      <div className="p-4 border-b border-gray-200">
        <h4 className="text-sm font-medium text-gray-700 mb-3 flex items-center">
          <ArrowsAllDirectionsIcon className="h-4 w-4 mr-2" />
          Movement
        </h4>
        <div className="grid grid-cols-2 gap-4">
          <div>
            <div className="grid grid-cols-3 gap-1 mb-2 text-center">
              <div />
              <Button
                type="button"
                variant="secondary"
                size="sm"
                onClick={() => handleMove(null, moveStep, null)}
                disabled={isPrinting}
                className="p-2"
                iconCenter={<ArrowUpIcon className="h-4 w-4" />}
              >
        </Button>
              <div />
              <Button
                type="button"
                variant="secondary"
                size="sm"
                onClick={() => handleMove(-moveStep, null, null)}
                disabled={isPrinting}
                className="p-2"
                iconCenter={<ArrowLeftIcon className="h-4 w-4" />}
              >
        </Button>
              <Button
                type="button"
                variant="secondary"
                size="sm"
                onClick={handleHomeXY}
                disabled={isPrinting}
                title="Home XY"
                aria-label="Home XY"
                className="p-2"
                iconCenter={<Home className="h-3 w-3" />}
              >
        </Button>
              <Button
                type="button"
                variant="secondary"
                size="sm"
                onClick={() => handleMove(moveStep, null, null)}
                disabled={isPrinting}
                className="p-2"
                iconCenter={<ArrowRightIcon className="h-4 w-4" />}
              >
        </Button>
              <div />
              <Button
                type="button"
                variant="secondary"
                size="sm"
                onClick={() => handleMove(null, -moveStep, null)}
                disabled={isPrinting}
                className="p-2"
                iconCenter={<ArrowDownIcon className="h-4 w-4" />}
              >
        </Button><div /></div><div className="text-center text-xs text-gray-500">X: {formatPosition(currentStatus.x)} Y: {formatPosition(currentStatus.y)}</div></div>
          <div className="text-center"><div className="space-y-1 mb-2">
            <Button
                type="button"
                variant="secondary"
                size="sm"
                onClick={() => handleMove(null, null, moveStep)}
                disabled={isPrinting}
                className="w-full"
              >
                Z+
              </Button>
              <Button
                type="button"
                variant="secondary"
                size="sm"
                onClick={handleHomeZ}
                disabled={isPrinting}
                title="Home Z"
                aria-label="Home Z"
                className="w-full"
                iconCenter={<Home className="h-3 w-3" />}
              >
        </Button>
              <Button
                type="button"
                variant="secondary"
                size="sm"
                onClick={() => handleMove(null, null, -moveStep)}
                disabled={isPrinting}
                className="w-full"
              >
                Z-
              </Button></div><div className="text-xs text-gray-500">Z: {formatPosition(currentStatus.z)}</div></div>
        </div>
        <div className="mt-4 grid grid-cols-2 gap-4">
          <div><label className="block text-xs text-gray-500 mb-1">Step Size</label><div className="flex space-x-1">{[1, 10, 50].map(step => <Button
                type="button"
                key={step}
                variant={moveStep === step ? 'primary' : 'secondary'}
                size="sm"
                onClick={() => setMoveStep(step)}
              >
                {step}
              </Button>)}</div></div>
          <div><label className="block text-xs text-gray-500 mb-1">Go To</label><div className="flex space-x-1"><Input type="number" placeholder="X" value={moveInputs.x} onChange={(e) => setMoveInputs(p => ({ ...p, x: Number(e.target.value) }))} disabled={isPrinting} aria-label="Go to X" className="w-12" /><Input type="number" placeholder="Y" value={moveInputs.y} onChange={(e) => setMoveInputs(p => ({ ...p, y: Number(e.target.value) }))} disabled={isPrinting} aria-label="Go to Y" className="w-12" /><Input type="number" placeholder="Z" value={moveInputs.z} onChange={(e) => setMoveInputs(p => ({ ...p, z: Number(e.target.value) }))} disabled={isPrinting} aria-label="Go to Z" className="w-12" /><Button
              type="button"
              variant="primary"
              size="sm"
              onClick={handleMoveTo}
              disabled={isPrinting}
            >
              GO
            </Button></div></div>
        </div>
      </div>
      <div className="p-4 border-b border-gray-200">
        <div className="flex items-center justify-between">
          <h4 className="text-sm font-medium text-gray-700">Print Controls</h4>
          <div className="flex space-x-2">
            {/* Only show Pause/Resume for supported backends */}
            {[PrinterBackend.Moonraker, PrinterBackend.PrusaLink, PrinterBackend.OctoPrint].includes(printer.backend) && (
              <>
                <Button
                  type="button"
                  variant="secondary"
                  size="sm"
                  onClick={handlePause}
                  disabled={!isPrinting}
                  iconLeft={<PauseIcon className="h-4 w-4" />}
                >
                  Pause
                </Button>
                <Button
                  type="button"
                  variant="success"
                  size="sm"
                  onClick={handleResume}
                  disabled={!isPaused}
                  iconLeft={<PlayIcon className="h-4 w-4" />}
                >
                  Resume
                </Button>
              </>
            )}
            <Button
              type="button"
              variant={isShutdown ? 'secondary' : 'danger'}
              size="sm"
              onClick={isShutdown ? handleFirmwareRestart : handleEmergencyStop}
              disabled={!currentStatus.isOnline}
              iconLeft={isShutdown ? <RotateCcw className="h-4 w-4" /> : <StopIcon className="h-4 w-4" />}
            >
              {isShutdown ? 'Restart' : 'Stop'}
            </Button>
          </div>
        </div>
        {progressNow > 0 && (
          <div className="mt-3">
            <div className="flex justify-between text-sm text-gray-600 mb-1">
              <span className="truncate">{currentStatus.jobName || 'Printing...'}</span>
              <span>{progressNow}%</span>
            </div>
            <div className="w-full" aria-label="Print progress">
              <div className="relative h-2 bg-gray-200 rounded-full overflow-hidden">
                <div className={`absolute left-0 top-0 h-2 bg-green-600 rounded-full transition-all duration-300 ${widthClass(progressNow)}`} />
              </div>
            </div>
          </div>
        )}
      </div>
      <div className="p-4">
        <h4 className="text-sm font-medium text-gray-700 mb-3 flex items-center"><FileText className="h-4 w-4 mr-2" />Files</h4>
          <div className="flex items-center space-x-2"><div className="flex-1"><FileUpload accept=".gcode" aria-label="Upload GCode file" onChange={(e) => setSelectedFile(e.target.files?.[0] || null)} disabled={isPrinting || isUploading} /></div>
          <Button
            type="button"
            variant="primary"
            size="sm"
            onClick={handleFileUpload}
            disabled={!selectedFile || isPrinting || isUploading}
            iconLeft={isUploading ? <RefreshCw className="h-4 w-4 animate-spin" /> : <Upload className="h-4 w-4" />}
          >
            Upload
          </Button></div>
      </div>
    </div>
  );
}
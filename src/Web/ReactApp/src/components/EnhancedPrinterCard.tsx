import React, { useState, useCallback } from 'react';
// ...existing code...
import moonrakerIcon from '@/assets/moonraker.svg';
import octoprintIcon from '@/assets/octoprint.svg';
import type { Printer } from '@/types/api';
import { PrinterBackend } from '@/types/api';
import { usePrinterStatusUpdates } from '@/hooks/useSignalR';
import { useAuth } from '@/contexts/AuthContext';
import { 
  ChevronDown, ChevronUp, Cog, Play, Pause, Square as StopIcon, Home, Upload, RefreshCw,
  Camera, CameraOff, ExternalLink, History, Thermometer, RotateCcw, Move, FileText
} from 'lucide-react';

interface EnhancedPrinterCardProps { printer: Printer; }
interface TempPresets { [k: string]: { hotend: number; bed: number }; }
const DEFAULT_PRESETS: TempPresets = { abs: { hotend: 250, bed: 90 }, asa: { hotend: 260, bed: 95 }, pla: { hotend: 210, bed: 60 }, pc: { hotend: 280, bed: 110 }, pctg: { hotend: 230, bed: 75 }, petg: { hotend: 240, bed: 70 } };

export function EnhancedPrinterCard({ printer }: EnhancedPrinterCardProps) {
  const { hasPermission } = useAuth();
  const { getPrinterStatus } = usePrinterStatusUpdates();
  const realtimeStatus = getPrinterStatus(printer.id);
  const [isExpanded, setIsExpanded] = useState(false);
  const [isCameraVisible, setIsCameraVisible] = useState(false);
  const [moveStep, setMoveStep] = useState(10);
  const [tempInputs, setTempInputs] = useState({ hotend: 200, bed: 60 });
  const [moveInputs, setMoveInputs] = useState({ x: 0, y: 0, z: 25 });
  const [selectedFile, setSelectedFile] = useState<File | null>(null);
  const [isUploading, setIsUploading] = useState(false);

  const currentStatus = {
    isOnline: realtimeStatus?.isOnline ?? printer.isOnline,
    state: realtimeStatus?.state ?? printer.state,
    progress: realtimeStatus?.progress ?? printer.progress,
    jobName: realtimeStatus?.jobName ?? printer.jobName,
    hotendTemp: realtimeStatus?.hotendTemp ?? printer.hotendTemp,
    bedTemp: realtimeStatus?.bedTemp ?? printer.bedTemp,
    hotendTarget: realtimeStatus?.hotendTarget ?? printer.hotendTarget,
    bedTarget: realtimeStatus?.bedTarget ?? printer.bedTarget,
    x: realtimeStatus?.x ?? printer.x,
    y: realtimeStatus?.y ?? printer.y,
    z: realtimeStatus?.z ?? printer.z,
    cameraStreamUrl: realtimeStatus?.cameraStreamUrl ?? printer.cameraStreamUrl,
    cameraSnapshotUrl: realtimeStatus?.cameraSnapshotUrl ?? printer.cameraSnapshotUrl,
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
  const formatTemperature = (t?: number, target?: number) => target !== undefined ? `${Math.round(t || 0)}° → ${Math.round(target)}°` : `${Math.round(t || 0)}°`;
  const formatPosition = (v?: number) => v !== undefined ? v.toFixed(1) : '--';
  const isPrinting = currentStatus.isOnline && currentStatus.state === 'printing';
  const isPaused = currentStatus.isOnline && currentStatus.state === 'paused';
  const isShutdown = currentStatus.state === 'shutdown';
  const apiCall = (path: string, body?: unknown) => {
    const init: RequestInit = { method: 'POST' };
    if (body && typeof body === 'object') {
      init.headers = { 'Content-Type': 'application/json' };
      init.body = JSON.stringify(body);
    }
    return fetch(path, init).catch(e => console.error(e));
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
  const handleFileUpload = useCallback(async () => { if (!selectedFile) return; const formData = new FormData(); formData.append('file', selectedFile); setIsUploading(true); try { const r = await fetch(`/api/printers/${printer.id}/files`, { method: 'POST', body: formData }); if (r.ok) setSelectedFile(null); } finally { setIsUploading(false); } }, [printer.id, selectedFile]);

  if (!isExpanded) {
    return (
      <div className="bg-white border border-gray-200 rounded-lg p-4 hover:shadow-md transition-shadow">
        <div className="flex items-center justify-between">
          <div className="flex items-center space-x-3 min-w-0 flex-1">
            <span className="text-xl">{getBackendIcon(printer.backend)}</span>
            <div className="min-w-0 flex-1">
              <h3 className="text-lg font-medium text-gray-900 truncate">{printer.name}</h3>
              {(printer.manufacturerName || printer.modelName) && <p className="text-sm text-gray-500 truncate">{[printer.manufacturerName, printer.modelName].filter(Boolean).join(' ')}</p>}
              <div className="flex items-center space-x-2 text-sm text-gray-500">
                <span>{printer.serverUrl}</span>
                <a href={printer.serverUrl?.replace(/:\d+/, ':80')} target="_blank" rel="noopener noreferrer" className="text-blue-500 hover:text-blue-700" aria-label="Open printer server URL in new tab" title="Open printer server URL in new tab"><ExternalLink className="h-3 w-3" aria-hidden="true" /></a>
              </div>
              <div className="flex items-center space-x-2 text-sm text-gray-500 mt-1">
                <span className={`inline-flex items-center px-2 py-0.5 rounded-full text-xs font-medium border ${getStatusColor(currentStatus.isOnline, currentStatus.state)}`}>{currentStatus.isOnline ? (currentStatus.state || 'Online') : 'Offline'}</span>
              </div>
            </div>
          </div>
          <div className="flex items-center space-x-2">
            {hasPermission('printers', 'execute') && currentStatus.isOnline && <>
              <button onClick={handlePause} disabled={!isPrinting} className="p-2 text-gray-500 hover:text-gray-700 disabled:opacity-50 disabled:cursor-not-allowed" title="Pause"><Pause className="h-4 w-4" /></button>
              <button onClick={handleResume} disabled={!isPaused} className="p-2 text-gray-500 hover:text-gray-700 disabled:opacity-50 disabled:cursor-not-allowed" title="Resume"><Play className="h-4 w-4" /></button>
              <button onClick={isShutdown ? handleFirmwareRestart : handleEmergencyStop} className={`p-2 ${isShutdown ? 'text-amber-600 hover:text-amber-700' : 'text-red-500 hover:text-red-700'}`} title={isShutdown ? 'Firmware Restart' : 'Emergency Stop'}>{isShutdown ? <RotateCcw className="h-4 w-4" /> : <StopIcon className="h-4 w-4" />}</button>
            </>}
            <button onClick={() => setIsExpanded(true)} className="p-2 text-gray-500 hover:text-gray-700" title="Expand"><ChevronDown className="h-4 w-4" /></button>
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
                <a href={printer.serverUrl?.replace(/:\d+/, ':80')} target="_blank" rel="noopener noreferrer" className="text-blue-500 hover:text-blue-700" aria-label="Open printer server URL in new tab" title="Open printer server URL in new tab"><ExternalLink className="h-3 w-3" aria-hidden="true" /></a>
                {(currentStatus.cameraSnapshotUrl || currentStatus.cameraStreamUrl) && (
                  <button onClick={() => setIsCameraVisible(!isCameraVisible)} className="text-blue-500 hover:text-blue-700" title={isCameraVisible ? 'Hide camera' : 'Show camera'}>
                    {isCameraVisible ? <CameraOff className="h-3 w-3" /> : <Camera className="h-3 w-3" />}
                  </button>
                )}
              </div>
            </div>
          </div>
          <div className="flex items-center space-x-2">
            <button onClick={() => setIsExpanded(false)} className="p-2 text-gray-500 hover:text-gray-700" title="Collapse"><ChevronUp className="h-4 w-4" /></button>
            <button className="p-2 text-gray-500 hover:text-gray-700" title="History"><History className="h-4 w-4" /></button>
            <button className="p-2 text-gray-500 hover:text-gray-700" title="Settings"><Cog className="h-4 w-4" /></button>
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
          <div><label className="block text-xs text-gray-500 mb-1" htmlFor={`hotend-${printer.id}`}>Hotend</label><div className="relative"><input id={`hotend-${printer.id}`} type="number" value={tempInputs.hotend} onChange={(e) => setTempInputs(p => ({ ...p, hotend: Number(e.target.value) }))} className="w-full px-2 py-1 text-sm border border-gray-300 rounded focus:ring-blue-500 focus:border-blue-500" disabled={isPrinting} aria-label="Hotend target temperature" /><span className="absolute right-2 top-1/2 -translate-y-1/2 text-xs text-gray-400">°C</span></div><div className="text-xs text-gray-500 mt-1">[{formatTemperature(currentStatus.hotendTemp, currentStatus.hotendTarget)}]</div></div>
          <div><label className="block text-xs text-gray-500 mb-1" htmlFor={`bed-${printer.id}`}>Bed</label><div className="relative"><input id={`bed-${printer.id}`} type="number" value={tempInputs.bed} onChange={(e) => setTempInputs(p => ({ ...p, bed: Number(e.target.value) }))} className="w-full px-2 py-1 text-sm border border-gray-300 rounded focus:ring-blue-500 focus:border-blue-500" disabled={isPrinting} aria-label="Bed target temperature" /><span className="absolute right-2 top-1/2 -translate-y-1/2 text-xs text-gray-400">°C</span></div><div className="text-xs text-gray-500 mt-1">[{formatTemperature(currentStatus.bedTemp, currentStatus.bedTarget)}]</div></div>
          <div className="flex items-end"><button onClick={handleSetTemperatures} disabled={isPrinting} className="w-full px-3 py-1 text-sm font-medium text-white bg-blue-600 hover:bg-blue-700 disabled:bg-gray-300 disabled:cursor-not-allowed rounded">SET</button></div>
        </div>
        <div className="flex flex-wrap gap-2">{Object.keys(DEFAULT_PRESETS).map(m => <button key={m} onClick={() => handleApplyPreset(m as keyof TempPresets)} disabled={isPrinting} className="px-3 py-1 text-xs font-medium text-white bg-gray-600 hover:bg-gray-700 disabled:bg-gray-300 disabled:cursor-not-allowed rounded" title={`${DEFAULT_PRESETS[m].hotend}°/${DEFAULT_PRESETS[m].bed}°`}>{m.toUpperCase()}</button>)}<button onClick={() => handleApplyPreset('pla')} disabled={isPrinting} className="px-3 py-1 text-xs font-medium text-white bg-blue-600 hover:bg-blue-700 disabled:bg-gray-300 disabled:cursor-not-allowed rounded" title="Cooldown (0°/0°)">❄</button></div>
      </div>
      <div className="p-4 border-b border-gray-200">
        <h4 className="text-sm font-medium text-gray-700 mb-3 flex items-center"><Move className="h-4 w-4 mr-2" />Movement</h4>
        <div className="grid grid-cols-2 gap-4">
          <div><div className="grid grid-cols-3 gap-1 mb-2 text-center"><div /><button onClick={() => handleMove(null, moveStep, null)} disabled={isPrinting} className="p-2 text-sm bg-gray-100 hover:bg-gray-200 disabled:bg-gray-50 disabled:cursor-not-allowed rounded">▲</button><div /><button onClick={() => handleMove(-moveStep, null, null)} disabled={isPrinting} className="p-2 text-sm bg-gray-100 hover:bg-gray-200 disabled:bg-gray-50 disabled:cursor-not-allowed rounded">◀</button><button onClick={handleHomeXY} disabled={isPrinting} className="p-2 text-sm bg-gray-200 hover:bg-gray-300 disabled:bg-gray-50 disabled:cursor-not-allowed rounded" title="Home XY"><Home className="h-3 w-3 mx-auto" /></button><button onClick={() => handleMove(moveStep, null, null)} disabled={isPrinting} className="p-2 text-sm bg-gray-100 hover:bg-gray-200 disabled:bg-gray-50 disabled:cursor-not-allowed rounded">▶</button><div /><button onClick={() => handleMove(null, -moveStep, null)} disabled={isPrinting} className="p-2 text-sm bg-gray-100 hover:bg-gray-200 disabled:bg-gray-50 disabled:cursor-not-allowed rounded">▼</button><div /></div><div className="text-center text-xs text-gray-500">X: {formatPosition(currentStatus.x)} Y: {formatPosition(currentStatus.y)}</div></div>
          <div className="text-center"><div className="space-y-1 mb-2"><button onClick={() => handleMove(null, null, moveStep)} disabled={isPrinting} className="w-full p-2 text-sm bg-gray-100 hover:bg-gray-200 disabled:bg-gray-50 disabled:cursor-not-allowed rounded">Z+</button><button onClick={handleHomeZ} disabled={isPrinting} className="w-full p-2 text-sm bg-gray-200 hover:bg-gray-300 disabled:bg-gray-50 disabled:cursor-not-allowed rounded" title="Home Z"><Home className="h-3 w-3 mx-auto" /></button><button onClick={() => handleMove(null, null, -moveStep)} disabled={isPrinting} className="w-full p-2 text-sm bg-gray-100 hover:bg-gray-200 disabled:bg-gray-50 disabled:cursor-not-allowed rounded">Z-</button></div><div className="text-xs text-gray-500">Z: {formatPosition(currentStatus.z)}</div></div>
        </div>
        <div className="mt-4 grid grid-cols-2 gap-4">
          <div><label className="block text-xs text-gray-500 mb-1">Step Size</label><div className="flex space-x-1">{[1, 10, 50].map(step => <button key={step} onClick={() => setMoveStep(step)} className={`px-2 py-1 text-xs rounded ${moveStep === step ? 'bg-blue-100 text-blue-700 border border-blue-300' : 'bg-gray-100 text-gray-700 hover:bg-gray-200'}`}>{step}</button>)}</div></div>
          <div><label className="block text-xs text-gray-500 mb-1">Go To</label><div className="flex space-x-1"><input type="number" placeholder="X" value={moveInputs.x} onChange={(e) => setMoveInputs(p => ({ ...p, x: Number(e.target.value) }))} className="w-12 px-1 py-1 text-xs border border-gray-300 rounded" disabled={isPrinting} aria-label="Go to X" /><input type="number" placeholder="Y" value={moveInputs.y} onChange={(e) => setMoveInputs(p => ({ ...p, y: Number(e.target.value) }))} className="w-12 px-1 py-1 text-xs border border-gray-300 rounded" disabled={isPrinting} aria-label="Go to Y" /><input type="number" placeholder="Z" value={moveInputs.z} onChange={(e) => setMoveInputs(p => ({ ...p, z: Number(e.target.value) }))} className="w-12 px-1 py-1 text-xs border border-gray-300 rounded" disabled={isPrinting} aria-label="Go to Z" /><button onClick={handleMoveTo} disabled={isPrinting} className="px-2 py-1 text-xs font-medium text-white bg-blue-600 hover:bg-blue-700 disabled:bg-gray-300 disabled:cursor-not-allowed rounded">GO</button></div></div>
        </div>
      </div>
      <div className="p-4 border-b border-gray-200">
        <div className="flex items-center justify-between">
          <h4 className="text-sm font-medium text-gray-700">Print Controls</h4>
          <div className="flex space-x-2">
            {/* Only show Pause/Resume for supported backends */}
            {[PrinterBackend.Moonraker, PrinterBackend.PrusaLink, PrinterBackend.OctoPrint].includes(printer.backend) && (
              <>
                <button onClick={handlePause} disabled={!isPrinting} className="px-3 py-1 text-sm font-medium text-white bg-yellow-600 hover:bg-yellow-700 disabled:bg-gray-300 disabled:cursor-not-allowed rounded flex items-center"><Pause className="h-3 w-3 mr-1" />Pause</button>
                <button onClick={handleResume} disabled={!isPaused} className="px-3 py-1 text-sm font-medium text-white bg-green-600 hover:bg-green-700 disabled:bg-gray-300 disabled:cursor-not-allowed rounded flex items-center"><Play className="h-3 w-3 mr-1" />Resume</button>
              </>
            )}
            <button onClick={isShutdown ? handleFirmwareRestart : handleEmergencyStop} disabled={!currentStatus.isOnline} className={`px-3 py-1 text-sm font-medium text-white rounded flex items-center disabled:bg-gray-300 disabled:cursor-not-allowed ${isShutdown ? 'bg-amber-600 hover:bg-amber-700' : 'bg-red-600 hover:bg-red-700'}`}>{isShutdown ? <RotateCcw className="h-3 w-3 mr-1" /> : <StopIcon className="h-3 w-3 mr-1" />}{isShutdown ? 'Restart' : 'Stop'}</button>
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
        <div className="flex items-center space-x-2"><div className="flex-1"><input type="file" accept=".gcode" aria-label="Upload GCode file" onChange={(e) => setSelectedFile(e.target.files?.[0] || null)} disabled={isPrinting || isUploading} className="block w-full text-sm text-gray-500 file:mr-4 file:py-1 file:px-3 file:rounded file:border-0 file:text-sm file:font-medium file:bg-blue-50 file:text-blue-700 hover:file:bg-blue-100 disabled:opacity-50" /></div><button onClick={handleFileUpload} disabled={!selectedFile || isPrinting || isUploading} className="px-3 py-1 text-sm font-medium text-white bg-blue-600 hover:bg-blue-700 disabled:bg-gray-300 disabled:cursor-not-allowed rounded flex items-center">{isUploading ? <RefreshCw className="h-3 w-3 mr-1 pf-animate-spin" /> : <Upload className="h-3 w-3 mr-1" />}Upload</button></div>
      </div>
    </div>
  );
}
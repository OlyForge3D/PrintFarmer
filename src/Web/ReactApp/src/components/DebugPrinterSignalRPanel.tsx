import React, { useEffect, useState } from 'react';
import { printerSignalRService } from '@/services/printer-signalr';
import { renderUnknown } from '@/utils/renderUnknown';

export function DebugPrinterSignalRPanel() {
  const [last, setLast] = useState<unknown>(null);
  useEffect(() => {
    const unsub = printerSignalRService.onPrinterStatusUpdate((s: unknown) => {
      setLast(s);
    });
    printerSignalRService.connect();
    return () => unsub();
  }, []);

  if (!import.meta.env.VITE_PRINTFARMER_DEBUG) return null;

  return (
    <div className="fixed right-3 bottom-3 z-50 bg-black/60 text-white p-2 rounded-md max-w-[420px]">
  <div className="text-xs font-bold">SignalR Debug</div>
  <div className="text-[11px] whitespace-pre-wrap max-h-[240px] overflow-auto">{renderUnknown(last)}</div>
    </div>
  );
}

export default DebugPrinterSignalRPanel;

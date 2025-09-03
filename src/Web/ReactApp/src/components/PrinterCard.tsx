import { PrinterBackend } from '@/types/api';
import type { Printer } from '@/types/api';
import { usePrinterStatusUpdates } from '@/hooks/useSignalR';

interface PrinterCardProps {
  printer: Printer;
}

export function PrinterCard({ printer }: PrinterCardProps) {
  const { getPrinterStatus } = usePrinterStatusUpdates();
  const realtimeStatus = getPrinterStatus(printer.id);

  // Use realtime status if available, otherwise use the printer data
  const currentStatus = realtimeStatus || {
    isOnline: printer.isOnline,
    state: printer.state,
    progress: printer.progress,
    jobName: printer.jobName,
    hotendTemp: printer.hotendTemp,
    bedTemp: printer.bedTemp,
    hotendTarget: printer.hotendTarget,
    bedTarget: printer.bedTarget,
  };

  const getStatusColor = (isOnline: boolean, state?: string) => {
    if (!isOnline) return 'bg-gray-100 text-gray-800';
    
    switch (state?.toLowerCase()) {
      case 'printing':
        return 'bg-green-100 text-green-800';
      case 'paused':
        return 'bg-yellow-100 text-yellow-800';
      case 'error':
        return 'bg-red-100 text-red-800';
      case 'ready':
      case 'idle':
        return 'bg-blue-100 text-blue-800';
      default:
        return 'bg-gray-100 text-gray-800';
    }
  };

  const getBackendIcon = (backend: PrinterBackend) => {
    switch (backend) {
      case PrinterBackend.Moonraker:
        return '🌙';
      case PrinterBackend.PrusaLink:
        return '🔗';
      case PrinterBackend.SDCP:
        return '📡';
      default:
        return '🖨️';
    }
  };

  return (
    <div className="bg-white overflow-hidden shadow rounded-lg">
      {/* Header */}
      <div className="px-4 py-5 sm:p-6">
        <div className="flex items-center justify-between">
          <div className="flex items-center">
            <span className="text-2xl mr-3">{getBackendIcon(printer.backend)}</span>
            <div>
              <h3 className="text-lg font-medium text-gray-900">
                {printer.name}
              </h3>
              <p className="text-sm text-gray-500">
                {printer.manufacturerName} {printer.modelName}
              </p>
            </div>
          </div>
          
          <span className={`inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-medium ${getStatusColor(currentStatus.isOnline, currentStatus.state)}`}>
            {currentStatus.isOnline ? (currentStatus.state || 'Unknown') : 'Offline'}
          </span>
        </div>

        {/* Progress Bar */}
        {currentStatus.isOnline && currentStatus.progress !== undefined && currentStatus.progress > 0 && (
          <div className="mt-4">
            <div className="flex justify-between text-sm text-gray-600 mb-1">
              <span>{currentStatus.jobName || 'Printing...'}</span>
              <span>{Math.round(currentStatus.progress)}%</span>
            </div>
            <div className="w-full bg-gray-200 rounded-full h-2">
              <div
                className="bg-blue-600 h-2 rounded-full transition-all duration-300"
                style={{ width: `${currentStatus.progress}%` }}
              ></div>
            </div>
          </div>
        )}

        {/* Temperature Display */}
        {currentStatus.isOnline && (currentStatus.hotendTemp || currentStatus.bedTemp) && (
          <div className="mt-4 grid grid-cols-2 gap-4">
            {currentStatus.hotendTemp !== undefined && (
              <div className="text-center">
                <div className="text-xs text-gray-500">Hotend</div>
                <div className="text-lg font-semibold text-gray-900">
                  {Math.round(currentStatus.hotendTemp)}°
                  {currentStatus.hotendTarget && (
                    <span className="text-sm text-gray-500">
                      /{Math.round(currentStatus.hotendTarget)}°
                    </span>
                  )}
                </div>
              </div>
            )}
            
            {currentStatus.bedTemp !== undefined && (
              <div className="text-center">
                <div className="text-xs text-gray-500">Bed</div>
                <div className="text-lg font-semibold text-gray-900">
                  {Math.round(currentStatus.bedTemp)}°
                  {currentStatus.bedTarget && (
                    <span className="text-sm text-gray-500">
                      /{Math.round(currentStatus.bedTarget)}°
                    </span>
                  )}
                </div>
              </div>
            )}
          </div>
        )}

        {/* Actions */}
        <div className="mt-4 flex justify-end space-x-2">
          <button className="inline-flex items-center px-3 py-2 border border-gray-300 shadow-sm text-sm leading-4 font-medium rounded-md text-gray-700 bg-white hover:bg-gray-50 focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-blue-500 transition-colors">
            <svg className="h-4 w-4 mr-1.5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M10.325 4.317c.426-1.756 2.924-1.756 3.35 0a1.724 1.724 0 002.573 1.066c1.543-.94 3.31.826 2.37 2.37a1.724 1.724 0 001.065 2.572c1.756.426 1.756 2.924 0 3.35a1.724 1.724 0 00-1.066 2.573c.94 1.543-.826 3.31-2.37 2.37a1.724 1.724 0 00-2.572 1.065c-.426 1.756-2.924 1.756-3.35 0a1.724 1.724 0 00-2.573-1.066c-1.543.94-3.31-.826-2.37-2.37a1.724 1.724 0 00-1.065-2.572c-1.756-.426-1.756-2.924 0-3.35a1.724 1.724 0 001.066-2.573c-.94-1.543.826-3.31 2.37-2.37.996.608 2.296.07 2.572-1.065z" />
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M15 12a3 3 0 11-6 0 3 3 0 016 0z" />
            </svg>
            Manage
          </button>
          
          {currentStatus.isOnline && (
            <button className="inline-flex items-center px-3 py-2 border border-transparent text-sm leading-4 font-medium rounded-md text-white bg-blue-600 hover:bg-blue-700 focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-blue-500 transition-colors">
              <svg className="h-4 w-4 mr-1.5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M14.828 14.828a4 4 0 01-5.656 0M9 10h1m4 0h1m-6 4h.01M19 10a9 9 0 11-18 0 9 9 0 0118 0z" />
              </svg>
              Control
            </button>
          )}
        </div>

        {/* Notes */}
        {printer.notes && (
          <div className="mt-3 pt-3 border-t border-gray-100">
            <p className="text-xs text-gray-500 italic">{printer.notes}</p>
          </div>
        )}
      </div>
    </div>
  );
}
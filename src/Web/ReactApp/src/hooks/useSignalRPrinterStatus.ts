/**
 * useSignalRPrinterStatus Hook
 * Manages real-time printer status updates via SignalR connection
 */

import { useEffect, useRef, useState } from 'react';
import { HubConnection, HubConnectionBuilder, LogLevel } from '@microsoft/signalr';

export interface PrinterStatusUpdate {
  printerId: string;
  name: string;
  state: 'Idle' | 'Printing' | 'Paused' | 'Error' | 'Offline';
  nozzlePosition?: {
    x: number;
    y: number;
    z: number;
  };
  temperatures?: {
    hotend: number;
    hotendTarget: number;
    bed: number;
    bedTarget: number;
  };
  progress?: number;
  jobName?: string;
  errorMessage?: string;
}

export interface UseSignalRPrinterStatusReturn {
  status: PrinterStatusUpdate | null;
  isConnected: boolean;
  error: string | null;
  reconnect: () => void;
}

const HUB_URL = import.meta.env.VITE_API_URL 
  ? `${import.meta.env.VITE_API_URL}/hubs/printers`
  : '/hubs/printers';

/**
 * Hook for managing real-time printer status via SignalR
 * Subscribes to printer-specific status updates and connection events
 *
 * @param printerId - The ID of the printer to monitor
 * @returns Object with status, connection state, error, and reconnect function
 *
 * @example
 * ```tsx
 * const { status, isConnected, error } = useSignalRPrinterStatus('printer-1');
 *
 * if (!isConnected) return <div>Disconnected from printer</div>;
 * if (error) return <div>Error: {error}</div>;
 * if (!status) return <div>Loading...</div>;
 *
 * return <PrinterBedVisualization status={status} />;
 * ```
 */
export function useSignalRPrinterStatus(printerId: string): UseSignalRPrinterStatusReturn {
  const [status, setStatus] = useState<PrinterStatusUpdate | null>(null);
  const [isConnected, setIsConnected] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const connectionRef = useRef<HubConnection | null>(null);
  const reconnectTimeoutRef = useRef<NodeJS.Timeout>();
  const reconnectAttemptsRef = useRef(0);

  // Build and manage SignalR connection
  useEffect(() => {
    if (!printerId) {
      setError('Printer ID is required');
      return;
    }

    const buildConnection = async () => {
      try {
        if (connectionRef.current) {
          await connectionRef.current.stop();
        }

        // Create new connection
        const connection = new HubConnectionBuilder()
          .withUrl(HUB_URL, {
            accessTokenFactory: () => {
              // Get token from localStorage if needed
              const token = localStorage.getItem('authToken');
              return token || '';
            },
          })
          .withAutomaticReconnect([0, 1000, 2000, 5000, 10000])
          .configureLogging(LogLevel.Warning)
          .build();

        // Set up event handlers
        connection.on('printerupdated', (update: PrinterStatusUpdate) => {
          if (update.printerId === printerId) {
            setStatus(update);
            setError(null);
            reconnectAttemptsRef.current = 0;
          }
        });

        connection.on('positionupdate', (data: any) => {
          // Real-time position update without full status
          if (data.printerId === printerId && status) {
            setStatus({
              ...status,
              nozzlePosition: {
                x: data.x ?? status.nozzlePosition?.x ?? 0,
                y: data.y ?? status.nozzlePosition?.y ?? 0,
                z: data.z ?? status.nozzlePosition?.z ?? 0,
              },
            });
          }
        });

        connection.on('temperatureupdate', (data: any) => {
          // Real-time temperature update
          if (data.printerId === printerId && status) {
            setStatus({
              ...status,
              temperatures: {
                hotend: data.hotend ?? status.temperatures?.hotend ?? 0,
                hotendTarget: data.hotendTarget ?? status.temperatures?.hotendTarget ?? 0,
                bed: data.bed ?? status.temperatures?.bed ?? 0,
                bedTarget: data.bedTarget ?? status.temperatures?.bedTarget ?? 0,
              },
            });
          }
        });

        connection.on('statechanged', (data: any) => {
          // Printer state changed
          if (data.printerId === printerId && status) {
            setStatus({
              ...status,
              state: data.state ?? status.state,
              errorMessage: data.error,
            });
          }
        });

        // Connection lifecycle events
        connection.onreconnecting(() => {
          setIsConnected(false);
          reconnectAttemptsRef.current++;
          if (reconnectAttemptsRef.current > 5) {
            setError('Lost connection to printer. Attempting to reconnect...');
          }
        });

        connection.onreconnected(() => {
          setIsConnected(true);
          setError(null);
          reconnectAttemptsRef.current = 0;
        });

        connection.onclose(() => {
          setIsConnected(false);
          // Attempt to reconnect after delay
          reconnectTimeoutRef.current = setTimeout(() => {
            buildConnection();
          }, 5000);
        });

        // Start connection
        await connection.start();
        connectionRef.current = connection;
        setIsConnected(true);
        setError(null);
        reconnectAttemptsRef.current = 0;

        // Request initial status
        try {
          await connection.invoke('SubscribeToPrinter', printerId);
        } catch (err) {
          console.error('Failed to subscribe to printer updates:', err);
        }
      } catch (err) {
        const errorMessage = err instanceof Error ? err.message : 'Failed to connect to printer hub';
        setError(errorMessage);
        setIsConnected(false);

        // Retry connection after delay
        reconnectTimeoutRef.current = setTimeout(() => {
          buildConnection();
        }, 3000);
      }
    };

    buildConnection();

    // Cleanup on unmount
    return () => {
      if (reconnectTimeoutRef.current) {
        clearTimeout(reconnectTimeoutRef.current);
      }

      if (connectionRef.current) {
        connectionRef.current.stop().catch((err) => console.error('Error stopping connection:', err));
      }
    };
  }, [printerId]);

  // Reconnect function for manual retry
  const reconnect = () => {
    setError(null);
    reconnectAttemptsRef.current = 0;

    if (connectionRef.current) {
      connectionRef.current
        .stop()
        .then(() => {
          connectionRef.current = null;
          // Trigger rebuild of connection via useEffect
        })
        .catch((err) => console.error('Error stopping connection for reconnect:', err));
    }
  };

  return { status, isConnected, error, reconnect };
}

export default useSignalRPrinterStatus;
